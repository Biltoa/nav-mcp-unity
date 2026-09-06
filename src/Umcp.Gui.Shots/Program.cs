using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Headless;
using Avalonia.Threading;
using Umcp.Gui;
using Umcp.Gui.ViewModels;
using Umcp.Gui.Views;

// Screenshots of the shipping UI, rendered off-screen.
//
// The alternative is a mock-up, and a mock-up is a drawing of what the app is supposed to look
// like. This boots the real App with its real styles, binds the real view model to whatever
// server is running, and captures the actual frames — so a screenshot in the documentation
// cannot drift from the product without this failing.
//
//   umcp-shots [output-dir] [--width 1280] [--height 860]
//
// It runs inside a real classic-desktop lifetime rather than SetupWithoutStarting: layout,
// bindings and rendering all need a dispatcher that is actually running, and a capture taken
// without one hangs waiting for a frame that will never be composed.

var output = Args.Positional() ?? "docs/images";
var width = Args.Int("--width", 1280);
var height = Args.Int("--height", 860);
Directory.CreateDirectory(output);

var builder = AppBuilder.Configure<App>()
    .UseSkia()
    .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
    .WithInterFont();

// The lambda's parameter is the AppBuilder; naming it "app" keeps the discard below a discard.
builder = builder.AfterSetup(app =>
{
    Dispatcher.UIThread.Post(() => { _ = CaptureAsync(); }, DispatcherPriority.Background);
});

builder.StartWithClassicDesktopLifetime(Array.Empty<string>());
return 0;

async Task CaptureAsync()
{
    var desktop = (IClassicDesktopStyleApplicationLifetime)Application.Current!.ApplicationLifetime!;
    var window = desktop.MainWindow!;
    var model = (MainViewModel)window.DataContext!;

    window.Width = width;
    window.Height = height;

    // Let the window's own startup finish: it attaches to a running server and polls once.
    await Task.Delay(4000);
    await model.RefreshAsync();
    await Task.Delay(1200);

    foreach (var (show, name) in new (Action, string)[]
             {
                 (() => model.IsOverview = true, "overview"),
                 (() => model.IsProjects = true, "projects"),
                 (() => model.IsConnections = true, "connections"),
                 (() => model.IsSettings = true, "settings")
             })
    {
        show();
        await Task.Delay(700);
        Save(window, Path.Combine(output, name + ".png"));
    }

    // The stopped state deserves a frame of its own: it is the first thing a new user sees, and
    // photographing it should not mean stopping somebody's running daemon.
    model.IsOverview = true;
    model.ForceStoppedForCapture();
    await Task.Delay(700);
    Save(window, Path.Combine(output, "overview-stopped.png"));

    desktop.Shutdown();
}

static void Save(Window window, string path)
{
    var frame = window.CaptureRenderedFrame();
    if (frame is null) { Console.Error.WriteLine($"[shots] no frame for {path}"); return; }
    frame.Save(path);
    Console.WriteLine($"[shots] {path}  {frame.PixelSize.Width}x{frame.PixelSize.Height}");
}

static class Args
{
    public static string? Positional() =>
        Environment.GetCommandLineArgs().Skip(1).FirstOrDefault(a => !a.StartsWith('-'));

    public static int Int(string name, int dflt)
    {
        var a = Environment.GetCommandLineArgs();
        for (var i = 0; i < a.Length - 1; i++)
            if (a[i] == name && int.TryParse(a[i + 1], out var v)) return v;
        return dflt;
    }
}
