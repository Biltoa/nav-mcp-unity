using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Umcp.Gui.ViewModels;
using Umcp.Gui.Views;

namespace Umcp.Gui;

public partial class App : Application
{
    MainWindow? _window;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // Closing the window must not stop the server. The daemon outliving its clients is
            // the design; an app that killed it on a window close would make "close the window"
            // a destructive action nobody expected.
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;

            var model = new MainViewModel();
            _window = new MainWindow { DataContext = model };
            desktop.MainWindow = _window;
            _window.Show();

            desktop.ShutdownRequested += async (_, _) => await model.ShutdownAsync();
        }

        base.OnFrameworkInitializationCompleted();
    }

    void OnTrayClicked(object? sender, EventArgs e) => ShowWindow();
    void OnTrayOpen(object? sender, EventArgs e) => ShowWindow();

    void OnTrayQuit(object? sender, EventArgs e)
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            desktop.Shutdown();
    }

    void ShowWindow()
    {
        if (_window is null) return;
        _window.Show();
        _window.WindowState = WindowState.Normal;
        _window.Activate();
    }
}
