using System.Linq;
using Newtonsoft.Json.Linq;

namespace Umcp.Agent
{
    /// <summary>
    /// Argument binding for the generated dispatch table. Arguments arrive as real JSON and are
    /// bound to the method's real signature — no <c>Dictionary&lt;string,string&gt;</c>, no
    /// "1,2,3" vectors, no per-handler re-parsing.
    ///
    /// The daemon has already validated against the generated schema before dispatch, so failures
    /// here are the belt to that braces.
    /// </summary>
    internal static class Bind
    {
        static JToken Get(JObject a, string name, bool required)
        {
            JToken t;
            if (a != null && a.TryGetValue(name, out t) && t.Type != JTokenType.Null) return t;
            if (required)
                throw new UmcpToolException("E_ARG_REQUIRED", "Missing required argument '" + name + "'.", name,
                    null, a == null ? null : Suggest.Closest(name, a.Properties().Select(p => p.Name).ToArray(), 3));
            return null;
        }

        static UmcpToolException Bad(string name, JToken t, string expected)
        {
            return new UmcpToolException("E_ARG_TYPE",
                "Argument '" + name + "' should be " + expected + ".", name, t.ToString(Newtonsoft.Json.Formatting.None),
                null, "Types are checked against the tool schema; see unity.catalog.");
        }

        public static string Str(JObject a, string name, bool required, string dflt = null)
        {
            var t = Get(a, name, required);
            if (t == null) return dflt;
            if (t.Type == JTokenType.Object || t.Type == JTokenType.Array) throw Bad(name, t, "a string");
            return t.ToObject<string>();
        }

        public static int Int(JObject a, string name, bool required, int dflt = 0)
        {
            var t = Get(a, name, required);
            if (t == null) return dflt;
            if (t.Type != JTokenType.Integer && t.Type != JTokenType.Float && t.Type != JTokenType.String)
                throw Bad(name, t, "an integer");
            return t.ToObject<int>();
        }

        public static float Flt(JObject a, string name, bool required, float dflt = 0f)
        {
            var t = Get(a, name, required);
            if (t == null) return dflt;
            if (t.Type != JTokenType.Integer && t.Type != JTokenType.Float && t.Type != JTokenType.String)
                throw Bad(name, t, "a number");
            return t.ToObject<float>();
        }

        public static bool Bool(JObject a, string name, bool required, bool dflt = false)
        {
            var t = Get(a, name, required);
            if (t == null) return dflt;
            if (t.Type != JTokenType.Boolean && t.Type != JTokenType.String) throw Bad(name, t, "true or false");
            return t.ToObject<bool>();
        }

        public static string[] StrArr(JObject a, string name, bool required)
        {
            var t = Get(a, name, required);
            if (t == null) return null;
            if (t.Type == JTokenType.String) return new[] { t.ToObject<string>() };
            var arr = t as JArray;
            if (arr == null) throw Bad(name, t, "an array of strings");
            return arr.Select(x => x.ToObject<string>()).ToArray();
        }

        public static float[] FltArr(JObject a, string name, bool required)
        {
            var t = Get(a, name, required);
            if (t == null) return null;
            var arr = t as JArray;
            if (arr == null) throw Bad(name, t, "an array of numbers, e.g. [0, 1, 0]");
            return arr.Select(x => x.ToObject<float>()).ToArray();
        }

        public static int[] IntArr(JObject a, string name, bool required)
        {
            var t = Get(a, name, required);
            if (t == null) return null;
            var arr = t as JArray;
            if (arr == null) throw Bad(name, t, "an array of integers");
            return arr.Select(x => x.ToObject<int>()).ToArray();
        }

        public static JObject Obj(JObject a, string name, bool required)
        {
            var t = Get(a, name, required);
            if (t == null) return null;
            var o = t as JObject;
            if (o == null) throw Bad(name, t, "an object");
            return o;
        }
    }

    /// <summary>Generated tool metadata, consumed by the agent at dispatch time.</summary>
    internal sealed class ToolMeta
    {
        public string Id;
        public string Skill;
        public string Summary;
        public bool Mutating;
        public string Retry;
        public string Cost;
        public string Undo;
        public string NoUndoReason;
    }
}
