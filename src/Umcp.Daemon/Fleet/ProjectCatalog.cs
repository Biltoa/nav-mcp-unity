using System.Text.Json;
using System.Text.Json.Nodes;

namespace Umcp.Daemon.Fleet;

/// <summary>A project on disk, whether or not an Editor is currently attached to it.</summary>
public sealed record KnownProject(
    string Path,
    string Name,
    string? EditorVersion,
    string? ProjectId,
    bool AgentInstalled,
    bool LockedByEditor,
    DateTimeOffset? LastModified,
    string Source);

/// <summary>
/// Projects the daemon knows about, and the small facts about each one that decide whether
/// <c>unity_projects(open:)</c> can succeed.
///
/// Identity is read from <c>ProjectSettings/UnityMCP.json</c>, the same file the agent mints its
/// GUID into. Reading it here means <c>open</c> can report the id it is about to wait for instead
/// of guessing which of several handshakes belongs to it.
/// </summary>
public static class ProjectCatalog
{
    /// <summary>Projects listed in the Hub's own database, newest first. Data only — never executed.</summary>
    public static IReadOnlyList<KnownProject> FromHub(string? hubConfigDir = null)
    {
        var file = Path.Combine(hubConfigDir ?? EditorInstalls.HubConfigDir, "projects-v1.json");
        if (!File.Exists(file)) return Array.Empty<KnownProject>();

        JsonNode? root;
        try { root = JsonNode.Parse(File.ReadAllText(file)); }
        catch { return Array.Empty<KnownProject>(); }

        if (root?["data"] is not JsonObject data) return Array.Empty<KnownProject>();

        var list = new List<KnownProject>();
        foreach (var (_, entry) in data)
        {
            var path = (string?)entry?["path"];
            if (string.IsNullOrWhiteSpace(path)) continue;
            var ms = (long?)entry?["lastModified"];
            list.Add(Describe(path!, (string?)entry?["title"],
                ms is null ? null : DateTimeOffset.FromUnixTimeMilliseconds(ms.Value), "hub"));
        }

        return list.OrderByDescending(p => p.LastModified ?? DateTimeOffset.MinValue).ToArray();
    }

    /// <summary>Read the facts about one project directory.</summary>
    public static KnownProject Describe(string path, string? title = null, DateTimeOffset? lastModified = null, string source = "path")
    {
        var normalised = EditorInstalls.Normalise(path);
        return new KnownProject(
            Path: normalised,
            Name: title ?? System.IO.Path.GetFileName(normalised.TrimEnd('/')),
            EditorVersion: EditorInstalls.ProjectVersion(path),
            ProjectId: ReadProjectId(path),
            AgentInstalled: AgentPackage.IsReferenced(path),
            LockedByEditor: IsLocked(path),
            LastModified: lastModified,
            Source: source);
    }

    /// <summary>True when the path looks like a Unity project at all.</summary>
    public static bool IsProject(string path) =>
        Directory.Exists(Path.Combine(path, "Assets")) &&
        Directory.Exists(Path.Combine(path, "ProjectSettings"));

    /// <summary>
    /// The GUID the agent mints into <c>ProjectSettings/UnityMCP.json</c>. Absent until the agent
    /// has run once in that project.
    /// </summary>
    public static string? ReadProjectId(string projectPath)
    {
        var file = Path.Combine(projectPath, "ProjectSettings", "UnityMCP.json");
        if (!File.Exists(file)) return null;
        try
        {
            var id = (string?)JsonNode.Parse(File.ReadAllText(file))?["projectId"];
            return string.IsNullOrWhiteSpace(id) ? null : id;
        }
        catch { return null; }
    }

    /// <summary>
    /// Unity holds <c>Temp/UnityLockfile</c> while a project is open, and refuses a second Editor
    /// on the same project. Knowing this lets <c>open</c> return a clear error instead of
    /// launching a process that will exit on its own.
    ///
    /// The file survives a crash, so its mere existence is not proof: a lock we can open for
    /// writing is a stale lock.
    /// </summary>
    public static bool IsLocked(string projectPath)
    {
        var lockFile = Path.Combine(projectPath, "Temp", "UnityLockfile");
        if (!File.Exists(lockFile)) return false;
        try
        {
            using var _ = new FileStream(lockFile, FileMode.Open, FileAccess.Write, FileShare.None);
            return false;   // opened it, so nobody holds it
        }
        catch (IOException) { return true; }
        catch { return false; }
    }

    public static JsonObject ToJson(KnownProject p) => new()
    {
        ["path"] = p.Path,
        ["name"] = p.Name,
        ["editorVersion"] = p.EditorVersion,
        ["projectId"] = p.ProjectId,
        ["agentInstalled"] = p.AgentInstalled,
        ["open"] = p.LockedByEditor,
        ["lastModified"] = p.LastModified?.ToString("O"),
        ["source"] = p.Source
    };

    internal static JsonSerializerOptions Indented { get; } = new() { WriteIndented = true };
}
