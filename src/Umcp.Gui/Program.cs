using Avalonia;

namespace Umcp.Gui;

/// <summary>
/// The double-clickable half of the Unity MCP Tool: a window that starts the server, links Unity
/// projects to it and connects the AI clients on this machine, for people who should never have
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
