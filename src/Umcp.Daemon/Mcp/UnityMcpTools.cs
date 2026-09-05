using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Server;
using Umcp.Daemon.Agent;
using Umcp.Daemon.Generated;

namespace Umcp.Daemon.Mcp;

/// <summary>
/// The entire MCP surface. Six tools, not 356.
///
/// Serialising name + description + inputSchema for the 356 tools of the implementation being
/// replaced costs ~227 KB, about 56,800 tokens, before the model has done anything. The Unity-side
/// catalog here is just as large in capability and costs nothing at baseline: it is reached
/// through <c>unity.run</c> and searched with <c>unity.find</c>.
/// </summary>
[McpServerToolType]
public sealed class UnityMcpTools
{
    readonly Dispatcher _dispatcher;
    readonly EditorRegistry _registry;
    readonly DaemonOptions _options;

    public UnityMcpTools(Dispatcher dispatcher, EditorRegistry registry, DaemonOptions options)
    {
        _dispatcher = dispatcher;
        _registry = registry;
        _options = options;
    }

    [McpServerTool(Name = "unity_run")]
    [Description("Run one Unity Editor tool by id, e.g. \"gameobject.create\". Use unity_find to discover ids and unity_catalog for a tool's schema.")]
    public async Task<string> RunAsync(
        [Description("Tool id, e.g. \"gameobject.create\"")] string tool,
        [Description("Tool arguments as a JSON object")] JsonElement? args = null,
        [Description("Project id, when more than one editor is connected")] string? project = null,
        [Description("Validate and report what would happen without applying it")] bool dryRun = false,
        CancellationToken ct = default)
    {
        var obj = ToObject(args);
        var result = await _dispatcher.RunToolAsync(tool, obj, project, dryRun, ct).ConfigureAwait(false);
        return result.ToJsonString();
    }

    [McpServerTool(Name = "unity_batch")]
    [Description("Run many Editor operations in a single Editor tick, as one undo group. Far faster than N separate calls: the Editor drains its whole queue per tick, so 32 operations cost about the wall time of one. Ops are [{\"op\":\"<tool id>\",\"args\":{...}}]; \"$1\" in a later op's args refers to the result of op 1.")]
    public async Task<string> BatchAsync(
        [Description("Array of {op, args} objects")] JsonElement ops,
        [Description("Revert the whole group if any op fails")] bool atomic = false,
        [Description("\"none\" | \"ids\" | \"summary\" | \"full\" — how much of each result to return")] string returns = "ids",
        [Description("Name shown in Unity's undo history")] string? undoName = null,
        [Description("Project id, when more than one editor is connected")] string? project = null,
        [Description("Validate without applying")] bool dryRun = false,
        CancellationToken ct = default)
    {
        if (ops.ValueKind != JsonValueKind.Array)
            return Envelope.Error("E_ARG_TYPE", "'ops' must be an array of {op, args} objects.",
                param: "ops", hint: "For example: [{\"op\":\"gameobject.create\",\"args\":{\"name\":\"A\"}}]").ToJsonString();

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

    [McpServerTool(Name = "unity_find", ReadOnly = true)]
    [Description("Search the Unity tool catalog by keyword. Returns ids and one-line summaries.")]
    public string Find(
        [Description("Keywords, e.g. \"create material\"")] string query,
        [Description("Maximum results (default 15)")] int limit = 15)
    {
        var hits = CatalogSearch.Search(query, Math.Clamp(limit, 1, 50));
        var items = new JsonArray();
        foreach (var (entry, score) in hits)
            items.Add(new JsonObject
            {
                ["id"] = entry.Id,
                ["summary"] = entry.Summary,
                ["mutating"] = entry.Mutating,
                ["score"] = Math.Round(score, 3)
            });

        return Envelope.Ok(new JsonObject
        {
            ["items"] = items,
            ["_total"] = ToolCatalog.All.Length,
            ["_returned"] = items.Count,
            ["_hint"] = "unity_catalog gives the full schema and examples for an id."
        }).ToJsonString();
    }

    [McpServerTool(Name = "unity_catalog", ReadOnly = true)]
    [Description("Full schema, classification and worked examples for a tool id, or the ids in a family (e.g. \"gameobject\").")]
    public string Catalog([Description("Tool id or family prefix")] string id)
    {
        if (ToolCatalog.ById.TryGetValue(id, out var entry))
        {
            return Envelope.Ok(new JsonObject
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
        }

        var family = ToolCatalog.All
            .Where(e => e.Id.StartsWith(id + ".", StringComparison.OrdinalIgnoreCase) ||
                        e.Skill.Equals(id, StringComparison.OrdinalIgnoreCase))
            .ToArray();

        if (family.Length == 0)
            return Envelope.Error("E_TOOL_NOT_FOUND", $"No tool or family '{id}'.",
                param: "id", value: id,
                didYouMean: Fuzzy.Closest(id, ToolCatalog.All.Select(e => e.Id).Concat(ToolCatalog.All.Select(e => e.Skill).Distinct()), 4),
                hint: "Families: " + string.Join(", ", ToolCatalog.All.Select(e => e.Skill).Distinct().OrderBy(s => s))).ToJsonString();

        var items = new JsonArray();
        foreach (var e in family)
            items.Add(new JsonObject { ["id"] = e.Id, ["summary"] = e.Summary, ["mutating"] = e.Mutating });

        return Envelope.Ok(new JsonObject { ["family"] = id, ["items"] = items, ["_returned"] = items.Count }).ToJsonString();
    }

    [McpServerTool(Name = "unity_status", ReadOnly = true)]
    [Description("Daemon and Editor health. Health is the last completed round trip, not socket state: a connected socket with a wedged Editor reports blocked, and names the dialog when it can.")]
    public async Task<string> StatusAsync(
        [Description("Project id. Defaults to all connected editors.")] string? project = null,
        CancellationToken ct = default)
    {
        var sessions = project is null
            ? _registry.Sessions.ToArray()
            : _registry.Sessions.Where(s => s.ProjectId == project).ToArray();

        var editors = new JsonArray();
        foreach (var s in sessions)
        {
            var o = s.StatusJson();
            var probe = await s.ProbeControlAsync(ct: ct).ConfigureAwait(false);
            var tickAge = (long?)probe?["msSinceTick"];
            o["msSinceTick"] = tickAge;
            o["controlChannel"] = probe is null ? "unreachable" : "ok";
            o["health"] = Health(s, tickAge);
            if (tickAge >= _options.BlockedTickAge.TotalMilliseconds)
            {
                var titles = WindowInspector.DialogTitles(s.UnityPid);
                if (titles.Length > 0) o["blockingWindow"] = titles[0];
            }
            editors.Add(o);
        }

        return Envelope.Ok(new JsonObject
        {
            ["daemon"] = new JsonObject
            {
                ["pid"] = Environment.ProcessId,
                ["httpPort"] = _options.HttpPort,
                ["agentPort"] = _options.AgentPort,
                ["uptimeSec"] = (long)(DateTime.UtcNow - DaemonInfo.StartedUtc).TotalSeconds,
                ["tools"] = ToolCatalog.All.Length
            },
            ["editors"] = editors
        }).ToJsonString();
    }

    [McpServerTool(Name = "unity_projects", ReadOnly = true)]
    [Description("List connected Unity editors and set the default project for subsequent calls.")]
    public string Projects([Description("Set this project as the default target")] string? use = null)
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

        var items = new JsonArray();
        foreach (var s in _registry.Sessions)
            items.Add(new JsonObject
            {
                ["projectId"] = s.ProjectId,
                ["name"] = s.ProjectName,
                ["path"] = s.ProjectPath,
                ["unityVersion"] = s.UnityVersion,
                ["epoch"] = s.Epoch,
                ["isDefault"] = s.ProjectId == _registry.DefaultProjectId
            });

        return Envelope.Ok(new JsonObject { ["items"] = items, ["_returned"] = items.Count }).ToJsonString();
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
