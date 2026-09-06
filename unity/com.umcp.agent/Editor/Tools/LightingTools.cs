using System;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Umcp.Agent
{
    /// <summary>
    /// Lighting and lightmapping, plus the build's own settings and its last report.
    ///
    /// Baking is the one operation here that takes minutes rather than milliseconds, so it is
    /// started asynchronously and reported on, never waited for inside an Editor tick. A tool that
    /// blocks the main thread for a five-minute bake is indistinguishable, to every health check in
    /// this system, from a wedged Editor.
    /// </summary>
    internal static class LightingTools
    {
        [UnityTool(Skill = "lighting", Id = "lighting.settings",
            Summary = "Read the active scene's lighting settings: ambient, fog, lightmapper, bake state, light count.",
            Retry = RetryClass.Read)]
        [Example("{ }")]
        public static object Settings()
        {
            var lights = Resolve.AllGameObjects()
                .Select(go => go.GetComponent<Light>())
                .Where(l => l != null)
                .ToArray();

            return new
            {
                scene = SceneManager.GetActiveScene().name,
                ambientMode = RenderSettings.ambientMode.ToString(),
                ambientIntensity = RenderSettings.ambientIntensity,
                ambientLight = Vec.Arr(RenderSettings.ambientLight),
                skybox = RenderSettings.skybox == null ? null : RenderSettings.skybox.name,
                fog = RenderSettings.fog,
                fogMode = RenderSettings.fogMode.ToString(),
                fogColor = Vec.Arr(RenderSettings.fogColor),
                fogDensity = RenderSettings.fogDensity,
                sun = RenderSettings.sun == null ? null : RenderSettings.sun.name,
                lightmapper = LightmapEditorSettings.lightmapper.ToString(),
                lightmapResolution = LightmapEditorSettings.bakeResolution,
                bakedLightmaps = LightmapSettings.lightmaps.Length,
                isBaking = Lightmapping.isRunning,
                bakeProgress = Lightmapping.isRunning ? Lightmapping.buildProgress : 0f,
                lights = new
                {
                    total = lights.Length,
                    realtime = lights.Count(l => l.lightmapBakeType == LightmapBakeType.Realtime),
                    mixed = lights.Count(l => l.lightmapBakeType == LightmapBakeType.Mixed),
                    baked = lights.Count(l => l.lightmapBakeType == LightmapBakeType.Baked),
                    directional = lights.Count(l => l.type == LightType.Directional)
                }
            };
        }

        [UnityTool(Skill = "lighting", Id = "lighting.bake",
            Summary = "Start or cancel an asynchronous lightmap bake, or report the running one. action: start | cancel | status.",
            Mutating = true, Retry = RetryClass.None, Cost = Cost.Expensive,
            NoUndoReason = "A bake writes lightmap assets and clears the previous ones; Unity does not undo it.")]
        [Example("{ \"action\": \"status\" }")]
        [Example("{ \"action\": \"start\" }")]
        public static object Bake([Doc("start | cancel | status (default status)")] string action = "status")
        {
            var verb = (action ?? "status").ToLowerInvariant();
            switch (verb)
            {
                case "status":
                    return new
                    {
                        running = Lightmapping.isRunning,
                        progress = Lightmapping.isRunning ? Lightmapping.buildProgress : 0f,
                        lightmaps = LightmapSettings.lightmaps.Length
                    };

                case "start":
                    if (Lightmapping.isRunning)
                        return new { started = false, running = true, progress = Lightmapping.buildProgress, note = "A bake is already running." };

                    // Async, always. BakeAsync returns immediately and the Editor keeps ticking,
                    // so health stays meaningful and queued operations still run.
                    bool ok = Lightmapping.BakeAsync();
                    return new
                    {
                        started = ok,
                        running = Lightmapping.isRunning,
                        note = ok
                            ? "Bake started. Poll with action:\"status\"; it takes minutes, and the Editor stays responsive."
                            : "Unity refused to start a bake. Check that the scene is saved and Auto Generate is off."
                    };

                case "cancel":
                    if (!Lightmapping.isRunning) return new { cancelled = false, running = false, note = "No bake is running." };
                    Lightmapping.Cancel();
                    return new { cancelled = true, running = Lightmapping.isRunning };

                default:
                    throw new UmcpToolException("E_ARG_VALUE", "Unknown action '" + action + "'.",
                        "action", action, new[] { "start", "cancel", "status" }, null);
            }
        }

        // ---------------------------------------------------------------- build settings

        [UnityTool(Skill = "build", Id = "build.settings",
            Summary = "Read the build scene list, target, and the player settings that matter for a build.",
            Retry = RetryClass.Read)]
        [Example("{ }")]
        public static object BuildSettings()
        {
            return new
            {
                activeTarget = EditorUserBuildSettings.activeBuildTarget.ToString(),
                targetGroup = EditorUserBuildSettings.selectedBuildTargetGroup.ToString(),
                development = EditorUserBuildSettings.development,
                companyName = PlayerSettings.companyName,
                productName = PlayerSettings.productName,
                version = PlayerSettings.bundleVersion,
                colorSpace = PlayerSettings.colorSpace.ToString(),
                scriptingBackend = PlayerSettings.GetScriptingBackend(
                    UnityEditor.Build.NamedBuildTarget.FromBuildTargetGroup(EditorUserBuildSettings.selectedBuildTargetGroup)).ToString(),
                scenes = EditorBuildSettings.scenes.Select(s => new
                {
                    path = s.path,
                    enabled = s.enabled,
                    exists = System.IO.File.Exists(s.path)
                }).ToArray(),
                _hint = "build.validateTarget checks this target for the failures a build will not report."
            };
        }

        [UnityTool(Skill = "build", Id = "build.scenes",
            Summary = "Change the build scene list: add, remove, enable or disable a scene. action: add | remove | enable | disable.",
            Mutating = true, Retry = RetryClass.Write,
            NoUndoReason = "EditorBuildSettings is project configuration, not object state, and is not on the undo stack.")]
        [Example("{ \"action\": \"add\", \"path\": \"Assets/Scenes/Main.unity\" }")]
        public static object BuildScenes(
            [Doc("add | remove | enable | disable")] string action,
            [Doc("Scene asset path")] string path,
            [Doc("Insert at this index when adding. Default: append.")] int index = -1)
        {
            var verb = (action ?? "").ToLowerInvariant();
            var normalised = Resolve.AssetPath(path, "path");

            var list = EditorBuildSettings.scenes.ToList();
            int at = list.FindIndex(s => string.Equals(s.path, normalised, StringComparison.OrdinalIgnoreCase));

            switch (verb)
            {
                case "add":
                    if (AssetDatabase.LoadAssetAtPath<SceneAsset>(normalised) == null)
                        throw new UmcpToolException("E_ASSET_NOT_FOUND", "No scene asset at '" + normalised + "'.",
                            "path", normalised, null, "Scene paths look like Assets/Scenes/Main.unity.");
                    if (at >= 0) return new { changed = false, note = "Already in the build list at index " + at + ".", scenes = list.Count };
                    var entry = new EditorBuildSettingsScene(normalised, true);
                    if (index >= 0 && index < list.Count) list.Insert(index, entry); else list.Add(entry);
                    break;

                case "remove":
                    if (at < 0) return new { changed = false, note = "Not in the build list.", scenes = list.Count };
                    list.RemoveAt(at);
                    break;

                case "enable":
                case "disable":
                    if (at < 0)
                        throw new UmcpToolException("E_NOT_IN_BUILD", "'" + normalised + "' is not in the build list.",
                            "path", normalised,
                            list.Select(s => s.path).ToArray(),
                            "Add it first with action:\"add\".");
                    list[at] = new EditorBuildSettingsScene(list[at].path, verb == "enable");
                    break;

                default:
                    throw new UmcpToolException("E_ARG_VALUE", "Unknown action '" + action + "'.",
                        "action", action, new[] { "add", "remove", "enable", "disable" }, null);
            }

            EditorBuildSettings.scenes = list.ToArray();
            return new
            {
                changed = true,
                action = verb,
                path = normalised,
                scenes = list.Select((s, i) => new { index = i, path = s.path, enabled = s.enabled }).ToArray()
            };
        }

        [UnityTool(Skill = "build", Id = "build.lastReport",
            Summary = "Summarise the last build: result, duration, size, and the largest content by category.",
            Retry = RetryClass.Read, Cost = Cost.Moderate)]
        [Example("{ }")]
        public static object LastReport([Doc("How many size categories to return (default 12)")] int limit = 12)
        {
            // Unity writes the report to Library/LastBuild.buildreport, which is not an asset until
            // it is copied into the project. Reading it in place is the only way to answer this
            // without writing something into the user's Assets/ folder.
            const string source = "Library/LastBuild.buildreport";
            if (!System.IO.File.Exists(source))
                return new { available = false, _hint = "No build has been made from this project since the Library was created." };

            const string temp = "Assets/UmcpLastBuildReport.buildreport";
            try
            {
                System.IO.File.Copy(source, temp, true);
                AssetDatabase.ImportAsset(temp, ImportAssetOptions.ForceSynchronousImport);
                var report = AssetDatabase.LoadAssetAtPath<UnityEditor.Build.Reporting.BuildReport>(temp);
                if (report == null) return new { available = false, _hint = "The report exists but could not be imported." };

                var summary = report.summary;
                var byCategory = report.packedAssets
                    .SelectMany(p => p.contents)
                    .GroupBy(c => System.IO.Path.GetExtension(c.sourceAssetPath))
                    .Select(g => new { extension = string.IsNullOrEmpty(g.Key) ? "(none)" : g.Key, bytes = (long)g.Sum(c => (decimal)c.packedSize), count = g.Count() })
                    .OrderByDescending(x => x.bytes)
                    .Take(Bounds.Limit(limit))
                    .ToArray();

                return new
                {
                    available = true,
                    result = summary.result.ToString(),
                    platform = summary.platform.ToString(),
                    startedAt = summary.buildStartedAt.ToString("O"),
                    seconds = summary.totalTime.TotalSeconds,
                    totalBytes = (long)summary.totalSize,
                    errors = summary.totalErrors,
                    warnings = summary.totalWarnings,
                    largestByExtension = byCategory
                };
            }
            finally
            {
                // Nothing the daemon writes may live under Assets/ for longer than it must.
                if (System.IO.File.Exists(temp)) AssetDatabase.DeleteAsset(temp);
            }
        }
    }
}
