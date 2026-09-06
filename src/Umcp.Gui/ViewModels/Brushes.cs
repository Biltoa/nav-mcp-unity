using System.Collections.Concurrent;
using Avalonia.Media;

namespace Umcp.Gui.ViewModels;

/// <summary>
/// Brushes by hex, made once.
///
/// Status colours are read on every poll; allocating a SolidColorBrush per read is a small leak
/// with a two-second clock on it, and identical brush instances also let the renderer skip work.
/// </summary>
public static class Brushes
{
    static readonly ConcurrentDictionary<string, IBrush> Cache = new();

    public static IBrush Of(string hex) =>
        Cache.GetOrAdd(hex, h =>
        {
            var brush = new SolidColorBrush(Color.Parse(h));
            brush.ToImmutable();
            return brush.ToImmutable();
        });
}
