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
        if (pid <= 0) return null;
        try
        {
            return OperatingSystem.IsWindows() ? FromWmi(pid) : FromPs(pid);
        }
        catch { /* WMI can be disabled, ps can be missing; unverified is a valid answer */ }
        return null;
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    static string? FromWmi(int pid)
    {
        using var searcher = new System.Management.ManagementObjectSearcher(
            $"SELECT CommandLine FROM Win32_Process WHERE ProcessId = {pid}");
        foreach (var o in searcher.Get())
            return o["CommandLine"] as string;
        return null;
    }

    /// <summary>
    /// macOS and Linux: <c>ps -o command= -p &lt;pid&gt;</c>. `command` rather than `args` because
    /// BSD ps on macOS truncates `args` at the terminal width unless it is the last column, and a
    /// truncated command line would read as a mismatched -projectPath — a confident wrong answer.
    /// </summary>
    static string? FromPs(int pid)
    {
        var psi = new ProcessStartInfo("ps")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        psi.ArgumentList.Add("-o");
        psi.ArgumentList.Add("command=");
        psi.ArgumentList.Add("-p");
        psi.ArgumentList.Add(pid.ToString());

        using var p = Process.Start(psi);
        if (p is null) return null;
        var text = p.StandardOutput.ReadToEnd().Trim();
        if (!p.WaitForExit(2000)) { try { p.Kill(true); } catch { } return null; }
        return string.IsNullOrWhiteSpace(text) ? null : text;
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
            // Unquoted, which is what `ps` reports on macOS and Linux even for a path that was
            // passed as one argument. Stopping at the first space would truncate every project
            // path with a space in it and report a mismatch that never happened, so the value
            // runs to the next argument — a space followed by a dash — or to the end.
            var end = rest.IndexOf(" -", StringComparison.Ordinal);
            value = (end < 0 ? rest : rest[..end]).Trim();
        }

        // Path.GetFullPath resolves against *this* machine's rules, so a Windows path parsed on
        // macOS — a daemon reading a command line captured elsewhere, or a test — comes back with
        // the working directory glued to the front of "E:\Projects\Game". An already-absolute
        // path needs no resolving, so recognising one first keeps the answer the caller's.
        if (LooksAbsolute(value)) return EditorInstalls.Normalise(value);

        try { return EditorInstalls.Normalise(Path.GetFullPath(value)); }
        catch { return EditorInstalls.Normalise(value); }
    }

    /// <summary>A drive-letter path, a UNC share, or a unix absolute path — on any host.</summary>
    static bool LooksAbsolute(string path)
    {
        if (string.IsNullOrEmpty(path)) return false;
        if (path[0] == '/' || path.StartsWith(@"\\", StringComparison.Ordinal)) return true;
        return path.Length >= 3 && char.IsLetter(path[0]) && path[1] == ':' &&
               (path[2] == '/' || path[2] == '\\');
    }

    public static bool IsAlive(int pid)
    {
        if (pid <= 0) return false;
        try { return !Process.GetProcessById(pid).HasExited; }
        catch { return false; }
    }
}
