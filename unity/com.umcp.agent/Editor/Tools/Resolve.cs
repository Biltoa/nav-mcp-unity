using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Umcp.Agent
{
    /// <summary>
    /// Target resolution. Every "not found" error carries did-you-mean candidates, because most
    /// agent retry loops are caused by a typo or a stale name rather than by a real failure.
    /// </summary>
    internal static class Resolve
    {
        /// <summary>
        /// Resolve a GameObject from: an instance id ("#12345" or a bare integer), a scene path
        /// ("Parent/Child"), or a plain name (first match, breadth-first over open scenes).
        /// </summary>
        public static GameObject GameObject(string target, string paramName = "target")
        {
            if (string.IsNullOrEmpty(target))
                throw new UmcpToolException("E_ARG_REQUIRED", "A target is required.", paramName);

            var go = TryGameObject(target);
            if (go != null) return go;

            var names = AllGameObjects().Select(g => g.name).Distinct().ToArray();
            throw new UmcpToolException(
                "E_TARGET_NOT_FOUND",
                "No GameObject matched '" + target + "'.",
                paramName, target,
                Suggest.Closest(target, names, 3),
                "Names are case-sensitive. Use scene.find to enumerate, or pass '#<instanceId>'.");
        }

        public static GameObject TryGameObject(string target)
        {
            if (string.IsNullOrEmpty(target)) return null;

            var idText = target.StartsWith("#") ? target.Substring(1) : target;
            int id;
            if (int.TryParse(idText, out id))
            {
                var byId = EditorUtility.InstanceIDToObject(id) as GameObject;
                if (byId != null) return byId;
                var comp = EditorUtility.InstanceIDToObject(id) as Component;
                if (comp != null) return comp.gameObject;
                if (target.StartsWith("#")) return null;
            }

            if (target.IndexOf('/') >= 0)
            {
                foreach (var root in AllRoots())
                {
                    var found = ByPath(root, target);
                    if (found != null) return found;
                }
                return null;
            }

            foreach (var go in AllGameObjects())
                if (go.name == target) return go;
            return null;
        }

        static GameObject ByPath(GameObject root, string path)
        {
            var parts = path.Split('/');
            if (root.name != parts[0]) return null;
            var t = root.transform;
            for (int i = 1; i < parts.Length; i++)
            {
                t = t.Find(parts[i]);
                if (t == null) return null;
            }
            return t.gameObject;
        }

        public static IEnumerable<GameObject> AllRoots()
        {
            var stage = PrefabStageUtility.GetCurrentPrefabStage();
            if (stage != null)
            {
                yield return stage.prefabContentsRoot;
                yield break;
            }
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                var scene = SceneManager.GetSceneAt(i);
                if (!scene.isLoaded) continue;
                foreach (var root in scene.GetRootGameObjects()) yield return root;
            }
        }

        public static IEnumerable<GameObject> AllGameObjects()
        {
            foreach (var root in AllRoots())
                foreach (var t in root.GetComponentsInChildren<Transform>(true))
                    yield return t.gameObject;
        }

        /// <summary>Full scene path of a transform, e.g. "Level/Props/Crate".</summary>
        public static string Path(Transform t)
        {
            var sb = new System.Text.StringBuilder(t.name);
            var p = t.parent;
            while (p != null) { sb.Insert(0, p.name + "/"); p = p.parent; }
            return sb.ToString();
        }

        /// <summary>
        /// Resolve a component Type by short or full name, searching loaded assemblies.
        /// Unity's own <c>GameObject.AddComponent(string)</c> was removed; this replaces it and
        /// gives a far better error when the name is wrong.
        /// </summary>
        public static Type ComponentType(string typeName, string paramName = "type")
        {
            if (string.IsNullOrEmpty(typeName))
                throw new UmcpToolException("E_ARG_REQUIRED", "A component type is required.", paramName);

            var t = FindType(typeName);
            if (t != null && typeof(Component).IsAssignableFrom(t)) return t;
            if (t != null)
                throw new UmcpToolException("E_TYPE_NOT_COMPONENT",
                    "'" + typeName + "' resolves to " + t.FullName + ", which is not a Component.",
                    paramName, typeName, null, "Pass a UnityEngine.Component-derived type.");

            var candidates = ComponentTypeNames();
            throw new UmcpToolException("E_TYPE_NOT_FOUND",
                "No component type named '" + typeName + "'.",
                paramName, typeName,
                Suggest.Closest(typeName, candidates, 3),
                "Use the short name (Rigidbody) or the full name (UnityEngine.Rigidbody).");
        }

        static string[] _componentNameCache;

        static string[] ComponentTypeNames()
        {
            if (_componentNameCache != null) return _componentNameCache;
            var names = new List<string>();
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type[] types;
                try { types = asm.GetTypes(); } catch { continue; }
                foreach (var t in types)
                    if (typeof(Component).IsAssignableFrom(t) && !t.IsAbstract) names.Add(t.Name);
            }
            _componentNameCache = names.Distinct().ToArray();
            return _componentNameCache;
        }

        public static Type FindType(string name)
        {
            var direct = Type.GetType(name, false);
            if (direct != null) return direct;
            foreach (var prefix in new[] { "UnityEngine.", "UnityEngine.UI.", "UnityEditor.", "TMPro." })
            {
                var t = Type.GetType(prefix + name + ", " + AssemblyFor(prefix), false);
                if (t != null) return t;
            }
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type[] types;
                try { types = asm.GetTypes(); } catch { continue; }
                foreach (var t in types)
                    if (t.Name == name || t.FullName == name) return t;
            }
            return null;
        }

        static string AssemblyFor(string prefix)
        {
            switch (prefix)
            {
                case "UnityEngine.": return "UnityEngine.CoreModule";
                case "UnityEngine.UI.": return "UnityEngine.UI";
                case "UnityEditor.": return "UnityEditor.CoreModule";
                default: return "Unity.TextMeshPro";
            }
        }

        /// <summary>Canonicalise an asset path and confine it under Assets/ or Packages/.</summary>
        public static string AssetPath(string path, string paramName = "path")
        {
            if (string.IsNullOrEmpty(path))
                throw new UmcpToolException("E_ARG_REQUIRED", "An asset path is required.", paramName);

            var p = path.Replace('\\', '/').Trim();
            if (p.Contains("../") || p.Contains("/.."))
                throw new UmcpToolException("E_PATH_ESCAPE",
                    "Relative segments are rejected, not resolved.", paramName, path, null,
                    "Pass a project-relative path under Assets/ or Packages/.");

            if (!p.StartsWith("Assets/") && !p.StartsWith("Packages/") && p != "Assets" && p != "Packages")
                throw new UmcpToolException("E_PATH_OUTSIDE_PROJECT",
                    "Asset paths must resolve under Assets/ or Packages/.", paramName, path, null,
                    "For example: Assets/Materials/Rock.mat");
            return p;
        }
    }

    /// <summary>Cheap Levenshtein-based suggestions for did-you-mean errors.</summary>
    internal static class Suggest
    {
        public static string[] Closest(string needle, IEnumerable<string> haystack, int take)
        {
            if (string.IsNullOrEmpty(needle) || haystack == null) return null;
            var scored = haystack
                .Where(s => !string.IsNullOrEmpty(s))
                .Distinct()
                .Select(s => new { s, d = Distance(needle.ToLowerInvariant(), s.ToLowerInvariant()) })
                .Where(x => x.d <= Math.Max(2, needle.Length / 2))
                .OrderBy(x => x.d)
                .Take(take)
                .Select(x => x.s)
                .ToArray();
            return scored.Length == 0 ? null : scored;
        }

        static int Distance(string a, string b)
        {
            if (a == b) return 0;
            if (a.Length == 0) return b.Length;
            if (b.Length == 0) return a.Length;
            var prev = new int[b.Length + 1];
            var cur = new int[b.Length + 1];
            for (int j = 0; j <= b.Length; j++) prev[j] = j;
            for (int i = 1; i <= a.Length; i++)
            {
                cur[0] = i;
                for (int j = 1; j <= b.Length; j++)
                {
                    int cost = a[i - 1] == b[j - 1] ? 0 : 1;
                    cur[j] = Math.Min(Math.Min(cur[j - 1] + 1, prev[j] + 1), prev[j - 1] + cost);
                }
                var t = prev; prev = cur; cur = t;
            }
            return prev[b.Length];
        }
    }
}
