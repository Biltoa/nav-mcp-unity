namespace Umcp.Daemon;

/// <summary>
/// Everything the daemon writes lives here — never under a Unity project's <c>Assets/</c>.
///
/// This is not a style preference. Shipping a server's log file inside <c>Assets/</c> makes
/// Unity's AssetDatabase try to import it while the server is still appending, which produced
/// "Amount of processed bytes '37966' does not match file size '37849'" in the Editor log of the
/// tool being replaced.
/// </summary>
public static class Paths
{
    public static string Root { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "UnityMCP");

    public static string TokenFile => Path.Combine(Root, "token");
    public static string LogDir => Path.Combine(Root, "logs");
    public static string AuditLog => Path.Combine(LogDir, "audit.jsonl");

    public static void EnsureCreated()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(LogDir);
    }
}
