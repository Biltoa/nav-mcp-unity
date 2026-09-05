namespace Umcp.Daemon.Security;

public enum Profile { ReadOnly, Standard, Full }

/// <summary>
/// What a given profile is allowed to do. Default is <c>standard</c>: mutations yes, arbitrary
/// code execution and irreversible operations no.
///
/// This is a second lock, not the first one — the first is that both listeners bind 127.0.0.1 and
/// every request carries a bearer token. The profile exists because "can reach the daemon" and
/// "should be able to run arbitrary C# in the Editor" are different questions.
/// </summary>
public static class Profiles
{
    /// <summary>
    /// Operations that need <c>full</c>. Each is either arbitrary code execution or an
    /// irreversible write to the user's project.
    /// </summary>
    public static readonly IReadOnlySet<string> FullOnly = new HashSet<string>(StringComparer.Ordinal)
    {
        "unity.script",     // arbitrary C# in the Editor
        "assets.delete",    // AssetDatabase deletion is not undoable
        "scene.save",       // overwrites the user's scene file
        "scene.create",     // writes a new scene asset
        "editor.stall"      // deliberately wedges the Editor; a diagnostic, not a feature
    };

    public static Profile Parse(string? s) => s?.ToLowerInvariant() switch
    {
        "readonly" or "read-only" => Profile.ReadOnly,
        "full" => Profile.Full,
        _ => Profile.Standard
    };

    public static string Name(Profile p) => p switch
    {
        Profile.ReadOnly => "readonly",
        Profile.Full => "full",
        _ => "standard"
    };

    /// <summary>Returns null when allowed, or a human explanation when not.</summary>
    public static string? Denies(Profile profile, string toolId, bool mutating)
    {
        if (profile == Profile.Full) return null;

        if (FullOnly.Contains(toolId))
            return $"'{toolId}' requires the \"full\" profile; this daemon is running \"{Name(profile)}\".";

        if (profile == Profile.ReadOnly && mutating)
            return $"'{toolId}' changes state, and this daemon is running the \"readonly\" profile.";

        return null;
    }
}
