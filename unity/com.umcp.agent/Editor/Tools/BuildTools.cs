using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace Umcp.Agent
{
    /// <summary>
    /// Build-target validation: the platform failures that are statically detectable, checked
    /// before the build rather than discovered by it.
    ///
    /// Every check here comes from a failure that actually cost this project a build. A WebGL
    /// build of it takes about 25 minutes, so "compile it and see" is not a debugging strategy —
    /// and none of these produce an error at build time anyway. They produce jittering audio,
    /// magenta geometry, grey text and clipped highlights *in the player*.
    ///
    /// The checks use reflection wherever a package type is involved (TextMeshPro, the render
    /// pipeline's volume components) because this agent has to load in a project that does not
    /// have those packages at all. A validator that cannot be installed is not a validator.
    /// </summary>
    internal static class BuildTools
    {
        internal sealed class Finding
        {
            public string severity;   // error | warning | info
            public string code;
            public string subject;    // the asset or object it is about
            public string detail;
            public string fix;
        }

        [UnityTool(Skill = "build", Id = "build.validateTarget",
            Summary = "Check a build target for the platform failures that are statically detectable: audio resampling, shader model, small SDF text, emissive clipping, build scenes.",
            Retry = RetryClass.Read, Cost = Cost.Expensive)]
        [Example("{ \"platform\": \"WebGL\" }")]
        [Example("{ \"platform\": \"Android\", \"checks\": [\"shader\", \"emissive\"], \"limit\": 20 }")]
        public static object ValidateTarget(
            [Doc("Build target, e.g. WebGL, Android, iOS, StandaloneWindows64. Defaults to the active target.")] string platform = null,
            [Doc("Which checks to run: scenes, audio, shader, text, emissive. Default all.")] string[] checks = null,
            [Doc("Maximum findings per check (default 25)")] int limit = 25)
        {
            BuildTarget target = EditorUserBuildSettings.activeBuildTarget;
            if (!string.IsNullOrEmpty(platform))
            {
                try { target = (BuildTarget)Enum.Parse(typeof(BuildTarget), platform, true); }
                catch
                {
                    throw new UmcpToolException("E_ARG_VALUE", "Unknown build target '" + platform + "'.",
                        "platform", platform,
                        Suggest.Closest(platform, Enum.GetNames(typeof(BuildTarget)), 3),
                        "Omit the argument to validate the active build target.");
                }
            }

            int cap = Bounds.Limit(limit);
            var wanted = new HashSet<string>(checks == null || checks.Length == 0
                ? new[] { "scenes", "audio", "shader", "text", "emissive" }
                : checks.Select(c => (c ?? "").ToLowerInvariant()));

            var findings = new List<Finding>();
            var ran = new List<string>();

            if (wanted.Contains("scenes")) { ran.Add("scenes"); findings.AddRange(CheckScenes(cap)); }
            if (wanted.Contains("audio")) { ran.Add("audio"); findings.AddRange(CheckAudio(target, cap)); }
            if (wanted.Contains("shader")) { ran.Add("shader"); findings.AddRange(CheckShaders(target, cap)); }
            if (wanted.Contains("text")) { ran.Add("text"); findings.AddRange(CheckSmallSdfText(cap)); }
            if (wanted.Contains("emissive")) { ran.Add("emissive"); findings.AddRange(CheckEmissive(cap)); }

            var graphicsApis = PlayerSettings.GetGraphicsAPIs(target).Select(a => a.ToString()).ToArray();

            return new
            {
                platform = target.ToString(),
                activeTarget = EditorUserBuildSettings.activeBuildTarget.ToString(),
                colorSpace = PlayerSettings.colorSpace.ToString(),
                graphicsApis,
                renderPipeline = AssetTools.PipelineName(),
                checksRun = ran.ToArray(),
                errors = findings.Count(f => f.severity == "error"),
                warnings = findings.Count(f => f.severity == "warning"),
                findings = findings.Take(cap * ran.Count).ToArray(),
                _hint = findings.Count == 0
                    ? "No statically detectable problems for this target."
                    : "Each finding names the asset and the fix. None of these fail the build; they fail in the player."
            };
        }

        // ---------------------------------------------------------------- scenes

        static IEnumerable<Finding> CheckScenes(int cap)
        {
            var scenes = EditorBuildSettings.scenes;
            if (scenes.Length == 0)
                yield return new Finding
                {
                    severity = "error",
                    code = "BUILD_NO_SCENES",
                    subject = "Build Settings",
                    detail = "No scenes are in the build.",
                    fix = "Add at least the startup scene to File > Build Settings."
                };

            int enabled = 0;
            int shown = 0;
            foreach (var s in scenes)
            {
                if (s.enabled) enabled++;
                if (shown >= cap) continue;
                if (!File.Exists(s.path))
                {
                    shown++;
                    yield return new Finding
                    {
                        severity = "error",
                        code = "BUILD_SCENE_MISSING",
                        subject = s.path,
                        detail = "A scene listed in the build does not exist on disk.",
                        fix = "Remove it from Build Settings, or restore the asset."
                    };
                }
            }

            if (scenes.Length > 0 && enabled == 0)
                yield return new Finding
                {
                    severity = "error",
                    code = "BUILD_NO_ENABLED_SCENES",
                    subject = "Build Settings",
                    detail = "Every scene in the build list is disabled.",
                    fix = "Enable the startup scene."
                };
        }

        // ---------------------------------------------------------------- audio

        /// <summary>
        /// WebGL has exactly one compression format — AAC — and re-encodes at the platform sample
        /// rate. A clip authored to loop seamlessly gains encoder padding and audibly jitters at
        /// the loop point unless its sample rate is pinned. Nothing about this is visible until
        /// the clip plays in a browser.
        /// </summary>
        static IEnumerable<Finding> CheckAudio(BuildTarget target, int cap)
        {
            bool webgl = target == BuildTarget.WebGL;
            var platformName = webgl ? "WebGL" : target.ToString();

            int shown = 0;
            foreach (var guid in AssetDatabase.FindAssets("t:AudioClip"))
            {
                if (shown >= cap) yield break;
                var path = AssetDatabase.GUIDToAssetPath(guid);
                var importer = AssetImporter.GetAtPath(path) as AudioImporter;
                if (importer == null) continue;

                var settings = importer.ContainsSampleSettingsOverride(platformName)
                    ? importer.GetOverrideSampleSettings(platformName)
                    : importer.defaultSampleSettings;

                if (webgl)
                {
                    bool pinned = settings.sampleRateSetting == AudioSampleRateSetting.OverrideSampleRate;
                    var clip = AssetDatabase.LoadAssetAtPath<AudioClip>(path);
                    bool loopCandidate = clip != null && clip.length <= 30f;   // stings and music beds, not dialogue

                    if (!pinned && loopCandidate)
                    {
                        shown++;
                        yield return new Finding
                        {
                            severity = "warning",
                            code = "AUDIO_WEBGL_RESAMPLED",
                            subject = path,
                            detail = "WebGL re-encodes to AAC at the platform sample rate; a looping clip gains " +
                                     "encoder padding and jitters at the loop point. Current setting: " +
                                     settings.sampleRateSetting + ".",
                            fix = "In the clip's WebGL override, set Sample Rate Setting to Override Sample Rate " +
                                  "(match the source, usually 44100)."
                        };
                    }
                }
                else if (settings.compressionFormat == AudioCompressionFormat.PCM && importer.forceToMono == false)
                {
                    var clip = AssetDatabase.LoadAssetAtPath<AudioClip>(path);
                    if (clip != null && clip.length > 10f)
                    {
                        shown++;
                        yield return new Finding
                        {
                            severity = "info",
                            code = "AUDIO_LARGE_PCM",
                            subject = path,
                            detail = "A " + clip.length.ToString("0.0") + " s clip is stored uncompressed (PCM).",
                            fix = "Vorbis with streaming is usually right for anything over ten seconds."
                        };
                    }
                }
            }
        }

        // ---------------------------------------------------------------- shaders

        /// <summary>
        /// A shader declaring <c>#pragma target 4.5</c> (or higher) does not run on GLES3. Unity
        /// does not fail the build for it: the material silently renders as the magenta error
        /// shader on the device, which is discovered by looking at the device.
        /// </summary>
        static IEnumerable<Finding> CheckShaders(BuildTarget target, int cap)
        {
            var apis = PlayerSettings.GetGraphicsAPIs(target);
            bool gles = apis.Any(a => a == GraphicsDeviceType.OpenGLES3 || a.ToString().StartsWith("OpenGL"));
            bool webgl = target == BuildTarget.WebGL;
            if (!gles && !webgl) yield break;

            int shown = 0;
            foreach (var guid in AssetDatabase.FindAssets("t:Shader"))
            {
                if (shown >= cap) yield break;
                var path = AssetDatabase.GUIDToAssetPath(guid);
                if (!path.EndsWith(".shader", StringComparison.OrdinalIgnoreCase)) continue;
                if (path.StartsWith("Packages/com.unity.render-pipelines", StringComparison.OrdinalIgnoreCase)) continue;

                string text;
                try { text = File.ReadAllText(path); }
                catch { continue; }

                foreach (var line in text.Split('\n'))
                {
                    var trimmed = line.Trim();
                    if (!trimmed.StartsWith("#pragma target", StringComparison.OrdinalIgnoreCase)) continue;

                    var value = trimmed.Substring("#pragma target".Length).Trim();
                    float level;
                    if (!float.TryParse(value, System.Globalization.NumberStyles.Float,
                            System.Globalization.CultureInfo.InvariantCulture, out level)) continue;
                    if (level < 4.5f) continue;

                    shown++;
                    yield return new Finding
                    {
                        severity = "error",
                        code = "SHADER_TARGET_TOO_HIGH",
                        subject = path,
                        detail = "#pragma target " + value + " requires shader model 4.5; this build target includes " +
                                 string.Join(", ", apis.Select(a => a.ToString()).ToArray()) +
                                 ", where the shader falls back to the error shader without a build error.",
                        fix = "Lower the pragma to 3.5 (and drop the SM4.5-only features), or drop the GLES3 " +
                              "graphics API for this target."
                    };
                    break;
                }
            }
        }

        // ---------------------------------------------------------------- TMP small text

        /// <summary>
        /// TextMeshPro's Mobile SDF shaders drop the features that keep small glyphs legible, and
        /// small text rendered with them turns into grey boxes on device. Detected by reflection:
        /// this project may not have TextMeshPro at all.
        /// </summary>
        static IEnumerable<Finding> CheckSmallSdfText(int cap)
        {
            var results = new List<Finding>();
            int shown = 0;

            foreach (var go in Resolve.AllGameObjects())
            {
                if (shown >= cap) break;
                foreach (var component in go.GetComponents<Component>())
                {
                    if (component == null) continue;
                    var type = component.GetType();
                    if (type.FullName != "TMPro.TextMeshProUGUI" && type.FullName != "TMPro.TextMeshPro") continue;

                    var sizeProp = type.GetProperty("fontSize", BindingFlags.Public | BindingFlags.Instance);
                    var matProp = type.GetProperty("fontSharedMaterial", BindingFlags.Public | BindingFlags.Instance);
                    if (sizeProp == null || matProp == null) continue;

                    float size;
                    try { size = Convert.ToSingle(sizeProp.GetValue(component, null)); }
                    catch { continue; }

                    var material = matProp.GetValue(component, null) as Material;
                    if (material == null || material.shader == null) continue;
                    if (material.shader.name.IndexOf("Mobile", StringComparison.OrdinalIgnoreCase) < 0) continue;
                    if (size >= 18f) continue;

                    shown++;
                    results.Add(new Finding
                    {
                        severity = "warning",
                        code = "TEXT_SMALL_MOBILE_SDF",
                        subject = Resolve.Path(go.transform),
                        detail = "Font size " + size.ToString("0.#") + " with the mobile SDF shader \"" +
                                 material.shader.name + "\". Small glyphs render as grey boxes on device.",
                        fix = "Use the non-mobile TMP shader for this text, or raise the font size above 18."
                    });
                    break;
                }
            }

            return results;
        }

        // ---------------------------------------------------------------- emissive under ACES

        /// <summary>
        /// ACES tonemapping desaturates and clips bright emissives: values above roughly 1.8 turn
        /// yellow and stop responding to further intensity. Only reported when an ACES tonemapper
        /// is actually in the project, which is read by reflection because Volume and Tonemapping
        /// live in the render-pipeline packages.
        /// </summary>
        static IEnumerable<Finding> CheckEmissive(int cap)
        {
            if (!AcesInUse()) yield break;

            int shown = 0;
            foreach (var guid in AssetDatabase.FindAssets("t:Material"))
            {
                if (shown >= cap) yield break;
                var path = AssetDatabase.GUIDToAssetPath(guid);
                if (path.StartsWith("Packages/", StringComparison.OrdinalIgnoreCase)) continue;

                var material = AssetDatabase.LoadAssetAtPath<Material>(path);
                if (material == null || !material.HasProperty("_EmissionColor")) continue;

                var color = material.GetColor("_EmissionColor");
                float peak = Mathf.Max(color.r, Mathf.Max(color.g, color.b));
                if (peak <= 1.8f) continue;

                shown++;
                yield return new Finding
                {
                    severity = "warning",
                    code = "EMISSIVE_CLIPS_UNDER_ACES",
                    subject = path,
                    detail = "Emission peaks at " + peak.ToString("0.00") +
                             "; ACES clips and yellows above about 1.8, so further intensity does nothing visible.",
                    fix = "Bring the emission below 1.8 and get the brightness from bloom, or switch the " +
                          "tonemapper to Neutral."
                };
            }
        }

        static bool AcesInUse()
        {
            // Volume components are package types. Reflection, and a failure to find them means
            // "no evidence of ACES", never "no ACES".
            foreach (var go in Resolve.AllGameObjects())
            {
                foreach (var component in go.GetComponents<Component>())
                {
                    if (component == null) continue;
                    if (component.GetType().FullName != "UnityEngine.Rendering.Volume") continue;

                    var profileProp = component.GetType().GetProperty("sharedProfile") ??
                                      component.GetType().GetProperty("profile");
                    if (profileProp == null) continue;

                    object profile;
                    try { profile = profileProp.GetValue(component, null); }
                    catch { continue; }
                    if (profile == null) continue;

                    var componentsProp = profile.GetType().GetField("components");
                    if (componentsProp == null) continue;

                    var list = componentsProp.GetValue(profile) as System.Collections.IEnumerable;
                    if (list == null) continue;

                    foreach (var item in list)
                    {
                        if (item == null) continue;
                        if (item.GetType().Name != "Tonemapping") continue;
                        var modeField = item.GetType().GetField("mode");
                        if (modeField == null) continue;
                        var mode = modeField.GetValue(item);
                        if (mode == null) continue;
                        var valueProp = mode.GetType().GetProperty("value");
                        var value = valueProp == null ? mode : valueProp.GetValue(mode, null);
                        if (value != null && value.ToString().IndexOf("ACES", StringComparison.OrdinalIgnoreCase) >= 0)
                            return true;
                    }
                }
            }
            return false;
        }
    }
}
