using System.Linq;
using UnityEditor;
using System.Diagnostics;
using UnityEditor.Compilation;
using UnityEngine;

namespace Umcp.Agent
{
    internal static class EditorTools
    {
        [UnityTool(Skill = "diagnostics", Id = "editor.ping", Summary = "Round-trip liveness probe. Executes on the Editor main thread.",
            Retry = RetryClass.Read)]
        [Example("{ }")]
        public static object Ping()
        {
            // The whole point: this must run *on the main thread*. A reply proves the pump is
            // alive, which "the socket is open" never did.
            return new { pong = true, frame = Time.frameCount, tickAgeMs = UmcpAgent.MsSinceLastTick };
        }

        [UnityTool(Skill = "diagnostics", Id = "editor.status", Summary = "Editor state: compiling, updating, play mode, focus, selection.",
            Retry = RetryClass.Read)]
        [Example("{ }")]
        public static object Status()
        {
            return new
            {
                unityVersion = Application.unityVersion,
                projectName = Application.productName,
                dataPath = Application.dataPath,
                renderPipeline = AssetTools.PipelineName(),
                isCompiling = EditorApplication.isCompiling,
                isUpdating = EditorApplication.isUpdating,
                isPlaying = EditorApplication.isPlaying,
                isPaused = EditorApplication.isPaused,
                epoch = UmcpAgent.Epoch,
                tickAgeMs = UmcpAgent.MsSinceLastTick,
                selection = Selection.gameObjects.Take(20).Select(g => g.name).ToArray()
            };
        }

        [UnityTool(Skill = "diagnostics", Id = "editor.selection.get", Summary = "Read the current Editor selection.", Retry = RetryClass.Read)]
        [Example("{ }")]
        public static object SelectionGet([Doc("Maximum entries (default 100)")] int limit = 100)
        {
            var objs = Selection.objects;
            var page = objs.Take(Bounds.Limit(limit)).Select(o => new
            {
                id = o.GetInstanceID(),
                name = o.name,
                type = o.GetType().Name,
                path = o is GameObject ? Resolve.Path(((GameObject)o).transform) : AssetDatabase.GetAssetPath(o)
            }).ToArray();
            return Res.Page(page, objs.Length, 0, page.Length);
        }

        [UnityTool(Skill = "diagnostics", Id = "editor.selection.set", Summary = "Set the Editor selection.",
            Mutating = true, Retry = RetryClass.Write, NoUndoReason = "Selection is editor UI state, not object state.")]
        [Example("{ \"targets\": [\"Player\", \"Enemy\"] }")]
        public static object SelectionSet([Doc("Paths, names or #instanceIds")] string[] targets)
        {
            var objs = (targets ?? new string[0]).Select(t => (Object)Resolve.GameObject(t, "targets")).ToArray();
            Selection.objects = objs;
            return new { selected = objs.Length };
        }

        [UnityTool(Skill = "diagnostics", Id = "console.read", Summary = "Read captured console messages. Bounded, newest last.",
            Retry = RetryClass.Read)]
        [Example("{ \"types\": [\"Error\", \"Exception\"], \"limit\": 20 }")]
        public static object ConsoleRead(
            [Doc("Filter by type: Log, Warning, Error, Assert, Exception")] string[] types = null,
            [Doc("Substring filter on the message")] string contains = null,
            [Doc("Maximum entries (default 50)")] int limit = 50,
            [Doc("Include stack traces (large)")] bool stackTrace = false)
        {
            return UmcpConsole.Read(types, contains, Bounds.Limit(limit), stackTrace);
        }

        [UnityTool(Skill = "diagnostics", Id = "console.clear", Summary = "Clear the captured console buffer and Unity's console window.",
            Mutating = true, Retry = RetryClass.None, NoUndoReason = "Log output is not object state.")]
        [Example("{ }")]
        public static object ConsoleClear()
        {
            int n = UmcpConsole.Clear();
            return new { cleared = n };
        }

        [UnityTool(Skill = "diagnostics", Id = "compile.errors", Summary = "Structured compile errors and warnings: file, line, column, message.",
            Retry = RetryClass.Read)]
        [Example("{ }")]
        public static object CompileErrors([Doc("Include warnings as well as errors")] bool warnings = false)
        {
            var msgs = UmcpConsole.CompilerMessages(warnings);
            return new
            {
                isCompiling = EditorApplication.isCompiling,
                count = msgs.Length,
                messages = msgs.Take(100).ToArray(),
                _truncated = msgs.Length > 100
            };
        }

        [UnityTool(Skill = "diagnostics", Id = "editor.compile", Summary = "Request a script recompilation. Triggers a domain reload.",
            Mutating = true, Retry = RetryClass.Compile, Cost = Cost.Expensive,
            NoUndoReason = "Compilation is not an undoable operation.")]
        [Example("{ }")]
        public static object Compile()
        {
            // The daemon holds subsequent ops across the reload and replays them, so this is
            // safe to call mid-sequence.
            UmcpAgent.NotifyCompileRequested();
            CompilationPipeline.RequestScriptCompilation();
            return new { requested = true, epoch = UmcpAgent.Epoch };
        }

        [UnityTool(Skill = "diagnostics", Id = "editor.stall", Summary = "Diagnostic: block the Editor main thread for N seconds, reproducing a modal dialog's effect.",
            Mutating = false, Retry = RetryClass.None, Cost = Cost.Expensive,
            NoUndoReason = "A diagnostic stall changes no state.")]
        [Example("{ \"seconds\": 12 }")]
        public static object Stall([Doc("How long to block the main thread, 1-60 s")] int seconds = 10)
        {
            // This exists to test E_EDITOR_BLOCKED without raising a modal of our own — we never
            // raise one, anywhere, because EditorUtility.DisplayDialog blocks
            // EditorApplication.update and wedges the whole bridge. A modal's *effect* is exactly
            // this: the main thread stops ticking while the socket stays healthy. Detection keys
            // on tick age, so reproducing the cause is unnecessary and reproducing the effect is
            // sufficient — and safe to run in CI.
            int s = seconds < 1 ? 1 : (seconds > 60 ? 60 : seconds);
            System.Threading.Thread.Sleep(s * 1000);
            return new { stalledSeconds = s };
        }

        [UnityTool(Skill = "diagnostics", Id = "editor.quit", Summary = "Quit this Editor. Saves first when asked; refuses on unsaved changes otherwise.",
            Mutating = true, Retry = RetryClass.None, Cost = Cost.Expensive,
            NoUndoReason = "Quitting the Editor is not an undoable operation.")]
        [Example("{ \"save\": true }")]
        public static object Quit(
            [Doc("Save open scenes and assets before quitting")] bool save = false,
            [Doc("Quit even with unsaved changes, discarding them")] bool force = false)
        {
            // EditorApplication.Exit is documented to exit immediately, *without* the usual
            // "save changes?" prompt. That makes it exactly the right call for a daemon (no
            // modal, ever) and exactly the wrong call to make casually: unsaved work would go
            // without a word. So the decision is explicit here, before anything exits.
            var dirty = new System.Collections.Generic.List<string>();
            for (int i = 0; i < UnityEngine.SceneManagement.SceneManager.sceneCount; i++)
            {
                var sc = UnityEngine.SceneManagement.SceneManager.GetSceneAt(i);
                if (sc.isDirty) dirty.Add(string.IsNullOrEmpty(sc.path) ? sc.name : sc.path);
            }

            if (dirty.Count > 0 && !save && !force)
                throw new UmcpToolException("E_UNSAVED_CHANGES",
                    "There are unsaved scene changes, so this Editor was not closed.",
                    param: "save", value: string.Join(", ", dirty),
                    hint: "Pass save:true to save them first, or force:true to discard them.");

            bool saved = false;
            if (save && dirty.Count > 0)
            {
                saved = UnityEditor.SceneManagement.EditorSceneManager.SaveOpenScenes();
                if (!saved)
                    throw new UmcpToolException("E_SAVE_FAILED",
                        "SaveOpenScenes() returned false; nothing was closed.",
                        hint: "An untitled scene has no path to save to. Save it once by hand, or pass force:true.");
                AssetDatabase.SaveAssets();
            }

            // Exit on a later tick so this result reaches the daemon first. A process that dies
            // mid-write turns a clean shutdown into an indistinguishable crash, and the
            // supervisor would then try to restart it.
            _quitAt = EditorApplication.timeSinceStartup + 0.4;
            EditorApplication.update -= QuitTick;
            EditorApplication.update += QuitTick;

            return new { quitting = true, saved = saved, dirtyScenes = dirty.ToArray(), pid = Process.GetCurrentProcess().Id };
        }

        static double _quitAt;

        static void QuitTick()
        {
            if (EditorApplication.timeSinceStartup < _quitAt) return;
            EditorApplication.update -= QuitTick;
            EditorApplication.Exit(0);
        }

        [UnityTool(Skill = "diagnostics", Id = "editor.assemblies", Summary = "List loaded Editor assemblies and their file paths. Code mode uses this to build its reference set.",
            Retry = RetryClass.Read)]
        [Example("{ }")]
        public static object Assemblies([Doc("Only assemblies backed by a file on disk")] bool fileBackedOnly = true)
        {
            var list = new System.Collections.Generic.List<object>();
            foreach (var asm in System.AppDomain.CurrentDomain.GetAssemblies())
            {
                string location = null;
                try { location = asm.IsDynamic ? null : asm.Location; } catch { }
                if (fileBackedOnly && string.IsNullOrEmpty(location)) continue;
                list.Add(new { name = asm.GetName().Name, path = location });
            }
            return new { count = list.Count, assemblies = list.ToArray() };
        }

        [UnityTool(Skill = "diagnostics", Id = "project.info", Summary = "Project identity: name, path, Unity version, pipeline, package count.",
            Retry = RetryClass.Read)]
        [Example("{ }")]
        public static object ProjectInfo()
        {
            var packages = UnityEditor.PackageManager.PackageInfo.GetAllRegisteredPackages();
            const int packageLimit = 200;
            var packageRows = (packages ?? new UnityEditor.PackageManager.PackageInfo[0])
                .OrderByDescending(package => package.isDirectDependency)
                .ThenBy(package => package.name, System.StringComparer.Ordinal)
                .Take(packageLimit)
                .Select(package => new
                {
                    name = package.name,
                    displayName = package.displayName,
                    version = package.version,
                    source = package.source.ToString(),
                    directDependency = package.isDirectDependency
                }).ToArray();
            return new
            {
                projectId = UmcpSettings.ProjectId,
                name = Application.productName,
                path = System.IO.Path.GetDirectoryName(Application.dataPath),
                unityVersion = Application.unityVersion,
                renderPipeline = AssetTools.PipelineName(),
                platform = EditorUserBuildSettings.activeBuildTarget.ToString(),
                scenesInBuild = EditorBuildSettings.scenes.Length,
                packageCount = packages == null ? 0 : packages.Length,
                packages = packageRows,
                packagesTruncated = packages != null && packages.Length > packageRows.Length,
                tags = UnityEditorInternal.InternalEditorUtility.tags,
                layers = UnityEditorInternal.InternalEditorUtility.layers,
                sortingLayers = SortingLayer.layers.Select(layer => new
                {
                    id = layer.id,
                    name = layer.name,
                    value = layer.value
                }).ToArray(),
                qualityLevels = QualitySettings.names
            };
        }
        // ---------------------------------------------------------------- undo

        [UnityTool(Skill = "diagnostics", Id = "editor.undo",
            Summary = "Undo the most recent change, by name. Every batch this tool runs is one undo step. action: peek | undo | redo.",
            Mutating = true, Retry = RetryClass.Write,
            NoUndoReason = "This is the undo operation; undoing it is redo.")]
        [Example("{ \"action\": \"peek\" }")]
        [Example("{ \"action\": \"undo\", \"expect\": \"MCP Batch\" }")]
        [Example("{ \"action\": \"undo\", \"steps\": 2 }")]
        public static object UndoStep(
            [Doc("peek | undo | redo. peek reports what would be undone without touching anything.")] string action = "peek",
            [Doc("How many steps (default 1, max 20)")] int steps = 1,
            [Doc("Only proceed if the next step's name matches this exactly. The guard against undoing a human's work.")] string expect = null)
        {
            var verb = (action ?? "peek").ToLowerInvariant();

            // Unity exposes the *name* of the group that a Ctrl+Z would collapse, and nothing else
            // — there is no readable undo stack. So "what will this undo" is one string, and it is
            // the only thing that can be checked before acting.
            var next = Undo.GetCurrentGroupName();

            if (verb == "peek")
                return new { next, canGuard = !string.IsNullOrEmpty(next) };

            if (verb != "undo" && verb != "redo")
                throw new UmcpToolException("E_BAD_ARG", "action must be peek, undo or redo.",
                    "action", action, new[] { "peek", "undo", "redo" }, null);

            // The guard exists because undo is Editor-wide, not ours: the last step may belong to
            // a person who was working in the Editor a second ago, and silently reverting that is
            // the worst thing this tool could do.
            if (verb == "undo" && !string.IsNullOrEmpty(expect) &&
                !string.Equals(expect, next, System.StringComparison.Ordinal))
            {
                throw new UmcpToolException("E_UNDO_MISMATCH",
                    "The next undo step is '" + next + "', not '" + expect + "'. Nothing was undone.",
                    "expect", expect, null,
                    "Somebody else changed the scene after this batch. Re-read the state before undoing.");
            }

            int count = System.Math.Max(1, System.Math.Min(steps, 20));
            var performed = new System.Collections.Generic.List<string>(count);

            for (int i = 0; i < count; i++)
            {
                var name = Undo.GetCurrentGroupName();
                if (verb == "undo") Undo.PerformUndo(); else Undo.PerformRedo();
                performed.Add(name);
            }

            // Unity applies undo lazily against the scene view; flushing here means the response
            // describes a scene that has actually changed, not one that is about to.
            Undo.FlushUndoRecordObjects();

            return new
            {
                action = verb,
                steps = performed.Count,
                names = performed.ToArray(),
                next = Undo.GetCurrentGroupName()
            };
        }
    }
}
