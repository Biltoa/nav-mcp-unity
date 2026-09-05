using System.Diagnostics;
using System.Drawing;
using System.Runtime.Versioning;
using System.Windows.Forms;
using Umcp.Daemon.Agent;
using Umcp.Daemon.Security;

namespace Umcp.Daemon.Tray;

/// <summary>
/// The tray UI. Notifications go here and never to Unity: a modal <c>EditorUtility.DisplayDialog</c>
/// blocks <c>EditorApplication.update</c>, which is the message pump, and wedges the whole bridge
/// while the socket still reports healthy. An update-check dialog exactly like that is what
/// deadlocked the tool being replaced for an hour of debugging.
///
/// It runs on its own STA thread so it can never block request handling.
/// </summary>
[SupportedOSPlatform("windows")]
public static class TrayHost
{
    public static void Start(IServiceProvider services, DaemonOptions options, TokenStore tokens)
    {
        var thread = new Thread(() => Run(services, options, tokens))
        {
            IsBackground = true,
            Name = "umcpd-tray"
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
    }

    static void Run(IServiceProvider services, DaemonOptions options, TokenStore tokens)
    {
        var registry = services.GetRequiredService<EditorRegistry>();
        var state = services.GetRequiredService<DaemonState>();

        using var icon = new NotifyIcon
        {
            Icon = SystemIcons.Application,
            Visible = true,
            Text = "Unity MCP Tool"
        };

        var menu = new ContextMenuStrip();
        var status = new ToolStripMenuItem("Starting…") { Enabled = false };
        var pause = new ToolStripMenuItem("Pause operations") { CheckOnClick = true };
        pause.CheckedChanged += (_, _) => state.Paused = pause.Checked;

        menu.Items.Add(status);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(pause);
        menu.Items.Add("Copy bearer token", null, (_, _) => Clipboard.SetText(tokens.Token));
        menu.Items.Add("Open log folder", null, (_, _) =>
            Process.Start(new ProcessStartInfo("explorer.exe", Paths.LogDir) { UseShellExecute = true }));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Quit daemon", null, (_, _) =>
        {
            icon.Visible = false;
            services.GetRequiredService<IHostApplicationLifetime>().StopApplication();
            Application.ExitThread();
        });
        icon.ContextMenuStrip = menu;

        using var timer = new System.Windows.Forms.Timer { Interval = 2000 };
        timer.Tick += (_, _) =>
        {
            var sessions = registry.Sessions;
            var line = sessions.Count == 0
                ? "No editor connected"
                : string.Join(" · ", sessions.Select(s =>
                    $"{s.ProjectName}: {(s.Reloading ? "reloading" : s.Compiling ? "compiling" : "ready")} " +
                    $"({s.OpsCompleted} ops, rt {s.LastRoundTripMs} ms)"));
            status.Text = line;
            icon.Text = ("Unity MCP Tool — " + line).Length > 63
                ? ("Unity MCP Tool — " + line)[..63]
                : "Unity MCP Tool — " + line;
        };
        timer.Start();

        Application.Run();
    }
}
