using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace Umcp.Agent
{
    /// <summary>
    /// Prefab override management, as one tool with an <c>action</c> discriminator rather than
    /// four.
    ///
    /// This is the consolidation rule from the plan applied to a new family: same capability, a
    /// quarter of the schema budget, and one place to describe the shared arguments. The tool
    /// being replaced has no prefab override support at all, which is why an agent editing a
    /// prefab instance there silently produces per-instance overrides nobody asked for.
    /// </summary>
    internal static class PrefabOverrideTools
    {
        [UnityTool(Skill = "assets", Id = "prefab.overrides",
            Summary = "List, apply or revert a prefab instance's overrides. action: list | apply | revert.",
            Mutating = true, Retry = RetryClass.Write, Undo = "Prefab overrides",
            Cost = Cost.Moderate)]
        [Example("{ \"target\": \"Enemy\", \"action\": \"list\" }")]
        [Example("{ \"target\": \"Enemy\", \"action\": \"revert\", \"properties\": [\"m_Name\"] }")]
        public static object Overrides(
            [Doc("Prefab instance: path, name or #id")] string target,
            [Doc("list | apply | revert (default list)")] string action = "list",
            [Doc("Property paths to act on. Omit for all of them.")] string[] properties = null,
            [Doc("Maximum entries returned (default 100)")] int limit = 100)
        {
            var go = Resolve.GameObject(target, "target");
            var verb = (action ?? "list").ToLowerInvariant();
            int cap = Bounds.Limit(limit);

            if (!PrefabUtility.IsPartOfPrefabInstance(go))
                throw new UmcpToolException("E_NOT_A_PREFAB_INSTANCE",
                    "'" + go.name + "' is not part of a prefab instance.", "target", target, null,
                    "prefab.create makes an asset from a scene object; prefab.instantiate places an instance.");

            var root = PrefabUtility.GetOutermostPrefabInstanceRoot(go);
            var assetPath = PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(root);

            if (PrefabUtility.IsPrefabAssetMissing(root))
                throw new UmcpToolException("E_MISSING_PREFAB_ASSET",
                    "The prefab asset for '" + root.name + "' no longer exists.", "target", target, null,
                    "Restore the asset, or unpack the instance.");

            switch (verb)
            {
                case "list": return List(root, assetPath, cap);
                case "apply": return Apply(root, assetPath, properties, cap);
                case "revert": return Revert(root, assetPath, properties, cap);
                default:
                    throw new UmcpToolException("E_ARG_VALUE", "Unknown action '" + action + "'.",
                        "action", action, new[] { "list", "apply", "revert" }, null);
            }
        }

        static object List(GameObject root, string assetPath, int cap)
        {
            var modifications = PrefabUtility.GetObjectOverrides(root, true)
                .Select(o => new
                {
                    kind = "property",
                    target = o.instanceObject == null ? null : Describe(o.instanceObject),
                    properties = PropertyNames(o.instanceObject, cap)
                }).Take(cap).ToArray();

            var added = PrefabUtility.GetAddedComponents(root)
                .Select(a => new
                {
                    kind = "addedComponent",
                    component = a.instanceComponent == null ? null : a.instanceComponent.GetType().Name,
                    on = a.instanceComponent == null ? null : Resolve.Path(a.instanceComponent.transform)
                }).Take(cap).ToArray();

            var removed = PrefabUtility.GetRemovedComponents(root)
                .Select(r => new
                {
                    kind = "removedComponent",
                    component = r.assetComponent == null ? null : r.assetComponent.GetType().Name
                }).Take(cap).ToArray();

            var addedObjects = PrefabUtility.GetAddedGameObjects(root)
                .Select(a => new
                {
                    kind = "addedGameObject",
                    path = a.instanceGameObject == null ? null : Resolve.Path(a.instanceGameObject.transform)
                }).Take(cap).ToArray();

            return new
            {
                instance = Resolve.Path(root.transform),
                asset = assetPath,
                propertyOverrides = modifications,
                addedComponents = added,
                removedComponents = removed,
                addedGameObjects = addedObjects,
                total = modifications.Length + added.Length + removed.Length + addedObjects.Length,
                _hint = "apply writes these into the prefab asset for every instance; revert discards them on this one."
            };
        }

        static string[] PropertyNames(UnityEngine.Object instanceObject, int cap)
        {
            if (instanceObject == null) return new string[0];
            var mods = PrefabUtility.GetPropertyModifications(instanceObject);
            if (mods == null) return new string[0];
            return mods.Select(m => m.propertyPath).Distinct().Take(cap).ToArray();
        }

        static object Apply(GameObject root, string assetPath, string[] properties, int cap)
        {
            // AutomatedAction, never UserAction: UserAction is what puts an entry in the user's
            // undo history *and* can raise Unity's own confirmation dialogs. This agent never
            // raises a modal, because a modal blocks the Editor's message pump and wedges the
            // whole bridge.
            if (properties == null || properties.Length == 0)
            {
                PrefabUtility.ApplyPrefabInstance(root, InteractionMode.AutomatedAction);
                return new { applied = "all", instance = Resolve.Path(root.transform), asset = assetPath };
            }

            var applied = new List<string>();
            var so = new SerializedObject(root);
            foreach (var path in properties.Take(cap))
            {
                var property = so.FindProperty(path);
                if (property == null) continue;
                PrefabUtility.ApplyPropertyOverride(property, assetPath, InteractionMode.AutomatedAction);
                applied.Add(path);
            }
            so.Dispose();

            if (applied.Count == 0)
                throw new UmcpToolException("E_PROPERTY_NOT_FOUND",
                    "None of the given properties exist on this instance.", "properties",
                    string.Join(", ", properties), null,
                    "Call this tool with action:\"list\" to see the override paths verbatim.");

            return new { applied = applied.ToArray(), instance = Resolve.Path(root.transform), asset = assetPath };
        }

        static object Revert(GameObject root, string assetPath, string[] properties, int cap)
        {
            if (properties == null || properties.Length == 0)
            {
                PrefabUtility.RevertPrefabInstance(root, InteractionMode.AutomatedAction);
                return new { reverted = "all", instance = Resolve.Path(root.transform), asset = assetPath };
            }

            var reverted = new List<string>();
            var so = new SerializedObject(root);
            foreach (var path in properties.Take(cap))
            {
                var property = so.FindProperty(path);
                if (property == null) continue;
                PrefabUtility.RevertPropertyOverride(property, InteractionMode.AutomatedAction);
                reverted.Add(path);
            }
            so.Dispose();

            if (reverted.Count == 0)
                throw new UmcpToolException("E_PROPERTY_NOT_FOUND",
                    "None of the given properties exist on this instance.", "properties",
                    string.Join(", ", properties), null,
                    "Call this tool with action:\"list\" to see the override paths verbatim.");

            return new { reverted = reverted.ToArray(), instance = Resolve.Path(root.transform), asset = assetPath };
        }

        static object Describe(UnityEngine.Object o)
        {
            var component = o as Component;
            if (component != null) return component.GetType().Name + " on " + Resolve.Path(component.transform);
            var go = o as GameObject;
            if (go != null) return Resolve.Path(go.transform);
            return o.name;
        }
    }
}
