using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace Umcp.Agent
{
    internal static class ComponentTools
    {
        [UnityTool(Id = "component.add", Summary = "Add a component to a GameObject.",
            Mutating = true, Retry = RetryClass.Write, Undo = "Add Component")]
        [Example("{ \"target\": \"Enemy\", \"type\": \"Rigidbody\" }")]
        [Example("{ \"target\": \"Enemy\", \"type\": \"Rigidbody\", \"props\": { \"mass\": 80 } }")]
        public static object Add(
            [Doc("Path, name or #instanceId")] string target,
            [Doc("Component type name, e.g. Rigidbody")] string type,
            [Doc("Optional property values to apply immediately")] JObject props = null)
        {
            var go = Resolve.GameObject(target);
            var t = Resolve.ComponentType(type);
            var existing = go.GetComponent(t);
            if (existing != null && Attribute.IsDefined(t, typeof(DisallowMultipleComponent)))
                throw new UmcpToolException("E_COMPONENT_EXISTS",
                    go.name + " already has a " + t.Name + " and it disallows multiples.",
                    "type", type, null, "Use component.set to change the existing one.");

            var c = Undo.AddComponent(go, t);
            var warnings = props == null ? new List<string>() : PropWriter.Apply(c, props);
            return new { id = c.GetInstanceID(), go = go.GetInstanceID(), type = t.Name, warnings = warnings.Count == 0 ? null : warnings.ToArray() };
        }

        [UnityTool(Id = "component.remove", Summary = "Remove a component from a GameObject.",
            Mutating = true, Retry = RetryClass.Write, Undo = "Remove Component")]
        [Example("{ \"target\": \"Enemy\", \"type\": \"Rigidbody\" }")]
        public static object Remove(
            [Doc("Path, name or #instanceId")] string target,
            [Doc("Component type name")] string type,
            [Doc("Remove every matching component rather than the first")] bool all = false)
        {
            var go = Resolve.GameObject(target);
            var t = Resolve.ComponentType(type);
            var comps = go.GetComponents(t).Where(c => c != null).ToArray();
            if (comps.Length == 0)
                throw new UmcpToolException("E_COMPONENT_NOT_FOUND",
                    go.name + " has no " + t.Name + ".", "type", type,
                    Suggest.Closest(type, go.GetComponents<Component>().Where(c => c != null).Select(c => c.GetType().Name).ToArray(), 3));

            int removed = 0;
            foreach (var c in all ? comps : comps.Take(1))
            {
                Undo.DestroyObjectImmediate(c);
                removed++;
            }
            return new { go = go.GetInstanceID(), type = t.Name, removed };
        }

        [UnityTool(Id = "component.get", Summary = "Read a component's serialized properties.",
            Retry = RetryClass.Read)]
        [Example("{ \"target\": \"Player\", \"type\": \"Rigidbody\" }")]
        public static object Get(
            [Doc("Path, name or #instanceId")] string target,
            [Doc("Component type name")] string type,
            [Doc("Only these property names")] string[] fields = null,
            [Doc("Include properties still at their default value")] bool includeDefaults = false)
        {
            var go = Resolve.GameObject(target);
            var t = Resolve.ComponentType(type);
            var c = go.GetComponent(t);
            if (c == null)
                throw new UmcpToolException("E_COMPONENT_NOT_FOUND", go.name + " has no " + t.Name + ".", "type", type,
                    Suggest.Closest(type, go.GetComponents<Component>().Where(x => x != null).Select(x => x.GetType().Name).ToArray(), 3));

            return new
            {
                id = c.GetInstanceID(),
                go = go.GetInstanceID(),
                type = t.Name,
                props = PropWriter.Read(c, fields, includeDefaults)
            };
        }

        [UnityTool(Id = "component.set", Summary = "Set serialized properties on a component.",
            Mutating = true, Retry = RetryClass.Write, Undo = "Set Component Properties")]
        [Example("{ \"target\": \"Enemy\", \"type\": \"Rigidbody\", \"props\": { \"mass\": 80, \"useGravity\": false } }")]
        public static object Set(
            [Doc("Path, name or #instanceId")] string target,
            [Doc("Component type name")] string type,
            [Doc("Property name to value. Vectors are arrays: { \"center\": [0,1,0] }")] JObject props)
        {
            var go = Resolve.GameObject(target);
            var t = Resolve.ComponentType(type);
            var c = go.GetComponent(t);
            if (c == null)
                throw new UmcpToolException("E_COMPONENT_NOT_FOUND", go.name + " has no " + t.Name + ".", "type", type,
                    Suggest.Closest(type, go.GetComponents<Component>().Where(x => x != null).Select(x => x.GetType().Name).ToArray(), 3));

            var warnings = PropWriter.Apply(c, props);
            return new { id = c.GetInstanceID(), type = t.Name, set = props.Properties().Count() - warnings.Count, warnings = warnings.Count == 0 ? null : warnings.ToArray() };
        }

        [UnityTool(Id = "component.list", Summary = "List the components on a GameObject.",
            Retry = RetryClass.Read)]
        [Example("{ \"target\": \"Player\" }")]
        public static object List([Doc("Path, name or #instanceId")] string target)
        {
            var go = Resolve.GameObject(target);
            var comps = go.GetComponents<Component>();
            return new
            {
                go = go.GetInstanceID(),
                components = comps.Select((c, i) => c == null
                    ? (object)new { index = i, type = "<missing script>", enabled = (bool?)null, missing = true }
                    : new { index = i, type = c.GetType().Name, enabled = EnabledOf(c), missing = false }).ToArray()
            };
        }

        static bool? EnabledOf(Component c)
        {
            var b = c as Behaviour; if (b != null) return b.enabled;
            var r = c as Renderer; if (r != null) return r.enabled;
            var col = c as Collider; if (col != null) return col.enabled;
            return null;
        }
    }

    /// <summary>
    /// Property read/write over <see cref="SerializedObject"/> so that every write participates in
    /// undo and dirties the object correctly, falling back to reflection for non-serialized
    /// accessors. Values are real JSON — numbers are numbers, vectors are arrays.
    /// </summary>
    internal static class PropWriter
    {
        public static List<string> Apply(Component c, JObject props)
        {
            var warnings = new List<string>();
            var so = new SerializedObject(c);
            so.Update();
            var reflectLater = new List<KeyValuePair<string, JToken>>();

            foreach (var kv in props)
            {
                var sp = so.FindProperty(kv.Key) ?? so.FindProperty("m_" + Cap(kv.Key));
                if (sp == null) { reflectLater.Add(kv); continue; }
                try { WriteSerialized(sp, kv.Value); }
                catch (UmcpToolException) { throw; }
                catch (Exception e) { warnings.Add(kv.Key + ": " + e.Message); }
            }
            so.ApplyModifiedProperties();

            if (reflectLater.Count > 0)
            {
                Undo.RecordObject(c, "Set Component Properties");
                foreach (var kv in reflectLater)
                {
                    if (!WriteReflected(c, kv.Key, kv.Value, warnings))
                        warnings.Add("no property '" + kv.Key + "' on " + c.GetType().Name);
                }
                EditorUtility.SetDirty(c);
            }
            return warnings;
        }

        static string Cap(string s) { return string.IsNullOrEmpty(s) ? s : char.ToUpperInvariant(s[0]) + s.Substring(1); }

        static void WriteSerialized(SerializedProperty sp, JToken v)
        {
            switch (sp.propertyType)
            {
                case SerializedPropertyType.Integer: sp.intValue = v.ToObject<int>(); break;
                case SerializedPropertyType.Boolean: sp.boolValue = v.ToObject<bool>(); break;
                case SerializedPropertyType.Float: sp.floatValue = v.ToObject<float>(); break;
                case SerializedPropertyType.String: sp.stringValue = v.ToObject<string>(); break;
                case SerializedPropertyType.Enum: sp.enumValueIndex = EnumIndex(sp, v); break;
                case SerializedPropertyType.Vector2: sp.vector2Value = (Vector2)ToV(v, 2); break;
                case SerializedPropertyType.Vector3: sp.vector3Value = ToV(v, 3); break;
                case SerializedPropertyType.Vector4: { var a = Nums(v, 4); sp.vector4Value = new Vector4(a[0], a[1], a[2], a[3]); break; }
                case SerializedPropertyType.Quaternion: { var a = Nums(v, 4); sp.quaternionValue = new Quaternion(a[0], a[1], a[2], a[3]); break; }
                case SerializedPropertyType.Color: { var a = Nums(v, v.Count() >= 4 ? 4 : 3); sp.colorValue = new Color(a[0], a[1], a[2], a.Length > 3 ? a[3] : 1f); break; }
                case SerializedPropertyType.LayerMask: sp.intValue = v.Type == JTokenType.String ? 1 << LayerMask.NameToLayer(v.ToObject<string>()) : v.ToObject<int>(); break;
                case SerializedPropertyType.ObjectReference: sp.objectReferenceValue = ToObjectRef(v); break;
                case SerializedPropertyType.Bounds: { var a = Nums(v, 6); sp.boundsValue = new UnityEngine.Bounds(new Vector3(a[0], a[1], a[2]), new Vector3(a[3], a[4], a[5])); break; }
                case SerializedPropertyType.Rect: { var a = Nums(v, 4); sp.rectValue = new Rect(a[0], a[1], a[2], a[3]); break; }
                default:
                    throw new UmcpToolException("E_PROP_UNSUPPORTED",
                        sp.propertyType + " properties are not settable through component.set.",
                        sp.name, null, null, "Use unity.script for this property (Phase 2).");
            }
        }

        static int EnumIndex(SerializedProperty sp, JToken v)
        {
            if (v.Type == JTokenType.Integer) return v.ToObject<int>();
            var name = v.ToObject<string>();
            var idx = Array.FindIndex(sp.enumNames, n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase));
            if (idx < 0)
                throw new UmcpToolException("E_ENUM_INVALID", "'" + name + "' is not a valid value for " + sp.name + ".",
                    sp.name, name, Suggest.Closest(name, sp.enumNames, 3), "One of: " + string.Join(", ", sp.enumNames));
            return idx;
        }

        static Vector3 ToV(JToken v, int n) { var a = Nums(v, n); return new Vector3(a[0], a[1], n > 2 ? a[2] : 0f); }

        static float[] Nums(JToken v, int n)
        {
            var arr = v as JArray;
            if (arr == null || arr.Count < n)
                throw new UmcpToolException("E_ARG_SHAPE", "Expected an array of " + n + " numbers.", null, v.ToString());
            return arr.Take(n).Select(x => x.ToObject<float>()).ToArray();
        }

        static UnityEngine.Object ToObjectRef(JToken v)
        {
            if (v.Type == JTokenType.Null) return null;
            var s = v.ToObject<string>();
            if (string.IsNullOrEmpty(s)) return null;
            if (s.StartsWith("#"))
            {
                int id;
                if (int.TryParse(s.Substring(1), out id)) return EditorUtility.InstanceIDToObject(id);
            }
            if (s.StartsWith("Assets/") || s.StartsWith("Packages/"))
            {
                var asset = AssetDatabase.LoadMainAssetAtPath(s);
                if (asset == null)
                    throw new UmcpToolException("E_ASSET_NOT_FOUND", "No asset at '" + s + "'.", null, s);
                return asset;
            }
            var go = Resolve.TryGameObject(s);
            if (go != null) return go;
            throw new UmcpToolException("E_TARGET_NOT_FOUND", "Cannot resolve object reference '" + s + "'.", null, s,
                null, "Use an asset path, a scene path, or \"#<instanceId>\".");
        }

        static bool WriteReflected(Component c, string name, JToken v, List<string> warnings)
        {
            var type = c.GetType();
            var pi = type.GetProperty(name);
            if (pi != null && pi.CanWrite)
            {
                try { pi.SetValue(c, Convert(v, pi.PropertyType), null); return true; }
                catch (Exception e) { warnings.Add(name + ": " + e.Message); return true; }
            }
            var fi = type.GetField(name);
            if (fi != null)
            {
                try { fi.SetValue(c, Convert(v, fi.FieldType)); return true; }
                catch (Exception e) { warnings.Add(name + ": " + e.Message); return true; }
            }
            return false;
        }

        static object Convert(JToken v, Type t)
        {
            if (t == typeof(Vector3)) return ToV(v, 3);
            if (t == typeof(Vector2)) return (Vector2)ToV(v, 2);
            if (t == typeof(Color)) { var a = Nums(v, 3); return new Color(a[0], a[1], a[2], v.Count() > 3 ? v[3].ToObject<float>() : 1f); }
            if (typeof(UnityEngine.Object).IsAssignableFrom(t)) return ToObjectRef(v);
            if (t.IsEnum) return v.Type == JTokenType.Integer ? Enum.ToObject(t, v.ToObject<int>()) : Enum.Parse(t, v.ToObject<string>(), true);
            return v.ToObject(t);
        }

        public static Dictionary<string, object> Read(Component c, string[] fields, bool includeDefaults)
        {
            var result = new Dictionary<string, object>();
            var so = new SerializedObject(c);
            var it = so.GetIterator();
            var wanted = fields == null || fields.Length == 0 ? null : new HashSet<string>(fields);

            bool enterChildren = true;
            while (it.NextVisible(enterChildren))
            {
                enterChildren = false;
                if (it.name == "m_Script") continue;
                var key = Friendly(it.name);
                if (wanted != null && !wanted.Contains(key) && !wanted.Contains(it.name)) continue;
                object value;
                try { value = ReadOne(it); } catch { continue; }
                if (!includeDefaults && IsDefaultish(value)) continue;
                result[key] = value;
            }
            return result;
        }

        static string Friendly(string n)
        {
            if (n.StartsWith("m_") && n.Length > 2) return char.ToLowerInvariant(n[2]) + n.Substring(3);
            return n;
        }

        static bool IsDefaultish(object v)
        {
            if (v == null) return true;
            if (v is bool) return !(bool)v;
            if (v is int) return (int)v == 0;
            if (v is float) return Mathf.Approximately((float)v, 0f);
            if (v is string) return ((string)v).Length == 0;
            return false;
        }

        static object ReadOne(SerializedProperty sp)
        {
            switch (sp.propertyType)
            {
                case SerializedPropertyType.Integer: return sp.intValue;
                case SerializedPropertyType.Boolean: return sp.boolValue;
                case SerializedPropertyType.Float: return sp.floatValue;
                case SerializedPropertyType.String: return sp.stringValue;
                case SerializedPropertyType.Enum: return sp.enumValueIndex >= 0 && sp.enumValueIndex < sp.enumNames.Length ? sp.enumNames[sp.enumValueIndex] : (object)sp.enumValueIndex;
                case SerializedPropertyType.Vector2: return Vec.Arr(sp.vector2Value);
                case SerializedPropertyType.Vector3: return Vec.Arr(sp.vector3Value);
                case SerializedPropertyType.Vector4: { var v = sp.vector4Value; return new[] { v.x, v.y, v.z, v.w }; }
                case SerializedPropertyType.Quaternion: return Vec.Arr(sp.quaternionValue);
                case SerializedPropertyType.Color: return Vec.Arr(sp.colorValue);
                case SerializedPropertyType.LayerMask: return sp.intValue;
                case SerializedPropertyType.ObjectReference:
                    return sp.objectReferenceValue == null ? null : sp.objectReferenceValue.name;
                case SerializedPropertyType.ArraySize: return sp.intValue;
                default: return null;
            }
        }
    }
}
