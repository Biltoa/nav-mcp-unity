using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using System.Windows.Input;
using Umcp.Gui.Services;

namespace Umcp.Gui.ViewModels;

/// <summary>
/// Everything the window shows, and the only place that decides what a state <em>means</em>.
///
/// The wording matters more than the plumbing here. "reloading", "E_EDITOR_BLOCKED" and
/// "agentResolved: false" are accurate and useless to the person this app exists for, so each one
/// is turned into a sentence that says what happened and what to do about it.
/// </summary>
public sealed class MainViewModel : INotifyPropertyChanged
{
    readonly ControlClient _client = new();
    readonly DaemonProcess _daemon = new();
    readonly GuiSettings _settings = GuiSettings.Load();

    public MainViewModel()
    {
        _client.Port = _settings.HttpPort;
        _profile = _settings.Profile;

        ToggleServerCommand = new RelayCommand(async _ => await ToggleServerAsync(), _ => !Busy);
        RefreshCommand = new RelayCommand(async _ => await RefreshAsync());
        PauseCommand = new RelayCommand(async _ => await SetPausedAsync(!Paused), _ => Running);
        OpenLogFolderCommand = new RelayCommand(_ => Reveal(UmcpPaths.LogDir));

        UnlinkCommand = new RelayCommand(async p => await UnlinkAsync(p as ProjectRow));
        OpenProjectCommand = new RelayCommand(async p => await OpenProjectAsync(p as ProjectRow), p => p is ProjectRow { CanOpen: true });
        CloseProjectCommand = new RelayCommand(async p => await CloseProjectAsync(p as ProjectRow), p => p is ProjectRow { Connected: true });
        RestartProjectCommand = new RelayCommand(async p => await RestartProjectAsync(p as ProjectRow), p => p is ProjectRow { IsOpen: true });
        RevealProjectCommand = new RelayCommand(p => { if (p is ProjectRow row) Reveal(row.Path); });

        ConnectClientCommand = new RelayCommand(p => ConnectClient(p as ClientRow));
        DisconnectClientCommand = new RelayCommand(p => DisconnectClient(p as ClientRow));

        Projects.CollectionChanged += (_, _) => Raise(nameof(HasNoProjects));

        AllowAllCommand = new RelayCommand(async _ => await SetAllAsync(true));
        AllowNoneCommand = new RelayCommand(async _ => await SetAllAsync(false));

        foreach (var client in ClientRegistrations.Known())
            Clients.Add(new ClientRow(client));
        RefreshClientRows();
    }

    // ---------------------------------------------------------------- state shown in the window

    bool _running, _busy, _paused;
    string _serverHeadline = "Server stopped";
    string _serverDetail = "Start the server, then link the Unity projects you want to control.";
    string _message = "";
    string _profile = "standard";
    string? _daemonVersion;
    string? _packagePath;
    int _editorCount;
    string _serverMeta = "";

    public bool Running { get => _running; private set { if (Set(ref _running, value)) { Raise(nameof(NotRunning)); Raise(nameof(ServerDotBrush)); Raise(nameof(EditorSummary)); Refresh(ToggleServerCommand, PauseCommand); } } }
    public bool NotRunning => !Running;

    // ---- which page the sidebar is showing. Radio buttons bind two-way, so setting one
    // ---- true must publish the other three as false.
    Page _page = Page.Overview;
    public enum Page { Overview, Projects, Connections, Settings }

    public bool IsOverview { get => _page == Page.Overview; set { if (value) Go(Page.Overview); } }
    public bool IsProjects { get => _page == Page.Projects; set { if (value) Go(Page.Projects); } }
    public bool IsConnections { get => _page == Page.Connections; set { if (value) Go(Page.Connections); } }
    public bool IsSettings { get => _page == Page.Settings; set { if (value) Go(Page.Settings); } }

    /// <summary>Overview centres itself in the window, so it lives outside the scroller.</summary>
    public bool NotOverview => _page != Page.Overview;

    void Go(Page page)
    {
        if (_page == page) return;
        _page = page;
        Raise(nameof(IsOverview));
        Raise(nameof(IsProjects));
        Raise(nameof(IsConnections));
        Raise(nameof(IsSettings));
        Raise(nameof(NotOverview));
    }
    /// <summary>
    /// Green when it is up, amber when paused, grey when it is not — the first thing a person
    /// looks at.
    ///
    /// A brush, not a hex string. A <c>SolidColorBrush</c> written inside <c>Ellipse.Fill</c> is
    /// not in the visual tree, so it never inherits a DataContext and a binding on its Color
    /// resolves to nothing at all: the dot keeps its space in the layout and is simply never
    /// painted, which is a hard bug to see and an easy one to avoid.
    /// </summary>
    public Avalonia.Media.IBrush ServerDotBrush =>
        Brushes.Of(Running ? (Paused ? "#FF8723" : "#3FB950") : "#5A6472");
    public bool HasNoProjects => Projects.Count == 0;
    public bool Busy { get => _busy; private set { if (Set(ref _busy, value)) Refresh(ToggleServerCommand); } }
    public bool Paused { get => _paused; private set { if (Set(ref _paused, value)) { Raise(nameof(ServerDotBrush)); Raise(nameof(PauseLabel)); } } }
    public string ServerHeadline { get => _serverHeadline; private set => Set(ref _serverHeadline, value); }
    public string ServerDetail { get => _serverDetail; private set => Set(ref _serverDetail, value); }
    public string Message { get => _message; private set => Set(ref _message, value); }

    /// <summary>readonly | standard | full — the second lock, after loopback and the token.</summary>
    public string Profile
    {
        get => _profile;
        set
        {
            if (!Set(ref _profile, value)) return;
            _settings.Profile = value;
            _settings.Save();
            if (Running) Message = "The profile applies the next time the server starts.";
        }
    }

    public string[] Profiles { get; } = { "readonly", "standard", "full" };

    public int Port
    {
        get => _settings.HttpPort;
        set
        {
            if (_settings.HttpPort == value) return;
            _settings.HttpPort = value;
            _settings.AgentPort = value + 1;
            _settings.Save();
            _client.Port = value;
            Raise(nameof(Port));
            Raise(nameof(Snippet));
            RefreshClientRows();
        }
    }

    public ObservableCollection<ProjectRow> Projects { get; } = new();
    public ObservableCollection<ClientRow> Clients { get; } = new();

    /// <summary>Every Editor tool, and whether a person has allowed it.</summary>
    public ObservableCollection<ToolRow> Tools { get; } = new();

    public string ToggleServerLabel => Running ? "Stop server" : "Start server";
    public string PauseLabel => Paused ? "Resume" : "Pause";

    /// <summary>The line under the headline: where it is listening and what it is carrying.</summary>
    public string ServerMeta => _serverMeta;

    /// <summary>
    /// The line under the buttons. Running, it explains that closing the window is safe —
    /// the question everybody asks once. Stopped, it says what is waiting, so the page reports
    /// readiness rather than looking broken.
    /// </summary>
    public string ServerFootnote
    {
        get
        {
            if (Running) return "Closing this window leaves the server running.";
            var linked = Projects.Count;
            return linked == 0
                ? "Link a Unity project once the server is up."
                : $"{linked} project{(linked == 1 ? "" : "s")} linked and waiting.";
        }
    }

    /// <summary>Shown in the sidebar, so "is anything actually attached" needs no navigation.</summary>
    public string EditorSummary => !Running
        ? "Server stopped"
        : _editorCount switch
        {
            0 => "No editor connected",
            1 => "1 editor connected",
            var n => $"{n} editors connected"
        };

    public string VersionLine => _daemonVersion is null
        ? $"NAV MCP {BuildVersion}"
        : $"NAV MCP {BuildVersion} · server {_daemonVersion}";

    static string BuildVersion =>
        System.Reflection.Assembly.GetExecutingAssembly().GetName().Version is { } v
            ? $"{v.Major}.{v.Minor}.{v.Build}"
            : "1.0.0";

    public string TokenPath => UmcpPaths.TokenFile;
    public string PackagePath => _packagePath ?? "not found next to this app";

    public bool StartServerOnLaunch
    {
        get => _settings.StartServerOnLaunch;
        set { _settings.StartServerOnLaunch = value; _settings.Save(); Raise(nameof(StartServerOnLaunch)); }
    }

    public bool StopServerOnExit
    {
        get => _settings.StopServerOnExit;
        set { _settings.StopServerOnExit = value; _settings.Save(); Raise(nameof(StopServerOnExit)); }
    }

    /// <summary>The config snippet for a client this app does not know about.</summary>
    public string Snippet =>
        ClientRegistrations.LocateShim() is { } shim
            ? ClientRegistrations.Snippet(shim, _settings.HttpPort)
            : "The umcp-stdio helper is missing from this install.";

    public ICommand ToggleServerCommand { get; }
    public ICommand RefreshCommand { get; }
    public ICommand PauseCommand { get; }
    public ICommand OpenLogFolderCommand { get; }
    public ICommand UnlinkCommand { get; }
    public ICommand OpenProjectCommand { get; }
    public ICommand CloseProjectCommand { get; }
    public ICommand RestartProjectCommand { get; }
    public ICommand RevealProjectCommand { get; }
    public ICommand AllowAllCommand { get; }
    public ICommand AllowNoneCommand { get; }
    public ICommand ConnectClientCommand { get; }
    public ICommand DisconnectClientCommand { get; }

    // ---------------------------------------------------------------- the server

    public async Task StartupAsync()
    {
        // Attach to a server that is already running before deciding whether to start one: the
        // daemon is designed to outlive every client, so finding one is the normal case, not
        // the exception.
        if (await _client.HealthAsync() is not null) { await RefreshAsync(); return; }
        if (_settings.StartServerOnLaunch) await ToggleServerAsync(start: true);
        else await RefreshAsync();
    }

    public async Task ShutdownAsync()
    {
        if (_settings.StopServerOnExit && _daemon.OwnsProcess)
            await _daemon.StopAsync(_client);
    }

    async Task ToggleServerAsync(bool? start = null)
    {
        var starting = start ?? !Running;
        Busy = true;
        Message = starting ? "Starting the server…" : "Stopping the server…";
        try
        {
            var (ok, message) = starting
                ? await _daemon.StartAsync(_settings, _client)
                : await _daemon.StopAsync(_client);
            Message = message;
            if (!ok && starting) ServerDetail = message;
        }
        finally
        {
            Busy = false;
            await RefreshAsync();
        }
    }

    async Task SetPausedAsync(bool on)
    {
        var result = await _client.PauseAsync(on);
        Paused = (bool?)result?["paused"] ?? on;
        Message = Paused
            ? "Paused. The server stays up and holds new operations until you resume."
            : "Resumed.";
    }

    /// <summary>Poll. Called on a timer and after every action, so it must never throw.</summary>
    public async Task RefreshAsync()
    {
        JsonObject? status;
        try { status = await _client.StatusAsync(); }
        catch { status = null; }

        if (status is null || (bool?)status["ok"] != true)
        {
            // A server that answers /health but not /api is an older umcpd holding the port —
            // one started by hand, or by a client through the shim, before this app existed.
            // Reporting that as "stopped" would be a lie the user can disprove in one click.
            if (await _client.HealthAsync() is { } health)
            {
                Running = false;
                ServerHeadline = "A different server is on this port";
                ServerDetail =
                    $"umcpd {(string?)health["daemon"]?["version"]} (process {(int?)health["daemon"]?["pid"]}) is using port " +
                    $"{_settings.HttpPort}, and it is too old for this app to control. Stop it, or move this app to another port in Settings.";
                _serverMeta = ServerDetail;
                Raise(nameof(ServerMeta));
                Raise(nameof(ServerFootnote));
                foreach (var row in Projects) row.ApplyServerDown();
                Raise(nameof(ToggleServerLabel));
                return;
            }

            Running = false;
            ServerHeadline = "Server stopped";
            ServerDetail = _settings.StartServerOnLaunch
                ? "Nothing is listening on port " + _settings.HttpPort + "."
                : "Press Start server to begin.";
            _serverMeta = $"Nothing is listening on 127.0.0.1:{_settings.HttpPort}. Your AI assistants cannot reach Unity.";
            Raise(nameof(ServerMeta));
            Raise(nameof(ServerFootnote));
            foreach (var row in Projects) row.ApplyServerDown();
            _allTools.Clear();
            Tools.Clear();
            Raise(nameof(ToolSummary));
            Raise(nameof(ToggleServerLabel));
            return;
        }

        Running = true;
        Paused = (bool?)status["paused"] ?? false;

        var daemon = status["daemon"];
        var editors = status["editors"] as JsonArray ?? new JsonArray();
        var connected = editors.Count;

        _daemonVersion = (string?)daemon?["version"];
        _packagePath = (string?)status["packagePath"];
        _editorCount = connected;
        Raise(nameof(VersionLine));
        Raise(nameof(PackagePath));
        Raise(nameof(EditorSummary));
        ServerHeadline = Paused ? "Server running — paused" : "Server running";
        ServerDetail =
            $"Listening on 127.0.0.1:{(int?)daemon?["httpPort"]} · {(int?)daemon?["tools"]} Editor tools · " +
            $"profile {(string?)daemon?["profile"]} · " +
            $"{connected} editor{(connected == 1 ? "" : "s")} connected · " +
            $"{(int?)daemon?["memory"]?["workingSetMB"]} MB";

        _serverMeta =
            $"127.0.0.1:{(int?)daemon?["httpPort"]}   ·   {(int?)daemon?["tools"]} Editor tools   ·   " +
            $"profile {(string?)daemon?["profile"]}   ·   " +
            (connected == 0 ? "no editor connected" : $"{connected} editor{(connected == 1 ? "" : "s")} connected");
        Raise(nameof(ServerMeta));
        Raise(nameof(ServerFootnote));

        MergeProjects(status["projects"] as JsonArray ?? new JsonArray(), editors);
        await LoadToolsAsync();
        Raise(nameof(ToggleServerLabel));
    }

    void MergeProjects(JsonArray projects, JsonArray editors)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var node in projects)
        {
            if (node is not JsonObject project) continue;
            var path = (string?)project["path"];
            if (path is null) continue;
            seen.Add(path);

            var row = Projects.FirstOrDefault(r => string.Equals(r.Path, path, StringComparison.OrdinalIgnoreCase));
            if (row is null)
            {
                row = new ProjectRow(path);
                Projects.Add(row);
            }

            // The editor entry, matched by the project's own GUID rather than by path: the id is
            // what the daemon routes on, and two checkouts of one project share a name.
            var projectId = (string?)project["projectId"];
            var editor = editors.OfType<JsonObject>()
                                .FirstOrDefault(e => projectId is not null && (string?)e["projectId"] == projectId);
            row.Apply(project, editor);
        }

        foreach (var gone in Projects.Where(r => !seen.Contains(r.Path)).ToList())
            Projects.Remove(gone);
    }

    // ---------------------------------------------------------------- projects

    /// <summary>Called by the view once a folder has been picked.</summary>
    public async Task LinkAsync(string path)
    {
        if (!Running)
        {
            Message = "Start the server first — it is what writes the link into the project.";
            return;
        }

        var result = await _client.LinkAsync(path);
        var name = (string?)result?["name"] ?? System.IO.Path.GetFileName(path.TrimEnd('/'));

        Message = (bool?)result?["ok"] != true
            ? (string?)result?["message"] ?? "Could not link that folder."
            : (bool?)result?["alreadyLinked"] == true
                // Linking the same folder twice is a normal mis-click, not an error. The list
                // holds it exactly once either way; say so rather than appearing to do nothing.
                ? $"{name} is already linked — nothing to do."
                : $"{name}: {(string?)result?["nextStep"]}";
        await RefreshAsync();
    }

    async Task UnlinkAsync(ProjectRow? row)
    {
        if (row is null) return;
        var result = await _client.UnlinkAsync(row.Path);
        Message = (bool?)result?["removedDependency"] == true
            ? $"Unlinked {row.Name}. Unity removes the package next time it resolves; nothing else in the project was touched."
            : $"Removed {row.Name} from the list.";
        await RefreshAsync();
    }

    async Task OpenProjectAsync(ProjectRow? row)
    {
        if (row is null) return;
        Message = $"Opening {row.Name} in Unity — a cold import can take minutes.";
        var result = await _client.OpenAsync(row.Path);
        Message = (bool?)result?["ok"] == true
            ? $"Unity is starting on {row.Name}."
            : (string?)result?["message"] ?? (string?)result?["error"] ?? $"Could not open {row.Name}.";
        await RefreshAsync();
    }

    async Task CloseProjectAsync(ProjectRow? row)
    {
        if (row?.ProjectId is null) return;
        var result = await _client.CloseAsync(row.ProjectId);
        Message = (bool?)result?["ok"] == true
            ? $"Asked Unity to save and close {row.Name}."
            : (string?)result?["message"] ?? $"Could not close {row.Name}.";
        await RefreshAsync();
    }

    async Task RestartProjectAsync(ProjectRow? row)
    {
        if (row is null) return;
        var target = row.ProjectId ?? row.Path;
        var result = await _client.RestartAsync(target);
        Message = (bool?)result?["ok"] == true
            ? $"Restarting {row.Name}."
            : (string?)result?["message"] ?? $"Could not restart {row.Name}.";
        await RefreshAsync();
    }

    // ---------------------------------------------------------------- permissions

    string _toolQuery = "";
    readonly List<ToolRow> _allTools = new();
    bool _applyingTools;

    /// <summary>The search box over the tool list.</summary>
    public string ToolQuery
    {
        get => _toolQuery;
        set { if (Set(ref _toolQuery, value)) ApplyToolFilter(); }
    }

    public string ToolSummary => _allTools.Count == 0
        ? "Loading the tool list…"
        : $"{_allTools.Count(t => t.Allowed)} of {_allTools.Count} tools allowed" +
          (Tools.Count == _allTools.Count ? "" : $" · showing {Tools.Count}");

    /// <summary>
    /// Fetched once per server connection rather than on every poll: the catalog is fixed for the
    /// life of a daemon, and re-reading 92 rows every two seconds would fight the checkboxes.
    /// </summary>
    async Task LoadToolsAsync()
    {
        if (_allTools.Count > 0) return;

        JsonObject? answer;
        try { answer = await _client.ToolsAsync(); }
        catch { return; }
        if (answer is null || (bool?)answer["ok"] != true) return;

        _allTools.Clear();
        foreach (var node in (answer["tools"] as JsonArray ?? new JsonArray()).OfType<JsonObject>())
            _allTools.Add(new ToolRow(node, OnToolToggled));

        ApplyToolFilter();
    }

    void ApplyToolFilter()
    {
        Tools.Clear();
        foreach (var tool in _allTools.Where(t => t.Matches(_toolQuery))) Tools.Add(tool);
        Raise(nameof(ToolSummary));
    }

    void OnToolToggled(ToolRow row)
    {
        if (_applyingTools) return;
        _ = SaveToolsAsync(row);
    }

    async Task SaveToolsAsync(ToolRow? changed)
    {
        var disabled = _allTools.Where(t => !t.Allowed).Select(t => t.Id).ToArray();
        var answer = await _client.SetToolsAsync(disabled);

        Raise(nameof(ToolSummary));
        Message = (bool?)answer?["ok"] == true
            ? changed is null
                ? $"{(int?)answer?["allowed"]} tools allowed, {(int?)answer?["disabled"]} turned off."
                : $"{changed.Id} is now {(changed.Allowed ? "allowed" : "turned off")}. It applies to the next call — no restart."
            : "Could not save the permission. Is the server running?";
    }

    async Task SetAllAsync(bool allowed)
    {
        // Only what the search is showing: "allow none" with a filter active means "none of
        // these", which is the useful meaning and the one the button is next to.
        _applyingTools = true;
        foreach (var tool in Tools) tool.SetQuietly(allowed);
        _applyingTools = false;
        await SaveToolsAsync(null);
    }

    // ---------------------------------------------------------------- AI clients

    void ConnectClient(ClientRow? row)
    {
        if (row is null) return;
        var shim = ClientRegistrations.LocateShim();
        if (shim is null)
        {
            Message = "The umcp-stdio helper is missing from this install, so nothing can be connected. Reinstall.";
            return;
        }

        var (ok, message) = ClientRegistrations.Register(row.Client, shim, _settings.HttpPort);
        Message = message;
        if (ok) RefreshClientRows();
    }

    void DisconnectClient(ClientRow? row)
    {
        if (row is null) return;
        var (ok, message) = ClientRegistrations.Unregister(row.Client);
        Message = message;
        if (ok) RefreshClientRows();
    }

    public void RefreshClientRows()
    {
        var shim = ClientRegistrations.LocateShim();
        foreach (var row in Clients) row.Refresh(shim, _settings.HttpPort);
    }

    // ---------------------------------------------------------------- odds and ends

    public async Task CopyAsync(string text, Avalonia.Input.Platform.IClipboard clipboard, string? note = null)
    {
        await clipboard.SetTextAsync(text);
        Message = note ?? "Copied.";
    }

    /// <summary>
    /// The bearer token, for someone wiring a client this app does not know about by hand. It is
    /// worth saying out loud that it changes on every server start, because a config file written
    /// with yesterday's token fails with "unauthorized" against a server that is working.
    /// </summary>
    public Task CopyTokenAsync(Avalonia.Input.Platform.IClipboard clipboard) =>
        _client.Token is { } token
            ? CopyAsync(token, clipboard, "Token copied. It changes every time the server restarts — the Connect buttons above avoid that entirely.")
            : Task.Run(() => Message = "No token yet; start the server first.");

    /// <summary>Show a folder in the platform's file manager. Never a shell string — an argument list.</summary>
    public static void Reveal(string path)
    {
        try
        {
            var psi = OperatingSystem.IsWindows() ? new System.Diagnostics.ProcessStartInfo("explorer.exe")
                    : OperatingSystem.IsMacOS() ? new System.Diagnostics.ProcessStartInfo("open")
                    : new System.Diagnostics.ProcessStartInfo("xdg-open");
            psi.ArgumentList.Add(path);
            psi.UseShellExecute = false;
            System.Diagnostics.Process.Start(psi);
        }
        catch { /* no file manager is not an error worth a dialog */ }
    }

    static void Refresh(params ICommand[] commands)
    {
        foreach (var command in commands) (command as RelayCommand)?.RaiseCanExecuteChanged();
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
