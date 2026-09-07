using System.Diagnostics;
using System.Runtime.Versioning;

namespace Umcp.Gui.Services;

/// <summary>
/// "Start NAV MCP when I sign in", on both platforms.
///
/// It lives in the app rather than in an installer because it is the only place that still works
/// after somebody moves the app: the setting is read and written against wherever this binary
/// actually is, every time. An installer checkbox that wrote an absolute path once would point at
/// nothing the first time the folder was renamed.
///
/// Windows: a value under <c>HKCU\Software\Microsoft\Windows\CurrentVersion\Run</c>. Per-user, no
/// administrator prompt, and the same mechanism the user can see and remove in Task Manager's
/// Startup tab — which matters, because a startup entry a person cannot find is malware behaviour.
///
/// macOS: a LaunchAgent plist in <c>~/Library/LaunchAgents</c>. Also per-user, also visible in
/// System Settings under Login Items.
/// </summary>
public static class LoginItem
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string ValueName = "NAV MCP";
    const string AgentLabel = "com.navmcp.app";

    public static bool Supported => OperatingSystem.IsWindows() || OperatingSystem.IsMacOS();

    /// <summary>The launcher this app was started from, quoted for the platform that needs it.</summary>
    static string ExecutablePath =>
        Environment.ProcessPath ?? System.Reflection.Assembly.GetEntryAssembly()?.Location ?? "";

    static string AgentPlist =>
        Path.Combine(UmcpPaths.Home(), "Library", "LaunchAgents", AgentLabel + ".plist");

    public static bool IsEnabled()
    {
        try
        {
            if (OperatingSystem.IsWindows()) return WindowsValue() is not null;
            if (OperatingSystem.IsMacOS()) return File.Exists(AgentPlist);
        }
        catch { /* an unreadable startup entry reads as "off"; the checkbox is not worth a crash */ }
        return false;
    }

    /// <summary>Returns null on success, or a sentence explaining why it did not happen.</summary>
    public static string? Set(bool enabled)
    {
        try
        {
            if (OperatingSystem.IsWindows()) { SetWindows(enabled); return null; }
            if (OperatingSystem.IsMacOS()) { SetMac(enabled); return null; }
            return "Starting at login is only wired up on Windows and macOS.";
        }
        catch (Exception e)
        {
            return $"Could not change the login item: {e.Message}";
        }
    }

    // ---------------------------------------------------------------- windows

    [SupportedOSPlatform("windows")]
    static string? WindowsValue()
    {
        using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKey);
        return key?.GetValue(ValueName) as string;
    }

    [SupportedOSPlatform("windows")]
    static void SetWindows(bool enabled)
    {
        using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(RunKey, writable: true)
                        ?? throw new InvalidOperationException("could not open the Run key");

        if (!enabled) { key.DeleteValue(ValueName, throwOnMissingValue: false); return; }

        // Quoted: the default install path has a space in it, and an unquoted Run value with a
        // space is the classic way to launch the wrong executable.
        key.SetValue(ValueName, $"\"{ExecutablePath}\"");
    }

    // ---------------------------------------------------------------- macos

    [SupportedOSPlatform("macos")]
    static void SetMac(bool enabled)
    {
        var plist = AgentPlist;

        if (!enabled)
        {
            if (File.Exists(plist))
            {
                Bootout(plist);
                File.Delete(plist);
            }
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(plist)!);

        // ProcessPath inside a bundle is Contents/MacOS/<name>; launchd is happy to run it
        // directly, and doing so avoids `open -a`, which would find a *different* copy of the app
        // if two are installed.
        File.WriteAllText(plist, $"""
            <?xml version="1.0" encoding="UTF-8"?>
            <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
            <plist version="1.0">
            <dict>
              <key>Label</key><string>{AgentLabel}</string>
              <key>ProgramArguments</key>
              <array><string>{System.Security.SecurityElement.Escape(ExecutablePath)}</string></array>
              <key>RunAtLoad</key><true/>
              <!-- Not KeepAlive: this is "start it when I sign in", not "restart it whenever the
                   user quits it", and launchd resurrecting an app somebody just closed is a
                   haunting rather than a feature. -->
              <key>ProcessType</key><string>Interactive</string>
            </dict>
            </plist>
            """);

        Bootstrap(plist);
    }

    /// <summary>
    /// Register the agent with the running launchd session, so it also takes effect without a
    /// sign-out. Failure is not fatal: the plist alone is enough at the next login.
    /// </summary>
    [SupportedOSPlatform("macos")]
    static void Bootstrap(string plist) => Launchctl("bootstrap", $"gui/{Uid()}", plist);

    [SupportedOSPlatform("macos")]
    static void Bootout(string plist) => Launchctl("bootout", $"gui/{Uid()}/{AgentLabel}", null);

    static string Uid() => Environment.GetEnvironmentVariable("UID") ?? RunAndRead("id", "-u") ?? "501";

    static void Launchctl(string verb, string domain, string? path)
    {
        try
        {
            var psi = new ProcessStartInfo("launchctl") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
            psi.ArgumentList.Add(verb);
            psi.ArgumentList.Add(domain);
            if (path is not null) psi.ArgumentList.Add(path);
            Process.Start(psi)?.WaitForExit(4000);
        }
        catch { /* the plist is what persists; launchctl only makes it live now */ }
    }

    static string? RunAndRead(string exe, string arg)
    {
        try
        {
            var psi = new ProcessStartInfo(exe) { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
            psi.ArgumentList.Add(arg);
            using var p = Process.Start(psi);
            if (p is null) return null;
            var text = p.StandardOutput.ReadToEnd().Trim();
            p.WaitForExit(3000);
            return string.IsNullOrWhiteSpace(text) ? null : text;
        }
        catch { return null; }
    }
}
