using System.Diagnostics;

namespace Umcp.Daemon.Fleet;

/// <summary>
/// Starting and stopping Editor processes, and proving that the arguments arrived intact.
///
/// The read-back is not defensive programming for its own sake. During Phase 0 testing,
/// <c>-projectPath E:\Unity Workspaces\Gameplay Portfolio</c> was passed as three unquoted
/// arguments; Unity took the first token as the project path, found nothing, and **exited with
/// code 0** after licensing — a silent success that looks exactly like a slow start. So the daemon
/// quotes, then reads the launched process's own command line back out of the OS and checks the
/// path it actually received.
/// </summary>
public static class EditorLauncher
{
    /// <summary>
    /// The argument list, in the order Unity wants it.
    ///
    /// <c>-projectPath</c> must be quoted whenever it contains a space. <c>-accept-apiupdate</c>
    /// suppresses the API-updater prompt, which is one of the few startup modals that can be
    /// suppressed at all — and a modal at startup means the Editor never handshakes, which is
    /// indistinguishable from a hang.
    /// </summary>
    public static List<string> BuildArguments(string projectPath, bool acceptApiUpdate = true, IEnumerable<string>? extra = null)
    {
        var args = new List<string> { "-projectPath", Path.GetFullPath(projectPath).TrimEnd('/', '\\') };
        if (acceptApiUpdate) args.Add("-accept-apiupdate");
        if (extra is not null) args.AddRange(extra);
        return args;
    }

    /// <summary>Render an argument list the way CreateProcess will parse it back. Quoting is the whole point.</summary>
    public static string Render(IEnumerable<string> args) => string.Join(' ', args.Select(Quote));

    public static string Quote(string arg)
    {
        if (arg.Length > 0 && !arg.Any(c => c is ' ' or '\t' or '"')) return arg;
        // Windows command-line quoting: backslashes are only special immediately before a quote.
        var sb = new System.Text.StringBuilder("\"");
        var backslashes = 0;
        foreach (var c in arg)
        {
            if (c == '\\') { backslashes++; continue; }
            if (c == '"') { sb.Append('\\', backslashes * 2 + 1).Append('"'); }
            else { sb.Append('\\', backslashes).Append(c); }
            backslashes = 0;
        }
        sb.Append('\\', backslashes * 2).Append('"');
        return sb.ToString();
    }

    public static Process Start(string exePath, IReadOnlyList<string> args)
    {
        var psi = new ProcessStartInfo(exePath) { UseShellExecute = false, CreateNoWindow = false };
        foreach (var a in args) psi.ArgumentList.Add(a);   // ArgumentList quotes each argument itself
        return Process.Start(psi) ?? throw new InvalidOperationException("Process.Start returned null.");
    }

    /// <summary>
    /// The command line of a running process, as the OS holds it. Null when it cannot be read —
    /// the process already exited, or WMI is unavailable — which the caller must treat as
    /// "unverified", never as "wrong".
    /// </summary>
    public static string? CommandLineOf(int pid)
    {
        if (!OperatingSystem.IsWindows()) return null;
        try
        {
            using var searcher = new System.Management.ManagementObjectSearcher(
                $"SELECT CommandLine FROM Win32_Process WHERE ProcessId = {pid}");
            foreach (var o in searcher.Get())
                return o["CommandLine"] as string;
        }
        catch { /* WMI can be disabled or slow; unverified is a valid answer */ }
        return null;
    }

    /// <summary>
    /// Did the process actually receive the project path we meant? Compares canonical paths, so
    /// separator and trailing-slash differences do not read as a mismatch.
    /// </summary>
    public static (bool? Verified, string? CommandLine) VerifyProjectPath(int pid, string projectPath)
    {
        var cmd = CommandLineOf(pid);
        if (cmd is null) return (null, null);

        var wanted = EditorInstalls.Normalise(Path.GetFullPath(projectPath));
        var got = ExtractProjectPath(cmd);
        return (got is not null && string.Equals(got, wanted, StringComparison.OrdinalIgnoreCase), cmd);
    }

    /// <summary>Pull the <c>-projectPath</c> value back out of a raw command line, quoted or not.</summary>
    public static string? ExtractProjectPath(string commandLine)
    {
        var i = commandLine.IndexOf("-projectPath", StringComparison.OrdinalIgnoreCase);
        if (i < 0) return null;
        var rest = commandLine[(i + "-projectPath".Length)..].TrimStart();
        if (rest.Length == 0) return null;

        string value;
        if (rest[0] == '"')
        {
            var end = rest.IndexOf('"', 1);
            if (end < 0) return null;
            value = rest[1..end];
        }
        else
        {
            var end = rest.IndexOf(' ');
            value = end < 0 ? rest : rest[..end];
        }

        try { return EditorInstalls.Normalise(Path.GetFullPath(value)); }
        catch { return EditorInstalls.Normalise(value); }
    }

    public static bool IsAlive(int pid)
    {
        if (pid <= 0) return false;
        try { return !Process.GetProcessById(pid).HasExited; }
        catch { return false; }
    }
}
