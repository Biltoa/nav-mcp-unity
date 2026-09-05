using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json.Nodes;
using Umcp.Daemon.Agent;

namespace Umcp.Daemon.Fleet;

/// <summary>What the daemon knows about one Editor process it is responsible for.</summary>
public sealed class LaunchRecord
{
    public required string ProjectPath { get; init; }
    public string? ProjectId { get; set; }
    public int Pid { get; set; }
    public string? ExePath { get; set; }
    public string? Version { get; set; }
    public DateTimeOffset StartedAt { get; set; } = DateTimeOffset.Now;
    public bool Launched { get; set; }
    public bool AutoRestart { get; set; }
    /// <summary>Set while a close or restart this daemon asked for is in progress, so the supervisor does not "recover" it.</summary>
    public bool ClosingDeliberately { get; set; }
    public string? LastExit { get; set; }
    public int Restarts { get; set; }
}

/// <summary>
/// <c>unity.projects.*</c>: list, open, close, use, restart — plus the supervisor that notices an
/// Editor dying and, when asked to, brings it back.
///
/// Three rules this class exists to hold:
///
///   * **The daemon outlives every Editor.** Nothing here ever exits the daemon, and there is no
///     parent-PID watchdog anywhere: an Editor dying is an event to report, not a reason to stop.
///   * **A disconnect is not a crash.** A domain reload closes the socket too. The supervisor
///     waits, then asks the operating system whether the process is still there, and only then
///     decides.
///   * **Restarting is bounded.** <see cref="CrashLoopBreaker"/>, per project.
/// </summary>
public sealed class FleetService : BackgroundService
{
    readonly EditorRegistry _registry;
    readonly Dispatcher _dispatcher;
    readonly DaemonOptions _options;
    readonly ILogger<FleetService> _log;
    readonly CrashLoopBreaker _breaker = new();
    readonly ConcurrentDictionary<string, LaunchRecord> _records = new(StringComparer.OrdinalIgnoreCase);

    public FleetService(EditorRegistry registry, Dispatcher dispatcher, DaemonOptions options, ILogger<FleetService> log)
    {
        _registry = registry;
        _dispatcher = dispatcher;
        _options = options;
        _log = log;

        _registry.SessionHandshake += OnHandshake;
        _registry.SessionClosed += OnClosed;
    }

    public IReadOnlyCollection<LaunchRecord> Records => _records.Values.ToArray();
    public CrashLoopBreaker Breaker => _breaker;

    // ---------------------------------------------------------------- adoption and supervision

    void OnHandshake(AgentSession s)
    {
        if (string.IsNullOrEmpty(s.ProjectPath)) return;
        var key = EditorInstalls.Normalise(s.ProjectPath);

        // An Editor the user opened by hand is adopted on sight: the daemon can then restart it
        // on request without having launched it. It is not auto-restarted unless someone asks —
        // silently relaunching a window a human closed is not recovery, it is a haunting.
        var rec = _records.GetOrAdd(key, _ => new LaunchRecord
        {
            ProjectPath = key,
            AutoRestart = _options.AutoRestart
        });

        rec.ProjectId = s.ProjectId;
        rec.Pid = s.UnityPid;
        rec.Version = s.UnityVersion;
        rec.ClosingDeliberately = false;
        rec.LastExit = null;
        _breaker.Reset(key);
    }

    void OnClosed(AgentSession s)
    {
        if (string.IsNullOrEmpty(s.ProjectPath)) return;
        var key = EditorInstalls.Normalise(s.ProjectPath);
        if (!_records.TryGetValue(key, out var rec)) return;
        if (rec.ClosingDeliberately) return;

        _ = Task.Run(() => AdjudicateAsync(rec, s));
    }

    /// <summary>
    /// Decide whether a closed socket was a reload or a death, then act. This runs off the socket
    /// thread and is allowed to take its time — the dispatcher is independently holding any ops.
    /// </summary>
    async Task AdjudicateAsync(LaunchRecord rec, AgentSession s)
    {
        // A domain reload tears the socket down and rebuilds it in a few seconds. Give it that
        // long before calling the process dead.
        for (var i = 0; i < 20; i++)
        {
            await Task.Delay(500).ConfigureAwait(false);
            if (_registry.Get(rec.ProjectId) is { Alive: true }) return;      // reconnected: a reload
            if (!EditorLauncher.IsAlive(rec.Pid)) break;                       // process gone: a death
            if (i == 19) return;                                               // still running, still silent: not ours to kill
        }

        rec.LastExit = $"process {rec.Pid} exited at {DateTimeOffset.Now:O}";
        _log.LogWarning("editor for {Project} died (pid {Pid}){Auto}", rec.ProjectPath, rec.Pid,
            rec.AutoRestart ? " — auto-restart is on" : "");

        if (!rec.AutoRestart) return;
        if (!_breaker.TryRestart(rec.ProjectPath, out var refusal))
        {
            _log.LogError("not restarting {Project}: {Refusal}", rec.ProjectPath, refusal);
            rec.LastExit += " · " + refusal;
            return;
        }

        rec.Restarts++;
        var result = await OpenAsync(rec.ProjectPath, rec.Version, installAgent: false, wait: true,
            timeout: TimeSpan.FromMinutes(5), CancellationToken.None).ConfigureAwait(false);
        _log.LogInformation("auto-restart of {Project}: {Ok}", rec.ProjectPath, (bool?)result["ok"] == true ? "connected" : "failed");
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        // A slow sweep for the case the socket never closed but the process is gone (a kill -9
        // can leave a half-open socket for minutes). Cheap, and it costs the Editor nothing.
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(15), ct).ConfigureAwait(false);
                foreach (var rec in _records.Values)
                {
                    if (rec.Pid <= 0 || rec.ClosingDeliberately) continue;
                    if (EditorLauncher.IsAlive(rec.Pid)) continue;
                    if (_registry.Get(rec.ProjectId) is { Alive: true } live && live.UnityPid != rec.Pid) continue;
                    if (rec.LastExit is not null) continue;
                    rec.LastExit = $"process {rec.Pid} is gone (noticed by sweep)";
                    _log.LogWarning("sweep: editor for {Project} is gone (pid {Pid})", rec.ProjectPath, rec.Pid);
                }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception e) { _log.LogWarning(e, "fleet sweep failed"); }
        }
    }

    // ---------------------------------------------------------------- list

    public JsonObject List(bool includeKnownProjects = true, bool includeInstalls = true)
    {
        var o = new JsonObject();

        if (includeInstalls)
        {
            var installs = EditorInstalls.Scan();
            o["editorInstalls"] = new JsonArray(installs.Select(i => (JsonNode)new JsonObject
            {
                ["version"] = i.Version,
                ["path"] = EditorInstalls.Normalise(i.ExePath),
                ["source"] = EditorInstalls.Normalise(i.Source)
            }).ToArray());
        }

        if (includeKnownProjects)
        {
            var connected = _registry.Sessions
                .Select(s => EditorInstalls.Normalise(s.ProjectPath))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            var known = ProjectCatalog.FromHub().ToList();
            foreach (var rec in _records.Values)
                if (!known.Any(k => string.Equals(k.Path, rec.ProjectPath, StringComparison.OrdinalIgnoreCase)) &&
                    ProjectCatalog.IsProject(rec.ProjectPath))
                    known.Add(ProjectCatalog.Describe(rec.ProjectPath, source: "daemon"));

            o["knownProjects"] = new JsonArray(known.Take(40).Select(p =>
            {
                var j = ProjectCatalog.ToJson(p);
                j["connected"] = connected.Contains(p.Path);
                return (JsonNode)j;
            }).ToArray());
        }

        o["supervised"] = new JsonArray(_records.Values.Select(r => (JsonNode)new JsonObject
        {
            ["path"] = r.ProjectPath,
            ["projectId"] = r.ProjectId,
            ["pid"] = r.Pid,
            ["launchedByDaemon"] = r.Launched,
            ["autoRestart"] = r.AutoRestart,
            ["restarts"] = r.Restarts,
            ["restartsLeft"] = _breaker.Remaining(r.ProjectPath),
            ["alive"] = EditorLauncher.IsAlive(r.Pid),
            ["lastExit"] = r.LastExit
        }).ToArray());

        return o;
    }

    // ---------------------------------------------------------------- open

    public async Task<JsonObject> OpenAsync(string path, string? version, bool installAgent, bool wait,
                                            TimeSpan timeout, CancellationToken ct)
    {
        string full;
        try { full = EditorInstalls.Normalise(Path.GetFullPath(path)); }
        catch (Exception e) { return Envelope.Error("E_ARG_VALUE", $"'{path}' is not a usable path: {e.Message}", param: "open", value: path); }

        if (!ProjectCatalog.IsProject(full))
            return Envelope.Error("E_NOT_A_PROJECT",
                $"'{full}' has no Assets/ and ProjectSettings/, so it is not a Unity project.",
                param: "open", value: path,
                hint: "unity_projects lists known projects with their paths.");

        // Already connected? Say so and return the existing editor rather than launching a second
        // process that Unity's own lock will refuse.
        var existing = _registry.Sessions.FirstOrDefault(s =>
            string.Equals(EditorInstalls.Normalise(s.ProjectPath), full, StringComparison.OrdinalIgnoreCase) && s.Alive);
        if (existing is not null)
            return Envelope.Ok(new JsonObject
            {
                ["opened"] = false,
                ["reason"] = "already connected",
                ["projectId"] = existing.ProjectId,
                ["pid"] = existing.UnityPid,
                ["path"] = full
            });

        if (ProjectCatalog.IsLocked(full))
            return Envelope.Error("E_PROJECT_LOCKED",
                $"'{full}' is already open in a Unity Editor (Temp/UnityLockfile is held).",
                param: "open", value: path,
                hint: "Unity allows one Editor per project. If that Editor is not connected, it is " +
                      "missing the com.umcp.agent package; if it crashed, delete Temp/UnityLockfile.");

        var wantedVersion = version ?? EditorInstalls.ProjectVersion(full);
        var installs = EditorInstalls.Scan();
        var (install, match) = EditorInstalls.Choose(installs, wantedVersion);
        if (install is null)
            return Envelope.Error("E_NO_EDITOR_INSTALL",
                $"No installed Editor matches {wantedVersion ?? "any version"}.",
                param: "version", value: wantedVersion,
                hint: installs.Count == 0
                    ? "No Editor installs were found under the Hub's install roots."
                    : "Installed: " + string.Join(", ", installs.Select(i => i.Version)));

        var notes = new List<string>();
        if (match != "exact" && wantedVersion is not null)
            notes.Add($"Project wants {wantedVersion}; using {install.Version} ({match}). Unity will ask to upgrade the project.");

        if (installAgent)
        {
            var packageDir = AgentPackage.Locate(_options.PackagePath);
            if (packageDir is null) notes.Add("Could not locate the com.umcp.agent package directory; launching without installing it.");
            else
            {
                // Before launch, deliberately: Unity resolves the manifest at startup, whereas a
                // running Editor only re-resolves when its window regains focus.
                var (changed, detail) = AgentPackage.Ensure(full, packageDir);
                notes.Add(changed ? "manifest: " + detail : "manifest: " + detail);
            }
        }

        var args = EditorLauncher.BuildArguments(full);
        Process proc;
        try { proc = EditorLauncher.Start(install.ExePath, args); }
        catch (Exception e)
        {
            return Envelope.Error("E_LAUNCH_FAILED", $"Could not start {install.ExePath}: {e.Message}",
                meta: new JsonObject { ["commandLine"] = install.ExePath + " " + EditorLauncher.Render(args) });
        }

        var (verified, commandLine) = EditorLauncher.VerifyProjectPath(proc.Id, full);
        if (verified == false)
        {
            // The exact failure this check exists for: an unquoted path with spaces reaches Unity
            // as several arguments and the Editor exits 0 without opening anything.
            try { if (!proc.HasExited) proc.Kill(true); } catch { }
            return Envelope.Error("E_LAUNCH_ARGS",
                "The launched process did not receive the project path we passed; it was killed rather than left running against the wrong project.",
                meta: new JsonObject { ["commandLine"] = commandLine, ["wanted"] = full });
        }

        var key = full;
        var rec = _records.GetOrAdd(key, _ => new LaunchRecord { ProjectPath = key });
        rec.Pid = proc.Id;
        rec.ExePath = install.ExePath;
        rec.Version = install.Version;
        rec.StartedAt = DateTimeOffset.Now;
        rec.Launched = true;
        rec.ClosingDeliberately = false;
        rec.LastExit = null;
        rec.AutoRestart = rec.AutoRestart || _options.AutoRestart;

        var data = new JsonObject
        {
            ["opened"] = true,
            ["path"] = full,
            ["pid"] = proc.Id,
            ["editorVersion"] = install.Version,
            ["editorPath"] = EditorInstalls.Normalise(install.ExePath),
            ["versionMatch"] = match,
            ["argumentsVerified"] = verified,   // null means WMI could not tell us; that is not a failure
            ["commandLine"] = commandLine
        };

        if (!wait)
        {
            data["handshake"] = "not awaited";
            return Envelope.Ok(data, warnings: notes.ToArray());
        }

        var session = await WaitForPathAsync(full, proc, timeout, ct).ConfigureAwait(false);
        if (session is null)
        {
            var alive = EditorLauncher.IsAlive(proc.Id);
            var titles = alive ? WindowInspector.DialogTitles(proc.Id) : Array.Empty<string>();
            data["handshake"] = "timeout";
            data["processAlive"] = alive;
            if (titles.Length > 0) data["blockingWindow"] = titles[0];

            return Envelope.Error("E_HANDSHAKE_TIMEOUT",
                alive
                    ? $"Unity launched (pid {proc.Id}) but never handshook within {timeout.TotalSeconds:0}s. " +
                      (titles.Length > 0
                          ? $"It is showing a window titled \"{titles[0]}\" — a human has to dismiss it."
                          : "It may be showing a modal dialog (a crashed session reopens with a \"Recovering Scene Backups\" prompt), still importing, or missing the com.umcp.agent package.")
                    : $"Unity exited during startup without handshaking (pid {proc.Id}).",
                hint: "The daemon is unaffected and keeps running. unity_projects shows the process state.",
                meta: data);
        }

        rec.ProjectId = session.ProjectId;
        data["handshake"] = "connected";
        data["projectId"] = session.ProjectId;
        data["name"] = session.ProjectName;
        data["startupMs"] = (long)(DateTimeOffset.Now - rec.StartedAt).TotalMilliseconds;
        return Envelope.Ok(data, warnings: notes.ToArray());
    }

    /// <summary>Wait for a handshake from the Editor at this path — identified by path, never by "the newest session".</summary>
    async Task<AgentSession?> WaitForPathAsync(string fullPath, Process proc, TimeSpan timeout, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline && !ct.IsCancellationRequested)
        {
            var s = _registry.Sessions.FirstOrDefault(x => x.Alive &&
                string.Equals(EditorInstalls.Normalise(x.ProjectPath), fullPath, StringComparison.OrdinalIgnoreCase));
            if (s is not null) return s;

            if (proc.HasExited) return null;
            await Task.Delay(500, ct).ConfigureAwait(false);
        }
        return null;
    }

    // ---------------------------------------------------------------- close and restart

    public async Task<JsonObject> CloseAsync(string project, bool save, bool force, CancellationToken ct)
    {
        var session = Resolve(project);
        if (session is null)
            return Envelope.Error("E_NO_EDITOR", $"No connected editor matches '{project}'.",
                param: "close", value: project,
                didYouMean: Fuzzy.Closest(project, _registry.Sessions.SelectMany(s => new[] { s.ProjectId, s.ProjectName, s.ProjectPath }), 3),
                hint: "Connected: " + string.Join(", ", _registry.Sessions.Select(s => $"{s.ProjectName} ({s.ProjectId})")));

        var key = EditorInstalls.Normalise(session.ProjectPath);
        var rec = _records.GetOrAdd(key, _ => new LaunchRecord { ProjectPath = key });
        rec.ClosingDeliberately = true;
        var pid = session.UnityPid;

        var args = new JsonObject { ["save"] = save, ["force"] = force };
        var quit = await _dispatcher.RunToolAsync("editor.quit", args, session.ProjectId, false, ct).ConfigureAwait(false);
        if ((bool?)quit["ok"] != true)
        {
            rec.ClosingDeliberately = false;
            return quit;
        }

        var exited = await WaitForExitAsync(pid, TimeSpan.FromSeconds(60), ct).ConfigureAwait(false);
        rec.LastExit = exited ? $"closed on request at {DateTimeOffset.Now:O}" : null;
        rec.ClosingDeliberately = exited;

        return Envelope.Ok(new JsonObject
        {
            ["closed"] = exited,
            ["projectId"] = session.ProjectId,
            ["path"] = key,
            ["pid"] = pid,
            ["saved"] = quit["data"]?["saved"]?.DeepClone(),
            ["note"] = exited ? null : "Quit was accepted but the process was still alive after 60 s."
        });
    }

    public async Task<JsonObject> RestartAsync(string project, bool save, bool force, CancellationToken ct)
    {
        var session = Resolve(project);
        string path;
        string? version;

        if (session is not null)
        {
            path = EditorInstalls.Normalise(session.ProjectPath);
            version = session.UnityVersion;
            var closed = await CloseAsync(project, save, force, ct).ConfigureAwait(false);
            if ((bool?)closed["ok"] != true) return closed;
        }
        else
        {
            // Restarting something that is already dead is the post-crash case, and is the point.
            var rec = _records.Values.FirstOrDefault(r =>
                string.Equals(r.ProjectId, project, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(r.ProjectPath, EditorInstalls.Normalise(project), StringComparison.OrdinalIgnoreCase));
            if (rec is null)
                return Envelope.Error("E_NO_EDITOR", $"No connected or known editor matches '{project}'.",
                    param: "restart", value: project,
                    hint: "unity_projects lists connected editors and supervised projects.");
            path = rec.ProjectPath;
            version = rec.Version;
        }

        var opened = await OpenAsync(path, version, installAgent: false, wait: true,
            timeout: _options.OpenTimeout, ct).ConfigureAwait(false);
        if (opened["data"] is JsonObject d) d["restarted"] = true;
        return opened;
    }

    public JsonObject SetAutoRestart(string project, bool on)
    {
        var session = Resolve(project);
        var key = session is not null
            ? EditorInstalls.Normalise(session.ProjectPath)
            : _records.Keys.FirstOrDefault(k => k.Equals(EditorInstalls.Normalise(project), StringComparison.OrdinalIgnoreCase));

        if (key is null)
            return Envelope.Error("E_NO_EDITOR", $"No connected or known editor matches '{project}'.",
                param: "autoRestart", value: project);

        var rec = _records.GetOrAdd(key, _ => new LaunchRecord { ProjectPath = key });
        rec.AutoRestart = on;
        if (on) _breaker.Reset(key);
        return Envelope.Ok(new JsonObject { ["path"] = key, ["autoRestart"] = on, ["restartsLeft"] = _breaker.Remaining(key) });
    }

    static async Task<bool> WaitForExitAsync(int pid, TimeSpan timeout, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline && !ct.IsCancellationRequested)
        {
            if (!EditorLauncher.IsAlive(pid)) return true;
            await Task.Delay(250, ct).ConfigureAwait(false);
        }
        return !EditorLauncher.IsAlive(pid);
    }

    /// <summary>Resolve a project id, name or path to a live session. Ambiguity is refused, never guessed.</summary>
    AgentSession? Resolve(string project)
    {
        var byId = _registry.Get(project);
        if (byId is not null) return byId;

        var normalised = EditorInstalls.Normalise(project);
        return _registry.Sessions.FirstOrDefault(s => s.Alive &&
                   (string.Equals(EditorInstalls.Normalise(s.ProjectPath), normalised, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(s.ProjectName, project, StringComparison.OrdinalIgnoreCase)));
    }
}
