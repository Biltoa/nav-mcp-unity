using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;

// The catalog smoke harness and the Phase 5 measurements.
//
// The point of the smoke run is coverage, not depth: every tool in the catalog is called once,
// with its own documented example, and the result is required to be a well-formed envelope.
// A tool that throws, hangs, or returns something that is not an envelope is a defect regardless
// of what it was trying to do — and a catalog nobody calls is a catalog that rots.

sealed partial class Bench
{
    /// <summary>
    /// Call every tool once. Reads run for real; mutating tools run through <c>dryRun</c>, which
    /// since Phase 5 is a real prediction rather than a promise, so this exercises argument
    /// binding and target resolution for them too.
    /// </summary>
    public async Task<JsonObject> SmokeAsync()
    {
        var (tools, catalogTotal) = await CatalogAsync();
        var failures = new JsonArray();
        var skipped = new JsonArray();
        int ran = 0, dry = 0, live = 0;
        var slowest = ("", 0L);

        var sw = Stopwatch.StartNew();
        foreach (var (id, mutating, example) in tools)
        {
            if (example is null)
            {
                skipped.Add(new JsonObject { ["tool"] = id, ["why"] = "no example" });
                continue;
            }

            // Two tools are excluded by name, and the reason is written down rather than implied:
            // editor.stall deliberately wedges the Editor for seconds, and editor.quit closes it.
            // A smoke run that closes the Editor cannot report its own results.
            if (id is "editor.stall" or "editor.quit")
            {
                skipped.Add(new JsonObject { ["tool"] = id, ["why"] = "would stall or close the Editor" });
                continue;
            }

            var call = new JsonObject { ["tool"] = id, ["args"] = example.DeepClone() };
            if (mutating) { call["dryRun"] = true; dry++; } else live++;

            var opSw = Stopwatch.StartNew();
            JsonObject result;
            try { result = await CallAsync("unity_run", call); }
            catch (Exception e) { result = new JsonObject { ["ok"] = false, ["code"] = "HARNESS_EXCEPTION", ["message"] = e.Message }; }
            var ms = opSw.ElapsedMilliseconds;
            if (ms > slowest.Item2) slowest = (id, ms);

            ran++;
            var ok = (bool?)result["ok"];
            if (ok is null)
                failures.Add(new JsonObject { ["tool"] = id, ["code"] = "NOT_AN_ENVELOPE", ["message"] = result.ToJsonString()[..Math.Min(160, result.ToJsonString().Length)] });
            else if (ok == false)
            {
                var code = (string?)result["code"] ?? "";
                // An example that names an object this project does not have is the example's
                // problem, not the tool's — but it is still reported, because an example that
                // cannot run is a documentation defect.
                failures.Add(new JsonObject
                {
                    ["tool"] = id,
                    ["code"] = code,
                    ["message"] = (string?)result["message"],
                    // These codes all mean "this project does not contain what the example names":
                    // an object, an asset, an optional package, a prefab instance, a marker from a
                    // previous call. That is a documentation mismatch, not a broken tool, and
                    // conflating the two would make the harness cry wolf on every project.
                    ["kind"] = code is "E_TARGET_NOT_FOUND" or "E_ASSET_NOT_FOUND" or "E_PACKAGE_MISSING"
                                    or "E_NOT_A_PREFAB_INSTANCE" or "E_MARKER_NOT_FOUND"
                        ? "example does not fit this project"
                        : "tool defect"
                });
            }
        }

        var defects = failures.Count(f => (string?)f?["kind"] == "tool defect");
        // Coverage is part of the result, not an assumption. The first run of this harness
        // reached 60 of 63 tools, because three of them had been added to the catalog but not to
        // any skill node's tool list — a gap invisible to every other check in the project.
        return new JsonObject
        {
            ["catalogTools"] = catalogTotal,
            ["reachedViaSkills"] = tools.Count,
            ["unreachable"] = catalogTotal - tools.Count,
            ["ran"] = ran,
            ["live"] = live,
            ["dryRun"] = dry,
            ["skipped"] = skipped,
            ["failures"] = failures,
            ["toolDefects"] = defects,
            ["seconds"] = Math.Round(sw.Elapsed.TotalSeconds, 1),
            ["slowest"] = new JsonObject { ["tool"] = slowest.Item1, ["ms"] = slowest.Item2 },
            ["pass"] = defects == 0 && ran > 0 && tools.Count == catalogTotal
        };
    }

    /// <summary>Every tool id, whether it mutates, and its first documented example.</summary>
    async Task<(List<(string Id, bool Mutating, JsonObject? Example)> Tools, int CatalogTotal)> CatalogAsync()
    {
        var list = new List<(string, bool, JsonObject?)>();
        var map = await CallAsync("unity_skill", new());
        var catalogTotal = (int?)map["data"]?["totalTools"] ?? -1;

        var skills = (map["data"]?["skills"] as JsonArray ?? new JsonArray())
            .Select(s => (string?)s?["id"]).Where(s => s is not null).Select(s => s!).ToList();
        foreach (var skill in skills.ToArray())
            skills.AddRange(((await CallAsync("unity_skill", new() { ["id"] = skill }))["data"]?["subSkills"] as JsonArray ?? new JsonArray())
                .Select(c => (string?)c?["id"]).Where(c => c is not null).Select(c => c!));

        var seen = new HashSet<string>();
        foreach (var skill in skills.Distinct())
        {
            var node = await CallAsync("unity_skill", new() { ["id"] = skill });
            foreach (var tool in node["data"]?["tools"] as JsonArray ?? new JsonArray())
            {
                var id = (string?)tool?["id"];
                if (id is null || !seen.Add(id)) continue;

                JsonObject? example = null;
                var examples = tool?["examples"] as JsonArray;
                if (examples is { Count: > 0 })
                {
                    var text = (string?)examples[0];
                    if (text is not null)
                        try { example = JsonNode.Parse(text) as JsonObject; } catch { }
                }
                list.Add((id, (bool?)tool?["mutating"] == true, example));
            }
        }
        return (list.OrderBy(t => t.Item1, StringComparer.Ordinal).ToList(), catalogTotal);
    }



    /// <summary>
    /// E_EDITOR_BLOCKED must mean "it did not run".
    ///
    /// It did not, once: sixty creates issued behind a stalled Editor all answered
    /// E_EDITOR_BLOCKED, and all sixty objects appeared the moment the stall cleared. An error for
    /// work that then happens is the worst answer this system can give, so the daemon now
    /// withdraws the operation before reporting the block — and this measures that it stayed
    /// withdrawn.
    /// </summary>
    public async Task<JsonObject> BlockedWithdrawalAsync(int operations = 24)
    {
        var tag = Prefix + "withdrawn_" + DateTime.Now.ToString("HHmmss");

        var stall = CallAsync("unity_run", new() { ["tool"] = "editor.stall", ["args"] = new JsonObject { ["seconds"] = 12 } });
        await Task.Delay(700);

        var calls = Enumerable.Range(0, operations).Select(i => CallAsync("unity_run", new()
        {
            ["tool"] = "gameobject.create",
            ["args"] = new JsonObject { ["name"] = tag + "_" + i }
        })).ToArray();

        var results = await Task.WhenAll(calls);
        var blocked = results.Count(r => (string?)r["code"] == "E_EDITOR_BLOCKED");
        var withdrawn = results.Count(r => (bool?)r["meta"]?["withdrawn"] == true);

        await stall;
        await Task.Delay(2000);   // let anything the Editor still holds drain

        var created = await CountAsync(null, $"//*[name^:{tag}]");

        return new JsonObject
        {
            ["operations"] = operations,
            ["blocked"] = blocked,
            ["reportedWithdrawn"] = withdrawn,
            ["objectsCreated"] = created,
            ["pass"] = blocked == operations && created == 0
        };
    }

    // ---------------------------------------------------------------- path confinement

    /// <summary>
    /// Every tool that takes an asset path must refuse one that escapes the project.
    ///
    /// This is a systematic probe rather than a spot check, because confinement is exactly the
    /// kind of rule that holds everywhere until somebody adds the ninety-first tool and forgets
    /// it. Five tools added in one sitting had done precisely that: they normalised separators and
    /// went straight to the filesystem, and one of them *writes a C# file*.
    /// </summary>
    public async Task<JsonObject> PathConfinementAsync()
    {
        var probed = new JsonArray();
        var accepted = new JsonArray();
        var pathParams = new[] { "path", "folder", "scriptFolder", "to", "source" };
        var escapes = new[] { "../escape/evil.cs", "C:/Windows/System32/evil.cs", "Library/evil.cs" };

        var (tools, _) = await CatalogAsync();
        foreach (var (id, mutating, _) in tools)
        {
            var detail = await CallAsync("unity_skill", new() { ["id"] = id });
            var schema = detail["data"]?["inputSchema"];
            var properties = schema?["properties"] as JsonObject;
            if (properties is null) continue;

            var required = (schema?["required"] as JsonArray ?? new JsonArray())
                .Select(r => (string?)r).Where(r => r is not null).Select(r => r!).ToArray();

            foreach (var name in pathParams)
            {
                if (!properties.ContainsKey(name)) continue;
                if ((string?)properties[name]?["type"] != "string") continue;

                foreach (var escape in escapes)
                {
                    var args = new JsonObject { [name] = escape };

                    // Fill the tool's other required arguments so the refusal we see is about the
                    // path and not about a missing argument.
                    foreach (var other in required)
                    {
                        if (other == name || args.ContainsKey(other)) continue;
                        args[other] = (string?)properties[other]?["type"] switch
                        {
                            "integer" or "number" => 1,
                            "boolean" => true,
                            "array" => new JsonArray(),
                            _ => "__umcp_probe"
                        };
                    }

                    var call = new JsonObject { ["tool"] = id, ["args"] = args };
                    if (mutating) call["dryRun"] = true;

                    var result = await CallAsync("unity_run", call);
                    var code = (string?)result["code"] ?? "";

                    // A mutating tool is probed through dryRun, which returns ok:true and puts its
                    // verdict in the payload. Reading only `ok` there would call every refusal an
                    // acceptance — which is how the first run of this probe reported 42 false
                    // failures.
                    var problems = result["data"]?["problems"] as JsonArray ?? new JsonArray();
                    var pathProblem = problems.Any(x => (string?)x?["code"] is "E_PATH_ESCAPE" or "E_PATH_OUTSIDE_PROJECT");
                    var confined = code is "E_PATH_ESCAPE" or "E_PATH_OUTSIDE_PROJECT" || pathProblem;
                    var refused = (bool?)result["ok"] != true || pathProblem;

                    probed.Add(new JsonObject
                    {
                        ["tool"] = id,
                        ["param"] = name,
                        ["value"] = escape,
                        ["code"] = confined && code.Length == 0 ? "dryRun:refused" : code
                    });

                    if (!refused || (!confined && code is not ("E_ARG_VALUE" or "E_ASSET_NOT_FOUND")))
                        accepted.Add(new JsonObject
                        {
                            ["tool"] = id,
                            ["param"] = name,
                            ["value"] = escape,
                            ["ok"] = result["ok"]?.DeepClone(),
                            ["code"] = code
                        });
                }
            }
        }

        var confinedCount = probed.Count(p => (string?)p?["code"] is "E_PATH_ESCAPE" or "E_PATH_OUTSIDE_PROJECT" or "dryRun:refused");
        return new JsonObject
        {
            ["probes"] = probed.Count,
            ["confined"] = confinedCount,
            ["notConfined"] = accepted,
            ["pass"] = accepted.Count == 0 && probed.Count > 0
        };
    }

    // ---------------------------------------------------------------- dry run

    /// <summary>A dry run must predict the effect and must change nothing. Both halves are checked.</summary>
    public async Task<JsonObject> DryRunAsync()
    {
        var name = Prefix + "dryrun_probe";
        await CallAsync("unity_run", new() { ["tool"] = "gameobject.create", ["args"] = new JsonObject { ["name"] = name } });

        var before = await CountAsync(null, $"//*[name^:{Prefix}dryrun_]");

        var delete = await CallAsync("unity_run", new()
        {
            ["tool"] = "gameobject.delete",
            ["args"] = new JsonObject { ["target"] = name },
            ["dryRun"] = true
        });

        var missing = await CallAsync("unity_run", new()
        {
            ["tool"] = "gameobject.rename",
            ["args"] = new JsonObject { ["target"] = Prefix + "does_not_exist", ["name"] = "x" },
            ["dryRun"] = true
        });

        var after = await CountAsync(null, $"//*[name^:{Prefix}dryrun_]");

        var effect = (string?)delete["data"]?["effect"] ?? "";
        var resolved = (delete["data"]?["resolved"] as JsonArray)?.Count ?? 0;
        var wouldSucceed = (bool?)delete["data"]?["wouldSucceed"];
        var missingCaught = (bool?)missing["data"]?["wouldSucceed"] == false &&
                            ((missing["data"]?["problems"] as JsonArray)?.Count ?? 0) > 0;

        return new JsonObject
        {
            ["effect"] = effect,
            ["resolvedTargets"] = resolved,
            ["wouldSucceed"] = wouldSucceed,
            ["missingTargetCaught"] = missingCaught,
            ["objectsBefore"] = before,
            ["objectsAfter"] = after,
            ["changedNothing"] = before == after,
            ["pass"] = before == after && before > 0 && resolved == 1 && wouldSucceed == true && missingCaught
        };
    }

    // ---------------------------------------------------------------- cancellation

    /// <summary>
    /// Cancel an operation that is queued behind a stalled Editor, then prove — after the stall
    /// clears — that it never ran. The weak version of this test checks that the *call* returned;
    /// the only version worth having checks that the *mutation* did not happen.
    /// </summary>
    public async Task<JsonObject> CancelAsync()
    {
        var name = Prefix + "cancelled_" + DateTime.Now.ToString("HHmmss");

        // Wedge the main thread for a few seconds so the operation is provably still queued.
        var stall = CallAsync("unity_run", new() { ["tool"] = "editor.stall", ["args"] = new JsonObject { ["seconds"] = 6 } });
        await Task.Delay(300);

        using var cts = new CancellationTokenSource();
        var sw = Stopwatch.StartNew();
        var pending = CallAsync("unity_run", new()
        {
            ["tool"] = "gameobject.create",
            ["args"] = new JsonObject { ["name"] = name }
        }, cts.Token);

        await Task.Delay(400);
        cts.Cancel();

        string outcome;
        long cancelMs;
        try { var r = await pending; outcome = (bool?)r["ok"] == true ? "completed" : (string?)r["code"] ?? "error"; }
        catch (OperationCanceledException) { outcome = "cancelled"; }
        catch (Exception e) { outcome = e.GetType().Name; }
        cancelMs = sw.ElapsedMilliseconds;

        await stall;                       // let the Editor finish stalling
        await Task.Delay(1500);            // and let any queued op drain

        var created = await CountAsync(null, $"//*[name^:{name}]");

        return new JsonObject
        {
            ["outcome"] = outcome,
            ["cancelMs"] = cancelMs,
            ["objectsCreated"] = created,
            ["mutationPrevented"] = created == 0,
            ["pass"] = created == 0 && outcome != "completed"
        };
    }

    /// <summary>The differentiator, measured on whatever project is connected.</summary>
    public async Task<JsonObject> ValidateTargetAsync()
    {
        var sw = Stopwatch.StartNew();
        var r = await CallAsync("unity_run", new()
        {
            ["tool"] = "build.validateTarget",
            ["args"] = new JsonObject { ["limit"] = 25 },
            ["maxResponseBytes"] = 60000
        });
        var ms = sw.ElapsedMilliseconds;

        var findings = r["data"]?["findings"] as JsonArray ?? new JsonArray();
        var codes = findings.Select(f => (string?)f?["code"]).Where(c => c is not null).Distinct().ToArray();

        return new JsonObject
        {
            ["ms"] = ms,
            ["platform"] = (string?)r["data"]?["platform"],
            ["graphicsApis"] = string.Join("|", (r["data"]?["graphicsApis"] as JsonArray ?? new JsonArray()).Select(a => (string?)a)),
            ["errors"] = (int?)r["data"]?["errors"] ?? -1,
            ["warnings"] = (int?)r["data"]?["warnings"] ?? -1,
            ["codes"] = string.Join("|", codes),
            ["bytes"] = System.Text.Encoding.UTF8.GetByteCount(r.ToJsonString()),
            ["pass"] = (bool?)r["ok"] == true
        };
    }

    /// <summary>scene.mark / scene.diff: an edit is visible as exactly the change that was made.</summary>
    public async Task<JsonObject> SceneDiffAsync()
    {
        var marker = "bench_" + DateTime.Now.ToString("HHmmss");
        var mark = await CallAsync("unity_run", new() { ["tool"] = "scene.mark", ["args"] = new JsonObject { ["name"] = marker } });

        var added = Prefix + "diff_added";
        await CallAsync("unity_run", new() { ["tool"] = "gameobject.create", ["args"] = new JsonObject { ["name"] = added } });
        await CallAsync("unity_run", new()
        {
            ["tool"] = "gameobject.setActive",
            ["args"] = new JsonObject { ["target"] = added, ["active"] = false }
        });

        var diff = await CallAsync("unity_run", new() { ["tool"] = "scene.diff", ["args"] = new JsonObject { ["marker"] = marker } });
        var counts = diff["data"]?["counts"];

        return new JsonObject
        {
            ["marked"] = (int?)mark["data"]?["objects"] ?? -1,
            ["added"] = (int?)counts?["added"] ?? -1,
            ["removed"] = (int?)counts?["removed"] ?? -1,
            ["changed"] = (int?)counts?["changed"] ?? -1,
            ["bytes"] = System.Text.Encoding.UTF8.GetByteCount(diff.ToJsonString()),
            ["pass"] = (int?)counts?["added"] == 1 && (int?)counts?["removed"] == 0
        };
    }
}
