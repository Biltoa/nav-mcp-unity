using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace Umcp.Agent
{
    /// <summary>
    /// Live evaluation of a <see cref="SceneSelector"/> against the open scenes, plus field
    /// projection. The grammar itself lives in SceneSelector.cs, which the daemon compiles too —
    /// the daemon evaluates the same selectors against its mirror, and a second parser would let
    /// the two drift, which would make <c>source: "mirror"</c> a lie.
    ///
    /// This is the single largest token saver on the read path: instead of dumping a scene —
    /// 138,205 bytes, about 34,500 tokens, roughly 17% of a 200k context window for one call in the
    /// implementation being replaced — the caller says which objects it wants and which fields.
    /// </summary>
    internal static class SceneQuery
    {
        internal static List<SceneSelector.Step> Parse(string selector)
        {
            try { return SceneSelector.Parse(selector); }
            catch (SceneSelector.ParseException e)
            {
                throw new UmcpToolException(e.Code, e.Message, "select", e.Value, e.DidYouMean, e.Hint);
            }
        }

        // ------------------------------------------------------------------ evaluation

        static Func<GameObject, bool> Compile(SceneSelector.Predicate p)
        {
            switch (p.Key)
            {
                case "active": return go => go.activeInHierarchy;
                case "inactive": return go => !go.activeInHierarchy;
                case "root": return go => go.transform.parent == null;
                case "leaf": return go => go.transform.childCount == 0;
                case "has": { var t = Resolve.ComponentType(p.Arg, "select"); return go => go.GetComponent(t) != null; }
                case "missing": { var t = Resolve.ComponentType(p.Arg, "select"); return go => go.GetComponent(t) == null; }
                case "tag": { var v = p.Arg; return go => go.CompareTag(v); }
                case "layer":
                    {
                        var idx = LayerMask.NameToLayer(p.Arg);
                        if (idx < 0)
                            throw new UmcpToolException("E_LAYER_NOT_FOUND", "Layer '" + p.Arg + "' is not defined.",
                                "select", p.Arg,
                                Suggest.Closest(p.Arg, UnityEditorInternal.InternalEditorUtility.layers, 3));
                        return go => go.layer == idx;
                    }
                case "name*": { var v = p.Arg; return go => go.name.IndexOf(v, StringComparison.OrdinalIgnoreCase) >= 0; }
                case "name^": { var v = p.Arg; return go => go.name.StartsWith(v, StringComparison.OrdinalIgnoreCase); }
                case "name$": { var v = p.Arg; return go => go.name.EndsWith(v, StringComparison.OrdinalIgnoreCase); }
                default:
                    throw new UmcpToolException(SceneSelector.ErrPredicate,
                        "Unknown predicate '" + p.Key + "'.", "select", p.Key);
            }
        }

        internal static IEnumerable<GameObject> Evaluate(List<SceneSelector.Step> steps, int maxDepth)
        {
            IEnumerable<GameObject> current = Resolve.AllRoots();

            for (int s = 0; s < steps.Count; s++)
            {
                var step = steps[s];
                var predicates = step.Predicates.Select(Compile).ToArray();
                var next = new List<GameObject>();
                var seen = new HashSet<int>();

                foreach (var go in current)
                {
                    if (s == 0)
                    {
                        // The first step matches the roots themselves, and — when it was reached
                        // by "//" — every descendant of a root as well.
                        Collect(go, step, predicates, step.Descendant, maxDepth, 0, next, seen);
                    }
                    else
                    {
                        var t = go.transform;
                        for (int i = 0; i < t.childCount; i++)
                            Collect(t.GetChild(i).gameObject, step, predicates, step.Descendant, maxDepth, 0, next, seen);
                    }
                }
                current = next;
                if (next.Count == 0) break;
            }
            return current;
        }

        static void Collect(GameObject go, SceneSelector.Step step, Func<GameObject, bool>[] predicates,
                            bool descend, int maxDepth, int depth, List<GameObject> into, HashSet<int> seen)
        {
            if (maxDepth >= 0 && depth > maxDepth) return;

            if (Matches(go, step, predicates) && seen.Add(go.GetInstanceID())) into.Add(go);

            if (!descend) return;
            var t = go.transform;
            for (int i = 0; i < t.childCount; i++)
                Collect(t.GetChild(i).gameObject, step, predicates, true, maxDepth, depth + 1, into, seen);
        }

        static bool Matches(GameObject go, SceneSelector.Step step, Func<GameObject, bool>[] predicates)
        {
            if (step.Name != "*" && go.name != step.Name) return false;
            for (int i = 0; i < predicates.Length; i++) if (!predicates[i](go)) return false;
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
                var dot = field.IndexOf('.');
                var value = dot > 0
                    ? ComponentField(go, field.Substring(0, dot), field.Substring(dot + 1))
                    : BuiltinField(go, field);
                if (value != null) result[field] = value;
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

        internal static readonly string[] KnownFields =
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
            var sp = so.FindProperty(propName)
                     ?? so.FindProperty("m_" + char.ToUpperInvariant(propName[0]) + propName.Substring(1));
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
            return v.ToString();
        }
    }

    internal static class SceneQueryTools
    {
        [UnityTool(Id = "scene.query", Skill = "scene",
            Summary = "Select GameObjects from the scene hierarchy with a path selector and return only the fields you ask for.",
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
            var steps = SceneQuery.Parse(select);
            var matches = SceneQuery.Evaluate(steps, maxDepth).ToList();

            if (countOnly) return new { count = matches.Count, select };

            var projection = fields == null || fields.Length == 0 ? new[] { "name", "path" } : fields;
            var page = matches.Skip(offset).Take(Bounds.Limit(limit))
                .Select(go => SceneQuery.Project(go, projection))
                .ToArray();

            bool truncated = offset + page.Length < matches.Count;
            return new
            {
                items = page,
                _total = matches.Count,
                _returned = page.Length,
                _offset = offset,
                _truncated = truncated,
                _select = select,
                _hint = truncated ? "re-query with offset=" + (offset + page.Length) + ", or narrow the selector" : null
            };
        }

        [UnityTool(Id = "scene.count", Skill = "scene",
            Summary = "Count GameObjects in the hierarchy matching a selector. The cheapest possible read.",
            Retry = RetryClass.Read)]
        [Example("{ \"select\": \"//*[has:Renderer]\" }")]
        public static object Count([Doc("Selector")] string select = "//*")
        {
            var steps = SceneQuery.Parse(select);
            return new { count = SceneQuery.Evaluate(steps, -1).Count(), select };
        }
    }
}
