using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Umcp.Agent
{
    /// <summary>
    /// Making an agent's edits reviewable: what is broken (<c>scene.validate</c>) and what changed
    /// (<c>scene.mark</c> / <c>scene.diff</c>).
    ///
    /// Both are the same argument. An agent that edits a scene and reports "done" is asking to be
    /// trusted; one that reports the eleven objects it added and the two references it broke is
    /// asking to be checked.
    /// </summary>
    internal static class InspectTools
    {
        // ---------------------------------------------------------------- validate

        [UnityTool(Skill = "scene", Id = "scene.validate",
            Summary = "Find broken things in the open scenes: missing scripts, missing prefab assets, dangling object references, missing materials.",
            Retry = RetryClass.Read, Cost = Cost.Moderate)]
        [Example("{ }")]
        [Example("{ \"checks\": [\"scripts\", \"references\"], \"limit\": 50 }")]
        public static object Validate(
            [Doc("Which checks: scripts, prefabs, references, materials. Default all.")] string[] checks = null,
            [Doc("Only inside this subtree (path or #id)")] string root = null,
            [Doc("Maximum findings (default 100)")] int limit = 100)
        {
            int cap = Bounds.Limit(limit);
            var wanted = new HashSet<string>(checks == null || checks.Length == 0
                ? new[] { "scripts", "prefabs", "references", "materials" }
                : checks.Select(c => (c ?? "").ToLowerInvariant()));

            IEnumerable<GameObject> scope;
            if (string.IsNullOrEmpty(root)) scope = Resolve.AllGameObjects();
            else
            {
                var rootGo = Resolve.GameObject(root, "root");
                scope = rootGo.GetComponentsInChildren<Transform>(true).Select(t => t.gameObject);
            }

            var findings = new List<object>();
            int scanned = 0;

            foreach (var go in scope)
            {
                scanned++;
                if (findings.Count >= cap) break;
                var path = Resolve.Path(go.transform);

                if (wanted.Contains("scripts"))
                {
                    var components = go.GetComponents<Component>();
                    for (int i = 0; i < components.Length; i++)
                    {
                        // A null entry in this array is a MonoBehaviour whose script asset is gone.
                        // Unity shows it as "Missing (Mono Script)" and it silently does nothing.
                        if (components[i] != null) continue;
                        findings.Add(new
                        {
                            severity = "error",
                            code = "MISSING_SCRIPT",
                            subject = path,
                            detail = "Component slot " + i + " has no script asset.",
                            fix = "Restore the script, or remove the component."
                        });
                    }
                }

                if (wanted.Contains("prefabs") && PrefabUtility.IsPartOfPrefabInstance(go))
                {
                    if (PrefabUtility.IsPrefabAssetMissing(go))
                        findings.Add(new
                        {
                            severity = "error",
                            code = "MISSING_PREFAB_ASSET",
                            subject = path,
                            detail = "This is a prefab instance whose prefab asset no longer exists.",
                            fix = "Restore the prefab asset, or unpack the instance."
                        });
                }

                if (wanted.Contains("references"))
                    foreach (var f in DanglingReferences(go, path, cap - findings.Count)) findings.Add(f);

                if (wanted.Contains("materials"))
                {
                    var renderer = go.GetComponent<Renderer>();
                    if (renderer != null)
                    {
                        var materials = renderer.sharedMaterials;
                        for (int i = 0; i < materials.Length; i++)
                        {
                            if (materials[i] == null)
                                findings.Add(new
                                {
                                    severity = "warning",
                                    code = "MISSING_MATERIAL",
                                    subject = path,
                                    detail = "Material slot " + i + " is empty; it renders magenta.",
                                    fix = "Assign a material to slot " + i + "."
                                });
                            else if (materials[i].shader == null || materials[i].shader.name == "Hidden/InternalErrorShader")
                                findings.Add(new
                                {
                                    severity = "error",
                                    code = "BROKEN_SHADER",
                                    subject = path,
                                    detail = "Material '" + materials[i].name + "' has no usable shader.",
                                    fix = "Reassign the shader, or fix its compile errors (see compile.errors)."
                                });
                        }
                    }
                }
            }

            var list = findings.Take(cap).ToArray();
            return new
            {
                scanned,
                checksRun = wanted.ToArray(),
                count = list.Length,
                findings = list,
                _truncated = findings.Count >= cap,
                _hint = list.Length == 0 ? "Nothing broken in the checks that were run." : null
            };
        }

        /// <summary>
        /// A reference that points at something that no longer exists, distinguished from one that
        /// was never set. Unity keeps the instance id of a deleted target, so
        /// <c>objectReferenceInstanceIDValue != 0</c> with a null value is exactly "this used to
        /// point somewhere". An unset field is not a defect and is not reported.
        /// </summary>
        static IEnumerable<object> DanglingReferences(GameObject go, string path, int budget)
        {
            if (budget <= 0) yield break;
            int found = 0;

            foreach (var component in go.GetComponents<Component>())
            {
                if (component == null) continue;   // already reported as a missing script
                SerializedObject so;
                try { so = new SerializedObject(component); }
                catch { continue; }

                var property = so.GetIterator();
                var enterChildren = true;
                while (property.NextVisible(enterChildren))
                {
                    enterChildren = false;
                    if (property.propertyType != SerializedPropertyType.ObjectReference) continue;
                    if (property.objectReferenceValue != null) continue;
                    if (property.objectReferenceInstanceIDValue == 0) continue;

                    found++;
                    yield return new
                    {
                        severity = "error",
                        code = "MISSING_REFERENCE",
                        subject = path,
                        detail = component.GetType().Name + "." + property.propertyPath +
                                 " points at an object that no longer exists.",
                        fix = "Reassign the reference, or clear it."
                    };
                    if (found >= budget) yield break;
                }
                so.Dispose();
            }
        }

        // ---------------------------------------------------------------- mark and diff

        sealed class Marker
        {
            public string id;
            public DateTime taken;
            public int epoch;
            public Dictionary<string, uint> nodes;   // path -> node hash
        }

        static readonly Dictionary<string, Marker> _markers = new Dictionary<string, Marker>();
        static readonly Queue<string> _markerOrder = new Queue<string>();
        const int MaxMarkers = 8;
        const int MaxMarkedObjects = 20000;

        [UnityTool(Skill = "scene", Id = "scene.mark",
            Summary = "Take a marker of the open scenes' current state, for scene.diff to compare against later.",
            Mutating = false, Retry = RetryClass.Read, Cost = Cost.Moderate)]
        [Example("{ }")]
        public static object Mark([Doc("Marker name. Defaults to an auto-generated one.")] string name = null)
        {
            var nodes = Snapshot();
            if (nodes.Count > MaxMarkedObjects)
                throw new UmcpToolException("E_TOO_LARGE",
                    "This scene has " + nodes.Count + " objects; markers are capped at " + MaxMarkedObjects + ".",
                    "name", name, null,
                    "Mark a subtree instead by working inside it, or use vcs.touched for asset-level change tracking.");

            var id = string.IsNullOrEmpty(name) ? "m" + (UmcpAgent.Epoch + "_" + DateTime.Now.ToString("HHmmss")) : name;
            var marker = new Marker { id = id, taken = DateTime.Now, epoch = UmcpAgent.Epoch, nodes = nodes };

            if (!_markers.ContainsKey(id)) _markerOrder.Enqueue(id);
            _markers[id] = marker;
            while (_markerOrder.Count > MaxMarkers)
            {
                var oldest = _markerOrder.Dequeue();
                if (oldest != id) _markers.Remove(oldest);
            }

            return new { marker = id, objects = nodes.Count, epoch = marker.epoch };
        }

        [UnityTool(Skill = "scene", Id = "scene.diff",
            Summary = "What changed in the open scenes since a marker: objects added, removed, renamed, reparented, or otherwise altered.",
            Retry = RetryClass.Read, Cost = Cost.Moderate)]
        [Example("{ \"marker\": \"before-edit\" }")]
        public static object Diff(
            [Doc("Marker id from scene.mark")] string marker,
            [Doc("Maximum entries per category (default 50)")] int limit = 50)
        {
            if (string.IsNullOrEmpty(marker))
                throw new UmcpToolException("E_ARG_REQUIRED", "A marker id is required.", "marker", null,
                    _markers.Keys.ToArray(), "Take one first with scene.mark.");

            Marker before;
            if (!_markers.TryGetValue(marker, out before))
                throw new UmcpToolException("E_MARKER_NOT_FOUND",
                    "No marker '" + marker + "'.", "marker", marker,
                    Suggest.Closest(marker, _markers.Keys.ToArray(), 3),
                    _markers.Count == 0
                        ? "Markers live in the Editor's memory and are lost on a domain reload. Take a new one with scene.mark."
                        : "Known markers: " + string.Join(", ", _markers.Keys.ToArray()));

            int cap = Bounds.Limit(limit);
            var after = Snapshot();

            var added = after.Keys.Where(p => !before.nodes.ContainsKey(p)).OrderBy(p => p).ToArray();
            var removed = before.nodes.Keys.Where(p => !after.ContainsKey(p)).OrderBy(p => p).ToArray();
            var changed = after.Keys.Where(p => before.nodes.ContainsKey(p) && before.nodes[p] != after[p])
                                    .OrderBy(p => p).ToArray();

            return new
            {
                marker = before.id,
                takenAt = before.taken.ToString("O"),
                markerEpoch = before.epoch,
                epoch = UmcpAgent.Epoch,
                stale = before.epoch != UmcpAgent.Epoch,
                objectsBefore = before.nodes.Count,
                objectsAfter = after.Count,
                added = added.Take(cap).ToArray(),
                removed = removed.Take(cap).ToArray(),
                changed = changed.Take(cap).ToArray(),
                counts = new { added = added.Length, removed = removed.Length, changed = changed.Length },
                _truncated = added.Length > cap || removed.Length > cap || changed.Length > cap,
                _hint = "changed means name, sibling order, active state, tag, layer or component set differs. " +
                        "A moved object shows as removed at its old path and added at the new one."
            };
        }

        /// <summary>
        /// path → hash of the fields the reconcile hash already covers. Reusing
        /// <see cref="MirrorHash.Node"/> means a diff and a reconcile agree about what "the same"
        /// means; two definitions would quietly disagree at the edges.
        /// </summary>
        static Dictionary<string, uint> Snapshot()
        {
            var map = new Dictionary<string, uint>();
            foreach (var go in Resolve.AllGameObjects())
            {
                var path = Resolve.Path(go.transform);
                var components = string.Join(",", go.GetComponents<Component>()
                    .Select(c => c == null ? "<missing>" : c.GetType().Name).ToArray());
                var hash = MirrorHash.Node(
                    go.name,
                    go.transform.GetSiblingIndex(),
                    go.activeSelf ? 1 : 0,
                    go.tag,
                    LayerMask.LayerToName(go.layer),
                    components);
                map[path] = hash;
            }
            return map;
        }

        // ---------------------------------------------------------------- vcs

        [UnityTool(Skill = "assets", Id = "vcs.touched",
            Summary = "Which assets changed recently, in VCS terms: modified files under Assets/, and git status when the project is a repository.",
            Retry = RetryClass.Read, Cost = Cost.Moderate)]
        [Example("{ \"sinceMinutes\": 60 }")]
        public static object Touched(
            [Doc("Look back this many minutes (default 60)")] int sinceMinutes = 60,
            [Doc("Maximum files (default 100)")] int limit = 100)
        {
            int cap = Bounds.Limit(limit);
            var since = DateTime.Now.AddMinutes(-Math.Max(1, sinceMinutes));
            var root = UmcpSettings.ProjectRoot;
            var assets = System.IO.Path.Combine(root, "Assets");

            var touched = new List<object>();
            int total = 0;
            foreach (var file in System.IO.Directory.GetFiles(assets, "*", System.IO.SearchOption.AllDirectories))
            {
                if (file.EndsWith(".meta", StringComparison.OrdinalIgnoreCase)) continue;
                System.IO.FileInfo info;
                try { info = new System.IO.FileInfo(file); }
                catch { continue; }
                if (info.LastWriteTime < since) continue;

                total++;
                if (touched.Count >= cap) continue;
                var relative = file.Substring(root.Length + 1).Replace('\\', '/');
                touched.Add(new
                {
                    path = relative,
                    modified = info.LastWriteTime.ToString("O"),
                    bytes = info.Length,
                    guid = AssetDatabase.AssetPathToGUID(relative)
                });
            }

            bool isGitRepo = System.IO.Directory.Exists(System.IO.Path.Combine(root, ".git"));
            return new
            {
                sinceMinutes,
                since = since.ToString("O"),
                count = total,
                files = touched.ToArray(),
                isGitRepo,
                _truncated = total > touched.Count,
                _hint = isGitRepo
                    ? "This project is a git repository; `git status` in the project root is authoritative about what is staged."
                    : "File modification times are the only evidence here — this project is not a git repository."
            };
        }
    }
}
