namespace Umcp.Daemon;

public sealed class DaemonOptions
{
    /// <summary>HTTP port: MCP streamable HTTP plus /health. Loopback only.</summary>
    public int HttpPort { get; init; } = 8730;

    /// <summary>Agent channel port. Unity connects out to this. 8086/8090 are taken by the tool being replaced.</summary>
    public int AgentPort { get; init; } = 8731;

    /// <summary>Also serve MCP over this process's stdio.</summary>
    public bool Stdio { get; init; }

    /// <summary>Show the tray UI (Windows only).</summary>
    public bool Tray { get; init; }

    public string? Token { get; init; }

    /// <summary>readonly | standard | full. Arbitrary code execution and irreversible writes need full.</summary>
    public Security.Profile Profile { get; init; } = Security.Profile.Standard;

    // -------- timeouts, per class rather than one global number --------

    /// <summary>How long an op may wait for an editor to appear or reappear. Covers a domain reload.</summary>
    public TimeSpan HoldTimeout { get; init; } = TimeSpan.FromSeconds(180);

    public TimeSpan ReadTimeout { get; init; } = TimeSpan.FromSeconds(15);
    public TimeSpan WriteTimeout { get; init; } = TimeSpan.FromSeconds(30);
    public TimeSpan CompileTimeout { get; init; } = TimeSpan.FromSeconds(300);

    /// <summary>Start probing the out-of-band control channel once an op has been in flight this long.</summary>
    public TimeSpan BlockProbeAfter { get; init; } = TimeSpan.FromSeconds(4);

    /// <summary>No main-thread tick for this long, with work outstanding, means blocked — not slow.</summary>
    public TimeSpan BlockedTickAge { get; init; } = TimeSpan.FromSeconds(8);

    /// <summary>Responses larger than this are truncated honestly unless the caller overrides.</summary>
    public int MaxResponseBytes { get; init; } = 32 * 1024;

    /// <summary>
    /// Arguments larger than this are refused.
    ///
    /// Unity will happily accept a 200 KB GameObject name — measured — and then carry it in the
    /// scene, in the mirror, and in every response that mentions it. There is no legitimate call
    /// of that shape, and refusing it is cheaper for everyone than discovering it later.
    /// </summary>
    public int MaxRequestBytes { get; init; } = 256 * 1024;

    // -------- fleet --------

    /// <summary>
    /// How long <c>unity_projects(open:)</c> waits for a launched Editor to handshake. A cold
    /// Library import on a large project is minutes, not seconds, and the alternative to waiting
    /// is reporting a failure that is really a slow import.
    /// </summary>
    public TimeSpan OpenTimeout { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Bring an Editor back when its process dies. Off by default: relaunching a window a human
    /// closed is not recovery. Per-project override via <c>unity_projects(autoRestart:)</c>.
    /// </summary>
    public bool AutoRestart { get; init; }

    /// <summary>Where com.umcp.agent lives, when it is not next to the binary.</summary>
    public string? PackagePath { get; init; }

    /// <summary>
    /// Minutes of code-mode idleness before the compiler's reference metadata is released.
    /// Zero keeps the default (10). Roslyn's metadata for the Editor's assemblies is ~150 MB.
    /// </summary>
    public int ScriptIdleMinutes { get; init; }

    public static DaemonOptions Parse(string[] args)
    {
        int Int(string name, int dflt)
        {
            var i = Array.IndexOf(args, name);
            return i >= 0 && i + 1 < args.Length && int.TryParse(args[i + 1], out var v) ? v : dflt;
        }
        string? Str(string name)
        {
            var i = Array.IndexOf(args, name);
            return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
        }

        return new DaemonOptions
        {
            HttpPort = Int("--port", 8730),
            AgentPort = Int("--agent-port", 8731),
            Stdio = args.Contains("--stdio"),
            Tray = args.Contains("--tray"),
            Token = Str("--token"),
            MaxResponseBytes = Int("--max-response-bytes", 32 * 1024),
            MaxRequestBytes = Int("--max-request-bytes", 256 * 1024),
            Profile = Security.Profiles.Parse(Str("--profile")),
            AutoRestart = args.Contains("--auto-restart"),
            PackagePath = Str("--package-path"),
            OpenTimeout = TimeSpan.FromSeconds(Int("--open-timeout", 300)),
            ScriptIdleMinutes = Int("--script-idle-minutes", 0)
        };
    }
}
