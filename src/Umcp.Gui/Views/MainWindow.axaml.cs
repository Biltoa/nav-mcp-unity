using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Umcp.Gui.ViewModels;

namespace Umcp.Gui.Views;

public partial class MainWindow : Window
{
    readonly DispatcherTimer _poll = new() { Interval = TimeSpan.FromSeconds(2) };

    public MainWindow()
    {
        InitializeComponent();
        ApplyChrome();

        // Polling, rather than a push channel, on purpose: the state being shown changes on
        // Unity's schedule (a reload, an import, a dialog someone opened), and two seconds of
        // staleness in a window costs nothing while a socket that must be reconnected costs a
        // class of bug.
        _poll.Tick += async (_, _) => { if (Model is { } model) await model.RefreshAsync(); };

        Opened += async (_, _) =>
        {
            _poll.Start();
            if (Model is { } model) await model.StartupAsync();
        };

        // Closing the window leaves the server running and the app in the tray. Quit from the
        // tray menu is the way out.
        Closing += (_, e) =>
        {
            if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime
                { ShutdownMode: ShutdownMode.OnExplicitShutdown })
            {
                e.Cancel = true;
                Hide();
            }
        };
    }

    MainViewModel? Model => DataContext as MainViewModel;

    void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    /// <summary>
    /// Windows gets the mark in its own title bar. macOS keeps the system chrome — its traffic
    /// lights are muscle memory and an app that redraws them reads as a port of a Windows app —
    /// so the bar is only inset far enough to clear them.
    /// </summary>
    void ApplyChrome()
    {
        var buttons = this.FindControl<StackPanel>("WindowButtons");
        var brand = this.FindControl<StackPanel>("Brand");

        if (OperatingSystem.IsWindows())
        {
            ExtendClientAreaToDecorationsHint = true;
            ExtendClientAreaChromeHints = Avalonia.Platform.ExtendClientAreaChromeHints.NoChrome;
            ExtendClientAreaTitleBarHeightHint = -1;
            return;
        }

        if (buttons is not null) buttons.IsVisible = false;
        if (OperatingSystem.IsMacOS() && brand is not null) brand.Margin = new Thickness(78, 0, 0, 0);
    }

    void OnTitleBarPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!OperatingSystem.IsWindows()) return;
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) BeginMoveDrag(e);
    }

    void OnMinimise(object? sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    void OnMaximise(object? sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    void OnClose(object? sender, RoutedEventArgs e) => Close();

    async void OnCopyToken(object? sender, RoutedEventArgs e)
    {
        if (Model is { } model && Clipboard is { } clipboard)
            await model.CopyTokenAsync(clipboard);
    }
}
