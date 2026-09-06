using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json.Nodes;
using Umcp.Daemon.Generated;
using Umcp.Daemon.Mirror;
using Umcp.Daemon.Script;
using Umcp.Daemon.Security;

namespace Umcp.Daemon.Agent;

/// <summary>One step of a long operation, as the caller sees it.</summary>
public readonly record struct OpProgress(string Label, string Message, int Percent, long ElapsedMs);

sealed class NoopDisposable : IDisposable
{
    public void Dispose() { }
}

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
    readonly ScriptCompiler _compiler;
    readonly MirrorService _mirror;
    readonly ILogger<Dispatcher> _log;

    // Reference paths change only when the AppDomain does, so they are cached per (project, epoch).
    readonly ConcurrentDictionary<string, string[]> _referenceCache = new();

    public Dispatcher(EditorRegistry registry, DaemonOptions options, AuditLog audit, DaemonState state,
                      ScriptCompiler compiler, MirrorService mirror, ILogger<Dispatcher> log)
    {
        _mirror = mirror;
        _registry = registry;
        _options = options;
        _audit = audit;
        _state = state;
        _compiler = compiler;
        _log = log;
    }

    public Task<JsonObject> RunToolAsync(string tool, JsonObject args, string? projectId, bool dryRun,
                                        CancellationToken ct, int? maxBytes = null, bool verify = false,
                                        IProgress<OpProgress>? progress = null)
    {
        if (!ToolCatalog.ById.TryGetValue(tool, out var entry))
        {
            return Task.FromResult(Envelope.Error("E_TOOL_NOT_FOUND", $"No tool named '{tool}'.",
                param: "tool", value: tool,
                didYouMean: Fuzzy.Closest(tool, ToolCatalog.All.Select(e => e.Id), 3),
                hint: "Use unity.find to search the catalog."));
        }

        var denied = Profiles.Denies(_options.Profile, entry.Id, entry.Mutating);
        if (denied is not null)
            return Task.FromResult(Envelope.Error("E_PROFILE_DENIED", denied,
                hint: "Restart the daemon with --profile full if this is intended."));

        var validation = SchemaCheck.Validate(entry, args);
        if (validation is not null) return Task.FromResult(validation);

        var tooLarge = TooLarge(args, tool);
        if (tooLarge is not null) return Task.FromResult(tooLarge);

        // Reads go to the mirror first. It answers in microseconds, it does not need an Editor
        // tick, and it keeps answering while the Editor is mid-reload — which is when half an
        // agent's calls would otherwise fail. Mutations always go live; the mirror is never
        // authoritative for writes.
        if (!dryRun && !entry.Mutating)
        {
            var served = _mirror.TryServe(projectId, tool, args, verify, out var why);
            if (served is not null) return Task.FromResult(served);
            if (why is not null && MirrorCandidates.Contains(tool))
                _log.LogDebug("{Tool} went live: {Reason}", tool, why);
        }

        var message = new JsonObject
        {
            ["t"] = "op",
            ["tool"] = tool,
            ["args"] = args.DeepClone(),
            ["key"] = Guid.NewGuid().ToString("N"),
            ["dryRun"] = dryRun
        };

        return DispatchAsync(message, projectId, TimeoutFor(entry.Retry), tool, entry.Mutating && !dryRun, ct, maxBytes, progress);
    }

    public Task<JsonObject> RunBatchAsync(JsonObject batch, string? projectId, CancellationToken ct,
                                          IProgress<OpProgress>? progress = null)
    {
        var oversized = TooLarge(batch["ops"], "unity.batch");
        if (oversized is not null) return Task.FromResult(oversized);

        batch["t"] = "batch";
        batch["key"] = Guid.NewGuid().ToString("N");

        // A batch is as slow as its slowest class, and as restricted as its most restricted op.
        var timeout = _options.WriteTimeout;
        if (batch["ops"] is JsonArray ops)
        {
            foreach (var op in ops)
            {
                var id = (string?)op?["op"];
                if (id is null) continue;
                if (!ToolCatalog.ById.TryGetValue(id, out var e))
                    return Task.FromResult(Envelope.Error("E_TOOL_NOT_FOUND", $"No tool named '{id}'.",
                        param: "ops", value: id,
                        didYouMean: Fuzzy.Closest(id, ToolCatalog.All.Select(x => x.Id), 3)));

                var opDenied = Profiles.Denies(_options.Profile, e.Id, e.Mutating);
                if (opDenied is not null)
                    return Task.FromResult(Envelope.Error("E_PROFILE_DENIED", opDenied,
                        hint: "Restart the daemon with --profile full if this is intended."));

                if (e.Retry == "Compile") timeout = _options.CompileTimeout;
            }
        }
        return DispatchAsync(batch, projectId, timeout, "unity.batch", mutating: true, ct, progress: progress);
    }

    /// <summary>Refuse an argument payload no legitimate call would send.</summary>
    JsonObject? TooLarge(JsonNode? args, string tool)
    {
        if (args is null) return null;
        var bytes = System.Text.Encoding.UTF8.GetByteCount(args.ToJsonString());
        if (bytes <= _options.MaxRequestBytes) return null;

        return Envelope.Error("E_ARG_TOO_LARGE",
            $"The arguments for '{tool}' are {bytes / 1024} KB; the limit is {_options.MaxRequestBytes / 1024} KB.",
            hint: "Split the work, or reference assets by path instead of embedding their contents. " +
                  "--max-request-bytes raises the limit if you genuinely need it.",
            meta: new JsonObject { ["bytes"] = bytes, ["limit"] = _options.MaxRequestBytes });
    }

    /// <summary>Tools the mirror can answer at all. Anything else is live by construction.</summary>
    static readonly HashSet<string> MirrorCandidates = new(StringComparer.Ordinal) { "scene.query", "scene.count" };

    TimeSpan TimeoutFor(string retryClass) => retryClass switch
    {
        "Compile" => _options.CompileTimeout,
        "Write" => _options.WriteTimeout,
        "None" => _options.WriteTimeout,
        _ => _options.ReadTimeout
    };

    // ---------------------------------------------------------------- code mode

    /// <summary>
    /// Compile the caller's C# here and execute it in the Editor: one round trip that returns its
    /// conclusion instead of N tool results that return their working. Measured on this project,
    /// 20 operations cost 145 B through code mode versus 6,020 B as tool calls.
    /// </summary>
    public async Task<JsonObject> RunScriptAsync(string code, string? projectId, CancellationToken ct)
    {
        var denied = Profiles.Denies(_options.Profile, "unity.script", mutating: true);
        if (denied is not null)
            return Envelope.Error("E_PROFILE_DENIED", denied,
                hint: "Code mode is arbitrary code execution in the Editor, so it is off unless the " +
                      "daemon was started with --profile full.");

        var session = _registry.Get(projectId) ??
                      await _registry.WaitForAsync(projectId, _options.HoldTimeout, ct).ConfigureAwait(false);
        if (session is null) return NoEditor(projectId, 0, 0);

        var references = await ReferencePathsAsync(session, ct).ConfigureAwait(false);
        if (references.Length == 0)
            return Envelope.Error("E_SCRIPT_REFERENCES",
                "Could not read the Editor's assembly list, so there is nothing to compile against.",
                hint: "Check unity.status; the Editor may be mid-reload.");

        var compiled = _compiler.Compile(code, references);
        if (!compiled.Ok)
        {
            var diagnostics = new JsonArray();
            foreach (var d in compiled.Diagnostics.Where(d => d.Severity == "error"))
                diagnostics.Add(new JsonObject
                {
                    ["line"] = d.Line,
                    ["column"] = d.Column,
                    ["id"] = d.Id,
                    ["message"] = d.Message
                });

            return Envelope.Error("E_SCRIPT_COMPILE",
                $"The script did not compile ({diagnostics.Count} error(s)).",
                hint: "Line numbers are relative to your code. Engine and Editor namespaces are " +
                      "already imported, and a bare expression is returned automatically.",
                meta: new JsonObject { ["ms"] = compiled.CompileMs, ["diagnostics"] = diagnostics });
        }

        var message = new JsonObject
        {
            ["t"] = "script",
            ["key"] = Guid.NewGuid().ToString("N"),
            ["type"] = compiled.TypeName,
            ["asm"] = Convert.ToBase64String(compiled.Assembly!)
        };

        var result = await DispatchAsync(message, projectId, _options.WriteTimeout,
            "unity.script", mutating: true, ct).ConfigureAwait(false);

        if (result["meta"] is JsonObject meta)
        {
            meta["compileMs"] = compiled.CompileMs;
            meta["compileCached"] = compiled.FromCache;
        }
        var warnings = compiled.Diagnostics.Where(d => d.Severity == "warning").Take(5).ToArray();
        if (warnings.Length > 0)
            result["warnings"] = new JsonArray(warnings
                .Select(w => (JsonNode)$"line {w.Line}: {w.Id} {w.Message}").ToArray());
        return result;
    }

    async Task<string[]> ReferencePathsAsync(AgentSession session, CancellationToken ct)
    {
        var cacheKey = session.ProjectId + "@" + session.Epoch;
        if (_referenceCache.TryGetValue(cacheKey, out var cached)) return cached;

        var message = new JsonObject
        {
            ["t"] = "op",
            ["tool"] = "editor.assemblies",
            ["args"] = new JsonObject { ["fileBackedOnly"] = true }
        };

        // Bypass the response cap: this is machinery, not something the model ever reads.
        var result = await DispatchAsync(message, session.ProjectId, _options.ReadTimeout,
            "editor.assemblies", mutating: false, ct, maxBytes: int.MaxValue).ConfigureAwait(false);

        if ((bool?)result["ok"] != true) return Array.Empty<string>();

        var paths = (result["data"]?["assemblies"] as JsonArray ?? new JsonArray())
            .Select(a => (string?)a?["path"])
            .Where(p => !string.IsNullOrEmpty(p))
            .Select(p => p!)
            .Distinct()
            .ToArray();

        _referenceCache[cacheKey] = paths;
        _log.LogInformation("code mode: cached {Count} reference assemblies for {Project} epoch {Epoch}",
            paths.Length, session.ProjectName, session.Epoch);
        return paths;
    }

    async Task<JsonObject> DispatchAsync(JsonObject message, string? projectId, TimeSpan opTimeout,
                                         string label, bool mutating, CancellationToken ct,
                                         int? maxBytes = null, IProgress<OpProgress>? progress = null)
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

                // A hold is the one thing that makes a call take minutes rather than milliseconds,
                // so it is the one thing worth telling the caller about while it happens.
                progress?.Report(new OpProgress(label, "waiting for an editor to connect", 10, total.ElapsedMilliseconds));
                session = await _registry.WaitForAsync(projectId, remaining, ct).ConfigureAwait(false);
                heldMs += holdStart.ElapsedMilliseconds;
                if (session is null)
                    return NoEditor(projectId, total.ElapsedMilliseconds, heldMs);
            }

            progress?.Report(new OpProgress(label, attempts == 1 ? "running in the Editor" : "replaying after a reload",
                                            attempts == 1 ? 50 : 60, total.ElapsedMilliseconds));

            // Cancellation reaches the Editor as its own frame, ahead of the queued operation.
            // Without this the caller's cancel only stops the *waiting*: the Editor still runs
            // the mutation, which is the worst of both outcomes.
            var key = (string?)message["key"];
            using var cancelRegistration = key is null
                ? (IDisposable)new NoopDisposable()
                : ct.Register(() =>
                {
                    _log.LogInformation("{Label}: caller cancelled; telling the Editor to drop key {Key}", label, key);
                    session!.SendCancel(key);
                });

            var result = await SendWatchedAsync(session, message, opTimeout, ct).ConfigureAwait(false);

            switch (result.Outcome)
            {
                case SendOutcome.Completed:
                    {
                        progress?.Report(new OpProgress(label, "complete", 100, total.ElapsedMilliseconds));
                        var envelope = Envelope.FromAgentResult(result.Result!, session, heldMs, attempts, maxBytes ?? _options.MaxResponseBytes);
                        if (mutating) _audit.Write(label, session.ProjectId, message, envelope);
                        return envelope;
                    }

                case SendOutcome.Blocked:
                    // Withdraw the operation before reporting the block. Without this the daemon
                    // says "blocked", the caller believes nothing happened, and the Editor runs
                    // the mutation the moment the modal is dismissed — measured: 60 creates all
                    // answered E_EDITOR_BLOCKED and all 60 objects appeared. An error for work
                    // that then happens is the worst answer this system can give.
                    if (key is not null) session.SendCancel(key);

                    return Envelope.Error("E_EDITOR_BLOCKED", result.Detail ?? "The Editor main thread is not ticking.",
                        hint: "Unity is very likely showing a modal dialog. Dismiss it in the Editor, then send the " +
                              "operation again — this one was withdrawn rather than left queued. This daemon never " +
                              "raises modals of its own.",
                        meta: new JsonObject
                        {
                            ["ms"] = total.ElapsedMilliseconds,
                            ["heldMs"] = heldMs,
                            ["epoch"] = session.Epoch,
                            ["project"] = session.ProjectName,
                            ["withdrawn"] = key is not null
                        });

                case SendOutcome.Overloaded:
                    return Envelope.Error("E_BUSY", result.Detail ?? "Too many operations are queued for this Editor.",
                        hint: "Wait for the queue to drain, or send fewer operations at once — unity_batch runs many " +
                              "operations as one queued item, in a single Editor tick.",
                        meta: new JsonObject
                        {
                            ["ms"] = total.ElapsedMilliseconds,
                            ["inFlight"] = session.InFlight,
                            ["limit"] = AgentSession.MaxInFlight
                        });

                case SendOutcome.Disconnected:
                    // The editor went away mid-flight — almost always a domain reload. Hold and
                    // replay with the same idempotency key rather than surfacing an error.
                    if (DateTime.UtcNow < holdDeadline)
                    {
                        _log.LogInformation("{Label}: editor went away mid-op, holding for replay (attempt {N})", label, attempts);
                        progress?.Report(new OpProgress(label, "the Editor is reloading; holding this operation", 30, total.ElapsedMilliseconds));
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
                    // Same rule as a block: the caller is being told it did not happen, so make
                    // that true where it still can be. An operation already executing cannot be
                    // withdrawn — the meta says "attempted" rather than claiming otherwise.
                    if (key is not null) session.SendCancel(key);

                    return Envelope.Error("E_TIMEOUT",
                        $"'{label}' did not complete within {opTimeout.TotalSeconds:0}s.",
                        hint: "The Editor is ticking but the operation is slow. It was withdrawn if it had not " +
                              "started yet; if it had, it may still finish. Check unity_projects.",
                        meta: new JsonObject
                        {
                            ["ms"] = total.ElapsedMilliseconds,
                            ["heldMs"] = heldMs,
                            ["lastRoundTripMs"] = session.LastRoundTripMs,
                            ["withdrawal"] = key is null ? "not attempted" : "attempted"
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
