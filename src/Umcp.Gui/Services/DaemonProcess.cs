using System.Diagnostics;

namespace Umcp.Gui.Services;

/// <summary>
/// Starting and stopping <c>umcpd</c> on behalf of a person who has never opened a terminal.
///
/// Three rules, all of them learned from the daemon's own design:
///
///   * **Attach, do not duplicate.** If something is already answering on the port, this app uses
///     it. Two daemons on one machine means two agent listeners and an Editor connected to the
///     wrong one.
///   * **Ask before killing.** Stop is an HTTP <c>/api/quit</c> first. Only a daemon this app
///     started, that ignored the request, is killed — and never one somebody else started.
///   * **Drain both pipes.** A redirected stream nobody reads fills its buffer (4 KB on Windows)
///     and then every write from the daemon blocks: the server stops serving, having logged its
///     way into a deadlock, with no visible symptom but silence.
/// </summary>
public sealed class DaemonProcess
{
    readonly object _gate = new();
    Process? _child;

    /// <summary>True when the running daemon is this app's child, so Stop may escalate to a kill.</summary>
    public bool OwnsProcess => _child is { HasExited: false };

    public int? Pid => _child is { HasExited: false } ? _child.Id : null;

    /// <summary>Where umcpd lives, or null when this is a broken install rather than a stopped server.</summary>
    public static string? Locate()
    {
        var name = UmcpPaths.ExeName("umcpd");
        var candidates = new List<string>
        {
            Path.Combine(AppContext.BaseDirectory, name),
            // macOS: the GUI runs from Contents/MacOS, the tools sit in Contents/Resources.
            Path.Combine(AppContext.BaseDirectory, "..", "Resources", name)
        };

        // Development layout: src/Umcp.Gui/bin/... beside src/Umcp.Daemon/bin/...
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
            foreach (var configuration in new[] { "Debug", "Release" })
                candidates.Add(Path.Combine(d.FullName, "src", "Umcp.Daemon", "bin", configuration, "net8.0", name));

        foreach (var candidate in candidates)
        {
            try { if (File.Exists(candidate)) return Path.GetFullPath(candidate); } catch { }
        }
        return null;
    }

    /// <summary>The Unity package this install carries, to hand the daemon explicitly.</summary>
    public static string? LocatePackage()
    {
        var candidates = new List<string>
        {
            Path.Combine(AppContext.BaseDirectory, "com.umcp.agent"),
            Path.Combine(AppContext.BaseDirectory, "..", "Resources", "com.umcp.agent")
        };
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
            candidates.Add(Path.Combine(d.FullName, "unity", "com.umcp.agent"));

        foreach (var candidate in candidates)
        {
            try { if (File.Exists(Path.Combine(candidate, "package.json"))) return Path.GetFullPath(candidate); } catch { }
        }
        return null;
    }

    /// <summary>
    /// Start a daemon, unless one is already answering. Returns a sentence fit to show a user.
    /// </summary>
    public async Task<(bool Ok, string Message)> StartAsync(GuiSettings settings, ControlClient client)
    {
        if (await client.HealthAsync() is not null)
            return (true, $"Attached to the server already running on port {settings.HttpPort}.");

        var exe = Locate();
        if (exe is null)
            return (false, "Cannot find umcpd next to this app. The install looks incomplete — reinstall the Unity MCP Tool.");

        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = Path.GetDirectoryName(exe)!
        };
        psi.ArgumentList.Add("--port");
        psi.ArgumentList.Add(settings.HttpPort.ToString());
        psi.ArgumentList.Add("--agent-port");
        psi.ArgumentList.Add(settings.AgentPort.ToString());
        psi.ArgumentList.Add("--profile");
        psi.ArgumentList.Add(settings.Profile);

        if (LocatePackage() is { } package)
        {
            psi.ArgumentList.Add("--package-path");
            psi.ArgumentList.Add(package);
        }

        Process child;
        try { child = Process.Start(psi) ?? throw new InvalidOperationException("the process did not start"); }
        catch (Exception e) { return (false, $"Could not start the server: {e.Message}"); }

        lock (_gate) _child = child;

        UmcpPaths.EnsureCreated();
        _ = DrainAsync(child.StandardOutput);
        _ = DrainAsync(child.StandardError);

        // Wait for it to answer rather than for a stopwatch: a port that is bound is the only
        // evidence that "started" means anything.
        for (var i = 0; i < 60; i++)
        {
            if (child.HasExited) break;
            if (await client.HealthAsync() is not null)
                return (true, $"Server running on port {settings.HttpPort}.");
            await Task.Delay(250);
        }

        if (child.HasExited)
        {
            var tail = LastLogLines(6);
            return (false, $"The server exited immediately (code {child.ExitCode}).{(tail.Length > 0 ? "\n" + string.Join("\n", tail) : "")}");
        }

        return (false, $"The server started but did not answer on port {settings.HttpPort} within 15 seconds. See the log.");
    }

    /// <summary>
    /// Ask the daemon to stop; escalate only against a process this app owns.
    /// </summary>
    public async Task<(bool Ok, string Message)> StopAsync(ControlClient client)
    {
        var asked = await client.QuitAsync();
        var child = _child;

        if (child is null || child.HasExited)
        {
            lock (_gate) _child = null;
            // Somebody else's daemon: it was asked, politely, and that is as far as this goes.
            if (await client.HealthAsync() is null) return (true, "Server stopped.");
            return asked is null
                ? (false, "The server did not answer the request to stop.")
                : (false, "The server was asked to stop but is still answering. It may be finishing an operation.");
        }

        for (var i = 0; i < 40; i++)
        {
            if (child.HasExited) { lock (_gate) _child = null; return (true, "Server stopped."); }
            await Task.Delay(250);
        }

        // Ours, and it did not go. A Unity Editor attached to it is unaffected: the agent
        // reconnects with backoff forever, and no operation is lost that had not started.
        try { child.Kill(entireProcessTree: true); child.WaitForExit(5000); }
        catch (Exception e) { return (false, $"Could not stop the server: {e.Message}"); }

        lock (_gate) _child = null;
        return (true, "Server stopped (it had to be forced).");
    }

    static async Task DrainAsync(StreamReader stream)
    {
        const long maxBytes = 8 * 1024 * 1024;
        var path = UmcpPaths.DaemonLog;
        try
        {
            while (await stream.ReadLineAsync() is { } line)
            {
                try
                {
                    var info = new FileInfo(path);
                    if (info.Exists && info.Length > maxBytes) File.WriteAllText(path, "");
                    await File.AppendAllTextAsync(path, line + Environment.NewLine);
                }
                catch { /* logging must never take the app down */ }
            }
        }
        catch { /* the daemon exited; nothing left to drain */ }
    }

    public static string[] LastLogLines(int count)
    {
        try
        {
            if (!File.Exists(UmcpPaths.DaemonLog)) return Array.Empty<string>();
            using var stream = new FileStream(UmcpPaths.DaemonLog, FileMode.Open, FileAccess.Read,
                                              FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            var ring = new Queue<string>(count);
            while (reader.ReadLine() is { } line)
            {
                if (ring.Count == count) ring.Dequeue();
                ring.Enqueue(line);
            }
            return ring.ToArray();
        }
        catch { return Array.Empty<string>(); }
    }
}
