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
            MaxResponseBytes = Int("--max-response-bytes", 32 * 1024)
        };
    }
}
