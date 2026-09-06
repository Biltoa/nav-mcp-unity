using System.Text.Json;
using System.Text.RegularExpressions;

namespace Umcp.Daemon.Fleet;

/// <summary>One installed Editor, as found on disk.</summary>
public sealed record EditorInstall(string Version, string ExePath, string Source)
{
    /// <summary>6000.3.10f1 → (6000, 3, 10, 'f', 1). Used for "close enough" matching, never for equality.</summary>
    public (int Major, int Minor, int Patch, char Kind, int Build) Parsed => EditorInstalls.ParseVersion(Version);
}

/// <summary>
/// Where the Editors are.
///
/// Discovery reads the Hub's <c>secondaryInstallPath.json</c> plus the default install roots and
/// then looks at the filesystem. It deliberately does <em>not</em> shell out to the Hub CLI:
/// <c>Unity Hub -- --headless editors</c> is deprecated as of Hub 3.18.0, it is slow (a full
/// Electron start), and it fails silently when the Hub is already running with a lock held.
/// The directory layout it would report is the layout we read directly.
/// </summary>
public static class EditorInstalls
{
    static readonly Regex VersionDir = new(@"^\d+\.\d+\.\d+[abfp]\d+$", RegexOptions.Compiled);

    public static string HubConfigDir => Umcp.UmcpPaths.UnityHubConfigDir;

    /// <summary>The roots to scan: the Hub's secondary install path, then the platform defaults.</summary>
    public static IReadOnlyList<string> Roots()
    {
        var roots = new List<string>();

        var secondary = Path.Combine(HubConfigDir, "secondaryInstallPath.json");
        if (File.Exists(secondary))
        {
            try
            {
                // The file is a bare JSON string, e.g. "E:\\Unity Editor" — not an object.
                var text = File.ReadAllText(secondary).Trim();
                var path = text.StartsWith('"') ? JsonSerializer.Deserialize<string>(text) : text;
                if (!string.IsNullOrWhiteSpace(path)) roots.Add(path!);
            }
            catch { /* a malformed Hub file must not take discovery down */ }
        }

        if (OperatingSystem.IsWindows())
        {
            roots.Add(@"C:\Program Files\Unity\Hub\Editor");
            var pf = Environment.GetEnvironmentVariable("ProgramFiles");
            if (pf is not null) roots.Add(Path.Combine(pf, "Unity", "Hub", "Editor"));
        }
        else if (OperatingSystem.IsMacOS()) roots.Add("/Applications/Unity/Hub/Editor");
        else roots.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Unity", "Hub", "Editor"));

        return roots.Select(Normalise).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    /// <summary>Enumerate installs under the given roots. Pure filesystem, no Hub process.</summary>
    public static IReadOnlyList<EditorInstall> Scan(IEnumerable<string>? roots = null)
    {
        var found = new Dictionary<string, EditorInstall>(StringComparer.OrdinalIgnoreCase);

        foreach (var root in roots ?? Roots())
        {
            if (!Directory.Exists(root)) continue;
            foreach (var dir in SafeDirs(root))
            {
                var version = Path.GetFileName(dir);
                if (!VersionDir.IsMatch(version)) continue;
                var exe = ExeUnder(dir);
                if (exe is null) continue;
                if (!found.ContainsKey(version)) found[version] = new EditorInstall(version, exe, root);
            }
        }

        return found.Values.OrderByDescending(i => i.Parsed).ToArray();
    }

    static string? ExeUnder(string versionDir)
    {
        var candidates = OperatingSystem.IsMacOS()
            ? new[] { Path.Combine(versionDir, "Unity.app", "Contents", "MacOS", "Unity") }
            : OperatingSystem.IsWindows()
                ? new[] { Path.Combine(versionDir, "Editor", "Unity.exe") }
                : new[] { Path.Combine(versionDir, "Editor", "Unity") };

        return candidates.FirstOrDefault(File.Exists);
    }

    static IEnumerable<string> SafeDirs(string root)
    {
        try { return Directory.EnumerateDirectories(root); }
        catch { return Array.Empty<string>(); }
    }

    /// <summary>
    /// Pick the Editor for a project version.
    ///
    /// Exact wins. Otherwise the newest install with the same major.minor, which is what the Hub
    /// offers and what Unity will accept without an upgrade prompt beyond the usual one. Anything
    /// further away is returned as a candidate but flagged, because opening a project in a
    /// different major version rewrites its library and is not something a daemon does silently.
    /// </summary>
    public static (EditorInstall? Install, string Match) Choose(IReadOnlyList<EditorInstall> installs, string? wanted)
    {
        if (installs.Count == 0) return (null, "none");
        if (string.IsNullOrWhiteSpace(wanted)) return (installs[0], "newest");

        var exact = installs.FirstOrDefault(i => string.Equals(i.Version, wanted, StringComparison.OrdinalIgnoreCase));
        if (exact is not null) return (exact, "exact");

        var w = ParseVersion(wanted!);
        var sameMinor = installs.Where(i => i.Parsed.Major == w.Major && i.Parsed.Minor == w.Minor)
                                .OrderByDescending(i => i.Parsed).FirstOrDefault();
        if (sameMinor is not null) return (sameMinor, "same-minor");

        var sameMajor = installs.Where(i => i.Parsed.Major == w.Major)
                                .OrderByDescending(i => i.Parsed).FirstOrDefault();
        if (sameMajor is not null) return (sameMajor, "same-major");

        return (null, "none");
    }

    public static (int Major, int Minor, int Patch, char Kind, int Build) ParseVersion(string v)
    {
        var m = Regex.Match(v ?? "", @"^(\d+)\.(\d+)\.(\d+)([abfp])(\d+)");
        if (!m.Success) return (0, 0, 0, 'a', 0);
        return (int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value), int.Parse(m.Groups[3].Value),
                m.Groups[4].Value[0], int.Parse(m.Groups[5].Value));
    }

    /// <summary>The version a project last opened with, from <c>ProjectSettings/ProjectVersion.txt</c>.</summary>
    public static string? ProjectVersion(string projectPath)
    {
        var file = Path.Combine(projectPath, "ProjectSettings", "ProjectVersion.txt");
        if (!File.Exists(file)) return null;
        foreach (var line in File.ReadLines(file))
            if (line.StartsWith("m_EditorVersion:", StringComparison.Ordinal))
                return line["m_EditorVersion:".Length..].Trim();
        return null;
    }

    internal static string Normalise(string p) => p.Replace('\\', '/').TrimEnd('/');
}
