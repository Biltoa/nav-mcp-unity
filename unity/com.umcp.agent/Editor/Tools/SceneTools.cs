using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Umcp.Agent
{
    internal static class SceneTools
    {
        [UnityTool(Id = "scene.info", Summary = "Summarise the active scene: counts, roots, render pipeline. Bounded.",
            Retry = RetryClass.Read)]
        [Example("{ }")]
        public static object Info([Doc("Maximum root names to return (default 50)")] int limit = 50)
        {
            var scene = SceneManager.GetActiveScene();
            var roots = scene.isLoaded ? scene.GetRootGameObjects() : new GameObject[0];
            int total = Resolve.AllGameObjects().Count();
            var names = roots.Take(Bounds.Limit(limit)).Select(g => g.name).ToArray();

            return new
            {
                name = scene.name,
                path = string.IsNullOrEmpty(scene.path) ? null : scene.path,
                loaded = scene.isLoaded,
                dirty = scene.isDirty,
                rootCount = roots.Length,
                objectCount = total,
                renderPipeline = UnityEngine.Rendering.GraphicsSettings.currentRenderPipeline == null
                    ? "Built-in"
                    : UnityEngine.Rendering.GraphicsSettings.currentRenderPipeline.GetType().Name,
                roots = names,
                _truncated = roots.Length > names.Length,
                _hint = roots.Length > names.Length ? "use gameobject.find for a filtered view" : null
            };
        }

        [UnityTool(Id = "scene.list", Summary = "List open scenes and their dirty state.", Retry = RetryClass.Read)]
        [Example("{ }")]
        public static object List()
        {
            return new
            {
                active = SceneManager.GetActiveScene().name,
                scenes = Enumerable.Range(0, SceneManager.sceneCount).Select(i =>
                {
                    var s = SceneManager.GetSceneAt(i);
                    return new { name = s.name, path = s.path, loaded = s.isLoaded, dirty = s.isDirty, rootCount = s.isLoaded ? s.rootCount : 0 };
                }).ToArray()
            };
        }

        [UnityTool(Id = "scene.roots", Summary = "List root GameObjects of a scene. Bounded.", Retry = RetryClass.Read)]
        [Example("{ \"limit\": 20 }")]
        public static object Roots(
            [Doc("Scene name. Defaults to the active scene.")] string scene = null,
            [Doc("Maximum results (default 100)")] int limit = 100,
            [Doc("Skip this many results")] int offset = 0)
        {
            var s = string.IsNullOrEmpty(scene) ? SceneManager.GetActiveScene() : SceneManager.GetSceneByName(scene);
            if (!s.IsValid())
                throw new UmcpToolException("E_SCENE_NOT_FOUND", "No open scene named '" + scene + "'.", "scene", scene,
                    Suggest.Closest(scene, Enumerable.Range(0, SceneManager.sceneCount).Select(i => SceneManager.GetSceneAt(i).name).ToArray(), 3));

            var roots = s.GetRootGameObjects();
            var page = roots.Skip(offset).Take(Bounds.Limit(limit))
                .Select(g => new { id = g.GetInstanceID(), name = g.name, active = g.activeSelf, childCount = g.transform.childCount })
                .ToArray();
            return Res.Page(page, roots.Length, offset, page.Length);
        }

        [UnityTool(Id = "scene.children", Summary = "List the children of a GameObject, to a bounded depth.",
            Retry = RetryClass.Read)]
        [Example("{ \"target\": \"Level\", \"maxDepth\": 2 }")]
        public static object Children(
            [Doc("Path, name or #instanceId")] string target,
            [Doc("How many levels to descend (default 1)")] int maxDepth = 1,
            [Doc("Maximum results (default 100)")] int limit = 100)
        {
            var root = Resolve.GameObject(target).transform;
            var all = new System.Collections.Generic.List<object>();
            int totalSeen = 0;
            int cap = Bounds.Limit(limit);

            void Walk(Transform t, int depth)
            {
                if (depth > maxDepth) return;
                for (int i = 0; i < t.childCount; i++)
                {
                    var c = t.GetChild(i);
                    totalSeen++;
                    if (all.Count < cap)
                        all.Add(new { id = c.gameObject.GetInstanceID(), name = c.name, depth, childCount = c.childCount, active = c.gameObject.activeSelf });
                    Walk(c, depth + 1);
                }
            }
            Walk(root, 1);
            return Res.Page(all.ToArray(), totalSeen, 0, all.Count);
        }

        [UnityTool(Id = "scene.open", Summary = "Open a scene by asset path.",
            Mutating = true, Retry = RetryClass.Write, NoUndoReason = "Scene loading is not an undoable operation in Unity.")]
        [Example("{ \"path\": \"Assets/Scenes/Main.unity\", \"mode\": \"single\" }")]
        public static object Open(
            [Doc("Scene asset path")] string path,
            [Doc("\"single\" replaces open scenes, \"additive\" adds")] string mode = "single")
        {
            mode = Values.Require("mode", mode, "single", "additive");
            var p = Resolve.AssetPath(path);
            if (AssetDatabase.LoadAssetAtPath<SceneAsset>(p) == null)
                throw new UmcpToolException("E_ASSET_NOT_FOUND", "No scene asset at '" + p + "'.", "path", path);

            // Never prompt. A modal here would block EditorApplication.update and wedge the bridge.
            var dirty = Enumerable.Range(0, SceneManager.sceneCount)
                .Select(SceneManager.GetSceneAt).Where(s => s.isDirty).Select(s => s.name).ToArray();
            if (mode == "single" && dirty.Length > 0)
                throw new UmcpToolException("E_SCENE_DIRTY",
                    "Refusing to replace unsaved scenes: " + string.Join(", ", dirty),
                    "mode", mode, null, "Save them first, or open additively.");

            var opened = EditorSceneManager.OpenScene(p, mode == "additive" ? OpenSceneMode.Additive : OpenSceneMode.Single);
            return new { name = opened.name, path = opened.path, rootCount = opened.rootCount };
        }

        [UnityTool(Id = "scene.save", Summary = "Save an open scene. Refuses to overwrite unless asked explicitly.",
            Mutating = true, Retry = RetryClass.Write, NoUndoReason = "Writing a file is not undoable.")]
        [Example("{ \"scene\": \"Main\", \"confirm\": true }")]
        public static object Save(
            [Doc("Scene name. Defaults to the active scene.")] string scene = null,
            [Doc("Must be true. Guards against an agent silently overwriting a user's scene.")] bool confirm = false,
            [Doc("Save to this path instead (Save As)")] string saveAs = null)
        {
            if (!confirm)
                throw new UmcpToolException("E_CONFIRM_REQUIRED",
                    "Saving a scene overwrites the user's file. Pass confirm=true.", "confirm", "false");

            var s = string.IsNullOrEmpty(scene) ? SceneManager.GetActiveScene() : SceneManager.GetSceneByName(scene);
            if (!s.IsValid())
                throw new UmcpToolException("E_SCENE_NOT_FOUND", "No open scene named '" + scene + "'.", "scene", scene);

            bool ok = string.IsNullOrEmpty(saveAs)
                ? EditorSceneManager.SaveScene(s)
                : EditorSceneManager.SaveScene(s, Resolve.AssetPath(saveAs, "saveAs"));
            return new { saved = ok, name = s.name, path = s.path };
        }

        [UnityTool(Id = "scene.create", Summary = "Create a new empty scene.",
            Mutating = true, Retry = RetryClass.Write, NoUndoReason = "Scene creation is not an undoable operation in Unity.")]
        [Example("{ \"path\": \"Assets/Scenes/New.unity\", \"setup\": \"defaultGameObjects\" }")]
        public static object Create(
            [Doc("Where to save the new scene")] string path,
            [Doc("\"empty\" or \"defaultGameObjects\"")] string setup = "defaultGameObjects",
            [Doc("\"single\" replaces open scenes, \"additive\" adds")] string mode = "additive")
        {
            setup = Values.Require("setup", setup, "empty", "defaultGameObjects");
            mode = Values.Require("mode", mode, "single", "additive");
            var p = Resolve.AssetPath(path);
            var slash = p.LastIndexOf('/');
            if (slash > 0)
            {
                var folder = p.Substring(0, slash);
                if (!AssetDatabase.IsValidFolder(folder)) AssetTools.CreateFolder(folder);
            }
            var s = EditorSceneManager.NewScene(
                setup == "empty" ? NewSceneSetup.EmptyScene : NewSceneSetup.DefaultGameObjects,
                mode == "single" ? NewSceneMode.Single : NewSceneMode.Additive);
            if (!EditorSceneManager.SaveScene(s, p))
            {
                EditorSceneManager.CloseScene(s, true);
                throw new UmcpToolException("E_SCENE_SAVE_FAILED",
                    "Unity could not save the new scene to '" + p + "'.", "path", path);
            }
            return new { name = s.name, path = s.path };
        }

        [UnityTool(Id = "scene.setActive", Summary = "Make an open scene the active scene.",
            Mutating = true, Retry = RetryClass.Write, NoUndoReason = "Active-scene selection is editor state, not object state.")]
        [Example("{ \"scene\": \"Main\" }")]
        public static object SetActive([Doc("Scene name")] string scene)
        {
            var s = SceneManager.GetSceneByName(scene);
            if (!s.IsValid())
                throw new UmcpToolException("E_SCENE_NOT_FOUND", "No open scene named '" + scene + "'.", "scene", scene,
                    Suggest.Closest(scene, Enumerable.Range(0, SceneManager.sceneCount).Select(i => SceneManager.GetSceneAt(i).name).ToArray(), 3));
            SceneManager.SetActiveScene(s);
            return new { active = s.name };
        }
    }
}
