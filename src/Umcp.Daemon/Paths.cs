namespace Umcp.Daemon;

/// <summary>
/// Everything the daemon writes lives here — never under a Unity project's <c>Assets/</c>.
///
/// This is not a style preference. Shipping a server's log file inside <c>Assets/</c> makes
/// Unity's AssetDatabase try to import it while the server is still appending, which produced
/// "Amount of processed bytes '37966' does not match file size '37849'" in the Editor log of the
/// tool being replaced.
///
/// The platform rules live in <see cref="Umcp.UmcpPaths"/>, which the shim and the GUI link too:
/// the token file is the one path they all have to agree on.
/// </summary>
public static class Paths
{
    public static string Root => Umcp.UmcpPaths.Root;
    public static string TokenFile => Umcp.UmcpPaths.TokenFile;
    public static string LogDir => Umcp.UmcpPaths.LogDir;
    public static string AuditLog => Umcp.UmcpPaths.AuditLog;

    public static void EnsureCreated() => Umcp.UmcpPaths.EnsureCreated();
}
