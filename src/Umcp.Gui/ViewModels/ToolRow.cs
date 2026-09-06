using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;

namespace Umcp.Gui.ViewModels;

/// <summary>
/// One Editor tool and whether it is allowed.
///
/// The checkbox writes through to the daemon, which enforces it in the dispatcher — a permission
/// that lives only in this window is not a permission, it is a suggestion the AI never sees.
/// </summary>
public sealed class ToolRow : INotifyPropertyChanged
{
    readonly Action<ToolRow> _changed;
    bool _allowed;

    public ToolRow(JsonObject json, Action<ToolRow> changed)
    {
        _changed = changed;
        Id = (string?)json["id"] ?? "";
        Domain = (string?)json["domain"] ?? "";
        Summary = (string?)json["summary"] ?? "";
        Mutating = (bool?)json["mutating"] ?? false;
        _allowed = (bool?)json["allowed"] ?? true;
    }

    public string Id { get; }
    public string Domain { get; }
    public string Summary { get; }
    public bool Mutating { get; }

    /// <summary>"changes the project" is the distinction people actually care about.</summary>
    public string Kind => Mutating ? "writes" : "reads";

    public bool Allowed
    {
        get => _allowed;
        set
        {
            if (_allowed == value) return;
            _allowed = value;
            Raise(nameof(Allowed));
            _changed(this);
        }
    }

    /// <summary>Set without telling the daemon — used when the server's answer is applied back.</summary>
    public void SetQuietly(bool allowed)
    {
        if (_allowed == allowed) return;
        _allowed = allowed;
        Raise(nameof(Allowed));
    }

    public bool Matches(string query)
    {
        if (string.IsNullOrWhiteSpace(query)) return true;
        return Id.Contains(query, StringComparison.OrdinalIgnoreCase)
            || Domain.Contains(query, StringComparison.OrdinalIgnoreCase)
            || Summary.Contains(query, StringComparison.OrdinalIgnoreCase);
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    void Raise([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
