using Umcp.Daemon.Agent;

namespace Umcp.Daemon.Mirror;

/// <summary>
/// Keeps the mirrors honest without being asked.
///
/// Two jobs. It wires the mirror to the registry's session events — handshake seeds, mirror frames
/// apply — and it periodically compares each mirror's hashes against the live hierarchy, repairing
/// any drift by re-seeding.
///
/// The periodic pass exists because <c>ObjectChangeEvents</c> is not guaranteed to cover every
/// mutation. Treating that gap as "probably fine" is how a cache starts lying; treating it as a
/// reconcile trigger costs one hash comparison a minute.
/// </summary>
public sealed class MirrorReconciler : BackgroundService
{
    readonly MirrorService _mirror;
    readonly EditorRegistry _registry;
    readonly Dispatcher _dispatcher;
    readonly ILogger<MirrorReconciler> _log;

    public MirrorReconciler(MirrorService mirror, EditorRegistry registry, Dispatcher dispatcher,
                            ILogger<MirrorReconciler> log)
    {
        _mirror = mirror;
        _registry = registry;
        _dispatcher = dispatcher;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        _mirror.Attach(_dispatcher);
        _registry.SessionHandshake += _mirror.OnHandshake;
        _registry.SessionEvent += (session, kind, node) => _mirror.OnAgentEvent(session, kind, node);

        // Anything already connected when this starts still needs seeding.
        foreach (var session in _registry.Sessions) _mirror.OnHandshake(session);

        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(TimeSpan.FromSeconds(60), ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }

            foreach (var session in _registry.Sessions)
            {
                if (!session.Alive || session.Reloading || session.Compiling) continue;
                try
                {
                    var result = await _mirror.ReconcileAsync(session, ct).ConfigureAwait(false);
                    if ((bool?)result["drift"] == true)
                        _log.LogInformation("periodic reconcile repaired drift on {Project}", session.ProjectName);
                }
                catch (Exception e)
                {
                    _log.LogDebug("reconcile skipped for {Project}: {Message}", session.ProjectName, e.Message);
                }
            }
        }
    }
}
