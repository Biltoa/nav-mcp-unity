using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Umcp.Agent
{
    internal static class GameObjectTools
    {
        [UnityTool(Id = "gameobject.create", Summary = "Create a GameObject in the active scene.",
            Mutating = true, Retry = RetryClass.Write, Undo = "Create GameObject")]
        [Example("{ \"name\": \"Enemy\", \"primitive\": \"Capsule\", \"parent\": \"Spawns\" }")]
        [Example("{ \"name\": \"Empty\" }")]
        public static object Create(
            [Doc("Name of the new GameObject")] string name,
            [Doc("Primitive type: Cube, Sphere, Capsule, Cylinder, Plane, Quad. Omit for an empty GameObject.")] string primitive = null,
            [Doc("Parent path or #instanceId")] string parent = null,
            [Doc("Local position as [x, y, z]")] float[] position = null,
            [Doc("Local euler angles as [x, y, z]")] float[] rotation = null,
            [Doc("Local scale as [x, y, z]")] float[] scale = null)
        {
            GameObject go;
            if (string.IsNullOrEmpty(primitive))
            {
                go = new GameObject(name);
            }
            else
            {
                PrimitiveType pt;
                if (!System.Enum.TryParse(primitive, true, out pt))
                    throw new UmcpToolException("E_ENUM_INVALID", "Unknown primitive '" + primitive + "'.",
                        "primitive", primitive,
                        Suggest.Closest(primitive, System.Enum.GetNames(typeof(PrimitiveType)), 3),
                        "One of: " + string.Join(", ", System.Enum.GetNames(typeof(PrimitiveType))));
                go = UnityEngine.GameObject.CreatePrimitive(pt);
                go.name = name;
            }

            Undo.RegisterCreatedObjectUndo(go, "Create GameObject");

            if (!string.IsNullOrEmpty(parent))
                Undo.SetTransformParent(go.transform, Resolve.GameObject(parent, "parent").transform, "Create GameObject");

            if (position != null) go.transform.localPosition = Vec.V3(position, "position");
            if (rotation != null) go.transform.localEulerAngles = Vec.V3(rotation, "rotation");
            if (scale != null) go.transform.localScale = Vec.V3(scale, "scale");

            return Res.Ref(go);
        }

        [UnityTool(Id = "gameobject.delete", Summary = "Delete a GameObject and its children.",
            Mutating = true, Retry = RetryClass.Write, Undo = "Delete GameObject")]
        [Example("{ \"target\": \"Enemy\" }")]
        public static object Delete([Doc("Path, name or #instanceId")] string target)
        {
            var go = Resolve.GameObject(target);
            var path = Resolve.Path(go.transform);
            Undo.DestroyObjectImmediate(go);
            return new { deleted = path };
        }

        [UnityTool(Id = "gameobject.rename", Summary = "Rename a GameObject.",
            Mutating = true, Retry = RetryClass.Write, Undo = "Rename GameObject")]
        [Example("{ \"target\": \"Cube\", \"name\": \"Crate\" }")]
        public static object Rename(
            [Doc("Path, name or #instanceId")] string target,
            [Doc("New name")] string name)
        {
            var go = Resolve.GameObject(target);
            Undo.RecordObject(go, "Rename GameObject");
            go.name = name;
            return Res.Ref(go);
        }

        [UnityTool(Id = "gameobject.duplicate", Summary = "Duplicate a GameObject in place.",
            Mutating = true, Retry = RetryClass.Write, Undo = "Duplicate GameObject")]
        [Example("{ \"target\": \"Crate\", \"name\": \"Crate (2)\" }")]
        public static object Duplicate(
            [Doc("Path, name or #instanceId")] string target,
            [Doc("Name for the copy. Defaults to Unity's naming.")] string name = null)
        {
            var src = Resolve.GameObject(target);
            var copy = Object.Instantiate(src, src.transform.parent);
            copy.name = string.IsNullOrEmpty(name) ? src.name + " (Clone)" : name;
            Undo.RegisterCreatedObjectUndo(copy, "Duplicate GameObject");
            return Res.Ref(copy);
        }

        [UnityTool(Id = "gameobject.setParent", Summary = "Reparent a GameObject.",
            Mutating = true, Retry = RetryClass.Write, Undo = "Set Parent")]
        [Example("{ \"target\": \"Crate\", \"parent\": \"Level/Props\" }")]
        [Example("{ \"target\": \"Crate\", \"parent\": null }")]
        public static object SetParent(
            [Doc("Path, name or #instanceId")] string target,
            [Doc("New parent path, or null to move to the scene root")] string parent = null,
            [Doc("Keep the object's world position")] bool worldPositionStays = true)
        {
            var go = Resolve.GameObject(target);
            var parentT = string.IsNullOrEmpty(parent) ? null : Resolve.GameObject(parent, "parent").transform;
            Undo.SetTransformParent(go.transform, parentT, "Set Parent");
            if (!worldPositionStays) go.transform.localPosition = Vector3.zero;
            return Res.Ref(go);
        }

        [UnityTool(Id = "gameobject.setActive", Summary = "Activate or deactivate a GameObject.",
            Mutating = true, Retry = RetryClass.Write, Undo = "Set Active")]
        [Example("{ \"target\": \"Enemy\", \"active\": false }")]
        public static object SetActive(
            [Doc("Path, name or #instanceId")] string target,
            [Doc("Desired active state")] bool active)
        {
            var go = Resolve.GameObject(target);
            Undo.RecordObject(go, "Set Active");
            go.SetActive(active);
            return new { id = go.GetInstanceID(), active };
        }

        [UnityTool(Id = "gameobject.setTag", Summary = "Set a GameObject's tag.",
            Mutating = true, Retry = RetryClass.Write, Undo = "Set Tag")]
        [Example("{ \"target\": \"Player\", \"tag\": \"Player\" }")]
        public static object SetTag(
            [Doc("Path, name or #instanceId")] string target,
            [Doc("Tag name. Must already exist in the Tag Manager.")] string tag)
        {
            var go = Resolve.GameObject(target);
            var tags = UnityEditorInternal.InternalEditorUtility.tags;
            if (!tags.Contains(tag))
                throw new UmcpToolException("E_TAG_NOT_FOUND", "Tag '" + tag + "' is not defined.",
                    "tag", tag, Suggest.Closest(tag, tags, 3),
                    "Define it in Project Settings > Tags and Layers first.");
            Undo.RecordObject(go, "Set Tag");
            go.tag = tag;
            return new { id = go.GetInstanceID(), tag };
        }

        [UnityTool(Id = "gameobject.setLayer", Summary = "Set a GameObject's layer, optionally recursively.",
            Mutating = true, Retry = RetryClass.Write, Undo = "Set Layer")]
        [Example("{ \"target\": \"Level\", \"layer\": \"Default\", \"recursive\": true }")]
        public static object SetLayer(
            [Doc("Path, name or #instanceId")] string target,
            [Doc("Layer name or index")] string layer,
            [Doc("Apply to all descendants too")] bool recursive = false)
        {
            var go = Resolve.GameObject(target);
            int index;
            if (!int.TryParse(layer, out index))
            {
                index = LayerMask.NameToLayer(layer);
                if (index < 0)
                    throw new UmcpToolException("E_LAYER_NOT_FOUND", "Layer '" + layer + "' is not defined.",
                        "layer", layer,
                        Suggest.Closest(layer, UnityEditorInternal.InternalEditorUtility.layers, 3),
                        "Define it in Project Settings > Tags and Layers first.");
            }
            var targets = recursive
                ? go.GetComponentsInChildren<Transform>(true).Select(t => t.gameObject).ToArray()
                : new[] { go };
            Undo.RecordObjects(targets, "Set Layer");
            foreach (var t in targets) t.layer = index;
            return new { id = go.GetInstanceID(), layer = index, applied = targets.Length };
        }

        [UnityTool(Id = "gameobject.get", Summary = "Read one GameObject: identity, transform and component names.",
            Retry = RetryClass.Read)]
        [Example("{ \"target\": \"Player\" }")]
        public static object Get(
            [Doc("Path, name or #instanceId")] string target,
            [Doc("Include the names of direct children")] bool children = false)
        {
            var go = Resolve.GameObject(target);
            var t = go.transform;
            return new
            {
                id = go.GetInstanceID(),
                name = go.name,
                path = Resolve.Path(t),
                active = go.activeSelf,
                activeInHierarchy = go.activeInHierarchy,
                tag = go.tag,
                layer = LayerMask.LayerToName(go.layer),
                isPrefabInstance = PrefabUtility.IsPartOfPrefabInstance(go),
                position = Vec.Arr(t.localPosition),
                rotation = Vec.Arr(t.localEulerAngles),
                scale = Vec.Arr(t.localScale),
                components = go.GetComponents<Component>().Select(c => c == null ? "<missing>" : c.GetType().Name).ToArray(),
                childCount = t.childCount,
                children = children ? Enumerable.Range(0, t.childCount).Select(i => t.GetChild(i).name).ToArray() : null
            };
        }

        [UnityTool(Id = "gameobject.find", Summary = "Find GameObjects by name, tag, layer or component. Bounded output.",
            Retry = RetryClass.Read)]
        [Example("{ \"name\": \"Enemy\", \"limit\": 20 }")]
        [Example("{ \"component\": \"Rigidbody\", \"includeInactive\": false }")]
        public static object Find(
            [Doc("Exact or substring name match")] string name = null,
            [Doc("Tag filter")] string tag = null,
            [Doc("Layer name filter")] string layer = null,
            [Doc("Component type name filter, e.g. Rigidbody")] string component = null,
            [Doc("Substring rather than exact name match")] bool contains = true,
            [Doc("Include inactive GameObjects")] bool includeInactive = true,
            [Doc("Maximum results (default 100)")] int limit = 100,
            [Doc("Skip this many results")] int offset = 0)
        {
            System.Type compType = string.IsNullOrEmpty(component) ? null : Resolve.ComponentType(component, "component");
            int layerIndex = -1;
            if (!string.IsNullOrEmpty(layer))
            {
                layerIndex = LayerMask.NameToLayer(layer);
                if (layerIndex < 0)
                    throw new UmcpToolException("E_LAYER_NOT_FOUND", "Layer '" + layer + "' is not defined.",
                        "layer", layer, Suggest.Closest(layer, UnityEditorInternal.InternalEditorUtility.layers, 3));
            }

            var all = Resolve.AllGameObjects().Where(go =>
            {
                if (!includeInactive && !go.activeInHierarchy) return false;
                if (!string.IsNullOrEmpty(name))
                {
                    if (contains) { if (go.name.IndexOf(name, System.StringComparison.OrdinalIgnoreCase) < 0) return false; }
                    else if (go.name != name) return false;
                }
                if (!string.IsNullOrEmpty(tag) && !go.CompareTag(tag)) return false;
                if (layerIndex >= 0 && go.layer != layerIndex) return false;
                if (compType != null && go.GetComponent(compType) == null) return false;
                return true;
            }).ToList();

            var page = all.Skip(offset).Take(Bounds.Limit(limit))
                .Select(go => new { id = go.GetInstanceID(), name = go.name, path = Resolve.Path(go.transform), active = go.activeInHierarchy })
                .ToArray();

            return Res.Page(page, all.Count, offset, page.Length);
        }
    }
}
