using System.Reflection;
using System.Text;
using System.Text.Json.Nodes;
using Umcp.Daemon.Generated;

namespace Umcp.Daemon.Mcp;

public sealed class SkillNode
{
    public required string Id { get; init; }
    public required string Title { get; init; }
    public string? Parent { get; init; }
    public string Covers { get; init; } = "";
    public string Excludes { get; init; } = "";
    /// <summary>Explicit tool ids, or empty to take every tool whose Skill matches this node.</summary>
    public string[] Tools { get; init; } = Array.Empty<string>();
    public required string Body { get; init; }
}

/// <summary>
/// The tool surface as an authored tree rather than a flat list.
///
/// Two things make this different from generic tool search. The tree is **authored**, so it can say
/// where to look instead of making the model guess keywords across hundreds of similar strings.
/// And each node carries **guidance**, not just schemas — including facts about the project that is
/// actually connected, which a schema can never express.
///
/// Baseline cost is six tools. A node is loaded only when the model asks for it.
/// </summary>
public sealed class SkillTree
{
    readonly Dictionary<string, SkillNode> _nodes = new(StringComparer.OrdinalIgnoreCase);
    readonly Bm25Index _index = new();

    public SkillTree()
    {
        LoadEmbedded();
        BuildIndex();
    }

    public IReadOnlyCollection<SkillNode> Nodes => _nodes.Values;
    public Bm25Index Index => _index;

    public SkillNode? Get(string id) => _nodes.GetValueOrDefault(id);

    void LoadEmbedded()
    {
        var asm = Assembly.GetExecutingAssembly();
        foreach (var name in asm.GetManifestResourceNames().Where(n => n.EndsWith(".md", StringComparison.Ordinal)))
        {
            using var stream = asm.GetManifestResourceStream(name);
            if (stream is null) continue;
            using var reader = new StreamReader(stream, Encoding.UTF8);
            var text = reader.ReadToEnd();

            // Resource names look like Umcp.Daemon.Skills.material.md
            var id = name;
            var marker = ".Skills.";
            var at = id.IndexOf(marker, StringComparison.Ordinal);
            if (at < 0) continue;
            id = id[(at + marker.Length)..^3];

            var node = ParseNode(id, text);
            _nodes[node.Id] = node;
        }
    }

    static SkillNode ParseNode(string id, string text)
    {
        var header = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var lines = text.Replace("\r\n", "\n").Split('\n');
        var i = 0;
        for (; i < lines.Length; i++)
        {
            var line = lines[i];
            if (line.Trim().Length == 0) { i++; break; }
            var colon = line.IndexOf(':');
            if (colon <= 0) break;
            header[line[..colon].Trim()] = line[(colon + 1)..].Trim();
        }

        return new SkillNode
        {
            Id = id,
            Title = header.GetValueOrDefault("title", id),
            // A dotted id nests by construction (scene.query under scene); anything else says so
            // in its own header. Grouping is what keeps the map affordable as domains are added.
            Parent = Blank(header.GetValueOrDefault("parent", id.Contains('.') ? id[..id.LastIndexOf('.')] : null)),
            Covers = header.GetValueOrDefault("covers", ""),
            Excludes = header.GetValueOrDefault("excludes", ""),
            Tools = header.GetValueOrDefault("tools", "")
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            Body = string.Join("\n", lines[i..]).Trim()
        };
    }

    static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    void BuildIndex()
    {
        foreach (var node in _nodes.Values)
            _index.Add(new SearchDoc(node.Id, "skill", node.Title,
                node.Covers + " " + node.Excludes + " " + node.Body, Boost: 1.4));

        foreach (var tool in ToolCatalog.All)
        {
            var schema = JsonNode.Parse(tool.InputSchema);
            var paramText = new StringBuilder();
            if (schema?["properties"] is JsonObject props)
                foreach (var (key, spec) in props)
                    paramText.Append(key).Append(' ').Append((string?)spec?["description"]).Append(' ');

            _index.Add(new SearchDoc(tool.Id, "tool", tool.Id,
                tool.Summary + " " + tool.Skill + " " + paramText));
        }
        _index.Build();
    }

    /// <summary>A node that documents Tier-0 behaviour and owns no catalog tools says so explicitly.</summary>
    public static bool IsGuidanceOnly(SkillNode node) =>
        node.Tools.Length == 1 && node.Tools[0].Equals("none", StringComparison.OrdinalIgnoreCase);

    public ToolEntry[] ToolsFor(SkillNode node)
    {
        if (IsGuidanceOnly(node)) return Array.Empty<ToolEntry>();

        if (node.Tools.Length > 0)
            return node.Tools.Select(id => ToolCatalog.ById.GetValueOrDefault(id))
                             .Where(t => t is not null).Select(t => t!).ToArray();

        return ToolCatalog.All
            .Where(t => t.Skill.Equals(node.Id, StringComparison.OrdinalIgnoreCase))
            .ToArray();
    }

    public SkillNode[] ChildrenOf(string id) =>
        _nodes.Values.Where(n => string.Equals(n.Parent, id, StringComparison.OrdinalIgnoreCase))
                     .OrderBy(n => n.Id, StringComparer.Ordinal).ToArray();

    public SkillNode[] Roots() =>
        _nodes.Values.Where(n => n.Parent is null).OrderBy(n => n.Id, StringComparer.Ordinal).ToArray();

    /// <summary>
    /// Splice live project facts into authored prose. The render-pipeline caveats that apply are
    /// the ones for the pipeline this project actually uses — detected, not assumed.
    /// </summary>
    public static string Render(string body, IReadOnlyDictionary<string, string> facts)
    {
        foreach (var (key, value) in facts)
            body = body.Replace("{{" + key + "}}", value, StringComparison.Ordinal);
        // Any placeholder we could not fill becomes an honest "unknown" rather than a raw token.
        while (true)
        {
            var open = body.IndexOf("{{", StringComparison.Ordinal);
            if (open < 0) break;
            var close = body.IndexOf("}}", open, StringComparison.Ordinal);
            if (close < 0) break;
            body = body.Remove(open, close - open + 2).Insert(open, "unknown (no editor connected)");
        }
        return body;
    }
}
