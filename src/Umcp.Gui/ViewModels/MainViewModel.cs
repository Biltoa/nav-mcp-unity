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
    int _last24h;
    int _failures24h;
    double? _medianMs;
    bool _last24hCapped;
    int _activityTick;

    public bool Running { get => _running; private set { if (Set(ref _running, value)) { Raise(nameof(NotRunning)); Raise(nameof(ServerDot)); Raise(nameof(EditorSummary)); Refresh(ToggleServerCommand, PauseCommand); } } }
    public bool NotRunning => !Running;

    // ---- which page the sidebar is showing. Radio buttons bind two-way, so setting one
    // ---- true must publish the other three as false.
    Page _page = Page.Overview;
    public enum Page { Overview, Projects, Connections, Settings }

    public bool IsOverview { get => _page == Page.Overview; set { if (value) Go(Page.Overview); } }
    public bool IsProjects { get => _page == Page.Projects; set { if (value) Go(Page.Projects); } }
    public bool IsConnections { get => _page == Page.Connections; set { if (value) Go(Page.Connections); } }
    public bool IsSettings { get => _page == Page.Settings; set { if (value) Go(Page.Settings); } }

    void Go(Page page)
    {
        if (_page == page) return;
        _page = page;
        Raise(nameof(IsOverview));
        Raise(nameof(IsProjects));
        Raise(nameof(IsConnections));
        Raise(nameof(IsSettings));
    }
    /// <summary>Green when it is up, grey when it is not: the first thing a person looks at.</summary>
    public string ServerDot => Running ? (Paused ? "#F59E0B" : "#10B981") : "#6B7280";
    public bool HasNoProjects => Projects.Count == 0;
    public bool Busy { get => _busy; private set { if (Set(ref _busy, value)) Refresh(ToggleServerCommand); } }
    public bool Paused { get => _paused; private set { if (Set(ref _paused, value)) { Raise(nameof(ServerDot)); Raise(nameof(PauseLabel)); } } }
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

    /// <summary>What the AI has been doing, newest first. The Overview's reason to exist.</summary>
    public ObservableCollection<ActivityRow> Activity { get; } = new();

    /// <summary>Either the setup steps still outstanding, or the states that want a human.</summary>
    public ObservableCollection<AttentionRow> Attention { get; } = new();

    public bool HasActivity => Activity.Count > 0;
    public bool HasNoActivity => Activity.Count == 0;

    // ---- the tiles across the top of the Overview. Counts, not prose.
    public string EditorsTile => _editorCount.ToString();
    public string ProjectsTile => Projects.Count.ToString();
    public string ClientsTile => Clients.Count(c => c.Connected).ToString();
    public string OperationsTile => _last24hCapped ? $"{_last24h}+" : _last24h.ToString();

    public string EditorsTileNote => _editorCount == 1 ? "Unity editor connected" : "Unity editors connected";
    public string ProjectsTileNote => Projects.Count == 1 ? "project linked" : "projects linked";
    public string ClientsTileNote => "AI assistants connected";
    public string OperationsTileNote => _medianMs is null
        ? "operations in 24 hours"
        : $"in 24 h · {_medianMs:0} ms typical";

    public string ActivityHeadline => _failures24h > 0
        ? $"Recent activity — {_failures24h} failed in the last 24 hours"
        : "Recent activity";

    public string ToggleServerLabel => Running ? "Stop server" : "Start server";
    public string PauseLabel => Paused ? "Resume" : "Pause";

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
                foreach (var row in Projects) row.ApplyServerDown();
                Raise(nameof(ToggleServerLabel));
                return;
            }

            Running = false;
            ServerHeadline = "Server stopped";
            ServerDetail = _settings.StartServerOnLaunch
                ? "Nothing is listening on port " + _settings.HttpPort + "."
                : "Press Start server to begin.";
            foreach (var row in Projects) row.ApplyServerDown();
            Activity.Clear();
            BuildAttention(new JsonArray());
            RaiseTiles();
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

        MergeProjects(status["projects"] as JsonArray ?? new JsonArray(), editors);
        await RefreshActivityAsync();
        BuildAttention(editors);
        RaiseTiles();
        Raise(nameof(ToggleServerLabel));
    }

    /// <summary>
    /// Pull the audit tail. Rebuilt wholesale rather than appended to: entries carry relative
    /// times ("4 minutes ago") that go stale on their own, so the list is cheap to replace and
    /// wrong to keep.
    /// </summary>
    async Task RefreshActivityAsync()
    {
        // Every third poll. The status tick has to be quick because it drives the dots; the audit
        // tail is thousands of lines to parse and nothing in it changes meaningfully in two
        // seconds.
        if (_activityTick++ % 3 != 0) return;

        JsonObject? feed;
        try { feed = await _client.ActivityAsync(25); }
        catch { return; }
        if (feed is null || (bool?)feed["ok"] != true) return;

        _last24h = (int?)feed["last24h"] ?? 0;
        _last24hCapped = (bool?)feed["last24hCapped"] ?? false;
        _failures24h = (int?)feed["failures24h"] ?? 0;
        _medianMs = (double?)feed["medianMs"];

        var rows = (feed["entries"] as JsonArray ?? new JsonArray())
                   .OfType<JsonObject>().Select(ActivityRow.From).ToList();

        // Only touch the collection when something actually changed: a list that rebuilds every
        // two seconds cannot be scrolled or selected.
        if (rows.Count == Activity.Count && rows.Zip(Activity).All(p => p.First == p.Second)) return;

        Activity.Clear();
        foreach (var row in rows) Activity.Add(row);
        Raise(nameof(HasActivity));
        Raise(nameof(HasNoActivity));
        Raise(nameof(ActivityHeadline));
    }

    /// <summary>
    /// The one card that changes with the situation: setup steps while things are missing, and
    /// what has gone wrong once they are not. Both answer "what do I do next", which is the only
    /// question an overview is for.
    /// </summary>
    void BuildAttention(JsonArray editors)
    {
        var rows = new List<AttentionRow>();

        rows.Add(new AttentionRow(
            Running ? "Server running" : "Start the server",
            Running ? $"Listening on 127.0.0.1:{_settings.HttpPort}." : "Nothing is listening yet — press Start server above.",
            Running ? "#3FB950" : "#FF8723", Running));

        var linked = Projects.Count;
        rows.Add(new AttentionRow(
            linked > 0 ? $"{linked} project{(linked == 1 ? "" : "s")} linked" : "Link a Unity project",
            linked > 0 ? "Projects tab lists them and what each one is doing." : "Projects tab · Link a project… and choose a Unity project folder.",
            linked > 0 ? "#3FB950" : "#FF8723", linked > 0));

        var connected = Clients.Count(c => c.Connected);
        rows.Add(new AttentionRow(
            connected > 0 ? $"{connected} assistant{(connected == 1 ? "" : "s")} connected" : "Connect an AI assistant",
            connected > 0 ? "Ask it to run unity_projects to check the link." : "Connections tab · Connect next to Claude Desktop, Claude Code or Cursor.",
            connected > 0 ? "#3FB950" : "#FF8723", connected > 0));

        // Anything actively wrong is appended, because a blocked Editor outranks a checklist.
        foreach (var project in Projects)
        {
            if (project.Status is "Waiting on you" or "Click the Unity window" or "Not linked" or "Folder missing")
                rows.Add(new AttentionRow($"{project.Name}: {project.Status.ToLowerInvariant()}", project.Detail, project.Badge, false));
        }

        if (rows.Count == Attention.Count && rows.Zip(Attention).All(p => p.First == p.Second)) return;
        Attention.Clear();
        foreach (var row in rows) Attention.Add(row);
    }

    void RaiseTiles()
    {
        Raise(nameof(EditorsTile));
        Raise(nameof(ProjectsTile));
        Raise(nameof(ClientsTile));
        Raise(nameof(OperationsTile));
        Raise(nameof(EditorsTileNote));
        Raise(nameof(ProjectsTileNote));
        Raise(nameof(OperationsTileNote));
        Raise(nameof(ActivityHeadline));
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
        Message = (bool?)result?["ok"] == true
            ? $"{Path.GetFileName(path.TrimEnd('/', '\\'))}: {(string?)result?["nextStep"]}"
            : (string?)result?["message"] ?? "Could not link that folder.";
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
