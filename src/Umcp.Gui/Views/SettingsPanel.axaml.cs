using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Umcp.Gui.ViewModels;

namespace Umcp.Gui.Views;

public partial class SettingsPanel : UserControl
{
    public SettingsPanel() => AvaloniaXamlLoader.Load(this);

    async void OnCopyToken(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel model) return;
        if (TopLevel.GetTopLevel(this)?.Clipboard is not { } clipboard) return;
        await model.CopyTokenAsync(clipboard);
    }
}
