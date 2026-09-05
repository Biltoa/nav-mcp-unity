using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Unity.Profiling;
using UnityEditor;
using UnityEngine;

namespace Umcp.Agent
{
    /// <summary>
    /// <c>profile.frame</c> — what the Editor is actually costing per frame, and
    /// <c>assets.addressables</c> — the Addressables catalog, when the package is installed.
    ///
    /// The profiler tool obeys the rule that no tool may change Editor state to satisfy a read:
    /// it does **not** enter Play mode. Entering Play mode to take a measurement is what lost the
    /// D3D12 device and crashed this Editor during the evaluation that started this project. What
    /// it reports is therefore Editor-loop cost — rendering the Scene and Game views, the
    /// AssetDatabase, and any Editor code — which is the right number for "what is making my
    /// Editor slow" and the wrong one for "what will my build run at". It says so in its own
    /// output rather than leaving that to be assumed.
    /// </summary>
    internal static class ProfileTools
    {
        const int Capacity = 120;

        static readonly List<ProfilerRecorder> _recorders = new List<ProfilerRecorder>();
        static readonly List<string> _names = new List<string>();
        static bool _started;

        static readonly (ProfilerCategory category, string name, string unit)[] Counters =
        {
            (ProfilerCategory.Internal,  "Main Thread",             "ms"),
            (ProfilerCategory.Render,    "Draw Calls Count",        "count"),
            (ProfilerCategory.Render,    "Batches Count",           "count"),
            (ProfilerCategory.Render,    "SetPass Calls Count",     "count"),
            (ProfilerCategory.Render,    "Triangles Count",         "count"),
            (ProfilerCategory.Render,    "Vertices Count",          "count"),
            (ProfilerCategory.Memory,    "GC Allocated In Frame",   "bytes"),
            (ProfilerCategory.Memory,    "System Used Memory",      "bytes"),
            (ProfilerCategory.Memory,    "Texture Memory",          "bytes")
        };

        [UnityTool(Skill = "diagnostics", Id = "profile.frame",
            Summary = "Editor frame cost: main-thread time, draw calls, batches, triangles, allocations, memory. Does not enter Play mode.",
            Retry = RetryClass.Read, Cost = Cost.Moderate)]
        [Example("{ }")]
        public static object Frame([Doc("How many recent frames to summarise (default 60)")] int frames = 60)
        {
            EnsureStarted();

            int want = Mathf.Clamp(frames <= 0 ? 60 : frames, 1, Capacity);
            var rows = new List<object>();
            int available = 0;

            for (int i = 0; i < _recorders.Count; i++)
            {
                var recorder = _recorders[i];
                if (!recorder.Valid) continue;

                int count = Mathf.Min(recorder.Count, want);
                available = Mathf.Max(available, count);
                if (count == 0)
                {
                    rows.Add(new { counter = _names[i], unit = Counters[i].unit, samples = 0, note = "no samples yet" });
                    continue;
                }

                // CopyTo takes the whole ring; "the last N frames" is a slice of it, not an
                // argument it accepts.
                var samples = new List<ProfilerRecorderSample>(recorder.Count);
                recorder.CopyTo(samples);
                var values = samples.Skip(Mathf.Max(0, samples.Count - count))
                                    .Select(s => (double)s.Value).ToArray();
                count = values.Length;

                double scale = Counters[i].unit == "ms" ? 1e-6 : 1.0;   // nanoseconds -> milliseconds
                rows.Add(new
                {
                    counter = _names[i],
                    unit = Counters[i].unit,
                    samples = count,
                    last = Math.Round(values[values.Length - 1] * scale, 3),
                    mean = Math.Round(values.Average() * scale, 3),
                    max = Math.Round(values.Max() * scale, 3)
                });
            }

            return new
            {
                mode = EditorApplication.isPlaying ? "play" : "edit",
                framesAvailable = available,
                framesRequested = want,
                counters = rows.ToArray(),
                _hint = available == 0
                    ? "Recorders were started by this call; the numbers arrive on subsequent frames. Call again."
                    : "Editor-loop cost, not player cost: this tool never enters Play mode, because doing so " +
                      "crashed this Editor during evaluation. For player numbers, build and profile the build."
            };
        }

        static void EnsureStarted()
        {
            if (_started) return;
            _started = true;
            foreach (var counter in Counters)
            {
                ProfilerRecorder recorder;
                try { recorder = ProfilerRecorder.StartNew(counter.category, counter.name, Capacity); }
                catch { continue; }
                _recorders.Add(recorder);
                _names.Add(counter.name);
            }
        }
    }

    /// <summary>
    /// Addressables, by reflection. The package is optional and most projects do not have it, so
    /// the tool reports its absence as an ordinary, actionable result rather than failing to
    /// compile the agent.
    /// </summary>
    internal static class AddressableTools
    {
        [UnityTool(Skill = "assets", Id = "assets.addressables",
            Summary = "Addressables settings, groups and entries. action: status | groups | entries.",
            Retry = RetryClass.Read, Cost = Cost.Moderate)]
        [Example("{ \"action\": \"status\" }")]
        [Example("{ \"action\": \"entries\", \"group\": \"Default Local Group\", \"limit\": 50 }")]
        public static object Addressables(
            [Doc("status | groups | entries (default status)")] string action = "status",
            [Doc("Group name, for action:entries")] string group = null,
            [Doc("Maximum entries (default 100)")] int limit = 100)
        {
            var settingsType = FindType("UnityEditor.AddressableAssets.AddressableAssetSettingsDefaultObject");
            if (settingsType == null)
                throw new UmcpToolException("E_PACKAGE_MISSING",
                    "The Addressables package is not installed in this project.", "action", action, null,
                    "Install com.unity.addressables from the Package Manager first.");

            var settingsProp = settingsType.GetProperty("Settings", BindingFlags.Public | BindingFlags.Static);
            var settings = settingsProp == null ? null : settingsProp.GetValue(null, null);
            if (settings == null)
                throw new UmcpToolException("E_NOT_CONFIGURED",
                    "Addressables is installed but this project has no settings asset yet.", "action", action, null,
                    "Window > Asset Management > Addressables > Groups, then Create Addressables Settings.");

            var groupsProp = settings.GetType().GetProperty("groups");
            var groups = groupsProp == null ? null : groupsProp.GetValue(settings, null) as System.Collections.IEnumerable;
            var groupList = groups == null ? new List<object>() : groups.Cast<object>().Where(g => g != null).ToList();

            int cap = Bounds.Limit(limit);
            var verb = (action ?? "status").ToLowerInvariant();

            if (verb == "status")
                return new
                {
                    installed = true,
                    groups = groupList.Count,
                    activeProfile = Read(settings, "activeProfileId"),
                    buildRemoteCatalog = Read(settings, "BuildRemoteCatalog"),
                    entries = groupList.Sum(g => EntriesOf(g).Count())
                };

            if (verb == "groups")
                return new
                {
                    count = groupList.Count,
                    groups = groupList.Take(cap).Select(g => new
                    {
                        name = Read(g, "Name"),
                        entries = EntriesOf(g).Count(),
                        readOnly = Read(g, "ReadOnly")
                    }).ToArray()
                };

            if (verb == "entries")
            {
                var chosen = string.IsNullOrEmpty(group)
                    ? groupList
                    : groupList.Where(g => string.Equals(Convert.ToString(Read(g, "Name")), group, StringComparison.OrdinalIgnoreCase)).ToList();

                if (chosen.Count == 0)
                    throw new UmcpToolException("E_GROUP_NOT_FOUND", "No Addressables group named '" + group + "'.",
                        "group", group,
                        groupList.Select(g => Convert.ToString(Read(g, "Name"))).ToArray(), null);

                var entries = chosen.SelectMany(g => EntriesOf(g).Select(e => new
                {
                    group = Convert.ToString(Read(g, "Name")),
                    address = Convert.ToString(Read(e, "address")),
                    path = Convert.ToString(Read(e, "AssetPath")),
                    guid = Convert.ToString(Read(e, "guid"))
                })).ToArray();

                return Res.Page(entries.Take(cap).ToArray(), entries.Length, 0, Math.Min(cap, entries.Length));
            }

            throw new UmcpToolException("E_ARG_VALUE", "Unknown action '" + action + "'.",
                "action", action, new[] { "status", "groups", "entries" }, null);
        }

        static IEnumerable<object> EntriesOf(object group)
        {
            var prop = group.GetType().GetProperty("entries");
            var value = prop == null ? null : prop.GetValue(group, null) as System.Collections.IEnumerable;
            if (value == null) return Enumerable.Empty<object>();
            return value.Cast<object>().Where(e => e != null);
        }

        static object Read(object target, string member)
        {
            if (target == null) return null;
            var type = target.GetType();
            var prop = type.GetProperty(member, BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static);
            if (prop != null) { try { return prop.GetValue(target, null); } catch { return null; } }
            var field = type.GetField(member, BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static);
            if (field != null) { try { return field.GetValue(target); } catch { return null; } }
            return null;
        }

        static Type FindType(string fullName)
        {
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type type;
                try { type = assembly.GetType(fullName, false); }
                catch { continue; }
                if (type != null) return type;
            }
            return null;
        }
    }
}
