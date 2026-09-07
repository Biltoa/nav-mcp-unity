using System.ComponentModel;
using System.Runtime.CompilerServices;
using Umcp.Gui.Services;

namespace Umcp.Gui.ViewModels;

/// <summary>
/// One MCP client on this machine, and whether it has been pointed at this server.
///
/// "Not installed" is reported as a fact, not an error: most people have one of these, not three.
/// </summary>
public sealed class ClientRow : INotifyPropertyChanged
{
    public ClientRow(McpClient client) => Client = client;

    public McpClient Client { get; }
    public string Name => Client.Name;

    bool _connected, _installed;
    string _detail = "";

    public bool Connected { get => _connected; private set { if (Set(ref _connected, value)) Raise(nameof(NotConnected)); } }
    public bool NotConnected => !Connected;
    public bool Installed { get => _installed; private set => Set(ref _installed, value); }
    public string Detail { get => _detail; private set => Set(ref _detail, value); }

    /// <summary>Green when wired up, orange when it is here and waiting, grey when absent.</summary>
    public Avalonia.Media.IBrush DotBrush =>
        Brushes.Of(Connected ? "#3FB950" : Installed ? "#FF8723" : "#5A6472");

    public void Refresh(string? shimPath, int port)
    {
        Installed = Client.DirectoryExists || Client.ConfigExists;
        Connected = shimPath is not null && ClientRegistrations.IsRegistered(Client, shimPath, port);

        Raise(nameof(DotBrush));

        Detail = Connected
            ? $"Connected on port {port}. {Client.Hint}"
            : Installed
                ? "Found on this machine, not connected yet."
                : "Not found on this machine — connecting anyway creates its config file.";
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        Raise(name);
        return true;
    }

    void Raise(string? name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
