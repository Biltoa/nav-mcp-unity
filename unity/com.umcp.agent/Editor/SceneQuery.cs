using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace Umcp.Agent
{
    /// <summary>
    /// Selector + projection over the open scenes. This is the single largest token saver on the
    /// read path: instead of dumping a scene — 138,205 bytes, about 34,500 tokens, roughly 17% of a
    /// 200 k context window for one call in the implementation being replaced — the caller says
    /// which objects it wants and which fields of them.
    ///
    /// Selector grammar, deliberately small:
    ///
    ///   /Root            a scene root named Root
    ///   /Root/Child      a direct child
    ///   //Button         any descendant named Button, at any depth
    ///   //Canvas//Button descendants of any Canvas
    ///   //*              everything
    ///
    /// Predicates chain, and all must hold:
    ///
    ///   [active] [inactive]        activeInHierarchy
    ///   [has:Rigidbody]            has that component
    ///   [missing:Rigidbody]        does not have it
    ///   [tag:Player]  [layer:Water]
    ///   [name*:Enemy] [name^:UI_] [name$:_LOD0]   contains / starts / ends
    ///   [root] [leaf]
    /// </summary>
    internal static class SceneQuery
    {
        // ------------------------------------------------------------------ selector

        internal sealed class Segment
        {
            public string Name;              // literal, or "*"
            public bool Descendant;          // reached by "//" rather than "/"
            public readonly List<Func<GameObject, bool>> Predicates = new List<Func<GameObject, bool>>();
        }

        internal static List<Segment> Parse(string selector)
        {
            if (string.IsNullOrEmpty(selector)) selector = "//*";
            if (!selector.StartsWith("/")) selector = "//" + selector;

            var segments = new List<Segment>();
            int i = 0;
            while (i < selector.Length)
            {
                bool descendant = false;
                if (selector[i] == '/')
                {
                    i++;
                    if (i < selector.Length && selector[i] == '/') { descendant = true; i++; }
                }
                if (i >= selector.Length) break;

                int start = i;
                while (i < selector.Length && selector[i] != '/' && selector[i] != '[') i++;
                var seg = new Segment { Name = selector.Substring(start, i - start), Descendant = descendant };
                if (seg.Name.Length == 0) seg.Name = "*";

                while (i < selector.Length && selector[i] == '[')
                {
                    int close = selector.IndexOf(']', i);
                    if (close < 0)
                        throw new UmcpToolException("E_SELECTOR_SYNTAX",
                            "Unclosed predicate in selector.", "select", selector, null,
                            "Predicates look like [active] or [has:Rigidbody].");
                    seg.Predicates.Add(Predicate(selector.Substring(i + 1, close - i - 1), selector));
                    i = close + 1;
                }
                segments.Add(seg);
            }

            if (segments.Count == 0)
                throw new UmcpToolException("E_SELECTOR_SYNTAX", "Empty selector.", "select", selector, null,
                    "For example: //Canvas//Button[active]");
            return segments;
        }

        static Func<GameObject, bool> Predicate(string body, string selector)
        {
            var colon = body.IndexOf(':');
            var key = (colon < 0 ? body : body.Substring(0, colon)).Trim();
            var arg = colon < 0 ? null : body.Substring(colon + 1).Trim();

            switch (key)
            {
                case "active": return go => go.activeInHierarchy;
                case "inactive": return go => !go.activeInHierarchy;
                case "root": return go => go.transform.parent == null;
                case "leaf": return go => go.transform.childCount == 0;
                case "has": { var t = Resolve.ComponentType(Need(arg, key, selector), "select"); return go => go.GetComponent(t) != null; }
                case "missing": { var t = Resolve.ComponentType(Need(arg, key, selector), "select"); return go => go.GetComponent(t) == null; }
                case "tag": { var v = Need(arg, key, selector); return go => go.CompareTag(v); }
                case "layer": { var v = Need(arg, key, selector); var idx = LayerMask.NameToLayer(v); return go => go.layer == idx; }
                case "name*": { var v = Need(arg, key, selector); return go => go.name.IndexOf(v, StringComparison.OrdinalIgnoreCase) >= 0; }
                case "name^": { var v = Need(arg, key, selector); return go => go.name.StartsWith(v, StringComparison.OrdinalIgnoreCase); }
                case "name$": { var v = Need(arg, key, selector); return go => go.name.EndsWith(v, StringComparison.OrdinalIgnoreCase); }
                default:
                    throw new UmcpToolException("E_SELECTOR_PREDICATE",
                        "Unknown predicate '" + key + "'.", "select", body,
                        Suggest.Closest(key, new[] { "active", "inactive", "root", "leaf", "has", "missing", "tag", "layer", "name*", "name^", "name$" }, 3),
                        "Predicates: [active] [inactive] [root] [leaf] [has:T] [missing:T] [tag:T] [layer:L] [name*:s] [name^:s] [name$:s]");
            }
        }

        static string Need(string arg, string key, string selector)
        {
            if (string.IsNullOrEmpty(arg))
                throw new UmcpToolException("E_SELECTOR_PREDICATE",
                    "Predicate '" + key + "' needs a value, e.g. [" + key + ":Rigidbody].", "select", selector);
            return arg;
        }

        // ------------------------------------------------------------------ evaluation

        internal static IEnumerable<GameObject> Evaluate(List<Segment> segments, int maxDepth)
        {
            IEnumerable<GameObject> current = Resolve.AllRoots();

            for (int s = 0; s < segments.Count; s++)
            {
                var seg = segments[s];
                var next = new List<GameObject>();
                var seen = new HashSet<int>();

                foreach (var go in current)
                {
                    if (s == 0)
                    {
                        // The first segment matches against the roots themselves, and — when it
                        // was reached by "//" — against every descendant of a root too.
                        Collect(go, seg, seg.Descendant, maxDepth, 0, next, seen, includeSelf: true);
                    }
                    else
                    {
                        var t = go.transform;
                        for (int i = 0; i < t.childCount; i++)
                            Collect(t.GetChild(i).gameObject, seg, seg.Descendant, maxDepth, 0, next, seen, includeSelf: true);
                    }
                }
                current = next;
                if (next.Count == 0) break;
            }
            return current;
        }

        static void Collect(GameObject go, Segment seg, bool descend, int maxDepth, int depth,
                            List<GameObject> into, HashSet<int> seen, bool includeSelf)
        {
            if (maxDepth >= 0 && depth > maxDepth) return;

            if (includeSelf && Matches(go, seg) && seen.Add(go.GetInstanceID()))
                into.Add(go);

            if (!descend) return;
            var t = go.transform;
            for (int i = 0; i < t.childCount; i++)
                Collect(t.GetChild(i).gameObject, seg, true, maxDepth, depth + 1, into, seen, true);
        }

        static bool Matches(GameObject go, Segment seg)
        {
            if (seg.Name != "*" && go.name != seg.Name) return false;
            foreach (var p in seg.Predicates) if (!p(go)) return false;
            return true;
        }

        // ------------------------------------------------------------------ projection

        /// <summary>
        /// Project a GameObject onto the requested fields. Anything not asked for is not
        /// serialised — most of that measured 138 KB was components sitting at their defaults.
        /// </summary>
        internal static Dictionary<string, object> Project(GameObject go, string[] fields)
        {
            var result = new Dictionary<string, object>();
            foreach (var field in fields)
            {
                object value;
                var dot = field.IndexOf('.');
                if (dot > 0)
                {
                    var typeName = field.Substring(0, dot);
                    var propName = field.Substring(dot + 1);
                    value = ComponentField(go, typeName, propName);
                }
                else
                {
                    value = BuiltinField(go, field);
                }
                if (value != null || field.EndsWith("?")) result[field] = value;
            }
            return result;
        }

        static object BuiltinField(GameObject go, string field)
        {
            var t = go.transform;
            switch (field)
            {
                case "id": return go.GetInstanceID();
                case "name": return go.name;
                case "path": return Resolve.Path(t);
                case "active": return go.activeSelf;
                case "activeInHierarchy": return go.activeInHierarchy;
                case "tag": return go.tag;
                case "layer": return LayerMask.LayerToName(go.layer);
                case "parent": return t.parent == null ? null : t.parent.name;
                case "childCount": return t.childCount;
                case "position": return Vec.Arr(t.localPosition);
                case "worldPosition": return Vec.Arr(t.position);
                case "rotation": return Vec.Arr(t.localEulerAngles);
                case "scale": return Vec.Arr(t.localScale);
                case "components": return go.GetComponents<Component>().Select(c => c == null ? "<missing>" : c.GetType().Name).ToArray();
                case "prefab": return PrefabUtility.IsPartOfPrefabInstance(go)
                    ? PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(go) : null;
                default:
                    throw new UmcpToolException("E_FIELD_UNKNOWN",
                        "Unknown field '" + field + "'.", "fields", field,
                        Suggest.Closest(field, KnownFields, 3),
                        "Fields: " + string.Join(", ", KnownFields) + ", or \"Component.property\" such as \"Rigidbody.mass\".");
            }
        }

        static readonly string[] KnownFields =
        {
            "id", "name", "path", "active", "activeInHierarchy", "tag", "layer", "parent",
            "childCount", "position", "worldPosition", "rotation", "scale", "components", "prefab"
        };

        static object ComponentField(GameObject go, string typeName, string propName)
        {
            var type = Resolve.FindType(typeName);
            if (type == null || !typeof(Component).IsAssignableFrom(type))
                throw new UmcpToolException("E_FIELD_UNKNOWN",
                    "'" + typeName + "' is not a component type.", "fields", typeName + "." + propName,
                    null, "Use \"Rigidbody.mass\" style field names.");

            var c = go.GetComponent(type);
            if (c == null) return null;

            var so = new SerializedObject(c);
            var sp = so.FindProperty(propName) ?? so.FindProperty("m_" + char.ToUpperInvariant(propName[0]) + propName.Substring(1));
            if (sp != null) return ReadSerialized(sp);

            var pi = type.GetProperty(propName);
            if (pi != null && pi.CanRead) return Simplify(pi.GetValue(c, null));
            var fi = type.GetField(propName);
            if (fi != null) return Simplify(fi.GetValue(c));

            throw new UmcpToolException("E_FIELD_UNKNOWN",
                type.Name + " has no property '" + propName + "'.", "fields", typeName + "." + propName);
        }

        static object ReadSerialized(SerializedProperty sp)
        {
            switch (sp.propertyType)
            {
                case SerializedPropertyType.Integer: return sp.intValue;
                case SerializedPropertyType.Boolean: return sp.boolValue;
                case SerializedPropertyType.Float: return sp.floatValue;
                case SerializedPropertyType.String: return sp.stringValue;
                case SerializedPropertyType.Enum:
                    return sp.enumValueIndex >= 0 && sp.enumValueIndex < sp.enumNames.Length
                        ? sp.enumNames[sp.enumValueIndex] : (object)sp.enumValueIndex;
                case SerializedPropertyType.Vector2: return Vec.Arr(sp.vector2Value);
                case SerializedPropertyType.Vector3: return Vec.Arr(sp.vector3Value);
                case SerializedPropertyType.Color: return Vec.Arr(sp.colorValue);
                case SerializedPropertyType.ObjectReference:
                    return sp.objectReferenceValue == null ? null : sp.objectReferenceValue.name;
                default: return null;
            }
        }

        static object Simplify(object v)
        {
            if (v == null) return null;
            if (v is Vector3) return Vec.Arr((Vector3)v);
            if (v is Vector2) return Vec.Arr((Vector2)v);
            if (v is Color) return Vec.Arr((Color)v);
            if (v is Quaternion) return Vec.Arr((Quaternion)v);
            if (v is UnityEngine.Object) return ((UnityEngine.Object)v).name;
            if (v is string || v is bool || v.GetType().IsPrimitive) return v;
            if (v is Enum) return v.ToString();
            return v.ToString();
        }
    }

    internal static class SceneQueryTools
    {
        [UnityTool(Id = "scene.query", Summary = "Select GameObjects from the scene hierarchy with a path selector and return only the fields you ask for.",
            Retry = RetryClass.Read)]
        [Example("{ \"select\": \"//Canvas//Button[active]\", \"fields\": [\"name\", \"path\"] }")]
        [Example("{ \"select\": \"//*[has:Rigidbody]\", \"fields\": [\"path\", \"Rigidbody.mass\"], \"limit\": 50 }")]
        [Example("{ \"select\": \"/Level/Props/*\", \"fields\": [\"name\", \"position\"] }")]
        public static object Query(
            [Doc("Selector, e.g. \"//Canvas//Button[active][has:Image]\". Defaults to everything.")] string select = "//*",
            [Doc("Fields to return. Built-ins plus \"Component.property\", e.g. \"Rigidbody.mass\".")] string[] fields = null,
            [Doc("Maximum results (default 100, hard cap 500)")] int limit = 100,
            [Doc("Skip this many matches")] int offset = 0,
            [Doc("Deepest level a \"//\" step will descend. -1 for unlimited.")] int maxDepth = -1,
            [Doc("Return only the number of matches")] bool countOnly = false)
        {
            var segments = SceneQuery.Parse(select);
            var matches = SceneQuery.Evaluate(segments, maxDepth).ToList();

            if (countOnly) return new { count = matches.Count, select };

            var projection = fields == null || fields.Length == 0
                ? new[] { "name", "path" }
                : fields;

            var page = matches.Skip(offset).Take(Bounds.Limit(limit))
                .Select(go => SceneQuery.Project(go, projection))
                .ToArray();

            return new
            {
                items = page,
                _total = matches.Count,
                _returned = page.Length,
                _offset = offset,
                _truncated = offset + page.Length < matches.Count,
                _select = select,
                _hint = offset + page.Length < matches.Count
                    ? "re-query with offset=" + (offset + page.Length) + ", or narrow the selector"
                    : null
            };
        }

        [UnityTool(Id = "scene.count", Summary = "Count GameObjects in the hierarchy matching a selector. The cheapest possible read.",
            Retry = RetryClass.Read)]
        [Example("{ \"select\": \"//*[has:Renderer]\" }")]
        public static object Count([Doc("Selector")] string select = "//*")
        {
            var segments = SceneQuery.Parse(select);
            return new { count = SceneQuery.Evaluate(segments, -1).Count(), select };
        }
    }
}
