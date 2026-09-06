using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Umcp.Gui.ViewModels;

namespace Umcp.Gui.Views;

public partial class MainWindow : Window
{
    readonly DispatcherTimer _poll = new() { Interval = TimeSpan.FromSeconds(2) };

    public MainWindow()
    {
        InitializeComponent();

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
        // tray menu is the way out — the daemon outliving its clients is the design, and a
        // window close that stopped it would be a destructive action nobody asked for.
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

    async void OnLinkClicked(object? sender, RoutedEventArgs e)
    {
        if (Model is not { } model) return;

        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Choose a Unity project folder",
            AllowMultiple = false
        });

        var folder = folders.FirstOrDefault();
        if (folder is null) return;

        // TryGetLocalPath is null for a folder that is not on this machine's filesystem — a
        // network share the picker surfaced, say. Linking one would write a manifest entry Unity
        // cannot resolve, so it is refused with the reason.
        var path = folder.TryGetLocalPath();
        if (path is null)
        {
            await model.LinkAsync("");   // the daemon answers with the sentence to show
            return;
        }

        await model.LinkAsync(path);
    }

    async void OnCopySnippet(object? sender, RoutedEventArgs e)
    {
        if (Model is { } model && Clipboard is { } clipboard)
            await model.CopyAsync(model.Snippet, clipboard, "Config copied. Paste it into your client's MCP settings.");
    }

    async void OnCopyToken(object? sender, RoutedEventArgs e)
    {
        if (Model is { } model && Clipboard is { } clipboard)
            await model.CopyTokenAsync(clipboard);
    }
}
