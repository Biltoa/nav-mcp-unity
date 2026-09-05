using UnityEditor;
using UnityEngine;

namespace Umcp.Agent
{
    internal static class TransformTools
    {
        [UnityTool(Id = "transform.set", Summary = "Set position, rotation and/or scale on a GameObject.",
            Mutating = true, Retry = RetryClass.Write, Undo = "Set Transform")]
        [Example("{ \"target\": \"Crate\", \"position\": [0, 1, 0], \"space\": \"local\" }")]
        [Example("{ \"target\": \"Crate\", \"scale\": [2, 2, 2] }")]
        public static object Set(
            [Doc("Path, name or #instanceId")] string target,
            [Doc("Position as [x, y, z]")] float[] position = null,
            [Doc("Euler angles as [x, y, z]")] float[] rotation = null,
            [Doc("Scale as [x, y, z] (always local)")] float[] scale = null,
            [Doc("\"local\" or \"world\" for position/rotation")] string space = "local")
        {
            var t = Resolve.GameObject(target).transform;
            bool world = space == "world";
            if (!world && space != "local")
                throw new UmcpToolException("E_ENUM_INVALID", "space must be \"local\" or \"world\".", "space", space);

            Undo.RecordObject(t, "Set Transform");
            if (position != null) { var v = Vec.V3(position, "position"); if (world) t.position = v; else t.localPosition = v; }
            if (rotation != null) { var v = Vec.V3(rotation, "rotation"); if (world) t.eulerAngles = v; else t.localEulerAngles = v; }
            if (scale != null) t.localScale = Vec.V3(scale, "scale");
            return Snapshot(t);
        }

        [UnityTool(Id = "transform.get", Summary = "Read a GameObject's transform in local and world space.",
            Retry = RetryClass.Read)]
        [Example("{ \"target\": \"Player\" }")]
        public static object Get([Doc("Path, name or #instanceId")] string target)
        {
            return Snapshot(Resolve.GameObject(target).transform);
        }

        [UnityTool(Id = "transform.translate", Summary = "Move a GameObject by a delta.",
            Mutating = true, Retry = RetryClass.None, Undo = "Translate")]
        [Example("{ \"target\": \"Crate\", \"delta\": [0, 0.5, 0] }")]
        public static object Translate(
            [Doc("Path, name or #instanceId")] string target,
            [Doc("Delta as [x, y, z]")] float[] delta,
            [Doc("\"local\" or \"world\"")] string space = "local")
        {
            var t = Resolve.GameObject(target).transform;
            Undo.RecordObject(t, "Translate");
            t.Translate(Vec.V3(delta, "delta"), space == "world" ? Space.World : Space.Self);
            return Snapshot(t);
        }

        [UnityTool(Id = "transform.rotate", Summary = "Rotate a GameObject by euler angles.",
            Mutating = true, Retry = RetryClass.None, Undo = "Rotate")]
        [Example("{ \"target\": \"Crate\", \"euler\": [0, 90, 0] }")]
        public static object Rotate(
            [Doc("Path, name or #instanceId")] string target,
            [Doc("Euler delta as [x, y, z]")] float[] euler,
            [Doc("\"local\" or \"world\"")] string space = "local")
        {
            var t = Resolve.GameObject(target).transform;
            Undo.RecordObject(t, "Rotate");
            t.Rotate(Vec.V3(euler, "euler"), space == "world" ? Space.World : Space.Self);
            return Snapshot(t);
        }

        [UnityTool(Id = "transform.lookAt", Summary = "Aim a GameObject at another GameObject or a world point.",
            Mutating = true, Retry = RetryClass.Write, Undo = "Look At")]
        [Example("{ \"target\": \"Camera\", \"at\": \"Player\" }")]
        public static object LookAt(
            [Doc("Path, name or #instanceId of the object to rotate")] string target,
            [Doc("Path, name or #instanceId to look at. Mutually exclusive with point.")] string at = null,
            [Doc("World point as [x, y, z]. Mutually exclusive with at.")] float[] point = null)
        {
            var t = Resolve.GameObject(target).transform;
            Vector3 p;
            if (!string.IsNullOrEmpty(at)) p = Resolve.GameObject(at, "at").transform.position;
            else if (point != null) p = Vec.V3(point, "point");
            else throw new UmcpToolException("E_ARG_REQUIRED", "Pass either 'at' or 'point'.", "at");

            Undo.RecordObject(t, "Look At");
            t.LookAt(p);
            return Snapshot(t);
        }

        static object Snapshot(Transform t)
        {
            return new
            {
                id = t.gameObject.GetInstanceID(),
                path = Resolve.Path(t),
                local = new { position = Vec.Arr(t.localPosition), rotation = Vec.Arr(t.localEulerAngles), scale = Vec.Arr(t.localScale) },
                world = new { position = Vec.Arr(t.position), rotation = Vec.Arr(t.eulerAngles), lossyScale = Vec.Arr(t.lossyScale) }
            };
        }
    }
}
