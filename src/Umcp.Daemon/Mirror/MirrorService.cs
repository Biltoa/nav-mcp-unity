using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json.Nodes;
using Umcp.Agent;
using Umcp.Daemon.Agent;

namespace Umcp.Daemon.Mirror;

/// <summary>
/// Owns one <see cref="SceneMirror"/> per project, keeps them fed, and answers reads from them.
///
/// The sequence that matters: the agent connects, the service seeds the mirror from a snapshot,
/// and from then on the Editor pushes deltas once per frame. Instance ids do not survive a domain
/// reload, so a reconnect re-seeds rather than patching — and while the Editor is gone the mirror
/// still answers reads, flagged <c>stale</c>. That is the difference between a recompile stopping
/// half the traffic and stopping all of it.
/// </summary>
public sealed class MirrorService
{
    readonly ConcurrentDictionary<string, SceneMirror> _mirrors = new(StringComparer.OrdinalIgnoreCase);
    readonly ConcurrentDictionary<string, byte> _seeding = new(StringComparer.OrdinalIgnoreCase);
    readonly EditorRegistry _registry;
    readonly DaemonOptions _options;
    readonly ILogger<MirrorService> _log;

    Dispatcher? _dispatcher;

    public MirrorService(EditorRegistry registry, DaemonOptions options, ILogger<MirrorService> log)
    {
        _registry = registry;
        _options = options;
        _log = log;
    }

    /// <summary>Set once at startup. Breaks the Dispatcher/MirrorService construction cycle.</summary>
    public void Attach(Dispatcher dispatcher) => _dispatcher = dispatcher;

    public SceneMirror For(string projectId) => _mirrors.GetOrAdd(projectId, _ => new SceneMirror());

    public SceneMirror? Find(string? projectId)
    {
        if (string.IsNullOrEmpty(projectId)) return null;
        return _mirrors.TryGetValue(projectId!, out var m) ? m : null;
    }

    public IReadOnlyDictionary<string, SceneMirror> All => _mirrors;

    // ------------------------------------------------------------------ feeding

    public void OnHandshake(AgentSession session)
    {
        // A reload gives every object a new instance id, so the old model is not stale, it is
        // wrong. Throw it away and re-seed.
        For(session.ProjectId).Invalidate("handshake");
        _ = SeedAsync(session, CancellationToken.None);
    }

    public void OnAgentEvent(AgentSession session, string kind, JsonNode message)
    {
        if (kind != "mirror") return;
        var mirror = For(session.ProjectId);

        if ((bool?)message["resync"] == true)
        {
            _log.LogInformation("mirror resync requested by {Project}", session.ProjectName);
            mirror.Invalidate("agent resync");
            _ = SeedAsync(session, CancellationToken.None);
            return;
        }

        if (!mirror.Seeded) return;    // deltas before the seed would build a partial model
        mirror.Apply(message);
    }

    public async Task<bool> SeedAsync(AgentSession session, CancellationToken ct)
    {
        if (_dispatcher is null) return false;
        if (!_seeding.TryAdd(session.ProjectId, 0)) return false;   // one seed at a time

        try
        {
            var sw = Stopwatch.StartNew();
            var result = await _dispatcher.RunToolAsync("mirror.snapshot", new JsonObject(),
                session.ProjectId, dryRun: false, ct, maxBytes: int.MaxValue).ConfigureAwait(false);

            if ((bool?)result["ok"] != true || result["data"] is null)
            {
                _log.LogWarning("mirror seed failed for {Project}: {Code}",
                    session.ProjectName, (string?)result["code"]);
                return false;
            }

            var mirror = For(session.ProjectId);
            mirror.Seed(result["data"]!, session.Epoch);
            _log.LogInformation("mirror seeded for {Project}: {Nodes} nodes in {Ms} ms (epoch {Epoch})",
                session.ProjectName, mirror.Count, sw.ElapsedMilliseconds, session.Epoch);
            return true;
        }
        catch (Exception e)
        {
            _log.LogWarning("mirror seed threw for {Project}: {Message}", session.ProjectName, e.Message);
            return false;
        }
        finally
        {
            _seeding.TryRemove(session.ProjectId, out _);
        }
    }

    // ------------------------------------------------------------------ reconcile

    /// <summary>
    /// Ask the Editor for subtree hashes and compare. Any mismatch re-seeds; the count of repairs
    /// is reported, because "zero drift" is only a meaningful claim if drift was actually looked for.
    /// </summary>
    public async Task<JsonObject> ReconcileAsync(AgentSession session, CancellationToken ct)
    {
        if (_dispatcher is null) return new JsonObject { ["ok"] = false, ["reason"] = "no dispatcher" };

        var mirror = For(session.ProjectId);
        if (!mirror.Seeded)
        {
            await SeedAsync(session, ct).ConfigureAwait(false);
            return new JsonObject { ["ok"] = true, ["seeded"] = true, ["drift"] = false };
        }

        var sw = Stopwatch.StartNew();
        var result = await _dispatcher.RunToolAsync("mirror.hashes", new JsonObject(),
            session.ProjectId, dryRun: false, ct, maxBytes: int.MaxValue).ConfigureAwait(false);

        if ((bool?)result["ok"] != true)
            return new JsonObject { ["ok"] = false, ["reason"] = (string?)result["code"] };

        var live = result["data"]!;
        var liveAll = (uint?)(long?)live["all"] ?? 0;
        var liveCount = (int?)live["count"] ?? 0;

        var (mineAll, mineCount, mineRoots) = mirror.Hashes();
        var drift = liveAll != mineAll || liveCount != mineCount;

        var details = new JsonArray();

        if (drift)
        {
            // Field-level diagnosis first, because it is the actionable half: a hash mismatch says
            // only that something is wrong, a field diff says which node and which field.
            try
            {
                var snapshot = await _dispatcher.RunToolAsync("mirror.snapshot", new JsonObject(),
                    session.ProjectId, dryRun: false, ct, maxBytes: int.MaxValue).ConfigureAwait(false);
                if ((bool?)snapshot["ok"] == true && snapshot["data"] is not null)
                    foreach (var entry in mirror.DiffAgainst(snapshot["data"]!))
                        details.Add(entry?.DeepClone());
            }
            catch { /* diagnosis must never block the repair */ }

            if (live["roots"] is JsonArray liveRoots)
            {
                var reported = 0;
                foreach (var r in liveRoots)
                {
                    if (reported >= 5) break;
                    var name = (string?)r?["name"] ?? "";
                    var hash = (uint?)(long?)r?["hash"] ?? 0;
                    var count = (int?)r?["count"] ?? 0;

                    var key = mineRoots.Keys.FirstOrDefault(k => k.StartsWith(name + "#", StringComparison.Ordinal));
                    if (key is null)
                    {
                        details.Add(new JsonObject { ["root"] = name, ["issue"] = "missing from mirror" });
                        reported++;
                    }
                    else if (mineRoots[key].hash != hash || mineRoots[key].count != count)
                    {
                        details.Add(new JsonObject
                        {
                            ["root"] = name,
                            ["issue"] = "subtree hash mismatch",
                            ["liveCount"] = count,
                            ["mirrorCount"] = mineRoots[key].count
                        });
                        reported++;
                    }
                }
            }
        }

        mirror.NoteReconcile(drift);
        if (drift)
        {
            _log.LogInformation("mirror drift on {Project}: live {LiveCount}/{LiveHash:x8} vs mirror {MineCount}/{MineHash:x8} — re-seeding",
                session.ProjectName, liveCount, liveAll, mineCount, mineAll);
            mirror.Invalidate("reconcile drift");
            await SeedAsync(session, ct).ConfigureAwait(false);
        }

        return new JsonObject
        {
            ["ok"] = true,
            ["drift"] = drift,
            ["ms"] = sw.ElapsedMilliseconds,
            ["liveNodes"] = liveCount,
            ["mirrorNodes"] = mineCount,
            ["liveHash"] = liveAll,
            ["mirrorHash"] = mineAll,
            ["details"] = details,
            ["reconciles"] = mirror.ReconcileCount,
            ["driftRepairs"] = mirror.DriftRepairs
        };
    }

    // ------------------------------------------------------------------ serving reads

    /// <summary>
    /// Answer a read from the mirror, or return null to let it go live. Never guesses: an
    /// unserviceable query is refused here rather than answered approximately.
    /// </summary>
    public JsonObject? TryServe(string? projectId, string tool, JsonObject args, bool verify, out string? reason)
    {
        reason = null;
        if (verify) { reason = "verify:true forces a live round trip"; return null; }

        var session = _registry.Get(projectId);
        var id = projectId ?? session?.ProjectId ?? _registry.DefaultProjectId;
        if (id is null) { reason = "no project"; return null; }

        var mirror = Find(id);
        if (mirror is null || !mirror.Seeded) { reason = "mirror not seeded"; return null; }

        return tool switch
        {
            "scene.query" => ServeQuery(mirror, session, args, ref reason),
            "scene.count" => ServeCount(mirror, session, args, ref reason),
            _ => null
        };
    }

    JsonObject? ServeQuery(SceneMirror mirror, AgentSession? session, JsonObject args, ref string? reason)
    {
        var select = (string?)args["select"] ?? "//*";
        var fields = (args["fields"] as JsonArray)?.Select(f => (string)f!).ToArray()
                     ?? new[] { "name", "path" };
        var limit = (int?)args["limit"] ?? 100;
        var offset = (int?)args["offset"] ?? 0;
        var maxDepth = (int?)args["maxDepth"] ?? -1;
        var countOnly = (bool?)args["countOnly"] ?? false;

        List<SceneSelector.Step> steps;
        try { steps = SceneSelector.Parse(select); }
        catch (SceneSelector.ParseException) { reason = "selector is invalid; let the Editor produce the error"; return null; }

        var serviceable = MirrorQuery.CanServe(mirror, steps, countOnly ? Array.Empty<string>() : fields);
        if (!serviceable.CanServe) { reason = serviceable.Reason; return null; }

        var matches = MirrorQuery.Evaluate(mirror, steps, maxDepth);

        if (countOnly)
            return Envelope.Ok(new JsonObject { ["count"] = matches.Count, ["select"] = select },
                Meta(mirror, session));

        var cap = limit <= 0 ? 100 : Math.Min(limit, 500);
        var page = new JsonArray();
        foreach (var node in matches.Skip(offset).Take(cap))
            page.Add(MirrorQuery.Project(mirror, node, fields));

        var truncated = offset + page.Count < matches.Count;
        var data = new JsonObject
        {
            ["items"] = page,
            ["_total"] = matches.Count,
            ["_returned"] = page.Count,
            ["_offset"] = offset,
            ["_truncated"] = truncated,
            ["_select"] = select,
            ["_hint"] = truncated ? $"re-query with offset={offset + page.Count}, or narrow the selector" : null
        };

        // The byte cap applies to mirror answers exactly as it does to live ones. Serving a read
        // faster is no reason to serve it unbounded.
        var meta = Meta(mirror, session);
        var (capped, wasCapped, bytes) = Envelope.Cap(data, _options.MaxResponseBytes);
        meta["truncated"] = wasCapped;
        if (wasCapped) meta["bytes"] = bytes;
        return Envelope.Ok(capped, meta);
    }

    JsonObject? ServeCount(SceneMirror mirror, AgentSession? session, JsonObject args, ref string? reason)
    {
        var select = (string?)args["select"] ?? "//*";
        List<SceneSelector.Step> steps;
        try { steps = SceneSelector.Parse(select); }
        catch (SceneSelector.ParseException) { reason = "selector is invalid; let the Editor produce the error"; return null; }

        var serviceable = MirrorQuery.CanServe(mirror, steps, Array.Empty<string>());
        if (!serviceable.CanServe) { reason = serviceable.Reason; return null; }

        var count = MirrorQuery.Evaluate(mirror, steps, -1).Count;
        return Envelope.Ok(new JsonObject { ["count"] = count, ["select"] = select }, Meta(mirror, session));
    }

    JsonObject Meta(SceneMirror mirror, AgentSession? session)
    {
        var alive = session is { Alive: true };
        var meta = new JsonObject
        {
            ["ms"] = 0,
            ["source"] = "mirror",
            ["staleMs"] = mirror.StaleMs,
            ["epoch"] = mirror.Epoch,
            ["revision"] = mirror.Revision,
            ["truncated"] = false
        };

        // While the Editor is gone the model cannot be refreshed, so say so rather than letting
        // "source: mirror" imply it is current.
        if (!alive)
        {
            meta["stale"] = true;
            meta["reason"] = session is null
                ? "no editor connected; answered from the last known state"
                : "editor is reloading; answered from the last known state";
        }
        else if (session!.Reloading)
        {
            meta["stale"] = true;
            meta["reason"] = "editor is reloading; answered from the last known state";
        }

        return meta;
    }
}
