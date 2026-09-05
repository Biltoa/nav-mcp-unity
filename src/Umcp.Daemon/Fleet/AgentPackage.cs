using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Umcp.Daemon.Fleet;

/// <summary>
/// Getting <c>com.umcp.agent</c> into a project's <c>Packages/manifest.json</c>, and knowing when
/// Unity has actually resolved it.
///
/// Two facts from Phase 1, both measured on this machine and both load-bearing:
///
///   1. **Unity re-resolves the manifest when the Editor regains window focus**, not on a timer and
///      not on <c>AssetDatabase.Refresh</c>. Adding the dependency to a *running* Editor did
///      nothing through several forced refreshes; <c>packages-lock.json</c> was rewritten the
///      moment the window was clicked. So: for a project we are about to launch, edit the manifest
///      *first* and let startup resolution do the work; for a project already open, say plainly
///      that the Editor has to be focused once.
///   2. Resolution is confirmed by reading <c>packages-lock.json</c>, never by waiting a while.
/// </summary>
public static class AgentPackage
{
    public const string PackageName = "com.umcp.agent";
    public const string ManifestBackupSuffix = ".umcp-backup";

    /// <summary>True when the project's manifest names the package at all.</summary>
    public static bool IsReferenced(string projectPath)
    {
        var manifest = ManifestPath(projectPath);
        if (!File.Exists(manifest)) return false;
        try
        {
            return JsonNode.Parse(File.ReadAllText(manifest))?["dependencies"]?[PackageName] is not null;
        }
        catch { return false; }
    }

    /// <summary>True when Unity has resolved it — the only honest definition of "installed".</summary>
    public static bool IsResolved(string projectPath)
    {
        var lockFile = Path.Combine(projectPath, "Packages", "packages-lock.json");
        if (!File.Exists(lockFile)) return false;
        try
        {
            return JsonNode.Parse(File.ReadAllText(lockFile))?["dependencies"]?[PackageName] is not null;
        }
        catch { return false; }
    }

    public static string ManifestPath(string projectPath) => Path.Combine(projectPath, "Packages", "manifest.json");

    /// <summary>
    /// Add the <c>file:</c> dependency, preserving every other entry and the file's formatting
    /// enough to be reviewable in VCS. Returns what happened, in words the caller can pass on.
    /// </summary>
    public static (bool Changed, string Detail) Ensure(string projectPath, string packageDir)
    {
        var manifest = ManifestPath(projectPath);
        if (!File.Exists(manifest)) return (false, $"No Packages/manifest.json under {projectPath}.");
        if (!File.Exists(Path.Combine(packageDir, "package.json")))
            return (false, $"No package.json under {packageDir}; nothing to reference.");

        JsonObject root;
        try { root = JsonNode.Parse(File.ReadAllText(manifest)) as JsonObject ?? new JsonObject(); }
        catch (Exception e) { return (false, $"Packages/manifest.json is not valid JSON: {e.Message}"); }

        if (root["dependencies"] is not JsonObject deps)
        {
            deps = new JsonObject();
            root["dependencies"] = deps;
        }

        // A relative file: path would be relative to the Packages folder and would break the
        // moment either tree moved. Absolute, forward-slashed, is what Unity accepts on Windows.
        var target = "file:" + EditorInstalls.Normalise(Path.GetFullPath(packageDir));
        if ((string?)deps[PackageName] == target) return (false, "already referenced");

        var previous = (string?)deps[PackageName];
        if (!File.Exists(manifest + ManifestBackupSuffix))
            File.Copy(manifest, manifest + ManifestBackupSuffix);

        deps[PackageName] = target;
        File.WriteAllText(manifest, root.ToJsonString(ProjectCatalog.Indented) + "\n", new UTF8Encoding(false));

        return (true, previous is null
            ? $"added {PackageName} -> {target}"
            : $"repointed {PackageName}: {previous} -> {target}");
    }

    /// <summary>Remove the dependency again. Used by the fleet tests so they leave no trace.</summary>
    public static bool Remove(string projectPath)
    {
        var manifest = ManifestPath(projectPath);
        if (!File.Exists(manifest)) return false;
        try
        {
            if (JsonNode.Parse(File.ReadAllText(manifest)) is not JsonObject root ||
                root["dependencies"] is not JsonObject deps || deps[PackageName] is null) return false;
            deps.Remove(PackageName);
            File.WriteAllText(manifest, root.ToJsonString(ProjectCatalog.Indented) + "\n", new UTF8Encoding(false));
            return true;
        }
        catch { return false; }
    }

    /// <summary>
    /// Where this daemon's copy of the package lives: an explicit setting, then the environment,
    /// then a walk up from the binary — which is what a repo checkout and a published build both
    /// look like.
    /// </summary>
    public static string? Locate(string? configured = null)
    {
        foreach (var candidate in Candidates(configured))
            if (candidate is not null && File.Exists(Path.Combine(candidate, "package.json")))
                return EditorInstalls.Normalise(Path.GetFullPath(candidate));
        return null;
    }

    static IEnumerable<string?> Candidates(string? configured)
    {
        yield return configured;
        yield return Environment.GetEnvironmentVariable("UMCP_PACKAGE_PATH");

        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 8 && dir is not null; i++, dir = dir.Parent)
        {
            yield return Path.Combine(dir.FullName, "unity", "com.umcp.agent");
            yield return Path.Combine(dir.FullName, "com.umcp.agent");
        }
    }

    /// <summary>Wait for Unity to write the package into <c>packages-lock.json</c>. Polls the file, never a timer.</summary>
    public static async Task<bool> WaitForResolveAsync(string projectPath, TimeSpan timeout, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline && !ct.IsCancellationRequested)
        {
            if (IsResolved(projectPath)) return true;
            await Task.Delay(500, ct).ConfigureAwait(false);
        }
        return IsResolved(projectPath);
    }
}
