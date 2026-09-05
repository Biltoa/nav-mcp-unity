using UnityEngine;

namespace Umcp.Agent
{
    /// <summary>Vector marshalling. Arrays in, arrays out — never "1,2,3" strings.</summary>
    internal static class Vec
    {
        public static Vector3 V3(float[] a, string param)
        {
            if (a == null || a.Length != 3)
                throw new UmcpToolException("E_ARG_SHAPE",
                    "Expected 3 numbers for '" + param + "'.", param,
                    a == null ? "null" : "[" + a.Length + " values]", null, "For example: [0, 1.5, 0]");
            return new Vector3(a[0], a[1], a[2]);
        }

        public static float[] Arr(Vector3 v) { return new[] { v.x, v.y, v.z }; }
        public static float[] Arr(Vector2 v) { return new[] { v.x, v.y }; }
        public static float[] Arr(Color c) { return new[] { c.r, c.g, c.b, c.a }; }
        public static float[] Arr(Quaternion q) { return new[] { q.x, q.y, q.z, q.w }; }
    }

    /// <summary>Result shapes. Small by construction.</summary>
    internal static class Res
    {
        public static object Ref(GameObject go)
        {
            return new { id = go.GetInstanceID(), name = go.name, path = Resolve.Path(go.transform) };
        }

        /// <summary>
        /// A page of results that truncates honestly. The tool being replaced returned a
        /// 138 KB scene dump with no indication that anything was elided; this always says.
        /// </summary>
        public static object Page(object items, int total, int offset, int returned)
        {
            bool truncated = offset + returned < total;
            return new
            {
                items,
                _total = total,
                _returned = returned,
                _offset = offset,
                _truncated = truncated,
                _hint = truncated ? "re-query with offset=" + (offset + returned) + ", or narrow the filter" : null
            };
        }
    }

    internal static class Bounds
    {
        public const int MaxLimit = 500;
        public static int Limit(int requested)
        {
            if (requested <= 0) return 100;
            return requested > MaxLimit ? MaxLimit : requested;
        }
    }
}
