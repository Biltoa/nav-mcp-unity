using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Umcp.Gui.ViewModels;

namespace Umcp.Gui.Views;

public partial class ConnectionsPanel : UserControl
{
    public ConnectionsPanel() => AvaloniaXamlLoader.Load(this);

    async void OnCopySnippet(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel model) return;
        if (TopLevel.GetTopLevel(this)?.Clipboard is not { } clipboard) return;
        await model.CopyAsync(model.Snippet, clipboard, "Config copied. Paste it into your client's MCP settings.");
    }
}
