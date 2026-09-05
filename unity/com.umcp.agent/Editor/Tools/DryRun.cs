using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace Umcp.Agent
{
    /// <summary>
    /// What a mutating operation would actually do, worked out without doing it.
    ///
    /// The Phase 1 implementation of <c>dryRun</c> validated and bound the arguments and then said
    /// "nothing was applied". That is honest but nearly useless: the questions a caller asks
    /// before a batch are *does my target exist*, *what exactly will change*, and *can I undo it* —
    /// and all three are answerable without touching anything.
    ///
    /// Two rules hold this together:
    ///
    ///   1. **A prediction never mutates.** Everything here is a read: resolve, count, compare.
    ///   2. **A prediction never guesses.** Where the effect cannot be predicted precisely, it
    ///      says so in `effect` rather than inventing a plausible sentence. An agent that trusts a
    ///      confident wrong prediction is worse off than one told "unknown".
    /// </summary>
    internal static class DryRun
    {
        public static object Predict(string tool, JObject args, ToolMeta meta)
        {
            var problems = new List<object>();
            var resolved = new List<object>();

            // Whatever the tool, the most common reason a real call fails is a target that is not
            // there. Resolving it here turns a mid-batch failure into a pre-flight answer.
            foreach (var name in new[] { "target", "parent", "source", "of", "on" })
                ResolveOne(args, name, resolved, problems);
            ResolveMany(args, "targets", resolved, problems);

            string effect;
            switch (tool)
            {
                case "gameobject.create":
                    effect = "Creates one GameObject named '" + Str(args, "name") + "'" +
                             (args["parent"] != null ? " under '" + Str(args, "parent") + "'" : " at the scene root") + ".";
                    break;

                case "gameobject.delete":
                    {
                        var go = Resolve.TryGameObject(Str(args, "target"));
                        int subtree = go == null ? -1 : go.GetComponentsInChildren<Transform>(true).Length;
                        effect = go == null
                            ? "Would delete a GameObject that does not currently exist."
                            : "Deletes '" + go.name + "' and " + (subtree - 1) + " descendant(s).";
                        break;
                    }

                case "gameobject.rename":
                    {
                        var go = Resolve.TryGameObject(Str(args, "target"));
                        effect = go == null ? "Target not found." :
                            "Renames '" + go.name + "' to '" + Str(args, "name") + "'.";
                        break;
                    }

                case "gameobject.setParent":
                    {
                        var go = Resolve.TryGameObject(Str(args, "target"));
                        var parent = Str(args, "parent");
                        effect = go == null ? "Target not found." :
                            "Reparents '" + go.name + "' from '" +
                            (go.transform.parent == null ? "<scene root>" : go.transform.parent.name) + "' to '" +
                            (string.IsNullOrEmpty(parent) ? "<scene root>" : parent) + "'.";
                        break;
                    }

                case "gameobject.setActive":
                    {
                        var go = Resolve.TryGameObject(Str(args, "target"));
                        bool wanted = args["active"] != null && args["active"].Type == JTokenType.Boolean && (bool)args["active"];
                        effect = go == null ? "Target not found." :
                            go.activeSelf == wanted
                                ? "No change: '" + go.name + "' is already " + (wanted ? "active" : "inactive") + "."
                                : "Sets '" + go.name + "' " + (wanted ? "active" : "inactive") + ".";
                        break;
                    }

                case "component.add":
                    {
                        var go = Resolve.TryGameObject(Str(args, "target"));
                        var type = Str(args, "type");
                        if (go == null) { effect = "Target not found."; break; }
                        var existing = go.GetComponents<Component>().Count(c => c != null && c.GetType().Name == type);
                        effect = "Adds a " + type + " to '" + go.name + "'" +
                                 (existing > 0 ? " (it already has " + existing + ")." : ".");
                        break;
                    }

                case "component.remove":
                    {
                        var go = Resolve.TryGameObject(Str(args, "target"));
                        var type = Str(args, "type");
                        if (go == null) { effect = "Target not found."; break; }
                        var present = go.GetComponents<Component>().Any(c => c != null && c.GetType().Name == type);
                        effect = present
                            ? "Removes the " + type + " from '" + go.name + "'."
                            : "Would fail: '" + go.name + "' has no " + type + ".";
                        break;
                    }

                case "component.set":
                    {
                        var go = Resolve.TryGameObject(Str(args, "target"));
                        var properties = args["properties"] as JObject;
                        effect = go == null ? "Target not found." :
                            "Sets " + (properties == null ? 0 : properties.Count) + " propert" +
                            ((properties != null && properties.Count == 1) ? "y" : "ies") +
                            " on " + Str(args, "type") + " of '" + go.name + "'.";
                        if (go != null && properties != null)
                            foreach (var p in properties.Properties())
                                problems.AddRange(CheckProperty(go, Str(args, "type"), p.Name));
                        break;
                    }

                case "assets.delete":
                    {
                        var path = Str(args, "path");
                        bool exists = !string.IsNullOrEmpty(path) && AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(path) != null;
                        var dependents = exists ? DependentCount(path) : 0;
                        effect = !exists
                            ? "Would fail: no asset at '" + path + "'."
                            : "Deletes '" + path + "'. " + dependents + " other asset(s) currently reference it. " +
                              "This is not undoable.";
                        break;
                    }

                case "assets.move":
                    {
                        var from = Str(args, "path");
                        var to = Str(args, "to");
                        var error = AssetDatabase.ValidateMoveAsset(from, to);
                        effect = string.IsNullOrEmpty(error)
                            ? "Moves '" + from + "' to '" + to + "', preserving its GUID."
                            : "Would fail: " + error;
                        break;
                    }

                case "scene.save":
                    {
                        var scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene();
                        effect = "Writes " + (scene.isDirty ? "the dirty" : "the unchanged") + " scene '" + scene.name +
                                 "' to " + (string.IsNullOrEmpty(scene.path) ? "a new path" : scene.path) + ". Overwrites the file on disk.";
                        break;
                    }

                case "material.create":
                    {
                        var path = Str(args, "path");
                        bool exists = !string.IsNullOrEmpty(path) && AssetDatabase.LoadAssetAtPath<Material>(path) != null;
                        effect = exists
                            ? "An asset already exists at '" + path + "'; the real call decides whether that is an overwrite or an error."
                            : "Creates a material asset at '" + path + "'.";
                        break;
                    }

                case "prefab.overrides":
                    {
                        var go = Resolve.TryGameObject(Str(args, "target"));
                        var action = (Str(args, "action") ?? "list").ToLowerInvariant();
                        if (go == null) { effect = "Target not found."; break; }
                        if (!PrefabUtility.IsPartOfPrefabInstance(go)) { effect = "Would fail: '" + go.name + "' is not a prefab instance."; break; }
                        var root = PrefabUtility.GetOutermostPrefabInstanceRoot(go);
                        int count = PrefabUtility.GetObjectOverrides(root, true).Count +
                                    PrefabUtility.GetAddedComponents(root).Count +
                                    PrefabUtility.GetRemovedComponents(root).Count;
                        effect = action == "list"
                            ? "Reads " + count + " override(s) on '" + root.name + "'."
                            : action + "s " + count + " override(s) on '" + root.name + "'" +
                              (action == "apply" ? ", changing the prefab asset for every instance of it." : ".");
                        break;
                    }

                case "editor.quit":
                    {
                        var dirty = new List<string>();
                        for (int i = 0; i < UnityEngine.SceneManagement.SceneManager.sceneCount; i++)
                        {
                            var sc = UnityEngine.SceneManagement.SceneManager.GetSceneAt(i);
                            if (sc.isDirty) dirty.Add(sc.name);
                        }
                        bool save = args["save"] != null && args["save"].Type == JTokenType.Boolean && (bool)args["save"];
                        bool force = args["force"] != null && args["force"].Type == JTokenType.Boolean && (bool)args["force"];
                        effect = dirty.Count > 0 && !save && !force
                            ? "Would refuse: " + dirty.Count + " unsaved scene(s) — " + string.Join(", ", dirty.ToArray()) + "."
                            : "Quits this Editor" + (save && dirty.Count > 0 ? ", saving " + dirty.Count + " scene(s) first." : ".");
                        break;
                    }

                case "editor.compile":
                    effect = "Requests a recompile, which triggers a domain reload. Queued operations are held and replayed.";
                    break;

                default:
                    effect = meta.Mutating
                        ? "No specific prediction for this tool; arguments were validated and every named target resolved."
                        : "Read-only tool.";
                    break;
            }

            return new
            {
                dryRun = true,
                tool,
                mutating = meta.Mutating,
                effect,
                undo = meta.Undo,
                undoable = meta.Undo != null,
                noUndoReason = meta.NoUndoReason,
                resolved = resolved.ToArray(),
                problems = problems.ToArray(),
                wouldSucceed = problems.Count == 0,
                note = "Nothing was applied. Predictions are made from current state; they are wrong if " +
                       "something else changes in between."
            };
        }

        // ---------------------------------------------------------------- helpers

        static void ResolveOne(JObject args, string field, List<object> resolved, List<object> problems)
        {
            var value = Str(args, field);
            if (string.IsNullOrEmpty(value)) return;

            var go = Resolve.TryGameObject(value);
            if (go != null)
            {
                resolved.Add(new { param = field, value, path = Resolve.Path(go.transform), id = go.GetInstanceID() });
                return;
            }

            // A path-looking value may be an asset rather than a scene object.
            if (value.StartsWith("Assets/") || value.StartsWith("Packages/"))
            {
                var asset = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(value);
                if (asset != null)
                {
                    resolved.Add(new { param = field, value, path = value, id = asset.GetInstanceID() });
                    return;
                }
            }

            problems.Add(new
            {
                param = field,
                value,
                problem = "No GameObject or asset matches this.",
                didYouMean = Suggest.Closest(value, Resolve.AllGameObjects().Select(g => g.name).Distinct().ToArray(), 3)
            });
        }

        static void ResolveMany(JObject args, string field, List<object> resolved, List<object> problems)
        {
            var array = args[field] as JArray;
            if (array == null) return;
            foreach (var item in array)
            {
                var value = item == null ? null : item.ToString();
                if (string.IsNullOrEmpty(value)) continue;
                var go = Resolve.TryGameObject(value);
                if (go != null) resolved.Add(new { param = field, value, path = Resolve.Path(go.transform), id = go.GetInstanceID() });
                else problems.Add(new { param = field, value, problem = "No GameObject matches this.", didYouMean = new string[0] });
            }
        }

        static IEnumerable<object> CheckProperty(GameObject go, string type, string property)
        {
            var component = go.GetComponents<Component>().FirstOrDefault(c => c != null && c.GetType().Name == type);
            if (component == null)
            {
                yield return new { param = "type", value = type, problem = "'" + go.name + "' has no " + type + ".", didYouMean = go.GetComponents<Component>().Where(c => c != null).Select(c => c.GetType().Name).Take(5).ToArray() };
                yield break;
            }

            var so = new SerializedObject(component);
            if (so.FindProperty(property) == null && so.FindProperty("m_" + property) == null)
                yield return new
                {
                    param = "properties",
                    value = property,
                    problem = "No serialized property '" + property + "' on " + type + ".",
                    didYouMean = PropertyNames(so).Take(5).ToArray()
                };
            so.Dispose();
        }

        static IEnumerable<string> PropertyNames(SerializedObject so)
        {
            var property = so.GetIterator();
            var enterChildren = true;
            while (property.NextVisible(enterChildren))
            {
                enterChildren = false;
                yield return property.name;
            }
        }

        static int DependentCount(string path)
        {
            var guid = AssetDatabase.AssetPathToGUID(path);
            if (string.IsNullOrEmpty(guid)) return 0;
            // Unity's own reverse-dependency search. Bounded by the filter, and a read.
            return AssetDatabase.FindAssets("ref:" + path).Length;
        }

        static string Str(JObject args, string field)
        {
            var token = args[field];
            return token == null || token.Type == JTokenType.Null ? null : token.ToString();
        }
    }
}
