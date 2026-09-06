using System.Text.Json.Nodes;
using Umcp.Daemon.Generated;

namespace Umcp.Daemon.Security;

/// <summary>
/// Per-tool permission, set by a human in the app and enforced here.
///
/// The profile answers "what class of thing may this AI do". This answers "and not that one" —
/// a user who is happy for an agent to build scenes but never to touch their audio mixer has no
/// way to say so with three profiles, and saying it by hand in a config file is not something the
/// audience for this app will do.
///
/// It is stored as the **disabled** set rather than the allowed one, deliberately: tools are
/// added to the catalog in every release, and a stored allow-list would silently deny every new
/// tool to everyone who had ever opened this panel.
///
/// Enforced in the dispatcher, next to the profile check. A permission that only exists in the
/// window is not a permission — it is a suggestion the AI never sees.
/// </summary>
public sealed class ToolPolicy
{
    /// <summary>
    /// Explicit resolver. An options instance built inline throws
    /// "must specify a TypeInfoResolver setting before being marked as read-only" inside this
    /// host, and the only symptom is a permission that silently fails to persist.
    /// </summary>
    static readonly System.Text.Json.JsonSerializerOptions Indented = new()
    {
        WriteIndented = true,
        TypeInfoResolver = new System.Text.Json.Serialization.Metadata.DefaultJsonTypeInfoResolver()
    };

    readonly object _gate = new();
    readonly string _file;
    HashSet<string> _disabled = new(StringComparer.Ordinal);

    public ToolPolicy() : this(Umcp.UmcpPaths.SettingsFile) { }

    public ToolPolicy(string file)
    {
        _file = file;
        Reload();
    }

    public IReadOnlyCollection<string> Disabled
    {
        get { lock (_gate) return _disabled.ToArray(); }
    }

    public bool IsAllowed(string toolId)
    {
        lock (_gate) return !_disabled.Contains(toolId);
    }

    /// <summary>Null when allowed, or a sentence fit to hand back to a model.</summary>
    public string? Denies(string toolId) =>
        IsAllowed(toolId) ? null
            : $"'{toolId}' is turned off in this machine's NAV MCP settings. A person has to allow it there.";

    /// <summary>
    /// Replace the set. Ids that are not in the catalog are dropped rather than stored: a stale
    /// name in the file would quietly become a permission for a future tool with that id.
    /// </summary>
    public void Set(IEnumerable<string> disabled)
    {
        var known = disabled.Where(ToolCatalog.ById.ContainsKey).ToHashSet(StringComparer.Ordinal);
        lock (_gate)
        {
            _disabled = known;
            Write();
        }
    }

    public void Reload()
    {
        lock (_gate)
        {
            try
            {
                if (!File.Exists(_file)) return;
                if (JsonNode.Parse(File.ReadAllText(_file))?["disabledTools"] is not JsonArray array) return;
                _disabled = array.Select(n => (string?)n)
                                 .Where(s => !string.IsNullOrWhiteSpace(s))
                                 .Select(s => s!)
                                 .ToHashSet(StringComparer.Ordinal);
            }
            catch { /* an unreadable settings file must not deny every tool */ }
        }
    }

    /// <summary>Caller holds the lock.</summary>
    void Write()
    {
        try
        {
            // The file's own directory, not the global one: they are the same in production and
            // different everywhere else, and "everywhere else" is where this silently failed.
            Directory.CreateDirectory(Path.GetDirectoryName(_file)!);

            JsonObject root;
            try { root = (File.Exists(_file) ? JsonNode.Parse(File.ReadAllText(_file)) as JsonObject : null) ?? new(); }
            catch { root = new(); }

            var array = new JsonArray();
            foreach (var id in _disabled.OrderBy(x => x, StringComparer.Ordinal)) array.Add(id);
            root["disabledTools"] = array;

            var temp = _file + ".tmp";
            File.WriteAllText(temp, root.ToJsonString(Indented) + "\n");
            File.Move(temp, _file, overwrite: true);
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"[umcpd] warning: could not save tool permissions: {e.Message}");
        }
    }
}
