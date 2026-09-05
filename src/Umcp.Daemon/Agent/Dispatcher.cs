using System.Diagnostics;
using System.Text.Json.Nodes;
using Umcp.Daemon.Generated;

namespace Umcp.Daemon.Agent;

/// <summary>
/// Owns the request lifecycle, so that a domain reload is invisible to the caller.
///
///   agent → daemon: op(id, idempotencyKey)
///   daemon:         enqueue, mark in-flight
///                   ├─ editor connected → dispatch
///                   └─ editor reloading → hold (the caller sees a slow call, not an error)
///   editor reconnects, announces epoch N+1
///   daemon:         replay held ops; the agent drops any key it already applied
///
/// Two separate clocks matter here. The <em>hold</em> clock covers waiting for an editor to exist
/// and is generous (a domain reload can take a minute). The <em>op</em> clock only starts once the
/// op has actually been handed to a live editor. Conflating them is what turns a normal recompile
/// into an agent-visible error.
/// </summary>
public sealed class Dispatcher
{
    readonly EditorRegistry _registry;
    readonly DaemonOptions _options;
    readonly AuditLog _audit;
    readonly DaemonState _state;
    readonly ILogger<Dispatcher> _log;

    public Dispatcher(EditorRegistry registry, DaemonOptions options, AuditLog audit, DaemonState state, ILogger<Dispatcher> log)
    {
        _registry = registry;
        _options = options;
        _audit = audit;
        _state = state;
        _log = log;
    }

    public Task<JsonObject> RunToolAsync(string tool, JsonObject args, string? projectId, bool dryRun, CancellationToken ct)
    {
        if (!ToolCatalog.ById.TryGetValue(tool, out var entry))
        {
            return Task.FromResult(Envelope.Error("E_TOOL_NOT_FOUND", $"No tool named '{tool}'.",
                param: "tool", value: tool,
                didYouMean: Fuzzy.Closest(tool, ToolCatalog.All.Select(e => e.Id), 3),
                hint: "Use unity.find to search the catalog."));
        }

        var validation = SchemaCheck.Validate(entry, args);
        if (validation is not null) return Task.FromResult(validation);

        var message = new JsonObject
        {
            ["t"] = "op",
            ["tool"] = tool,
            ["args"] = args.DeepClone(),
            ["key"] = Guid.NewGuid().ToString("N"),
            ["dryRun"] = dryRun
        };

        return DispatchAsync(message, projectId, TimeoutFor(entry.Retry), tool, entry.Mutating && !dryRun, ct);
    }

    public Task<JsonObject> RunBatchAsync(JsonObject batch, string? projectId, CancellationToken ct)
    {
        batch["t"] = "batch";
        batch["key"] = Guid.NewGuid().ToString("N");

        // A batch is as slow as its slowest class.
        var timeout = _options.WriteTimeout;
        if (batch["ops"] is JsonArray ops)
        {
            foreach (var op in ops)
            {
                var id = (string?)op?["op"];
                if (id is not null && ToolCatalog.ById.TryGetValue(id, out var e) && e.Retry == "Compile")
                    timeout = _options.CompileTimeout;
            }
        }
        return DispatchAsync(batch, projectId, timeout, "unity.batch", mutating: true, ct);
    }

    TimeSpan TimeoutFor(string retryClass) => retryClass switch
    {
        "Compile" => _options.CompileTimeout,
        "Write" => _options.WriteTimeout,
        "None" => _options.WriteTimeout,
        _ => _options.ReadTimeout
    };

    async Task<JsonObject> DispatchAsync(JsonObject message, string? projectId, TimeSpan opTimeout,
                                         string label, bool mutating, CancellationToken ct)
    {
        if (_state.Paused)
            return Envelope.Error("E_PAUSED", "The daemon is paused from the tray UI.",
                hint: "Uncheck \"Pause operations\" in the Unity MCP Tool tray menu.");

        var total = Stopwatch.StartNew();
        var holdDeadline = DateTime.UtcNow + _options.HoldTimeout;
        var attempts = 0;
        var heldMs = 0L;

        while (true)
        {
            attempts++;

            var session = _registry.Get(projectId);
            if (session is null)
            {
                var holdStart = Stopwatch.StartNew();
                var remaining = holdDeadline - DateTime.UtcNow;
                if (remaining <= TimeSpan.Zero)
                    return NoEditor(projectId, total.ElapsedMilliseconds, heldMs);

                session = await _registry.WaitForAsync(projectId, remaining, ct).ConfigureAwait(false);
                heldMs += holdStart.ElapsedMilliseconds;
                if (session is null)
                    return NoEditor(projectId, total.ElapsedMilliseconds, heldMs);
            }

            var result = await SendWatchedAsync(session, message, opTimeout, ct).ConfigureAwait(false);

            switch (result.Outcome)
            {
                case SendOutcome.Completed:
                    {
                        var envelope = Envelope.FromAgentResult(result.Result!, session, heldMs, attempts, _options.MaxResponseBytes);
                        if (mutating) _audit.Write(label, session.ProjectId, message, envelope);
                        return envelope;
                    }

                case SendOutcome.Blocked:
                    return Envelope.Error("E_EDITOR_BLOCKED", result.Detail ?? "The Editor main thread is not ticking.",
                        hint: "Unity is very likely showing a modal dialog. Dismiss it in the Editor; queued operations " +
                              "will then run. This daemon never raises modals of its own.",
                        meta: new JsonObject
                        {
                            ["ms"] = total.ElapsedMilliseconds,
                            ["heldMs"] = heldMs,
                            ["epoch"] = session.Epoch,
                            ["project"] = session.ProjectName
                        });

                case SendOutcome.Disconnected:
                    // The editor went away mid-flight — almost always a domain reload. Hold and
                    // replay with the same idempotency key rather than surfacing an error.
                    if (DateTime.UtcNow < holdDeadline)
                    {
                        _log.LogInformation("{Label}: editor went away mid-op, holding for replay (attempt {N})", label, attempts);
                        var holdStart = Stopwatch.StartNew();
                        var next = await _registry.WaitForAsync(projectId ?? session.ProjectId,
                            holdDeadline - DateTime.UtcNow, ct).ConfigureAwait(false);
                        heldMs += holdStart.ElapsedMilliseconds;
                        if (next is not null) continue;
                    }
                    return Envelope.Error("E_EDITOR_GONE",
                        "The Unity editor disconnected and did not come back within the hold window.",
                        hint: "Check that the Editor is still running. The daemon stays up regardless.",
                        meta: new JsonObject { ["ms"] = total.ElapsedMilliseconds, ["heldMs"] = heldMs });

                default:
                    return Envelope.Error("E_TIMEOUT",
                        $"'{label}' did not complete within {opTimeout.TotalSeconds:0}s.",
                        hint: "The Editor is ticking but the operation is slow. Check unity.status.",
                        meta: new JsonObject
                        {
                            ["ms"] = total.ElapsedMilliseconds,
                            ["heldMs"] = heldMs,
                            ["lastRoundTripMs"] = session.LastRoundTripMs
                        });
            }
        }
    }

    /// <summary>
    /// Send, while a watchdog independently asks the out-of-band control channel whether the main
    /// thread is still ticking. Without this, a wedged Editor looks exactly like a slow one until
    /// the op timeout expires — which is how a modal dialog turned into a silent 30 s timeout.
    /// </summary>
    async Task<OpResult> SendWatchedAsync(AgentSession session, JsonObject message, TimeSpan opTimeout, CancellationToken ct)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var send = session.SendAsync(message, opTimeout, linked.Token);
        var watch = WatchForBlockAsync(session, linked.Token);

        var winner = await Task.WhenAny(send, watch).ConfigureAwait(false);
        if (winner == send)
        {
            linked.Cancel();
            return await send.ConfigureAwait(false);
        }

        var detail = await watch.ConfigureAwait(false);
        if (detail is null)
        {
            // Watchdog gave up without a verdict; fall back to the ordinary send.
            return await send.ConfigureAwait(false);
        }
        linked.Cancel();
        return new OpResult(SendOutcome.Blocked, null, 0, detail);
    }

    async Task<string?> WatchForBlockAsync(AgentSession session, CancellationToken ct)
    {
        try
        {
            await Task.Delay(_options.BlockProbeAfter, ct).ConfigureAwait(false);

            while (!ct.IsCancellationRequested)
            {
                var status = await session.ProbeControlAsync(ct: ct).ConfigureAwait(false);
                if (status is not null)
                {
                    var tickAge = (long?)status["msSinceTick"] ?? 0;
                    if (tickAge >= _options.BlockedTickAge.TotalMilliseconds)
                    {
                        var titles = WindowInspector.DialogTitles(session.UnityPid);
                        var named = titles.Length > 0 ? $" Dialog: \"{titles[0]}\"." : "";
                        return $"No main-thread tick for {tickAge / 1000.0:0.0} s while {(long?)status["queueDepth"] ?? 0} " +
                               $"operation(s) are queued.{named}";
                    }
                }
                await Task.Delay(1000, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { }
        return null;
    }

    JsonObject NoEditor(string? projectId, long ms, long heldMs)
    {
        var known = _registry.KnownProjectIds;
        return Envelope.Error("E_NO_EDITOR",
            projectId is null
                ? "No Unity editor is connected."
                : $"No connected editor for project '{projectId}'.",
            hint: known.Length == 0
                ? "Open the project in Unity with the com.umcp.agent package installed. The daemon keeps running either way."
                : "Connected projects: " + string.Join(", ", known),
            meta: new JsonObject { ["ms"] = ms, ["heldMs"] = heldMs });
    }
}
