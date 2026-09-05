using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Umcp.Agent
{
    /// <summary>
    /// The Editor half of the mirror: turn Unity's own change stream into compact deltas and push
    /// them to the daemon, so reads never have to wait for a tick.
    ///
    /// It subscribes to <c>ObjectChangeEvents.changesPublished</c> — Unity's typed stream of every
    /// undoable change, flushed once per frame — and, whenever it sees a change kind it does not
    /// specifically understand, asks the daemon to re-snapshot rather than guessing. A gap treated
    /// as "nothing happened" is exactly how a mirror starts lying; a gap treated as a resync
    /// trigger costs one snapshot.
    ///
    /// Everything here runs on the main thread. The delta is handed to the connection, which is
    /// the only thing that crosses a thread boundary.
    /// </summary>
    internal static class UmcpMirror
    {
        /// <summary>Ids whose own record changed.</summary>
        static readonly HashSet<int> _dirty = new HashSet<int>();
        /// <summary>Ids whose whole subtree must be resent (structure or ordering changed).</summary>
        static readonly HashSet<int> _subtree = new HashSet<int>();
        /// <summary>Ids known to be gone.</summary>
        static readonly HashSet<int> _removed = new HashSet<int>();

        static bool _resync;
        /// <summary>
        /// Creating or destroying a root shifts every later root's sibling index, and Unity emits
        /// no event for the roots that merely moved. Their records — and therefore their hashes —
        /// would silently go stale, so any root-level create or destroy re-emits them all. Roots
        /// are few; this is cheaper than the resync it prevents.
        /// </summary>
        static bool _rootOrderDirty;
        static int _seq;
        static Action<string> _send;

        public static bool Enabled { get; private set; }
        public static int Sequence { get { return _seq; } }

        public static void Start(Action<string> send)
        {
            _send = send;
            ObjectChangeEvents.changesPublished -= OnChanges;
            ObjectChangeEvents.changesPublished += OnChanges;
            EditorSceneManagerHooks.Install();
            Reset();
            Enabled = true;
        }

        public static void Stop()
        {
            ObjectChangeEvents.changesPublished -= OnChanges;
            EditorSceneManagerHooks.Uninstall();
            Enabled = false;
            _send = null;
        }

        public static void Reset()
        {
            _dirty.Clear();
            _subtree.Clear();
            _removed.Clear();
            _resync = false;
            _rootOrderDirty = false;
        }

        /// <summary>Ask the daemon to throw its model away and re-snapshot.</summary>
        public static void RequestResync() { _resync = true; }

        // ------------------------------------------------------------------ change stream

        static void OnChanges(ref ObjectChangeEventStream stream)
        {
            for (int i = 0; i < stream.length; i++)
            {
                switch (stream.GetEventType(i))
                {
                    case ObjectChangeKind.CreateGameObjectHierarchy:
                        {
                            CreateGameObjectHierarchyEventArgs e;
                            stream.GetCreateGameObjectHierarchyEvent(i, out e);
                            _subtree.Add(e.instanceId);
                            _rootOrderDirty = true;
                            break;
                        }
                    case ObjectChangeKind.DestroyGameObjectHierarchy:
                        {
                            DestroyGameObjectHierarchyEventArgs e;
                            stream.GetDestroyGameObjectHierarchyEvent(i, out e);
                            _removed.Add(e.instanceId);
                            // Removing a child shifts every later sibling's index, and Unity emits
                            // nothing for the siblings that merely moved. Re-emit the parent's
                            // whole subtree rather than just the parent, or those indices — and
                            // therefore the hashes — go quietly stale.
                            if (e.parentInstanceId != 0) _subtree.Add(e.parentInstanceId);
                            else _rootOrderDirty = true;
                            break;
                        }
                    case ObjectChangeKind.ChangeGameObjectStructureHierarchy:
                        {
                            ChangeGameObjectStructureHierarchyEventArgs e;
                            stream.GetChangeGameObjectStructureHierarchyEvent(i, out e);
                            _subtree.Add(e.instanceId);
                            break;
                        }
                    case ObjectChangeKind.ChangeGameObjectStructure:
                        {
                            ChangeGameObjectStructureEventArgs e;
                            stream.GetChangeGameObjectStructureEvent(i, out e);
                            _dirty.Add(e.instanceId);
                            break;
                        }
                    case ObjectChangeKind.ChangeGameObjectParent:
                        {
                            ChangeGameObjectParentEventArgs e;
                            stream.GetChangeGameObjectParentEvent(i, out e);
                            _subtree.Add(e.instanceId);
                            // Both parents' child lists changed, so both need their whole subtree
                            // re-emitted: leaving a parent renumbers the siblings left behind.
                            if (e.newParentInstanceId != 0) _subtree.Add(e.newParentInstanceId);
                            if (e.previousParentInstanceId != 0) _subtree.Add(e.previousParentInstanceId);
                            if (e.newParentInstanceId == 0 || e.previousParentInstanceId == 0) _rootOrderDirty = true;
                            break;
                        }
                    case ObjectChangeKind.ChangeGameObjectOrComponentProperties:
                        {
                            ChangeGameObjectOrComponentPropertiesEventArgs e;
                            stream.GetChangeGameObjectOrComponentPropertiesEvent(i, out e);
                            _dirty.Add(e.instanceId);
                            break;
                        }
                    case ObjectChangeKind.ChangeChildrenOrder:
                        {
                            ChangeChildrenOrderEventArgs e;
                            stream.GetChangeChildrenOrderEvent(i, out e);
                            _subtree.Add(e.instanceId);
                            break;
                        }
                    case ObjectChangeKind.UpdatePrefabInstances:
                        {
                            UpdatePrefabInstancesEventArgs e;
                            stream.GetUpdatePrefabInstancesEvent(i, out e);
                            foreach (var id in e.instanceIds) _subtree.Add(id);
                            break;
                        }
                    case ObjectChangeKind.ChangeScene:
                    case ObjectChangeKind.ChangeRootOrder:
                        _resync = true;
                        break;
                    case ObjectChangeKind.ChangeAssetObjectProperties:
                        // Assets are not part of the scene mirror in this phase.
                        break;
                    default:
                        // An event kind we do not model. Treat it as a reconcile trigger, never as
                        // "nothing happened".
                        _resync = true;
                        break;
                }
            }
        }

        // ------------------------------------------------------------------ flush

        /// <summary>Called from the main-thread pump once per tick. Sends at most one delta.</summary>
        public static void Flush()
        {
            if (!Enabled || _send == null) return;
            if (!_resync && _dirty.Count == 0 && _subtree.Count == 0 && _removed.Count == 0) return;

            _seq++;

            if (_resync)
            {
                _dirty.Clear(); _subtree.Clear(); _removed.Clear();
                _resync = false; _rootOrderDirty = false;
                _send("{\"t\":\"mirror\",\"resync\":true,\"seq\":" + _seq +
                      ",\"epoch\":" + UmcpAgent.Epoch + "}");
                return;
            }

            var nodes = new JArray();
            var emitted = new HashSet<int>();

            // Snapshot the set: EmitSubtree can discover more removals as it walks.
            foreach (var id in _subtree.ToArray())
            {
                var go = EditorUtility.InstanceIDToObject(id) as GameObject;
                if (go == null) { _removed.Add(id); continue; }
                EmitSubtree(go, nodes, emitted);
            }
            foreach (var id in _dirty)
            {
                if (emitted.Contains(id)) continue;
                var go = EditorUtility.InstanceIDToObject(id) as GameObject;
                if (go == null)
                {
                    // Could be a Component id; the change belongs to its GameObject.
                    var c = EditorUtility.InstanceIDToObject(id) as Component;
                    if (c == null) continue;
                    go = c.gameObject;
                    if (emitted.Contains(go.GetInstanceID())) continue;
                }
                EmitNode(go, nodes, emitted);
            }

            if (_rootOrderDirty)
                foreach (var root in Resolve.AllRoots())
                    EmitNode(root, nodes, emitted);

            var removed = new JArray();
            foreach (var id in _removed) if (!emitted.Contains(id)) removed.Add(id);

            _dirty.Clear(); _subtree.Clear(); _removed.Clear(); _rootOrderDirty = false;

            if (nodes.Count == 0 && removed.Count == 0) return;

            var msg = new JObject
            {
                ["t"] = "mirror",
                ["seq"] = _seq,
                ["epoch"] = UmcpAgent.Epoch,
                ["nodes"] = nodes,
                ["removed"] = removed
            };
            _send(msg.ToString(Formatting.None));
        }

        static void EmitSubtree(GameObject go, JArray into, HashSet<int> emitted)
        {
            EmitNode(go, into, emitted);
            var t = go.transform;
            for (int i = 0; i < t.childCount; i++) EmitSubtree(t.GetChild(i).gameObject, into, emitted);
        }

        static void EmitNode(GameObject go, JArray into, HashSet<int> emitted)
        {
            if (!emitted.Add(go.GetInstanceID())) return;
            into.Add(Record(go));
        }

        // ------------------------------------------------------------------ node records

        /// <summary>
        /// One node, as a positional array: [id, name, parent, sibling, flags, tag, layer,
        /// components, scene]. Layer is a name rather than an index because the daemon has no layer
        /// table and could not otherwise evaluate [layer:Water] itself. Positional because these
        /// frames go to the daemon, never to a model,
        /// and a 4,000-object scene is worth the terseness.
        /// </summary>
        internal static JArray Record(GameObject go)
        {
            var t = go.transform;
            var parent = t.parent;
            // activeSelf only. activeInHierarchy is derived from the ancestor chain, and Unity
            // emits no event for the descendants whose derived value changed when a parent was
            // deactivated or reparented — so mirroring it would drift by construction. The daemon
            // computes it from the chain instead.
            int flags = go.activeSelf ? 1 : 0;

            return new JArray
            {
                go.GetInstanceID(),
                go.name,
                parent == null ? 0 : parent.gameObject.GetInstanceID(),
                t.GetSiblingIndex(),
                flags,
                go.tag,
                LayerMask.LayerToName(go.layer),
                Components(go),
                parent == null ? go.scene.name : null
            };
        }

        internal static string Components(GameObject go)
        {
            var comps = go.GetComponents<Component>();
            var sb = new StringBuilder();
            for (int i = 0; i < comps.Length; i++)
            {
                if (i > 0) sb.Append('|');
                sb.Append(comps[i] == null ? "<missing>" : comps[i].GetType().Name);
            }
            return sb.ToString();
        }

        internal static uint HashOf(GameObject go)
        {
            var t = go.transform;
            int flags = go.activeSelf ? 1 : 0;
            var h = MirrorHash.Node(go.name, t.GetSiblingIndex(), flags, go.tag, LayerMask.LayerToName(go.layer), Components(go));
            for (int i = 0; i < t.childCount; i++) h = MirrorHash.Fold(h, HashOf(t.GetChild(i).gameObject));
            return h;
        }
    }

    /// <summary>Scene open/close/activate is a structural change the object stream does not carry.</summary>
    internal static class EditorSceneManagerHooks
    {
        static bool _installed;

        public static void Install()
        {
            if (_installed) return;
            UnityEditor.SceneManagement.EditorSceneManager.sceneOpened += OnSceneOpened;
            UnityEditor.SceneManagement.EditorSceneManager.sceneClosed += OnSceneClosed;
            SceneManager.activeSceneChanged += OnActiveSceneChanged;
            _installed = true;
        }

        public static void Uninstall()
        {
            if (!_installed) return;
            UnityEditor.SceneManagement.EditorSceneManager.sceneOpened -= OnSceneOpened;
            UnityEditor.SceneManagement.EditorSceneManager.sceneClosed -= OnSceneClosed;
            SceneManager.activeSceneChanged -= OnActiveSceneChanged;
            _installed = false;
        }

        static void OnSceneOpened(Scene scene, UnityEditor.SceneManagement.OpenSceneMode mode) { UmcpMirror.RequestResync(); }
        static void OnSceneClosed(Scene scene) { UmcpMirror.RequestResync(); }
        static void OnActiveSceneChanged(Scene from, Scene to) { UmcpMirror.RequestResync(); }
    }

    internal static class MirrorTools
    {
        [UnityTool(Id = "mirror.snapshot", Skill = "diagnostics",
            Summary = "Full compact hierarchy snapshot. Used by the daemon to seed its mirror; rarely useful directly.",
            Retry = RetryClass.Read, Cost = Cost.Moderate)]
        [Example("{ }")]
        public static object Snapshot()
        {
            var nodes = new List<JArray>();
            foreach (var root in Resolve.AllRoots())
                foreach (var t in root.GetComponentsInChildren<Transform>(true))
                    nodes.Add(UmcpMirror.Record(t.gameObject));

            return new
            {
                epoch = UmcpAgent.Epoch,
                seq = UmcpMirror.Sequence,
                count = nodes.Count,
                activeScene = SceneManager.GetActiveScene().name,
                nodes
            };
        }

        [UnityTool(Id = "mirror.hashes", Skill = "diagnostics",
            Summary = "Per-root subtree hashes, for reconciling the daemon's mirror against the live hierarchy.",
            Retry = RetryClass.Read)]
        [Example("{ }")]
        public static object Hashes()
        {
            var roots = new List<object>();
            uint all = MirrorHash.Start();
            int count = 0;

            foreach (var root in Resolve.AllRoots())
            {
                var h = UmcpMirror.HashOf(root);
                var n = root.GetComponentsInChildren<Transform>(true).Length;
                count += n;
                all = MirrorHash.Fold(all, h);
                roots.Add(new { id = root.GetInstanceID(), name = root.name, hash = h, count = n });
            }

            return new { epoch = UmcpAgent.Epoch, seq = UmcpMirror.Sequence, all, count, roots = roots.ToArray() };
        }
    }
}
