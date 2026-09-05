using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

// umcp-bench — the measurement harness.
//
// Every headline claim in this project has to be a measurement, not an assertion, and every
// measurement has to state the Editor's focus state: window focus alone was worth 3.3× on the
// implementation being replaced (107.3 → 33.0 ms/op), so a benchmark that does not record it is
// not comparable to anything.
//
// Usage:  umcp-bench [--port 8730] [--only name] [--json out.json]

var port = ArgInt("--port", 8730);
var only = Arg("--only");
var jsonOut = Arg("--json");
var token = Arg("--token") ?? Environment.GetEnvironmentVariable("UMCP_TOKEN") ?? ReadTokenFile();

var transport = new HttpClientTransport(new HttpClientTransportOptions
{
    Endpoint = new Uri($"http://127.0.0.1:{port}/mcp"),
    AdditionalHeaders = token is null ? null : new Dictionary<string, string> { ["Authorization"] = "Bearer " + token }
});

await using var client = await McpClient.CreateAsync(transport);
var bench = new Bench(client);

var results = new JsonArray();
var focus = await bench.FocusStateAsync();

Console.WriteLine($"umcp-bench — Editor focus state: {focus}");
Console.WriteLine(new string('-', 78));

var all = new (string name, Func<Task<JsonObject>> run)[]
{
    ("toolsurface", bench.ToolSurfaceAsync),
    ("ping-sequential", () => bench.SequentialAsync(16)),
    ("ping-concurrent-32", () => bench.ConcurrentAsync(32)),
    ("batch-32", () => bench.Batch32Async()),
    ("batch-dependent", bench.BatchDependentAsync),
    ("payload-scene-info", bench.PayloadAsync),
    ("reload-hold-replay", bench.ReloadAsync),
    ("skill-tree", bench.SkillTreeAsync),
    ("scene-query-vs-dump", bench.SceneQueryAsync),
    ("code-mode", bench.CodeModeAsync),
    ("blocked-detection", bench.BlockedProbeAsync),
    ("blocked-under-stall", bench.BlockedUnderStallAsync),
    ("cleanup", bench.CleanupAsync)
};

foreach (var (name, run) in all)
{
    if (only is not null && !name.Contains(only, StringComparison.OrdinalIgnoreCase)) continue;
    Console.Write($"{name,-22} ");
    var sw = Stopwatch.StartNew();
    JsonObject row;
    try { row = await run(); }
    catch (Exception e) { row = new JsonObject { ["error"] = e.Message }; }
    row["name"] = name;
    // Never clobber a measurement the test itself made: the harness clock includes warm-up.
    row["harnessMs"] = sw.ElapsedMilliseconds;
    row["focus"] = focus;
    results.Add(row);
    Console.WriteLine(Summarise(row));
}

Console.WriteLine(new string('-', 78));
if (jsonOut is not null)
{
    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(jsonOut))!);
    await File.WriteAllTextAsync(jsonOut, new JsonObject
    {
        ["when"] = DateTimeOffset.Now.ToString("O"),
        ["focus"] = focus,
        ["results"] = results
    }.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    Console.WriteLine($"wrote {jsonOut}");
}
return 0;

static string Summarise(JsonObject row)
{
    if (row["error"] is not null) return "ERROR: " + row["error"];
    var parts = new List<string>();
    foreach (var (k, v) in row)
    {
        if (k is "name" or "focus") continue;
        parts.Add($"{k}={v?.ToJsonString().Trim('"')}");
    }
    return string.Join("  ", parts);
}

static string? Arg(string name)
{
    var a = Environment.GetCommandLineArgs();
    for (var i = 0; i < a.Length - 1; i++) if (a[i] == name) return a[i + 1];
    return null;
}

static int ArgInt(string name, int dflt) => int.TryParse(Arg(name), out var v) ? v : dflt;

static string? ReadTokenFile()
{
    var p = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "UnityMCP", "token");
    return File.Exists(p) ? File.ReadAllText(p).Trim() : null;
}

sealed class Bench(McpClient client)
{
    const string Prefix = "__UMCP_BENCH_";

    public async Task<string> FocusStateAsync()
    {
        var status = await CallAsync("unity_projects", new());
        var pid = (int?)status["data"]?["editors"]?[0]?["pid"] ?? 0;
        if (pid == 0) return "no-editor";
        if (!OperatingSystem.IsWindows()) return "unknown";

        var fg = GetForegroundWindow();
        if (fg == IntPtr.Zero) return "unfocused";
        GetWindowThreadProcessId(fg, out var fgPid);
        return fgPid == pid ? "FOCUSED" : "unfocused";
    }

    /// <summary>
    /// The baseline cost of simply having this server attached: name + description + inputSchema
    /// for every exposed tool. Measured at 227,065 B (~56,800 tokens) for the 356-tool
    /// implementation being replaced.
    /// </summary>
    public async Task<JsonObject> ToolSurfaceAsync()
    {
        var tools = await client.ListToolsAsync();
        var bytes = 0;
        foreach (var t in tools)
        {
            bytes += Encoding.UTF8.GetByteCount(t.Name);
            bytes += Encoding.UTF8.GetByteCount(t.Description ?? "");
            bytes += Encoding.UTF8.GetByteCount(t.JsonSchema.ToString());
        }
        return new JsonObject
        {
            ["tools"] = tools.Count,
            ["bytes"] = bytes,
            ["approxTokens"] = bytes / 4,
            ["target"] = "<1000 tokens",
            ["pass"] = bytes / 4 < 1000
        };
    }

    /// <summary>N sequential round trips: the shape that costs ~100 ms per op.</summary>
    public async Task<JsonObject> SequentialAsync(int n)
    {
        await CallAsync("unity_run", new() { ["tool"] = "editor.ping" });   // warm

        var times = new List<long>();
        for (var i = 0; i < n; i++)
        {
            var sw = Stopwatch.StartNew();
            await CallAsync("unity_run", new() { ["tool"] = "editor.ping" });
            times.Add(sw.ElapsedMilliseconds);
        }
        times.Sort();
        return new JsonObject
        {
            ["ops"] = n,
            ["msPerOp"] = Math.Round(times.Average(), 1),
            ["p50"] = times[n / 2],
            ["min"] = times[0],
            ["max"] = times[^1]
        };
    }

    /// <summary>N concurrent round trips: the Editor drains its whole queue in one tick.</summary>
    public async Task<JsonObject> ConcurrentAsync(int n)
    {
        await CallAsync("unity_run", new() { ["tool"] = "editor.ping" });   // warm

        var sw = Stopwatch.StartNew();
        var tasks = Enumerable.Range(0, n)
            .Select(_ => CallAsync("unity_run", new() { ["tool"] = "editor.ping" }))
            .ToArray();
        var all = await Task.WhenAll(tasks);
        var wall = sw.ElapsedMilliseconds;

        return new JsonObject
        {
            ["ops"] = n,
            ["burstMs"] = wall,
            ["msPerOp"] = Math.Round((double)wall / n, 2),
            ["ok"] = all.Count(r => (bool?)r["ok"] == true)
        };
    }

    /// <summary>The headline number: 32 operations in one Editor tick. Target ≤150 ms.</summary>
    public async Task<JsonObject> Batch32Async()
    {
        await CallAsync("unity_run", new() { ["tool"] = "editor.ping" });   // warm

        var ops = new JsonArray();
        for (var i = 0; i < 32; i++)
            ops.Add(new JsonObject
            {
                ["op"] = "gameobject.create",
                ["args"] = new JsonObject { ["name"] = Prefix + "b32_" + i }
            });

        var sw = Stopwatch.StartNew();
        var result = await CallAsync("unity_batch", new()
        {
            ["ops"] = ops,
            ["returns"] = "ids",
            ["undoName"] = "umcp-bench 32"
        });
        var wall = sw.ElapsedMilliseconds;

        var count = (int?)result["data"]?["count"] ?? 0;
        var bytes = Encoding.UTF8.GetByteCount(result.ToJsonString());

        return new JsonObject
        {
            ["ops"] = 32,
            ["batchMs"] = wall,
            ["msPerOp"] = Math.Round((double)wall / 32, 2),
            ["created"] = count,
            ["responseBytes"] = bytes,
            ["target"] = "<=150ms",
            ["pass"] = wall <= 150 && count == 32
        };
    }

    /// <summary>
    /// A dependent batch: op 2 and 3 refer to op 1's result with "$1". Serial round trips are
    /// what kill the naive design; this proves dependency does not force them.
    /// </summary>
    public async Task<JsonObject> BatchDependentAsync()
    {
        var ops = new JsonArray
        {
            new JsonObject { ["op"] = "gameobject.create", ["args"] = new JsonObject { ["name"] = Prefix + "dep", ["primitive"] = "Capsule" } },
            new JsonObject { ["op"] = "component.add", ["args"] = new JsonObject { ["target"] = "$1", ["type"] = "Rigidbody" } },
            new JsonObject { ["op"] = "component.set", ["args"] = new JsonObject { ["target"] = "$1", ["type"] = "Rigidbody", ["props"] = new JsonObject { ["mass"] = 80 } } },
            new JsonObject { ["op"] = "transform.set", ["args"] = new JsonObject { ["target"] = "$1", ["position"] = new JsonArray(0, 3, 0) } }
        };

        var sw = Stopwatch.StartNew();
        var result = await CallAsync("unity_batch", new() { ["ops"] = ops, ["atomic"] = true, ["returns"] = "summary" });
        return new JsonObject
        {
            ["ops"] = 4,
            ["batchMs"] = sw.ElapsedMilliseconds,
            ["ok"] = (bool?)result["ok"] ?? false,
            ["count"] = (int?)result["data"]?["count"] ?? 0,
            ["error"] = result["data"]?["error"]?.DeepClone() ?? result["message"]?.DeepClone()
        };
    }

    /// <summary>Response size on a read. One scene read cost 138,205 B before.</summary>
    public async Task<JsonObject> PayloadAsync()
    {
        var info = await CallAsync("unity_run", new() { ["tool"] = "scene.info" });
        var find = await CallAsync("unity_run", new()
        {
            ["tool"] = "gameobject.find",
            ["args"] = new JsonObject { ["name"] = "", ["limit"] = 500 }
        });

        var infoBytes = Encoding.UTF8.GetByteCount(info.ToJsonString());
        var findBytes = Encoding.UTF8.GetByteCount(find.ToJsonString());
        return new JsonObject
        {
            ["sceneInfoBytes"] = infoBytes,
            ["find500Bytes"] = findBytes,
            ["objectsInScene"] = (int?)info["data"]?["objectCount"] ?? 0,
            ["cap"] = 32768,
            ["pass"] = infoBytes <= 32768 && findBytes <= 32768
        };
    }

    /// <summary>
    /// Force a recompile mid-sequence and count agent-visible errors. The exit criterion is zero:
    /// a domain reload should look like a slow call, not a failure.
    /// </summary>
    public async Task<JsonObject> ReloadAsync()
    {
        var errors = new JsonArray();
        var sw = Stopwatch.StartNew();

        var compile = await CallAsync("unity_run", new() { ["tool"] = "editor.compile" });
        if ((bool?)compile["ok"] != true) errors.Add("compile: " + compile["code"]);

        // Fire a mixed sequence straight into the reload window.
        var during = new List<Task<JsonObject>>();
        for (var i = 0; i < 8; i++)
        {
            during.Add(CallAsync("unity_run", new() { ["tool"] = "editor.ping" }));
            during.Add(CallAsync("unity_run", new()
            {
                ["tool"] = "gameobject.create",
                ["args"] = new JsonObject { ["name"] = Prefix + "reload_" + i }
            }));
            await Task.Delay(120);
        }

        var results = await Task.WhenAll(during);
        foreach (var r in results)
            if ((bool?)r["ok"] != true) errors.Add((string?)r["code"] + ": " + (string?)r["message"]);

        var held = results.Count(r => (long?)r["meta"]?["heldMs"] > 0);
        var replayed = results.Count(r => (bool?)r["meta"]?["replayed"] == true);

        return new JsonObject
        {
            ["opsDuringReload"] = results.Length,
            ["visibleErrors"] = errors.Count,
            ["heldOps"] = held,
            ["replayedOps"] = replayed,
            ["sequenceMs"] = sw.ElapsedMilliseconds,
            ["errors"] = errors,
            ["pass"] = errors.Count == 0
        };
    }

    /// <summary>
    /// What a task actually costs in tokens: the always-loaded surface, plus the domains a task
    /// loads on demand. The budget to beat is 56,800 tokens at baseline for every task.
    /// </summary>
    public async Task<JsonObject> SkillTreeAsync()
    {
        var map = await CallAsync("unity_skill", new());
        var mapBytes = Encoding.UTF8.GetByteCount(map.ToJsonString());

        var loaded = new JsonObject();
        var total = 0;
        foreach (var id in new[] { "material", "scene.query", "gameobject" })
        {
            var node = await CallAsync("unity_skill", new() { ["id"] = id });
            var bytes = Encoding.UTF8.GetByteCount(node.ToJsonString());
            loaded[id] = bytes / 4;
            total += bytes;
        }

        var find = await CallAsync("unity_find", new() { ["query"] = "assign a texture to a material" });
        var top = (string?)find["data"]?["items"]?[0]?["id"];

        return new JsonObject
        {
            ["mapTokens"] = mapBytes / 4,
            ["perSkillTokens"] = loaded,
            ["typicalTaskTokens"] = 660 + mapBytes / 4 + total / 4,
            ["findTop"] = top,
            ["baseline"] = 56800,
            ["pass"] = 660 + mapBytes / 4 + total / 4 < 15000
        };
    }

    /// <summary>
    /// The exit criterion this phase exists for: replace the 138 KB scene read with a query.
    /// Both numbers are measured here, against the same scene, in the same run.
    /// </summary>
    public async Task<JsonObject> SceneQueryAsync()
    {
        // The "dump" comparison: every object with everything a naive lister would return.
        var dump = await CallAsync("unity_run", new()
        {
            ["tool"] = "scene.query",
            ["args"] = new JsonObject
            {
                ["select"] = "//*",
                ["fields"] = new JsonArray("id", "name", "path", "active", "activeInHierarchy", "tag",
                                           "layer", "parent", "childCount", "position", "rotation",
                                           "scale", "components"),
                ["limit"] = 500
            },
            ["maxResponseBytes"] = 4_000_000
        });

        // The projected query: the same scene, the question actually being asked.
        var sw = Stopwatch.StartNew();
        var query = await CallAsync("unity_run", new()
        {
            ["tool"] = "scene.query",
            ["args"] = new JsonObject
            {
                ["select"] = "//*[has:Renderer]",
                ["fields"] = new JsonArray("path", "MeshRenderer.sharedMaterial"),
                ["limit"] = 50
            }
        });
        var queryMs = sw.ElapsedMilliseconds;

        var count = await CallAsync("unity_run", new()
        {
            ["tool"] = "scene.count",
            ["args"] = new JsonObject { ["select"] = "//*[has:Renderer]" }
        });

        // The criterion is the *scene overview* read — the call that cost 138,205 B before.
        var overview = await CallAsync("unity_run", new() { ["tool"] = "scene.info" });
        var overviewBytes = Encoding.UTF8.GetByteCount(overview.ToJsonString());

        var dumpBytes = Encoding.UTF8.GetByteCount(dump.ToJsonString());
        var queryBytes = Encoding.UTF8.GetByteCount(query.ToJsonString());
        var countBytes = Encoding.UTF8.GetByteCount(count.ToJsonString());

        return new JsonObject
        {
            ["dumpBytes"] = dumpBytes,
            ["dumpTruncated"] = (bool?)dump["meta"]?["truncated"] ?? false,
            ["queryBytes"] = queryBytes,
            ["queryMs"] = queryMs,
            ["countBytes"] = countBytes,
            ["objectsMatched"] = (int?)count["data"]?["count"] ?? -1,
            ["overviewBytes"] = overviewBytes,
            ["baselineOverviewBytes"] = 138205,
            ["target"] = "scene overview < 2048 B (was 138,205 B)",
            ["pass"] = overviewBytes < 2048 && (bool?)query["ok"] == true && (bool?)count["ok"] == true
        };
    }

    /// <summary>
    /// Code mode against the same workload as batch: 20 creates. Batching collapses time, code
    /// mode collapses context, and the two are complementary rather than alternatives.
    /// </summary>
    public async Task<JsonObject> CodeModeAsync()
    {
        var ops = new JsonArray();
        for (var i = 0; i < 20; i++)
            ops.Add(new JsonObject
            {
                ["op"] = "gameobject.create",
                ["args"] = new JsonObject { ["name"] = Prefix + "cmp_" + i }
            });

        var swBatch = Stopwatch.StartNew();
        var batch = await CallAsync("unity_batch", new() { ["ops"] = ops, ["returns"] = "ids" });
        var batchMs = swBatch.ElapsedMilliseconds;
        var batchBytes = Encoding.UTF8.GetByteCount(batch.ToJsonString());

        var swScript = Stopwatch.StartNew();
        var script = await CallAsync("unity_script", new()
        {
            ["code"] = """
                for (int i = 0; i < 20; i++) {
                    var go = new GameObject("__UMCP_BENCH_cms_" + i);
                    Undo.RegisterCreatedObjectUndo(go, "bench code mode");
                }
                return new { created = 20 };
                """
        });
        var scriptMs = swScript.ElapsedMilliseconds;
        var scriptBytes = Encoding.UTF8.GetByteCount(script.ToJsonString());

        // Re-run to show the compile cache doing its job.
        var swCached = Stopwatch.StartNew();
        var cached = await CallAsync("unity_script", new() { ["code"] = "Object.FindObjectsByType<Renderer>(FindObjectsSortMode.None).Length" });
        var firstCompileMs = (long?)cached["meta"]?["compileMs"] ?? -1;
        var firstMs = swCached.ElapsedMilliseconds;
        var swAgain = Stopwatch.StartNew();
        var again = await CallAsync("unity_script", new() { ["code"] = "Object.FindObjectsByType<Renderer>(FindObjectsSortMode.None).Length" });
        var cachedRoundTripMs = swAgain.ElapsedMilliseconds;
        var cachedCompileMs = (long?)again["meta"]?["compileMs"] ?? -1;

        var denied = (string?)script["code"] == "E_PROFILE_DENIED";

        return new JsonObject
        {
            ["batchMs"] = batchMs,
            ["batchBytes"] = batchBytes,
            ["scriptMs"] = scriptMs,
            ["scriptBytes"] = scriptBytes,
            ["bytesRatio"] = scriptBytes == 0 ? 0 : Math.Round((double)batchBytes / scriptBytes, 1),
            ["scriptOk"] = (bool?)script["ok"] ?? false,
            ["scriptCode"] = (string?)script["code"],
            ["queryFirstMs"] = firstMs,
            ["queryCachedMs"] = cachedRoundTripMs,
            ["queryBytes"] = Encoding.UTF8.GetByteCount(again.ToJsonString()),
            ["compileMsFirst"] = firstCompileMs,
            ["compileMsCached"] = cachedCompileMs,
            ["rendererCount"] = again["data"]?["value"]?.DeepClone(),
            ["pass"] = denied || ((bool?)script["ok"] == true && scriptBytes < batchBytes)
        };
    }

    /// <summary>
    /// Confirms the out-of-band control channel answers and reports tick age. The full modal test
    /// needs a human to raise a dialog; this proves the mechanism that detects one is live.
    /// </summary>
    public async Task<JsonObject> BlockedProbeAsync()
    {
        var status = await CallAsync("unity_projects", new());
        var editor = status["data"]?["editors"]?[0];
        return new JsonObject
        {
            ["controlChannel"] = editor?["controlChannel"]?.DeepClone(),
            ["msSinceTick"] = editor?["msSinceTick"]?.DeepClone(),
            ["health"] = editor?["health"]?.DeepClone(),
            ["lastRoundTripMs"] = editor?["lastRoundTripMs"]?.DeepClone(),
            ["pass"] = (string?)editor?["controlChannel"] == "ok"
        };
    }

    /// <summary>
    /// Stall the Editor main thread and confirm a concurrent operation is told
    /// E_EDITOR_BLOCKED — with a reason — rather than sitting in a silent timeout.
    ///
    /// A modal dialog produces exactly this condition: the pump stops while the socket stays
    /// ESTABLISHED and /health cheerfully reports connected. Detection keys on main-thread tick
    /// age, so a stall is the same signal from the detector's point of view, and unlike a real
    /// dialog it can run unattended.
    /// </summary>
    public async Task<JsonObject> BlockedUnderStallAsync()
    {
        var stall = CallAsync("unity_run", new()
        {
            ["tool"] = "editor.stall",
            ["args"] = new JsonObject { ["seconds"] = 12 }
        });

        await Task.Delay(1500);   // let the stall reach the main thread

        var sw = Stopwatch.StartNew();
        var victim = await CallAsync("unity_run", new() { ["tool"] = "editor.ping" });
        var detectMs = sw.ElapsedMilliseconds;

        var code = (string?)victim["code"];
        try { await stall; } catch { }

        // The daemon gave up on the stalling op long before Unity did: the Editor is still
        // asleep. Wait for a real completed round trip before letting the next test run, or it
        // measures the tail of this one.
        var recoveredMs = await WaitUntilHealthyAsync(TimeSpan.FromSeconds(60));

        return new JsonObject
        {
            ["code"] = code,
            ["detectMs"] = detectMs,
            ["message"] = (string?)victim["message"],
            ["recoveredMs"] = recoveredMs,
            ["target"] = "E_EDITOR_BLOCKED within 15000ms",
            ["pass"] = code == "E_EDITOR_BLOCKED" && detectMs <= 15000 && recoveredMs >= 0
        };
    }

    /// <summary>
    /// Wait until a ping actually completes. Returns how long that took, or -1 on give-up.
    /// </summary>
    public async Task<long> WaitUntilHealthyAsync(TimeSpan timeout)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < timeout)
        {
            var r = await CallAsync("unity_run", new() { ["tool"] = "editor.ping" });
            if ((bool?)r["ok"] == true) return sw.ElapsedMilliseconds;
            await Task.Delay(500);
        }
        return -1;
    }

    /// <summary>Delete everything this harness created. The scene is never saved.</summary>
    public async Task<JsonObject> CleanupAsync()
    {
        var find = await CallAsync("unity_run", new()
        {
            ["tool"] = "gameobject.find",
            ["args"] = new JsonObject { ["name"] = Prefix, ["limit"] = 500 }
        });

        // A refused query is not an empty scene. Reporting "nothing left" because the question
        // could not be asked is exactly the kind of rounded-up pass this project exists to avoid.
        if ((bool?)find["ok"] != true)
            return new JsonObject
            {
                ["deleted"] = 0,
                ["remaining"] = "unknown",
                ["queryFailed"] = (string?)find["code"] ?? "unknown",
                ["pass"] = false
            };

        var items = find["data"]?["items"] as JsonArray ?? new JsonArray();
        if (items.Count == 0) return new JsonObject { ["deleted"] = 0, ["remaining"] = 0, ["pass"] = true };

        var ops = new JsonArray();
        foreach (var item in items)
            ops.Add(new JsonObject
            {
                ["op"] = "gameobject.delete",
                ["args"] = new JsonObject { ["target"] = "#" + (int?)item?["id"] }
            });

        var result = await CallAsync("unity_batch", new() { ["ops"] = ops, ["returns"] = "none", ["undoName"] = "umcp-bench cleanup" });

        var check = await CallAsync("unity_run", new()
        {
            ["tool"] = "gameobject.find",
            ["args"] = new JsonObject { ["name"] = Prefix, ["limit"] = 500 }
        });
        var remaining = (bool?)check["ok"] == true ? (int?)check["data"]?["_total"] ?? -1 : -1;

        return new JsonObject
        {
            ["found"] = items.Count,
            ["deleted"] = (int?)result["data"]?["count"] ?? 0,
            ["remaining"] = remaining,
            ["pass"] = remaining == 0
        };
    }

    async Task<JsonObject> CallAsync(string tool, JsonObject args)
    {
        var dict = args.ToDictionary(kv => kv.Key, kv => (object?)JsonSerializer.Deserialize<JsonElement>(kv.Value!.ToJsonString()));
        var result = await client.CallToolAsync(tool, dict);
        var text = result.Content.OfType<TextContentBlock>().FirstOrDefault()?.Text ?? "{}";
        return JsonNode.Parse(text) as JsonObject ?? new JsonObject { ["ok"] = false, ["message"] = text };
    }

    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hWnd, out int pid);
}
