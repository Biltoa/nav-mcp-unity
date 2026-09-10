using System.Collections.Concurrent;
using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using Umcp.Daemon.Agent;
using Umcp.Daemon.Generated;
using Umcp.Daemon.Mirror;
using Umcp.Daemon.Security;

namespace Umcp.Daemon.Mcp;

/// <summary>
/// Tier 0: the entire always-loaded MCP surface. Six tools, not 356.
///
/// Serialising name + description + inputSchema for the 356 tools of the implementation being
/// replaced costs ~227 KB, about 56,800 tokens, before the model has done anything at all. The
/// Unity-side catalog here is just as capable and costs nothing at baseline: it is searched with
/// <c>unity_find</c>, explained by <c>unity_skill</c>, and reached through <c>unity_run</c>,
/// <c>unity_batch</c> and <c>unity_script</c>.
/// </summary>
[McpServerToolType]
public sealed class UnityMcpTools
{
    readonly Dispatcher _dispatcher;
    readonly EditorRegistry _registry;
    readonly DaemonOptions _options;
    readonly SkillTree _skills;
    readonly MirrorService _mirror;
    readonly Fleet.FleetService _fleet;

    // project.info per (project, epoch): a domain reload is the only thing that can change it.
    readonly ConcurrentDictionary<string, Dictionary<string, string>> _facts = new();

    public UnityMcpTools(Dispatcher dispatcher, EditorRegistry registry, DaemonOptions options,
                         SkillTree skills, MirrorService mirror, Fleet.FleetService fleet)
    {
        _mirror = mirror;
        _fleet = fleet;
        _dispatcher = dispatcher;
        _registry = registry;
        _options = options;
        _skills = skills;
    }

    // ------------------------------------------------------------------ execution

    [McpServerTool(Name = "unity_run")]
    [Description("Run one Editor tool by id. Ids from unity_find, schemas from unity_skill.")]
    public async Task<string> RunAsync(
        [Description("Tool id")] string tool,
        [Description("Arguments, a JSON object")] JsonElement? args = null,
        [Description("Project id, when several editors are connected")] string? project = null,
        [Description("Report what would happen; apply nothing")] bool dryRun = false,
        [Description("Raise the 32 KB response cap for this call")] int? maxResponseBytes = null,
        [Description("Force a live read instead of the mirror")] bool verify = false,
        IProgress<ProgressNotificationValue>? progress = null,
        CancellationToken ct = default)
    {
        using var linked = RequestAbort.Link(ct);
        var result = await _dispatcher
            .RunToolAsync(tool, ToObject(args), project, dryRun, linked.Token, maxResponseBytes, verify, Sink(progress))
            .ConfigureAwait(false);
        return result.ToJsonString();
    }

    [McpServerTool(Name = "unity_batch")]
    [Description("Many Editor ops in one tick, one undo group: 32 ops cost about the wall time of one. ops is [{\"op\":\"<id>\",\"args\":{}}]; \"$1\" refers to op 1's result.")]
    public async Task<string> BatchAsync(
        [Description("The ops")] JsonElement ops,
        [Description("Revert the group if any op fails")] bool atomic = false,
        [Description("Per-op result: none | ids | summary | full")] string returns = "ids",
        [Description("Undo-history name")] string? undoName = null,
        [Description("Project id, when several editors are connected")] string? project = null,
        [Description("Validate without applying")] bool dryRun = false,
        IProgress<ProgressNotificationValue>? progress = null,
        CancellationToken ct = default)
    {
        if (ops.ValueKind != JsonValueKind.Array)
            return Envelope.Error("E_ARG_TYPE", "'ops' must be an array of {op, args} objects.",
                param: "ops",
                hint: "For example: [{\"op\":\"gameobject.create\",\"args\":{\"name\":\"A\"}}]").ToJsonString();

        var batch = new JsonObject
        {
            ["ops"] = JsonNode.Parse(ops.GetRawText()),
            ["atomic"] = atomic,
            ["returns"] = returns,
            ["dryRun"] = dryRun
        };
        if (undoName is not null) batch["undoName"] = undoName;

        using var linked = RequestAbort.Link(ct);
        var result = await _dispatcher.RunBatchAsync(batch, project, linked.Token, Sink(progress)).ConfigureAwait(false);
        return result.ToJsonString();
    }

    [McpServerTool(Name = "unity_script")]
    [Description("Run C# in the Editor; returns only what the code returns. Use for any loop, filter or aggregate. Engine and Editor namespaces imported; a bare expression is returned. Needs the \"full\" profile.")]
    public async Task<string> ScriptAsync(
        [Description("C# statements. Return a summary, not raw rows.")] string code,
        [Description("Project id, when several editors are connected")] string? project = null,
        CancellationToken ct = default)
    {
        using var linked = RequestAbort.Link(ct);
        var result = await _dispatcher.RunScriptAsync(code, project, linked.Token).ConfigureAwait(false);
        return result.ToJsonString();
    }

    // ------------------------------------------------------------------ discovery

    [McpServerTool(Name = "unity_find", ReadOnly = true)]
    [Description("Search tools and skills by keyword.")]
    public string Find(
        [Description("Keywords")] string query,
        [Description("Restrict to tool or skill")] string? kind = null,
        [Description("Maximum results")] int limit = 12)
    {
        var hits = _skills.Index.Search(query, Math.Clamp(limit, 1, 40), kind);
        var items = new JsonArray();
        foreach (var hit in hits)
        {
            var o = new JsonObject { ["id"] = hit.Doc.Id, ["kind"] = hit.Doc.Kind, ["score"] = Math.Round(hit.Score, 2) };
            if (hit.Doc.Kind == "tool" && ToolCatalog.ById.TryGetValue(hit.Doc.Id, out var t))
            {
                o["summary"] = t.Summary;
                o["mutating"] = t.Mutating;
            }
            else
            {
                o["summary"] = hit.Doc.Title;
            }
            items.Add(o);
        }

        return Envelope.Ok(new JsonObject
        {
            ["items"] = items,
            ["_returned"] = items.Count,
            ["_hint"] = "unity_skill(<skill id>) loads guidance and schemas; unity_skill(<tool id>) gives one tool's schema."
        }).ToJsonString();
    }

    [McpServerTool(Name = "unity_skill", ReadOnly = true)]
    [Description("Load a domain skill: guidance plus its tools' schemas. A tool id gives that tool alone. No argument gives the map.")]
    public async Task<string> SkillAsync(
        [Description("Skill id, or a tool id. Omit for the map.")] string? id = null,
        [Description("Project id, when several editors are connected")] string? project = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(id)) return Map();

        if (ToolCatalog.ById.TryGetValue(id, out var tool)) return ToolDetail(tool);

        var node = _skills.Get(id);
        if (node is null)
        {
            var candidates = _skills.Nodes.Select(n => n.Id).Concat(ToolCatalog.All.Select(t => t.Id));
            return Envelope.Error("E_SKILL_NOT_FOUND", $"No skill or tool '{id}'.",
                param: "id", value: id,
                didYouMean: Fuzzy.Closest(id, candidates, 4),
                hint: "Skills: " + string.Join(", ", _skills.Nodes.Select(n => n.Id).OrderBy(x => x))).ToJsonString();
        }

        var facts = await FactsAsync(project, ct).ConfigureAwait(false);
        var tools = _skills.ToolsFor(node);
        var schemas = new JsonArray();
        foreach (var t in tools)
            schemas.Add(new JsonObject
            {
                ["id"] = t.Id,
                ["summary"] = t.Summary,
                ["mutating"] = t.Mutating,
                ["undo"] = t.Undo ?? t.NoUndoReason,
                ["inputSchema"] = JsonNode.Parse(t.InputSchema),
                ["examples"] = new JsonArray(t.Examples.Select(e => (JsonNode)e!).ToArray())
            });

        var children = _skills.ChildrenOf(node.Id).Select(c => (JsonNode)new JsonObject
        {
            ["id"] = c.Id,
            ["title"] = c.Title
        }).ToArray();

        return Envelope.Ok(new JsonObject
        {
            ["id"] = node.Id,
            ["title"] = node.Title,
            ["covers"] = node.Covers,
            ["excludes"] = node.Excludes,
            ["guidance"] = SkillTree.Render(node.Body, facts),
            ["subSkills"] = new JsonArray(children),
            ["tools"] = schemas
        }).ToJsonString();
    }

    string Map()
    {
        var roots = new JsonArray();
        foreach (var n in _skills.Roots())
            roots.Add(new JsonObject
            {
                ["id"] = n.Id,
                ["title"] = n.Title,
                ["covers"] = n.Covers,
                ["tools"] = _skills.ToolsFor(n).Length,
                ["subSkills"] = new JsonArray(_skills.ChildrenOf(n.Id).Select(c => (JsonNode)c.Id!).ToArray())
            });

        return Envelope.Ok(new JsonObject
        {
            ["skills"] = roots,
            ["totalTools"] = ToolCatalog.All.Length,
            ["profile"] = Profiles.Name(_options.Profile),
            ["_hint"] = "unity_skill(\"<id>\") loads one. unity_find searches everything."
        }).ToJsonString();
    }

    static string ToolDetail(ToolEntry entry) => Envelope.Ok(new JsonObject
    {
        ["id"] = entry.Id,
        ["summary"] = entry.Summary,
        ["skill"] = entry.Skill,
        ["mutating"] = entry.Mutating,
        ["retry"] = entry.Retry,
        ["cost"] = entry.Cost,
        ["undo"] = entry.Undo ?? entry.NoUndoReason,
        ["undoable"] = entry.Undo is not null,
        ["inputSchema"] = JsonNode.Parse(entry.InputSchema),
        ["examples"] = new JsonArray(entry.Examples.Select(e => (JsonNode)e!).ToArray())
    }).ToJsonString();

    // ------------------------------------------------------------------ editors and health

    [McpServerTool(Name = "unity_projects")]
    [Description("Editors, compile/reload state, health, and the fleet: open, close, restart, set the default. Reads remain available during reload; writes wait. Health is the last completed round trip: \"blocked\" means a modal has wedged the Editor and a human must dismiss it.")]
    public async Task<string> ProjectsAsync(
        [Description("Make this project the default target")] string? use = null,
        [Description("Check the mirror against the live hierarchy and repair drift")] bool reconcile = false,
        [Description("Launch an Editor for this path")] string? open = null,
        [Description("Quit this editor (id or path)")] string? close = null,
        [Description("Close then reopen; works after a crash too")] string? restart = null,
        [Description("Save scenes before closing")] bool save = false,
        [Description("Close even with unsaved changes, discarding them")] bool discard = false,
        [Description("Reopen this Editor if its process dies")] bool? autoRestart = null,
        [Description("Also list projects on disk and Editor installs")] bool discover = false,
        CancellationToken ct = default)
    {
        // The fleet verbs are exclusive: doing two of them in one call would make the result
        // ambiguous about which one failed.
        var verbs = new[] { open, close, restart }.Count(v => v is not null);
        if (verbs > 1)
            return Envelope.Error("E_ARG_CONFLICT", "Pass only one of open, close or restart per call.").ToJsonString();

        if (open is not null)
            return (await _fleet.OpenAsync(open, null, installAgent: true, wait: true, _options.OpenTimeout, ct)
                                .ConfigureAwait(false)).ToJsonString();

        if (close is not null)
            return (await _fleet.CloseAsync(close, save, discard, ct).ConfigureAwait(false)).ToJsonString();

        if (restart is not null)
            return (await _fleet.RestartAsync(restart, save, discard, ct).ConfigureAwait(false)).ToJsonString();

        if (autoRestart is not null && use is not null)
            return _fleet.SetAutoRestart(use, autoRestart.Value).ToJsonString();

        if (use is not null)
        {
            if (_registry.Get(use) is null)
                return Envelope.Error("E_NO_EDITOR", $"No connected editor for '{use}'.",
                    param: "use", value: use,
                    didYouMean: Fuzzy.Closest(use, _registry.Sessions.SelectMany(s => new[] { s.ProjectId, s.ProjectName }), 3),
                    hint: "Connected: " + string.Join(", ", _registry.Sessions.Select(s => $"{s.ProjectName} ({s.ProjectId})"))).ToJsonString();
            _registry.DefaultProjectId = use;
        }

        if (autoRestart is not null)
        {
            var target = _registry.DefaultProjectId;
            if (target is null)
                return Envelope.Error("E_NO_EDITOR", "autoRestart needs a project: pass it with use, or connect an editor first.",
                    param: "autoRestart").ToJsonString();
            var set = _fleet.SetAutoRestart(target, autoRestart.Value);
            if ((bool?)set["ok"] != true) return set.ToJsonString();
        }

        var editors = new JsonArray();
        foreach (var s in _registry.StatusSessions)
        {
            // A retained reload snapshot is deliberately disconnected and its old control
            // listener is already gone. Probing it can spend the full two-second connect timeout
            // to rediscover what Reloading/Alive already tell us.
            var probe = s.Alive && !s.Reloading
                ? await s.ProbeControlAsync(ct: ct).ConfigureAwait(false)
                : null;
            var tickAge = (long?)probe?["msSinceTick"];

            var o = new JsonObject
            {
                ["projectId"] = s.ProjectId,
                ["name"] = s.ProjectName,
                ["path"] = s.ProjectPath,
                ["unityVersion"] = s.UnityVersion,
                ["pid"] = s.UnityPid,
                ["epoch"] = s.Epoch,
                ["isDefault"] = s.ProjectId == _registry.DefaultProjectId,
                ["health"] = Health(s, tickAge),
                ["connected"] = s.Alive,
                ["compiling"] = s.Compiling,
                ["reloading"] = s.Reloading,
                ["inFlight"] = s.InFlight,
                ["msSinceTick"] = tickAge,
                ["lastRoundTripMs"] = s.LastRoundTripMs,
                ["opsCompleted"] = s.OpsCompleted,
                ["controlChannel"] = probe is null ? "unreachable" : "ok",
                ["mirror"] = _mirror.For(s.ProjectId).StatusJson()
            };

            if (reconcile)
                o["reconcile"] = await _mirror.ReconcileAsync(s, ct).ConfigureAwait(false);

            if (tickAge >= _options.BlockedTickAge.TotalMilliseconds)
            {
                var titles = WindowInspector.DialogTitles(s.UnityPid);
                if (titles.Length > 0) o["blockingWindow"] = titles[0];
            }
            editors.Add(o);
        }

        var data = new JsonObject
        {
            ["editors"] = editors,
            ["daemon"] = new JsonObject
            {
                ["pid"] = Environment.ProcessId,
                ["uptimeSec"] = (long)(DateTime.UtcNow - DaemonInfo.StartedUtc).TotalSeconds,
                ["profile"] = Profiles.Name(_options.Profile),
                ["tools"] = ToolCatalog.All.Length
            }
        };

        // Discovery is opt-in because it touches the filesystem and the Hub's database, and
        // because a list of 40 projects is not what a caller asking "is Unity alive" wants.
        var fleet = _fleet.List(includeKnownProjects: discover, includeInstalls: discover);
        foreach (var (k, v) in fleet.ToArray()) { fleet.Remove(k); data[k] = v; }

        return Envelope.Ok(data).ToJsonString();
    }

    string Health(AgentSession s, long? tickAge)
    {
        if (s.Reloading) return "reloading";
        if (!s.Alive) return "gone";
        if (tickAge is null) return s.MsSinceLastResponse < 5000 ? "ok" : "unknown";
        if (tickAge >= _options.BlockedTickAge.TotalMilliseconds) return "blocked";
        if (tickAge >= 5000) return "degraded";
        return "ok";
    }

    // ------------------------------------------------------------------ project facts

    /// <summary>
    /// Facts spliced into authored guidance, so the caveats a skill gives are the ones that apply
    /// to the project that is actually open — detected, not assumed.
    /// </summary>
    async Task<Dictionary<string, string>> FactsAsync(string? project, CancellationToken ct)
    {
        var session = _registry.Get(project);
        if (session is null) return new Dictionary<string, string>();

        var key = session.ProjectId + "@" + session.Epoch;
        if (_facts.TryGetValue(key, out var cached)) return cached;

        var info = await _dispatcher.RunToolAsync("project.info", new JsonObject(), project, false, ct)
                                    .ConfigureAwait(false);
        var facts = new Dictionary<string, string>();
        if ((bool?)info["ok"] == true && info["data"] is JsonObject d)
        {
            facts["project"] = (string?)d["name"] ?? session.ProjectName;
            facts["pipeline"] = (string?)d["renderPipeline"] ?? "unknown";
            facts["unityVersion"] = (string?)d["unityVersion"] ?? session.UnityVersion;
            facts["platform"] = (string?)d["platform"] ?? "unknown";
            _facts[key] = facts;
        }
        return facts;
    }

    /// <summary>
    /// Bridge the dispatcher's progress to MCP's, when the client asked for progress at all.
    ///
    /// Progress exists here for one situation: an operation held across a domain reload or an
    /// auto-restart takes tens of seconds, and a client with no signal cannot tell that from a
    /// hang. It is never used to narrate work that is already fast.
    /// </summary>
    static IProgress<OpProgress>? Sink(IProgress<ProgressNotificationValue>? progress)
    {
        if (progress is null) return null;
        return new Progress<OpProgress>(p => progress.Report(new ProgressNotificationValue
        {
            Progress = p.Percent,
            Total = 100,
            Message = $"{p.Label}: {p.Message} ({p.ElapsedMs} ms)"
        }));
    }

    static JsonObject ToObject(JsonElement? args)
    {
        if (args is null || args.Value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined) return new JsonObject();
        if (args.Value.ValueKind != JsonValueKind.Object) return new JsonObject();
        return JsonNode.Parse(args.Value.GetRawText()) as JsonObject ?? new JsonObject();
    }
}

public static class DaemonInfo
{
    public static readonly DateTime StartedUtc = DateTime.UtcNow;
}
