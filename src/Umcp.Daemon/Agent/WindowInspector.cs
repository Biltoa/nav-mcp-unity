using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace Umcp.Daemon.Agent;

/// <summary>
/// Names the dialog that is blocking Unity.
///
/// "E_EDITOR_BLOCKED: no main-thread tick for 12.4 s — Unity is likely showing a modal dialog"
/// is already far better than a bare timeout. Naming the dialog turns an hour of debugging into
/// a glance at the error. We only ever <em>report</em> it: auto-clicking someone else's modal is
/// dangerous, because "Recover scene backups?" and "Enter safe mode?" are not ours to answer.
/// </summary>
public static class WindowInspector
{
    public static string[] DialogTitles(int pid)
    {
        if (pid <= 0 || !OperatingSystem.IsWindows()) return Array.Empty<string>();
        try { return EnumerateWindows(pid); }
        catch { return Array.Empty<string>(); }
    }

    [SupportedOSPlatform("windows")]
    static string[] EnumerateWindows(int pid)
    {
        var titles = new List<(string title, bool likelyDialog)>();

        EnumWindows((hWnd, _) =>
        {
            GetWindowThreadProcessId(hWnd, out var owner);
            if (owner != pid) return true;
            if (!IsWindowVisible(hWnd)) return true;

            var title = new StringBuilder(512);
            GetWindowText(hWnd, title, title.Capacity);
            var cls = new StringBuilder(256);
            GetClassName(hWnd, cls, cls.Capacity);

            var text = title.ToString();
            if (string.IsNullOrWhiteSpace(text)) return true;

            var className = cls.ToString();
            // Unity's own top-level editor window is "UnityContainerWndClass"; modal dialogs are
            // either the Win32 "#32770" dialog class or an owned Unity popup.
            var isDialog = className == "#32770" || GetWindow(hWnd, GW_OWNER) != IntPtr.Zero;
            // Only dialog-like windows are reported. Naming Unity's own main window as the
            // blocking dialog would be a confident wrong answer, which is worse than none.
            if (isDialog) titles.Add((text, true));
            return true;
        }, IntPtr.Zero);

        return titles.OrderByDescending(t => t.likelyDialog).Select(t => t.title).ToArray();
    }

    const uint GW_OWNER = 4;

    delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")] static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hWnd, out int lpdwProcessId);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);
    [DllImport("user32.dll")] static extern IntPtr GetWindow(IntPtr hWnd, uint uCmd);
}
