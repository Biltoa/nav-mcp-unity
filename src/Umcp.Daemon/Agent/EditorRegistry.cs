using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;

namespace Umcp.Daemon.Agent;

/// <summary>
/// projectId → session. One daemon, one port, many editors.
///
/// Identity is a GUID minted by the project and stored in ProjectSettings, never a port number.
/// Port-per-project identity is how a server launched from one project ends up serving another.
/// </summary>
public sealed class EditorRegistry
{
    readonly ConcurrentDictionary<string, AgentSession> _byProject = new(StringComparer.OrdinalIgnoreCase);
    readonly ConcurrentDictionary<string, AgentSession> _recentReloads = new(StringComparer.OrdinalIgnoreCase);
    readonly ConcurrentDictionary<AgentSession, byte> _pending = new();
    readonly ConcurrentDictionary<string, List<TaskCompletionSource<AgentSession>>> _waiters = new(StringComparer.OrdinalIgnoreCase);
    readonly object _waiterLock = new();
    readonly ILogger<EditorRegistry> _log;

    public string? DefaultProjectId { get; set; }

    /// <summary>Raised once a session has identified itself.</summary>
    public event Action<AgentSession>? SessionHandshake;
    /// <summary>Raised when a session's socket goes away — a domain reload, a quit, or a crash.
    /// Which of those it was is the supervisor's job to decide, not this one's.</summary>
    public event Action<AgentSession>? SessionClosed;
    /// <summary>Raised for every lifecycle or mirror event an agent pushes.</summary>
    public event Action<AgentSession, string, System.Text.Json.Nodes.JsonNode>? SessionEvent;

    public EditorRegistry(ILogger<EditorRegistry> log) => _log = log;

    public IReadOnlyCollection<AgentSession> Sessions => _byProject.Values.ToArray();

    /// <summary>
    /// Live sessions plus the short-lived disconnected session left by a domain reload. Keeping
    /// that last snapshot makes status continuous while dispatch independently waits for the new
    /// AppDomain to connect. It is bounded to one entry per project and expires automatically.
    /// </summary>
    public IReadOnlyCollection<AgentSession> StatusSessions
    {
        get
        {
            // Match the default compile timeout: status must not disappear while dispatch is
            // still legitimately holding work for an unusually long Unity compile.
            const long maxReloadAgeMs = 300_000;
            foreach (var (id, session) in _recentReloads)
                if (session.MsSinceLastResponse >= maxReloadAgeMs)
                    _recentReloads.TryRemove(new KeyValuePair<string, AgentSession>(id, session));

            var live = _byProject.Values.ToArray();
            var liveIds = live.Select(s => s.ProjectId).ToHashSet(StringComparer.OrdinalIgnoreCase);
            return live.Concat(_recentReloads.Values.Where(s => !liveIds.Contains(s.ProjectId))).ToArray();
        }
    }

    public void Add(AgentSession session)
    {
        _pending[session] = 0;
        session.Handshake += OnHandshake;
        session.Closed += OnClosed;
        session.Event += (s, kind, node) => SessionEvent?.Invoke(s, kind, node);
        session.Start();
    }

    void OnHandshake(AgentSession s)
    {
        if (string.IsNullOrEmpty(s.ProjectId)) return;
        _pending.TryRemove(s, out _);
        _recentReloads.TryRemove(s.ProjectId, out _);

        if (_byProject.TryGetValue(s.ProjectId, out var existing) && existing != s)
        {
            if (existing.Alive && existing.UnityPid != s.UnityPid)
            {
                // Refuse a second live connection claiming an id that is already connected.
                _log.LogWarning("refusing duplicate connection for {ProjectId} (pid {New} vs {Existing})",
                    s.ProjectId, s.UnityPid, existing.UnityPid);
                _ = s.DisposeAsync();
                return;
            }
            _ = existing.DisposeAsync();
        }

        _byProject[s.ProjectId] = s;
        DefaultProjectId ??= s.ProjectId;
        _log.LogInformation("editor connected: {Project} ({ProjectId}) epoch {Epoch}, {Tools} tools, control port {Port}",
            s.ProjectName, s.ProjectId, s.Epoch, s.ToolCount, s.ControlPort);

        ReleaseWaiters(s);
        SessionHandshake?.Invoke(s);
    }

    void OnClosed(AgentSession s)
    {
        _pending.TryRemove(s, out _);
        if (!string.IsNullOrEmpty(s.ProjectId) && _byProject.TryGetValue(s.ProjectId, out var cur) && cur == s)
        {
            _byProject.TryRemove(s.ProjectId, out _);
            if (s.Reloading) _recentReloads[s.ProjectId] = s;
            _log.LogInformation("editor disconnected: {Project} ({ProjectId}){Reloading}",
                s.ProjectName, s.ProjectId, s.Reloading ? " — domain reload, holding ops" : "");
        }
        SessionClosed?.Invoke(s);
    }

    void ReleaseWaiters(AgentSession s)
    {
        List<TaskCompletionSource<AgentSession>>? list = null, anyList = null;
        lock (_waiterLock)
        {
            if (_waiters.TryRemove(s.ProjectId, out var l)) list = l;
            if (_waiters.TryRemove("*", out var a)) anyList = a;
        }
        foreach (var tcs in (list ?? new()).Concat(anyList ?? new())) tcs.TrySetResult(s);
    }

    public AgentSession? Get(string? projectId)
    {
        if (!string.IsNullOrEmpty(projectId))
            return _byProject.TryGetValue(projectId!, out var s) && s.Alive ? s : null;

        if (!string.IsNullOrEmpty(DefaultProjectId) &&
            _byProject.TryGetValue(DefaultProjectId!, out var d) && d.Alive) return d;

        var live = _byProject.Values.Where(x => x.Alive).ToArray();
        return live.Length == 1 ? live[0] : null;
    }

    public string[] KnownProjectIds => _byProject.Keys.ToArray();

    /// <summary>
    /// Wait for an editor to (re)appear. This is the hold half of hold-and-replay: during a
    /// domain reload the agent is simply gone, and the daemon owns the request lifecycle rather
    /// than surfacing a disconnect to the caller.
    /// </summary>
    public async Task<AgentSession?> WaitForAsync(string? projectId, TimeSpan timeout, CancellationToken ct)
    {
        var existing = Get(projectId);
        if (existing is not null) return existing;

        var key = string.IsNullOrEmpty(projectId) ? "*" : projectId!;
        var tcs = new TaskCompletionSource<AgentSession>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_waiterLock) _waiters.GetOrAdd(key, _ => new()).Add(tcs);

        // Re-check: a session may have arrived between Get() and registering the waiter.
        existing = Get(projectId);
        if (existing is not null) return existing;

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var done = await Task.WhenAny(tcs.Task, Task.Delay(timeout, timeoutCts.Token)).ConfigureAwait(false);
        timeoutCts.Cancel();

        if (done == tcs.Task) return await tcs.Task.ConfigureAwait(false);

        lock (_waiterLock)
            if (_waiters.TryGetValue(key, out var l)) l.Remove(tcs);
        return null;
    }
}

/// <summary>
/// The agent listener. Loopback only, explicitly — never <c>listen(port)</c> with no host.
/// </summary>
public sealed class AgentServer : BackgroundService
{
    readonly EditorRegistry _registry;
    readonly ILogger<AgentServer> _log;
    readonly ILoggerFactory _loggerFactory;
    readonly IHostApplicationLifetime _lifetime;
    readonly int _port;

    public AgentServer(EditorRegistry registry, ILogger<AgentServer> log, ILoggerFactory loggerFactory,
                       IHostApplicationLifetime lifetime, DaemonOptions options)
    {
        _registry = registry;
        _log = log;
        _loggerFactory = loggerFactory;
        _lifetime = lifetime;
        _port = options.AgentPort;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        var listener = new TcpListener(IPAddress.Loopback, _port);
        try
        {
            listener.Start();
        }
        catch (SocketException e)
        {
            // Almost always a second daemon. Say so once and shut down cleanly: an unhandled
            // exception out of a background service takes the host down with a stack trace, which
            // is the least useful way to say "that port is taken".
            Console.Error.WriteLine($"[umcpd] agent port {_port} is already in use ({e.SocketErrorCode}).");
            Console.Error.WriteLine("[umcpd] another daemon is probably running. Use it, or start this one with " +
                                   "--port <n> --agent-port <n>.");
            _lifetime.StopApplication();
            return;
        }

        _log.LogInformation("agent channel listening on 127.0.0.1:{Port}", _port);
        ct.Register(listener.Stop);

        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await listener.AcceptTcpClientAsync(ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
            catch (SocketException) { break; }

            _log.LogInformation("agent socket accepted from {Remote}; awaiting hello", client.Client.RemoteEndPoint);
            var session = new AgentSession(client);
            _registry.Add(session);
        }
    }
}
