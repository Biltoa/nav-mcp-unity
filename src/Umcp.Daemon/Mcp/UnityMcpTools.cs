using System.Collections.Concurrent;
using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Server;
using Umcp.Daemon.Agent;
using Umcp.Daemon.Generated;
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

    // project.info per (project, epoch): a domain reload is the only thing that can change it.
    readonly ConcurrentDictionary<string, Dictionary<string, string>> _facts = new();

    public UnityMcpTools(Dispatcher dispatcher, EditorRegistry registry, DaemonOptions options, SkillTree skills)
    {
        _dispatcher = dispatcher;
        _registry = registry;
        _options = options;
        _skills = skills;
    }

    // ------------------------------------------------------------------ execution

    [McpServerTool(Name = "unity_run")]
    [Description("Run one Unity Editor tool by id, e.g. \"scene.query\" or \"gameobject.create\". Find ids with unity_find; get a tool's schema from unity_skill.")]
    public async Task<string> RunAsync(
        [Description("Tool id, e.g. \"gameobject.create\"")] string tool,
        [Description("Tool arguments, a JSON object")] JsonElement? args = null,
        [Description("Project id, when more than one editor is connected")] string? project = null,
        [Description("Validate and report what would happen, without applying it")] bool dryRun = false,
        [Description("Raise the 32 KB response cap for this call. Use only when you truly need the whole payload.")] int? maxResponseBytes = null,
        CancellationToken ct = default)
    {
        var result = await _dispatcher.RunToolAsync(tool, ToObject(args), project, dryRun, ct, maxResponseBytes).ConfigureAwait(false);
        return result.ToJsonString();
    }

    [McpServerTool(Name = "unity_batch")]
    [Description("Run many Editor operations in one Editor tick, as one undo group. The Editor drains its whole queue per tick, so 32 operations cost about the wall time of one. ops is [{\"op\":\"<tool id>\",\"args\":{...}}]; \"$1\" in a later op refers to op 1's result.")]
    public async Task<string> BatchAsync(
        [Description("Array of {op, args} objects")] JsonElement ops,
        [Description("Revert the whole group if any op fails")] bool atomic = false,
        [Description("How much of each result to return: none | ids | summary | full")] string returns = "ids",
        [Description("Name shown in Unity's undo history")] string? undoName = null,
        [Description("Project id, when more than one editor is connected")] string? project = null,
        [Description("Validate without applying")] bool dryRun = false,
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

        var result = await _dispatcher.RunBatchAsync(batch, project, ct).ConfigureAwait(false);
        return result.ToJsonString();
    }

    [McpServerTool(Name = "unity_script")]
    [Description("Run C# inside the Editor and return only what the code returns. Use this whenever the task needs a loop, a filter, a conditional or an aggregate — one round trip instead of N tool results. Engine and Editor namespaces are already imported; a bare expression is returned automatically. Requires the \"full\" profile.")]
    public async Task<string> ScriptAsync(
        [Description("C# statements. Return an anonymous object, array or primitive — a summary, not raw rows.")] string code,
        [Description("Project id, when more than one editor is connected")] string? project = null,
        CancellationToken ct = default)
    {
        var result = await _dispatcher.RunScriptAsync(code, project, ct).ConfigureAwait(false);
        return result.ToJsonString();
    }

    // ------------------------------------------------------------------ discovery

    [McpServerTool(Name = "unity_find", ReadOnly = true)]
    [Description("Search the tool catalog and the skill tree by keyword. Returns skill nodes to load and tool ids to run.")]
    public string Find(
        [Description("Keywords, e.g. \"assign material to renderer\"")] string query,
        [Description("Restrict to \"tool\" or \"skill\"")] string? kind = null,
        [Description("Maximum results (default 12)")] int limit = 12)
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
    [Description("Load a skill: guidance for a domain plus the schemas of its tools. Also accepts a single tool id for that tool's full schema and examples. Call with no argument for the map of domains.")]
    public async Task<string> SkillAsync(
        [Description("Skill id (\"material\", \"scene.query\"), or a tool id (\"gameobject.create\"). Omit for the map.")] string? id = null,
        [Description("Project id, when more than one editor is connected")] string? project = null,
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

    [McpServerTool(Name = "unity_projects", ReadOnly = true)]
    [Description("List connected Unity editors with their health, and optionally set the default target. Health is the last completed round trip, not socket state: a connected socket with a wedged Editor reports \"blocked\" and names the dialog when it can.")]
    public async Task<string> ProjectsAsync(
        [Description("Set this project id as the default target for later calls")] string? use = null,
        CancellationToken ct = default)
    {
        if (use is not null)
        {
            if (_registry.Get(use) is null)
                return Envelope.Error("E_NO_EDITOR", $"No connected editor for '{use}'.",
                    param: "use", value: use,
                    didYouMean: Fuzzy.Closest(use, _registry.Sessions.SelectMany(s => new[] { s.ProjectId, s.ProjectName }), 3),
                    hint: "Connected: " + string.Join(", ", _registry.Sessions.Select(s => $"{s.ProjectName} ({s.ProjectId})"))).ToJsonString();
            _registry.DefaultProjectId = use;
        }

        var editors = new JsonArray();
        foreach (var s in _registry.Sessions)
        {
            var probe = await s.ProbeControlAsync(ct: ct).ConfigureAwait(false);
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
                ["msSinceTick"] = tickAge,
                ["lastRoundTripMs"] = s.LastRoundTripMs,
                ["opsCompleted"] = s.OpsCompleted,
                ["controlChannel"] = probe is null ? "unreachable" : "ok"
            };

            if (tickAge >= _options.BlockedTickAge.TotalMilliseconds)
            {
                var titles = WindowInspector.DialogTitles(s.UnityPid);
                if (titles.Length > 0) o["blockingWindow"] = titles[0];
            }
            editors.Add(o);
        }

        return Envelope.Ok(new JsonObject
        {
            ["editors"] = editors,
            ["daemon"] = new JsonObject
            {
                ["pid"] = Environment.ProcessId,
                ["uptimeSec"] = (long)(DateTime.UtcNow - DaemonInfo.StartedUtc).TotalSeconds,
                ["profile"] = Profiles.Name(_options.Profile),
                ["tools"] = ToolCatalog.All.Length
            }
        }).ToJsonString();
    }

    string Health(AgentSession s, long? tickAge)
    {
        if (!s.Alive) return "gone";
        if (s.Reloading) return "reloading";
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
