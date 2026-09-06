using System.Text.Json.Nodes;
using Umcp.Daemon.Agent;
using Umcp.Daemon.Fleet;

namespace Umcp.Daemon.Api;

/// <summary>
/// The local control surface the GUI drives: link a project, open or close an Editor, pause, stop.
///
/// Deliberately not MCP. The GUI is not a model — it wants a list, a button and an answer, and
/// making a desktop app speak streamable-HTTP MCP to ask "which projects are linked" would be a
/// protocol tax paid on every click. It is the same process, the same bearer token and the same
/// loopback-only listener as <c>/mcp</c>, so it adds a shape, not a door.
///
/// Every handler returns <c>{ ok, ... }</c>, or <c>{ ok: false, message }</c> with a 4xx. The GUI
/// shows <c>message</c> to a non-technical user verbatim, so the messages here are written for a
/// person, not for a log.
/// </summary>
public static class ControlApi
{
    public static void MapControlApi(this WebApplication app)
    {
        var group = app.MapGroup("/api");

        // ---------------------------------------------------------------- state

        group.MapGet("/status", async (EditorRegistry registry, DaemonOptions opts, FleetService fleet, LinkedProjects linked, DaemonState state) =>
        {
            var report = await HealthReport.BuildAsync(registry, opts);
            report["paused"] = state.Paused;
            report["packagePath"] = AgentPackage.Locate(opts.PackagePath);
            report["tokenFile"] = Paths.TokenFile;
            report["logDir"] = Paths.LogDir;

            // A project the user linked, described as it is right now: does its manifest still
            // name the package, is an Editor holding it, has an agent ever connected.
            var projects = new JsonArray();
            foreach (var entry in linked.All())
            {
                var known = ProjectCatalog.Describe(entry.Path, source: "linked");
                var json = ProjectCatalog.ToJson(known);
                json["autoRestart"] = entry.AutoRestart;
                json["linkedAt"] = entry.LinkedAt.ToString("O");
                json["exists"] = Directory.Exists(entry.Path);
                json["isProject"] = ProjectCatalog.IsProject(entry.Path);
                json["agentResolved"] = AgentPackage.IsResolved(entry.Path);
                json["connected"] = known.ProjectId is not null &&
                                    registry.Sessions.Any(s => s.ProjectId == known.ProjectId && s.Alive);
                projects.Add(json);
            }
            report["projects"] = projects;

            var installs = new JsonArray();
            foreach (var i in EditorInstalls.Scan())
                installs.Add(new JsonObject { ["version"] = i.Version, ["path"] = i.ExePath });
            report["editorInstalls"] = installs;

            return Results.Json(report);
        });

        group.MapGet("/logs", (int? tail) =>
        {
            var lines = TailLines(Umcp.UmcpPaths.DaemonLog, Math.Clamp(tail ?? 200, 1, 2000));
            return Results.Json(new JsonObject
            {
                ["ok"] = true,
                ["file"] = Umcp.UmcpPaths.DaemonLog,
                ["lines"] = new JsonArray(lines.Select(l => (JsonNode)l!).ToArray())
            });
        });

        // What the AI has actually been doing, from the audit log — the one question a person
        // asks about a tool that edits their project while they are not looking. Read from the
        // tail of the file rather than kept in memory: it survives a daemon restart, and the
        // daemon already writes it for reasons that have nothing to do with this window.
        group.MapGet("/activity", (int? limit, EditorRegistry registry, LinkedProjects linked) =>
        {
            var want = Math.Clamp(limit ?? 40, 1, 200);

            // projectId is a GUID in the log; a person needs the folder's name.
            var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var session in registry.Sessions)
                if (session.ProjectId is { Length: > 0 } id) names[id] = session.ProjectName ?? id;
            foreach (var entry in linked.All())
                if (ProjectCatalog.ReadProjectId(entry.Path) is { Length: > 0 } id)
                    names[id] = Path.GetFileName(entry.Path.TrimEnd('/'));

            // Deep enough that a busy day is counted honestly, shallow enough to stay cheap.
            // When every line read is still inside the window the count is a floor, not a total,
            // and it says so rather than reporting the cap as if it were the answer.
            const int scan = 4000;
            var lines = TailLines(Paths.AuditLog, scan);
            var entries = new JsonArray();
            var durations = new List<double>();
            var since = DateTimeOffset.Now.AddHours(-24);
            var today = 0;
            var failures = 0;

            foreach (var line in lines)
            {
                JsonObject? row;
                try { row = JsonNode.Parse(line) as JsonObject; } catch { continue; }
                if (row is null) continue;

                var stamp = DateTimeOffset.TryParse((string?)row["ts"], out var t) ? t : (DateTimeOffset?)null;
                if (stamp >= since)
                {
                    today++;
                    if ((bool?)row["ok"] == false) failures++;
                    if (row["ms"] is not null && double.TryParse(row["ms"]!.ToString(), out var ms)) durations.Add(ms);
                }

                var projectId = (string?)row["projectId"] ?? "";
                entries.Add(new JsonObject
                {
                    ["ts"] = stamp?.ToString("O"),
                    ["tool"] = (string?)row["tool"],
                    ["ok"] = (bool?)row["ok"] ?? false,
                    ["ms"] = row["ms"]?.DeepClone(),
                    ["code"] = row["code"]?.DeepClone(),
                    ["project"] = names.TryGetValue(projectId, out var name) ? name : null
                });
            }

            // Newest first, and only as many as were asked for.
            var ordered = new JsonArray();
            for (var i = entries.Count - 1; i >= 0 && ordered.Count < want; i--)
            {
                var node = entries[i]!;
                entries.RemoveAt(i);
                ordered.Add(node);
            }

            durations.Sort();
            return Results.Json(new JsonObject
            {
                ["ok"] = true,
                ["entries"] = ordered,
                ["last24h"] = today,
                ["last24hCapped"] = lines.Length >= scan && today >= scan,
                ["failures24h"] = failures,
                // Median, not mean: one 40-second import would otherwise describe every other call.
                ["medianMs"] = durations.Count == 0 ? null : durations[durations.Count / 2]
            });
        });

        // ---------------------------------------------------------------- linking

        // Linking is the whole point of the GUI: it writes the file: dependency into the project's
        // manifest so a human never has to open manifest.json and get the JSON right.
        group.MapPost("/projects/link", (LinkRequest body, DaemonOptions opts, LinkedProjects linked) =>
        {
            var path = (body.Path ?? "").Trim();
            if (path.Length == 0) return Bad("No project folder given.");
            path = Path.GetFullPath(path);

            if (!Directory.Exists(path)) return Bad($"There is no folder at {path}.");
            if (!ProjectCatalog.IsProject(path))
                return Bad($"{path} is not a Unity project — a project folder has Assets and ProjectSettings inside it. " +
                           "If you picked the folder that contains your projects, choose the project itself.");

            var package = AgentPackage.Locate(opts.PackagePath);
            if (package is null)
                return Bad("Cannot find com.umcp.agent next to this daemon. Reinstall, or start umcpd with --package-path.");

            var (changed, detail) = AgentPackage.Ensure(path, package);
            var entry = linked.Add(path);

            // Unity resolves the manifest at startup, and a running Editor only re-resolves when
            // its window regains focus. Saying so here is the difference between "it works" and
            // "I clicked Link and nothing happened".
            var open = ProjectCatalog.IsLocked(path);
            return Results.Json(new JsonObject
            {
                ["ok"] = true,
                ["path"] = entry.Path,
                ["changed"] = changed,
                ["detail"] = detail,
                ["editorOpen"] = open,
                ["nextStep"] = open
                    ? "Unity is already running on this project — click its window once so it re-reads the package list."
                    : "Open the project in Unity; it picks the package up at startup."
            });
        });

        group.MapPost("/projects/unlink", (LinkRequest body, LinkedProjects linked) =>
        {
            var path = (body.Path ?? "").Trim();
            if (path.Length == 0) return Bad("No project folder given.");

            // Removing the dependency is what actually unlinks it; forgetting it in settings only
            // hides it from the list. Both, in that order.
            var removedDependency = Directory.Exists(path) && AgentPackage.Remove(path);
            var forgotten = linked.Remove(path);
            return Results.Json(new JsonObject
            {
                ["ok"] = forgotten || removedDependency,
                ["removedDependency"] = removedDependency,
                ["forgotten"] = forgotten
            });
        });

        // ---------------------------------------------------------------- the fleet

        group.MapPost("/projects/open", async (OpenRequest body, FleetService fleet, LinkedProjects linked, DaemonOptions opts, CancellationToken ct) =>
        {
            var path = (body.Path ?? "").Trim();
            if (path.Length == 0) return Bad("No project folder given.");
            if (!ProjectCatalog.IsProject(path)) return Bad($"{path} is not a Unity project folder.");

            linked.Add(path);
            var result = await fleet.OpenAsync(path, body.Version, installAgent: true, wait: body.Wait ?? false, opts.OpenTimeout, ct);
            return Results.Json(result);
        });

        group.MapPost("/projects/close", async (CloseRequest body, FleetService fleet, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(body.Project)) return Bad("No project given.");
            return Results.Json(await fleet.CloseAsync(body.Project!, body.Save ?? true, body.Force ?? false, ct));
        });

        group.MapPost("/projects/restart", async (CloseRequest body, FleetService fleet, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(body.Project)) return Bad("No project given.");
            return Results.Json(await fleet.RestartAsync(body.Project!, body.Save ?? true, body.Force ?? false, ct));
        });

        group.MapPost("/projects/autorestart", (AutoRestartRequest body, FleetService fleet, LinkedProjects linked) =>
        {
            if (string.IsNullOrWhiteSpace(body.Project)) return Bad("No project given.");
            var on = body.On ?? false;
            linked.SetAutoRestart(body.Project!, on);
            return Results.Json(fleet.SetAutoRestart(body.Project!, on));
        });

        // ---------------------------------------------------------------- the switch

        group.MapPost("/pause", (PauseRequest body, DaemonState state) =>
        {
            state.Paused = body.On ?? true;
            return Results.Json(new JsonObject { ["ok"] = true, ["paused"] = state.Paused });
        });

        // Stopping the daemon from the GUI's Stop button. The GUI usually owns the process and
        // could simply kill it, but it can also be attached to a daemon it did not start — one
        // launched at login, or by the shim — and killing someone else's process without asking
        // it to stop first is how in-flight work gets lost.
        group.MapPost("/quit", (IHostApplicationLifetime life) =>
        {
            _ = Task.Run(async () =>
            {
                await Task.Delay(150);   // let this response reach the caller first
                life.StopApplication();
            });
            return Results.Json(new JsonObject { ["ok"] = true, ["stopping"] = true });
        });
    }

    static IResult Bad(string message) =>
        Results.Json(new JsonObject { ["ok"] = false, ["message"] = message }, statusCode: 400);

    /// <summary>The last N lines of a file another process is appending to, or nothing.</summary>
    static string[] TailLines(string path, int count)
    {
        try
        {
            if (!File.Exists(path)) return Array.Empty<string>();
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            var ring = new Queue<string>(count);
            while (reader.ReadLine() is { } line)
            {
                if (ring.Count == count) ring.Dequeue();
                ring.Enqueue(line);
            }
            return ring.ToArray();
        }
        catch { return Array.Empty<string>(); }
    }

    public sealed record LinkRequest(string? Path);
    public sealed record OpenRequest(string? Path, string? Version, bool? Wait);
    public sealed record CloseRequest(string? Project, bool? Save, bool? Force);
    public sealed record AutoRestartRequest(string? Project, bool? On);
    public sealed record PauseRequest(bool? On);
}
