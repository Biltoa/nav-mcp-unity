using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;

namespace Umcp.Gui.ViewModels;

/// <summary>
/// One linked Unity project, as a person needs it described.
///
/// The states below are the ones that actually happen, and each has a sentence that says what to
/// do rather than what is wrong. "Not resolved yet" is the one that matters most: Unity re-reads
/// the package list only at startup or when its window regains focus, so a link made while the
/// Editor is open looks broken until someone clicks Unity once — and nobody guesses that.
/// </summary>
public sealed class ProjectRow : INotifyPropertyChanged
{
    public ProjectRow(string path)
    {
        Path = path;
        Name = System.IO.Path.GetFileName(path.TrimEnd('/', '\\'));
    }

    public string Path { get; }
    public string Name { get; }

    string _status = "…", _detail = "", _badge = "#6B7280";
    bool _connected, _isOpen, _canOpen;
    string? _projectId;

    public string Status { get => _status; private set => Set(ref _status, value); }
    public string Detail { get => _detail; private set => Set(ref _detail, value); }
    /// <summary>Colour of the status dot. Grey means "nothing wrong, nothing running".</summary>
    public string Badge { get => _badge; private set { if (Set(ref _badge, value)) Raise(nameof(BadgeBrush)); } }

    /// <summary>The dot itself. Bound to Fill directly — a brush inside Ellipse.Fill has no DataContext.</summary>
    public Avalonia.Media.IBrush BadgeBrush => Brushes.Of(_badge);
    public bool Connected { get => _connected; private set => Set(ref _connected, value); }
    public bool IsOpen { get => _isOpen; private set => Set(ref _isOpen, value); }
    public bool CanOpen { get => _canOpen; private set => Set(ref _canOpen, value); }
    public string? ProjectId { get => _projectId; private set => Set(ref _projectId, value); }

    public void Apply(JsonObject project, JsonObject? editor)
    {
        ProjectId = (string?)project["projectId"];
        Connected = (bool?)project["connected"] ?? false;
        IsOpen = (bool?)project["open"] ?? false;
        CanOpen = !IsOpen && (bool?)project["exists"] == true && (bool?)project["isProject"] == true;

        var exists = (bool?)project["exists"] ?? false;
        var isProject = (bool?)project["isProject"] ?? false;
        var referenced = (bool?)project["agentInstalled"] ?? false;
        var resolved = (bool?)project["agentResolved"] ?? false;
        var version = (string?)project["editorVersion"];

        if (!exists)
        {
            Set("Folder missing", "#EF4444", $"There is nothing at {Path} any more. Remove it from the list.");
            return;
        }
        if (!isProject)
        {
            Set("Not a Unity project", "#EF4444", "This folder has no Assets and ProjectSettings inside it.");
            return;
        }
        if (!referenced)
        {
            Set("Not linked", "#F59E0B", "The project's package list no longer mentions the agent. Link it again.");
            return;
        }

        if (Connected && editor is not null)
        {
            var health = (string?)editor["health"] ?? "ok";
            var reloading = (bool?)editor["reloading"] ?? false;
            var compiling = (bool?)editor["compiling"] ?? false;

            switch (health)
            {
                case "blocked":
                    var dialog = (string?)editor["blockingDialog"];
                    Set("Waiting on you", "#EF4444", dialog is null
                        ? "Unity is showing a dialog and cannot do anything until someone answers it."
                        : $"Unity is showing \"{dialog}\". Answer it in the Editor and work continues.");
                    return;
                case "degraded":
                    Set("Busy", "#F59E0B", "Unity is importing or baking. Operations are queued, not lost.");
                    return;
            }

            if (reloading) { Set("Recompiling", "#F59E0B", "Scripts are reloading. Reads still work; changes wait."); return; }
            if (compiling) { Set("Compiling", "#F59E0B", "Unity is compiling scripts."); return; }

            Set("Ready", "#10B981", $"Connected{(version is null ? "" : $" · Unity {version}")}.");
            return;
        }

        if (IsOpen)
        {
            // The Editor holds the project's lock but no agent has connected: either Unity has
            // not resolved the package yet, or it resolved it before the window was focused.
            Set(resolved ? "Connecting…" : "Click the Unity window", "#F59E0B",
                resolved
                    ? "Unity is open and the package is installed; the agent connects within a few seconds."
                    : "Unity is open but has not picked the package up yet. Click its window once — that is when Unity re-reads its package list.");
            return;
        }

        Set("Unity not open", "#6B7280",
            $"Linked{(version is null ? "" : $" · last opened with Unity {version}")}. Open it to control it.");
    }

    /// <summary>The server went away; say so once rather than leaving a stale green dot.</summary>
    public void ApplyServerDown()
    {
        Connected = false;
        CanOpen = false;
        Set("Server stopped", "#6B7280", "Start the server to see this project's state.");
    }

    void Set(string status, string badge, string detail)
    {
        Status = status;
        Badge = badge;
        Detail = detail;
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
