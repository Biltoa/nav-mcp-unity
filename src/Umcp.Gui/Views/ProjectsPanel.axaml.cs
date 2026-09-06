using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using Umcp.Gui.ViewModels;

namespace Umcp.Gui.Views;

public partial class ProjectsPanel : UserControl
{
    public ProjectsPanel() => AvaloniaXamlLoader.Load(this);

    async void OnLinkClicked(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel model) return;

        var storage = TopLevel.GetTopLevel(this)?.StorageProvider;
        if (storage is null) return;

        var folders = await storage.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Choose a Unity project folder",
            AllowMultiple = false
        });

        var folder = folders.FirstOrDefault();
        if (folder is null) return;

        // TryGetLocalPath is null for a folder that is not on this machine's filesystem — a
        // network location the picker surfaced, say. Linking one would write a manifest entry
        // Unity cannot resolve, so it is refused with the reason.
        await model.LinkAsync(folder.TryGetLocalPath() ?? "");
    }
}
