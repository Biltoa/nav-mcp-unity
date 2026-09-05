using System.Diagnostics;
using System.Text.Json.Nodes;

// The fleet measurements: several Editors at once, and one of them killed.
//
// These are opt-in (`--fleet`) because they launch real Unity Editors, which costs minutes and
// gigabytes. Everything they touch lives under a scratch root outside the user's workspaces, and
// the scenes are never saved.

sealed partial class Bench
{
    public string FleetRoot { get; set; } = @"D:\umcp-fleet-scratch";
    public int FleetProjects { get; set; } = 3;
    public bool FleetKeep { get; set; } = true;

    string[] ScratchPaths => Enumerable.Range(1, FleetProjects)
        .Select(i => Path.Combine(FleetRoot, "p" + i)).ToArray();

    // ---------------------------------------------------------------- discovery

    /// <summary>What the daemon can see without launching anything: installs, projects, supervision.</summary>
    public async Task<JsonObject> FleetDiscoveryAsync()
    {
        var sw = Stopwatch.StartNew();
        var r = await CallAsync("unity_projects", new() { ["discover"] = true });
        var ms = sw.ElapsedMilliseconds;

        var installs = r["data"]?["editorInstalls"] as JsonArray ?? new JsonArray();
        var projects = r["data"]?["knownProjects"] as JsonArray ?? new JsonArray();
        var editors = r["data"]?["editors"] as JsonArray ?? new JsonArray();

        // A connected editor that discovery cannot find on disk means the two halves disagree
        // about what a project is — worth failing on, not worth guessing about.
        var connectedPaths = editors.Select(e => (string?)e?["path"]).Where(p => p is not null).ToArray();
        var matched = connectedPaths.Count(p => projects.Any(k =>
            string.Equals((string?)k?["path"], p, StringComparison.OrdinalIgnoreCase)));

        return new JsonObject
        {
            ["ms"] = ms,
            ["installs"] = installs.Count,
            ["versions"] = string.Join("|", installs.Select(i => (string?)i?["version"])),
            ["knownProjects"] = projects.Count,
            ["connected"] = editors.Count,
            ["connectedAlsoListed"] = matched,
            ["pass"] = installs.Count > 0 && projects.Count > 0 && matched == connectedPaths.Length
        };
    }

    /// <summary>The project id that was the default before the fleet run took it over.</summary>
    public async Task<string?> DefaultProjectIdAsync()
    {
        var r = await CallAsync("unity_projects", new());
        return (string?)(r["data"]?["editors"] as JsonArray)?
            .FirstOrDefault(e => (bool?)e?["isDefault"] == true)?["projectId"];
    }

    // ---------------------------------------------------------------- scratch projects

    /// <summary>
    /// Create the scratch projects if they are not there, with Unity's own
    /// <c>-createProject</c> in batch mode. Done here rather than in the daemon on purpose: the
    /// daemon opens projects, it does not invent them.
    /// </summary>
    public async Task<JsonObject> FleetPrepareAsync()
    {
        var editorExe = await NewestEditorExeAsync();
        if (editorExe is null)
            return new JsonObject { ["error"] = "no Editor install found via unity_projects(discover)", ["pass"] = false };

        Directory.CreateDirectory(FleetRoot);
        var created = 0;
        var reused = 0;
        var sw = Stopwatch.StartNew();

        foreach (var path in ScratchPaths)
        {
            if (Directory.Exists(Path.Combine(path, "Assets")))
            {
                reused++;
                continue;
            }

            var psi = new ProcessStartInfo(editorExe) { UseShellExecute = false };
            foreach (var a in new[] { "-createProject", path, "-batchmode", "-quit", "-nographics", "-accept-apiupdate" })
                psi.ArgumentList.Add(a);

            var proc = Process.Start(psi)!;
            await proc.WaitForExitAsync();
            if (!Directory.Exists(Path.Combine(path, "Assets")))
                return new JsonObject { ["error"] = $"createProject failed for {path} (exit {proc.ExitCode})", ["pass"] = false };
            created++;
        }

        return new JsonObject
        {
            ["root"] = FleetRoot,
            ["created"] = created,
            ["reused"] = reused,
            ["seconds"] = Math.Round(sw.Elapsed.TotalSeconds, 1),
            ["pass"] = created + reused == FleetProjects
        };
    }

    async Task<string?> NewestEditorExeAsync()
    {
        var r = await CallAsync("unity_projects", new() { ["discover"] = true });
        return (string?)(r["data"]?["editorInstalls"] as JsonArray)?.FirstOrDefault()?["path"];
    }

    // ---------------------------------------------------------------- open

    /// <summary>Open every scratch project and wait for its handshake. One daemon, one port, N editors.</summary>
    public async Task<JsonObject> FleetOpenAsync()
    {
        var opened = new JsonArray();
        var ids = new List<string>();
        var failures = 0;

        foreach (var path in ScratchPaths)
        {
            var sw = Stopwatch.StartNew();
            var r = await CallAsync("unity_projects", new() { ["open"] = path });
            var ok = (bool?)r["ok"] == true;
            var id = (string?)r["data"]?["projectId"];

            if (ok && id is not null) ids.Add(id);
            else failures++;

            opened.Add(new JsonObject
            {
                ["path"] = path,
                ["ok"] = ok,
                ["projectId"] = id,
                ["pid"] = (int?)r["data"]?["pid"],
                ["argsVerified"] = r["data"]?["argumentsVerified"]?.DeepClone(),
                ["handshake"] = (string?)r["data"]?["handshake"] ?? (string?)r["code"],
                ["startupSec"] = Math.Round(sw.Elapsed.TotalSeconds, 1)
            });
        }

        FleetIds = ids.ToArray();
        return new JsonObject
        {
            ["projects"] = FleetProjects,
            ["connected"] = ids.Count,
            ["failures"] = failures,
            ["editors"] = opened,
            ["pass"] = failures == 0 && ids.Count == FleetProjects
        };
    }

    public string[] FleetIds { get; private set; } = Array.Empty<string>();

    /// <summary>Re-read the connected ids for the scratch paths, so the isolation run can stand alone.</summary>
    async Task<string[]> ScratchIdsAsync()
    {
        if (FleetIds.Length == FleetProjects) return FleetIds;

        var r = await CallAsync("unity_projects", new());
        var editors = r["data"]?["editors"] as JsonArray ?? new JsonArray();
        var wanted = ScratchPaths.Select(p => p.Replace('\\', '/')).ToArray();
        FleetIds = editors
            .Where(e => wanted.Any(w => string.Equals((string?)e?["path"], w, StringComparison.OrdinalIgnoreCase)))
            .Select(e => (string?)e?["projectId"] ?? "")
            .Where(s => s.Length > 0)
            .ToArray();
        return FleetIds;
    }

    // ---------------------------------------------------------------- isolation

    /// <summary>
    /// Interleaved traffic across every open editor, then a check that each one holds exactly its
    /// own objects and none of anybody else's.
    ///
    /// Cross-talk is the specific failure of port-per-project identity: a request routed by port
    /// reaches whichever Editor answered that port last. Objects are named per project, so a
    /// single leaked object is visible and attributable.
    /// </summary>
    public async Task<JsonObject> FleetIsolationAsync(int rounds = 8)
    {
        var ids = await ScratchIdsAsync();
        if (ids.Length < 2)
            return new JsonObject { ["error"] = "fewer than two scratch editors connected", ["pass"] = false };

        // A tag per run: the scratch scenes are never saved but they are also never reopened
        // between runs, so counting by prefix alone would count the previous run's objects too.
        var tag = DateTime.Now.ToString("HHmmss");
        var sw = Stopwatch.StartNew();
        var sent = 0;

        for (var round = 0; round < rounds; round++)
        {
            // Round-robin, all in flight at once: the point is that concurrent traffic for
            // different projects does not mix, not that serial traffic does not.
            var calls = ids.Select((id, index) => CallAsync("unity_run", new()
            {
                ["tool"] = "gameobject.create",
                ["args"] = new JsonObject { ["name"] = $"{Prefix}fleet_{tag}_p{index}_{round}" },
                ["project"] = id
            })).ToArray();

            await Task.WhenAll(calls);
            sent += calls.Length;
        }

        var perProject = new JsonArray();
        var crossTalk = 0;
        var mine = 0;

        for (var index = 0; index < ids.Length; index++)
        {
            var own = await CountAsync(ids[index], $"//*[name^:{Prefix}fleet_{tag}_p{index}_]");
            var foreignTotal = 0;
            for (var other = 0; other < ids.Length; other++)
            {
                if (other == index) continue;
                foreignTotal += await CountAsync(ids[index], $"//*[name^:{Prefix}fleet_{tag}_p{other}_]");
            }

            mine += own;
            crossTalk += foreignTotal;
            perProject.Add(new JsonObject { ["projectId"] = ids[index], ["own"] = own, ["foreign"] = foreignTotal });
        }

        return new JsonObject
        {
            ["editors"] = ids.Length,
            ["opsSent"] = sent,
            ["seconds"] = Math.Round(sw.Elapsed.TotalSeconds, 1),
            ["ownObjects"] = mine,
            ["crossTalk"] = crossTalk,
            ["detail"] = perProject,
            ["pass"] = crossTalk == 0 && mine == sent
        };
    }

    async Task<int> CountAsync(string projectId, string selector)
    {
        var r = await CallAsync("unity_run", new()
        {
            ["tool"] = "scene.count",
            ["args"] = new JsonObject { ["select"] = selector },
            ["project"] = projectId,
            ["verify"] = true
        });
        return (int?)r["data"]?["count"] ?? (int?)r["data"]?["_total"] ?? -1;
    }

    // ---------------------------------------------------------------- crash and restart

    /// <summary>
    /// Kill an Editor outright, mid-sequence, and measure three things: that the daemon survives
    /// it, that the operation in flight is held rather than failed, and that the project comes
    /// back and finishes the sequence.
    ///
    /// This is the test Phase 1 deferred, because back then the only Editor available to kill was
    /// the user's own live session.
    /// </summary>
    public async Task<JsonObject> FleetCrashRestartAsync()
    {
        var ids = await ScratchIdsAsync();
        if (ids.Length == 0) return new JsonObject { ["error"] = "no scratch editors connected", ["pass"] = false };

        var victim = ids[^1];
        await CallAsync("unity_projects", new() { ["use"] = victim, ["autoRestart"] = true });

        var status = await CallAsync("unity_projects", new());
        var daemonPidBefore = (int?)status["data"]?["daemon"]?["pid"] ?? -1;
        var editor = (status["data"]?["editors"] as JsonArray)?
            .FirstOrDefault(e => (string?)e?["projectId"] == victim);
        var pid = (int?)editor?["pid"] ?? 0;
        var epochBefore = (int?)editor?["epoch"] ?? -1;
        if (pid <= 0) return new JsonObject { ["error"] = "no pid for the victim editor", ["pass"] = false };

        // A sequence, killed in the middle of it. Op 3 is in flight when the process dies.
        var sequence = new List<JsonObject>();
        for (var i = 0; i < 3; i++)
            sequence.Add(await CallAsync("unity_run", new()
            {
                ["tool"] = "gameobject.create",
                ["args"] = new JsonObject { ["name"] = $"{Prefix}crash_pre_{i}" },
                ["project"] = victim
            }));

        var sw = Stopwatch.StartNew();
        var inFlight = CallAsync("unity_run", new()
        {
            ["tool"] = "gameobject.create",
            ["args"] = new JsonObject { ["name"] = Prefix + "crash_inflight" },
            ["project"] = victim
        });

        await Task.Delay(150);
        var killed = false;
        try { Process.GetProcessById(pid).Kill(true); killed = true; } catch { }

        // The op issued *while the Editor is dead* is the one that proves the claim: the daemon
        // owns the request lifecycle, so a caller mid-sequence sees a slow call rather than an
        // error, right across a crash and an auto-restart. The op that was already in flight is
        // usually finished before the kill lands — a tick is ~100 ms — so it is reported, not
        // relied on.
        var downSw = Stopwatch.StartNew();
        var duringDown = CallAsync("unity_run", new()
        {
            ["tool"] = "gameobject.create",
            ["args"] = new JsonObject { ["name"] = Prefix + "crash_during_down" },
            ["project"] = victim
        });

        JsonObject held;
        try { held = await inFlight.WaitAsync(TimeSpan.FromMinutes(6)); }
        catch (TimeoutException) { held = new JsonObject { ["ok"] = false, ["code"] = "HARNESS_TIMEOUT" }; }
        var heldMs = sw.ElapsedMilliseconds;

        JsonObject down;
        try { down = await duringDown.WaitAsync(TimeSpan.FromMinutes(6)); }
        catch (TimeoutException) { down = new JsonObject { ["ok"] = false, ["code"] = "HARNESS_TIMEOUT" }; }
        var downMs = downSw.ElapsedMilliseconds;

        // The daemon must be the same process it was before the Editor died. No parent-PID
        // watchdog anywhere is the design; this is where that is checked rather than asserted.
        var afterKill = await CallAsync("unity_projects", new());
        var daemonPidAfter = (int?)afterKill["data"]?["daemon"]?["pid"] ?? -2;

        var reconnectMs = await WaitForProjectAsync(victim, TimeSpan.FromMinutes(6));
        var post = await CallAsync("unity_run", new()
        {
            ["tool"] = "gameobject.create",
            ["args"] = new JsonObject { ["name"] = Prefix + "crash_post" },
            ["project"] = victim
        });

        var status2 = await CallAsync("unity_projects", new());
        var editor2 = (status2["data"]?["editors"] as JsonArray)?
            .FirstOrDefault(e => (string?)e?["projectId"] == victim);

        return new JsonObject
        {
            ["killedPid"] = killed ? pid : 0,
            ["daemonPidBefore"] = daemonPidBefore,
            ["daemonPidAfter"] = daemonPidAfter,
            ["daemonSurvived"] = daemonPidBefore > 0 && daemonPidBefore == daemonPidAfter,
            ["inFlightOk"] = (bool?)held["ok"] == true,
            ["inFlightCode"] = (string?)held["code"],
            ["inFlightMs"] = heldMs,
            ["duringDownOk"] = (bool?)down["ok"] == true,
            ["duringDownCode"] = (string?)down["code"],
            ["duringDownMs"] = downMs,
            ["autoRestartSec"] = reconnectMs < 0 ? -1 : Math.Round(reconnectMs / 1000.0, 1),
            ["newPid"] = (int?)editor2?["pid"],
            ["pidChanged"] = (int?)editor2?["pid"] != pid,
            ["epochBefore"] = epochBefore,
            ["epochAfter"] = (int?)editor2?["epoch"],
            ["resumedOk"] = (bool?)post["ok"] == true,
            ["pass"] = killed
                       && daemonPidBefore > 0 && daemonPidBefore == daemonPidAfter
                       && reconnectMs >= 0
                       && (bool?)down["ok"] == true
                       && (bool?)post["ok"] == true
        };
    }

    /// <summary>Wait for a project id to be connected again, and say how long it took.</summary>
    async Task<long> WaitForProjectAsync(string projectId, TimeSpan timeout)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < timeout)
        {
            var r = await CallAsync("unity_projects", new());
            var e = (r["data"]?["editors"] as JsonArray)?.FirstOrDefault(x => (string?)x?["projectId"] == projectId);
            if (e is not null && (string?)e["health"] is "ok" or "degraded") return sw.ElapsedMilliseconds;
            await Task.Delay(1000);
        }
        return -1;
    }

    /// <summary>Close the scratch editors and hand the default target back to whoever had it.</summary>
    public async Task<JsonObject> FleetCleanupAsync(string? restoreDefault = null)
    {
        var ids = await ScratchIdsAsync();
        var closed = 0;
        var failed = new JsonArray();

        foreach (var id in ids)
        {
            // The scratch scenes are dirty by construction — this harness created objects in
            // them — and they are scratch, so they are discarded rather than written to disk.
            var r = await CallAsync("unity_projects", new() { ["close"] = id, ["save"] = false, ["discard"] = true });
            if ((bool?)r["data"]?["closed"] == true) closed++;
            else failed.Add(new JsonObject { ["projectId"] = id, ["code"] = (string?)r["code"], ["message"] = (string?)r["message"] });
        }

        if (restoreDefault is not null)
            await CallAsync("unity_projects", new() { ["use"] = restoreDefault, ["autoRestart"] = false });

        if (!FleetKeep)
        {
            foreach (var path in ScratchPaths)
                try { if (Directory.Exists(path)) Directory.Delete(path, true); } catch { }
        }

        FleetIds = Array.Empty<string>();
        return new JsonObject
        {
            ["closed"] = closed,
            ["failed"] = failed,
            ["scratchKept"] = FleetKeep,
            ["root"] = FleetRoot,
            ["pass"] = failed.Count == 0
        };
    }
}
