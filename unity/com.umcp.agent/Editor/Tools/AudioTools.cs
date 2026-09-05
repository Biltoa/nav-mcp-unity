using System;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace Umcp.Agent
{
    /// <summary>
    /// Audio: the project's output configuration, and the per-clip import settings that decide
    /// what a build actually ships.
    ///
    /// Import settings are the half that matters and the half nobody looks at. A 40 MB WAV set to
    /// Decompress On Load costs its whole size in memory at scene load; the same clip as streamed
    /// Vorbis costs a buffer. <c>build.validateTarget</c> flags the platform-specific traps, and
    /// this tool is how they get fixed in bulk.
    /// </summary>
    internal static class AudioTools
    {
        [UnityTool(Skill = "audio", Id = "audio.settings",
            Summary = "Project audio configuration: output rate, DSP buffer, virtual and real voices, spatializer.",
            Retry = RetryClass.Read)]
        [Example("{ }")]
        public static object Settings()
        {
            var config = AudioSettings.GetConfiguration();
            return new
            {
                sampleRate = config.sampleRate,
                dspBufferSize = config.dspBufferSize,
                numRealVoices = config.numRealVoices,
                numVirtualVoices = config.numVirtualVoices,
                speakerMode = config.speakerMode.ToString(),
                driverCapabilities = AudioSettings.driverCapabilities.ToString(),
                spatializerPlugin = AudioSettings.GetSpatializerPluginName(),
                listeners = Resolve.AllGameObjects().Count(go => go.GetComponent<AudioListener>() != null),
                sources = Resolve.AllGameObjects().Count(go => go.GetComponent<AudioSource>() != null),
                _hint = Resolve.AllGameObjects().Count(go => go.GetComponent<AudioListener>() != null) > 1
                    ? "More than one AudioListener is active; Unity warns and uses one of them arbitrarily."
                    : null
            };
        }

        [UnityTool(Skill = "audio", Id = "audio.clips",
            Summary = "List audio clips with the import settings that decide memory and quality: load type, compression, sample rate.",
            Retry = RetryClass.Read, Cost = Cost.Moderate)]
        [Example("{ \"limit\": 25 }")]
        [Example("{ \"folder\": \"Assets/Audio/Music\", \"platform\": \"WebGL\" }")]
        public static object Clips(
            [Doc("Restrict to this folder")] string folder = null,
            [Doc("Report the override for this platform, e.g. WebGL, Android. Default: the default settings.")] string platform = null,
            [Doc("Maximum clips (default 50)")] int limit = 50)
        {
            var folders = string.IsNullOrEmpty(folder) ? null : new[] { folder.Replace('\\', '/') };
            var guids = folders == null
                ? AssetDatabase.FindAssets("t:AudioClip")
                : AssetDatabase.FindAssets("t:AudioClip", folders);

            int cap = Bounds.Limit(limit);
            var rows = guids.Take(cap).Select(guid =>
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                var importer = AssetImporter.GetAtPath(path) as AudioImporter;
                var clip = AssetDatabase.LoadAssetAtPath<AudioClip>(path);
                if (importer == null) return null;

                var settings = !string.IsNullOrEmpty(platform) && importer.ContainsSampleSettingsOverride(platform)
                    ? importer.GetOverrideSampleSettings(platform)
                    : importer.defaultSampleSettings;

                return (object)new
                {
                    path,
                    seconds = clip == null ? 0f : clip.length,
                    channels = clip == null ? 0 : clip.channels,
                    frequency = clip == null ? 0 : clip.frequency,
                    loadType = settings.loadType.ToString(),
                    compression = settings.compressionFormat.ToString(),
                    quality = settings.quality,
                    sampleRateSetting = settings.sampleRateSetting.ToString(),
                    overrideSampleRate = settings.sampleRateOverride,
                    forceToMono = importer.forceToMono,
                    loadInBackground = importer.loadInBackground,
                    platformOverride = !string.IsNullOrEmpty(platform) && importer.ContainsSampleSettingsOverride(platform)
                };
            }).Where(r => r != null).ToArray();

            return Res.Page(rows, guids.Length, 0, rows.Length);
        }

        [UnityTool(Skill = "audio", Id = "audio.setImportSettings",
            Summary = "Set audio import settings on one clip or a whole folder, optionally as a platform override.",
            Mutating = true, Retry = RetryClass.Write, Cost = Cost.Moderate,
            NoUndoReason = "Import settings are asset metadata; the AssetDatabase does not participate in Unity's undo stack.")]
        [Example("{ \"path\": \"Assets/Audio/Music\", \"loadType\": \"Streaming\", \"compression\": \"Vorbis\" }")]
        [Example("{ \"path\": \"Assets/Audio/SFX/Engine.wav\", \"platform\": \"WebGL\", \"overrideSampleRate\": 44100 }")]
        public static object SetImportSettings(
            [Doc("Clip path, or a folder to apply to every clip inside it")] string path,
            [Doc("DecompressOnLoad | CompressedInMemory | Streaming")] string loadType = null,
            [Doc("PCM | Vorbis | ADPCM | MP3 | AAC")] string compression = null,
            [Doc("Compression quality, 0-1")] float? quality = null,
            [Doc("Pin the sample rate to this value, in Hz. Use for WebGL loops.")] int? overrideSampleRate = null,
            [Doc("Apply as an override for this platform, e.g. WebGL")] string platform = null,
            [Doc("Force to mono")] bool? forceToMono = null,
            [Doc("Maximum clips to touch (default 200)")] int limit = 200)
        {
            if (string.IsNullOrEmpty(path))
                throw new UmcpToolException("E_ARG_REQUIRED", "A clip or folder path is required.", "path");
            var normalised = path.Replace('\\', '/');

            var targets = AssetDatabase.IsValidFolder(normalised)
                ? AssetDatabase.FindAssets("t:AudioClip", new[] { normalised })
                              .Select(AssetDatabase.GUIDToAssetPath).Take(Bounds.Limit(limit) * 4).ToArray()
                : new[] { normalised };

            if (targets.Length == 0)
                throw new UmcpToolException("E_ASSET_NOT_FOUND", "No audio clips at '" + normalised + "'.",
                    "path", normalised, null, "Point at a .wav/.ogg/.mp3 asset or at a folder containing some.");

            var changed = 0;
            var touched = new System.Collections.Generic.List<string>();

            foreach (var target in targets)
            {
                var importer = AssetImporter.GetAtPath(target) as AudioImporter;
                if (importer == null) continue;

                var settings = !string.IsNullOrEmpty(platform) && importer.ContainsSampleSettingsOverride(platform)
                    ? importer.GetOverrideSampleSettings(platform)
                    : importer.defaultSampleSettings;

                if (loadType != null) settings.loadType = Parse<AudioClipLoadType>(loadType, "loadType");
                if (compression != null) settings.compressionFormat = Parse<AudioCompressionFormat>(compression, "compression");
                if (quality != null) settings.quality = Mathf.Clamp01(quality.Value);
                if (overrideSampleRate != null)
                {
                    // The WebGL fix from build.validateTarget: pinning the rate is what stops the
                    // AAC re-encode from adding padding and making a loop audibly jitter.
                    settings.sampleRateSetting = AudioSampleRateSetting.OverrideSampleRate;
                    settings.sampleRateOverride = (uint)Mathf.Max(8000, overrideSampleRate.Value);
                }
                if (forceToMono != null) importer.forceToMono = forceToMono.Value;

                if (string.IsNullOrEmpty(platform)) importer.defaultSampleSettings = settings;
                else importer.SetOverrideSampleSettings(platform, settings);

                importer.SaveAndReimport();
                changed++;
                if (touched.Count < 25) touched.Add(target);
            }

            return new
            {
                changed,
                platform = string.IsNullOrEmpty(platform) ? "default" : platform,
                sample = touched.ToArray(),
                _truncated = changed > touched.Count,
                _hint = "Reimporting audio is not undoable; the previous settings are only in version control."
            };
        }

        static T Parse<T>(string value, string param) where T : struct
        {
            T parsed;
            if (Enum.TryParse(value, true, out parsed)) return parsed;
            throw new UmcpToolException("E_ARG_VALUE", "Unknown " + param + " '" + value + "'.",
                param, value, Enum.GetNames(typeof(T)), null);
        }
    }
}
