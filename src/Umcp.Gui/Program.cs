using Avalonia;

namespace Umcp.Gui;

/// <summary>
/// NAV MCP: the double-clickable half of the tool. A window that starts the server, links Unity
/// projects to it and connects the MCP clients on this machine, for people who should never have
/// to see a command line to use any of that.
/// </summary>
static class Program
{
    [STAThread]
    public static void Main(string[] args) =>
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
