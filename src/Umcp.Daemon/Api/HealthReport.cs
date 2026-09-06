using System.Text.Json.Nodes;
using Umcp.Daemon.Agent;
using Umcp.Daemon.Generated;
using Umcp.Daemon.Mcp;

namespace Umcp.Daemon.Api;

/// <summary>
/// The one description of "how is it going" — served unauthenticated at <c>/health</c> and reused
/// by the control API the GUI polls. Two implementations of this would eventually disagree, and
/// the disagreement would be visible to a user as a GUI that says "ready" about an Editor that is
/// showing a modal dialog.
/// </summary>
public static class HealthReport
{
    /// <summary>
    /// Health is the last completed round trip, never "the socket is open". A socket stays open
    /// through a modal dialog, which is exactly the case a user needs told about.
    /// </summary>
    public static async Task<JsonObject> BuildAsync(EditorRegistry registry, DaemonOptions opts)
    {
        var editors = new JsonArray();
        foreach (var s in registry.Sessions)
        {
            var o = s.StatusJson();
            var probe = await s.ProbeControlAsync();
            var tickAge = (long?)probe?["msSinceTick"];
            o["msSinceTick"] = tickAge;
            o["health"] = tickAge is null
                ? (s.MsSinceLastResponse < 5000 ? "ok" : "unknown")
                : tickAge >= opts.BlockedTickAge.TotalMilliseconds ? "blocked"
                : tickAge >= 5000 ? "degraded" : "ok";

            // The GUI shows this verbatim: "blocked" alone tells a non-technical user nothing,
            // "blocked — Recovering Scene Backups" tells them where to click.
            if ((string?)o["health"] == "blocked" && s.UnityPid > 0)
            {
                var titles = WindowInspector.DialogTitles(s.UnityPid);
                if (titles.Length > 0) o["blockingDialog"] = titles[0];
            }
            editors.Add(o);
        }

        return new JsonObject
        {
            ["ok"] = true,
            ["daemon"] = new JsonObject
            {
                ["pid"] = Environment.ProcessId,
                ["version"] = BuildInfo.Version,
                ["startedUtc"] = DaemonInfo.StartedUtc.ToString("O"),
                ["uptimeSec"] = (long)(DateTime.UtcNow - DaemonInfo.StartedUtc).TotalSeconds,
                // Memory is reported because this process is expected to run for weeks. A number
                // nobody can see is a leak nobody finds.
                ["memory"] = new JsonObject
                {
                    ["workingSetMB"] = Environment.WorkingSet / (1024 * 1024),
                    ["privateMB"] = System.Diagnostics.Process.GetCurrentProcess().PrivateMemorySize64 / (1024 * 1024),
                    ["managedHeapMB"] = GC.GetTotalMemory(false) / (1024 * 1024),
                    ["gen2Collections"] = GC.CollectionCount(2)
                },
                ["httpPort"] = opts.HttpPort,
                ["agentPort"] = opts.AgentPort,
                ["tools"] = ToolCatalog.All.Length,
                ["profile"] = Security.Profiles.Name(opts.Profile)
            },
            ["editors"] = editors
        };
    }
}
