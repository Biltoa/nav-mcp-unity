namespace Umcp;

/// <summary>
/// Where the tool keeps its own state, on every platform it runs on.
///
/// Linked into the daemon, the shim, the GUI and the bench rather than copied: the token file is
/// the one path all four have to agree on, and a shim that looks in the wrong folder reports
/// "unauthorized" against a daemon that is running perfectly.
///
/// Nothing here is ever inside a Unity project. Shipping a log file under <c>Assets/</c> makes the
/// AssetDatabase import it while it is still being appended to.
/// </summary>
public static class UmcpPaths
{
    /// <summary>
    /// Windows: <c>%LOCALAPPDATA%\UnityMCP</c>.
    /// macOS: <c>~/Library/Application Support/UnityMCP</c> — <c>SpecialFolder.LocalApplicationData</c>
    /// answers <c>~/.local/share</c> there, which is the Linux convention, not the Mac one.
    /// Linux: <c>$XDG_DATA_HOME</c>, else <c>~/.local/share</c>.
    /// </summary>
    public static string Root { get; } = Resolve();

    /// <summary>
    /// The legacy, port-less token path. Still written, because a client configured before this
    /// existed reads it, and still read as a fallback.
    /// </summary>
    public static string TokenFile => Path.Combine(Root, "token");

    /// <summary>
    /// The token for the daemon on a given port.
    ///
    /// One file for every daemon was a bug with a quiet symptom: a second daemon — started by hand
    /// on another port, or by the shim because a client launched before the app did — replaced the
    /// running daemon's token, and every client of the working server began answering 401 to
    /// requests that had been fine a second earlier. The port is the only thing that distinguishes
    /// two daemons on one machine, so it goes in the filename.
    /// </summary>
    public static string TokenFileFor(int port) => Path.Combine(Root, $"token-{port}");

    /// <summary>
    /// Read the token for a port: its own file first, then the legacy one. The fallback matters
    /// for a shim or a bench run pointed at a daemon older than this change.
    /// </summary>
    public static string? ReadToken(int port)
    {
        foreach (var candidate in new[] { TokenFileFor(port), TokenFile })
        {
            try
            {
                if (File.Exists(candidate))
                {
                    var text = File.ReadAllText(candidate).Trim();
                    if (text.Length > 0) return text;
                }
            }
            catch { /* an unreadable token file is the same as an absent one */ }
        }
        return null;
    }
    public static string LogDir => Path.Combine(Root, "logs");
    public static string AuditLog => Path.Combine(LogDir, "audit.jsonl");
    public static string DaemonLog => Path.Combine(LogDir, "daemon.log");
    public static string SettingsFile => Path.Combine(Root, "settings.json");

    static string Resolve()
    {
        var overridden = Environment.GetEnvironmentVariable("UMCP_HOME");
        if (!string.IsNullOrWhiteSpace(overridden)) return overridden;

        if (OperatingSystem.IsMacOS())
            return Path.Combine(Home(), "Library", "Application Support", "UnityMCP");

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "UnityMCP");
    }

    public static string Home() =>
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) is { Length: > 0 } p
            ? p
            : Environment.GetEnvironmentVariable("HOME") ?? ".";

    public static void EnsureCreated()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(LogDir);
    }

    /// <summary>
    /// The Unity Hub's config directory. <c>SpecialFolder.ApplicationData</c> is
    /// <c>~/.config</c> on macOS, but the Hub writes under <c>~/Library/Application Support</c>,
    /// so the two disagree exactly where it matters.
    /// </summary>
    public static string UnityHubConfigDir =>
        OperatingSystem.IsMacOS()
            ? Path.Combine(Home(), "Library", "Application Support", "UnityHub")
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "UnityHub");

    /// <summary>The executable name of a tool binary on this platform.</summary>
    public static string ExeName(string stem) => OperatingSystem.IsWindows() ? stem + ".exe" : stem;

    /// <summary>
    /// Restrict a file to its owner. An ACL on Windows, mode 0600 elsewhere. Best effort by
    /// design: a token that could not be locked down is worth a warning, not a dead daemon.
    /// </summary>
    public static string? RestrictToOwner(string path)
    {
        try
        {
            if (OperatingSystem.IsWindows()) return RestrictWindows(path);
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            return null;
        }
        catch (Exception e) { return e.Message; }
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    static string? RestrictWindows(string path)
    {
        var info = new FileInfo(path);
        var security = info.GetAccessControl();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        var me = System.Security.Principal.WindowsIdentity.GetCurrent().User;
        if (me is not null)
            security.AddAccessRule(new System.Security.AccessControl.FileSystemAccessRule(
                me, System.Security.AccessControl.FileSystemRights.FullControl,
                System.Security.AccessControl.AccessControlType.Allow));
        info.SetAccessControl(security);
        return null;
    }
}
