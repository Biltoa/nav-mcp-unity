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
var reloads = ArgInt("--reloads", 5);
var soakSeconds = ArgInt("--soak", 0);
var fleet = Environment.GetCommandLineArgs().Contains("--fleet");
var smoke = Environment.GetCommandLineArgs().Contains("--smoke");
var fleetRoot = Arg("--fleet-root") ?? @"D:\umcp-fleet-scratch";
var fleetProjects = ArgInt("--fleet-projects", 3);
var fleetClean = Environment.GetCommandLineArgs().Contains("--fleet-clean");
var token = Arg("--token") ?? Environment.GetEnvironmentVariable("UMCP_TOKEN") ?? ReadTokenFile();

var transport = new HttpClientTransport(new HttpClientTransportOptions
{
    Endpoint = new Uri($"http://127.0.0.1:{port}/mcp"),
    AdditionalHeaders = token is null ? null : new Dictionary<string, string> { ["Authorization"] = "Bearer " + token }
});

await using var client = await McpClient.CreateAsync(transport);
var bench = new Bench(client);

bench.FleetRoot = fleetRoot;
bench.FleetProjects = fleetProjects;
bench.FleetKeep = !fleetClean;

var results = new JsonArray();
var focus = await bench.FocusStateAsync();

Console.WriteLine($"umcp-bench — Editor focus state: {focus}");
Console.WriteLine(new string('-', 78));

var all = new (string name, Func<Task<JsonObject>> run)[]
{
    ("toolsurface", bench.ToolSurfaceAsync),
    ("project-metadata", bench.ProjectMetadataAsync),
    ("ping-sequential", () => bench.SequentialAsync(16)),
    ("ping-concurrent-32", () => bench.ConcurrentAsync(32)),
    ("batch-32", () => bench.Batch32Async()),
    ("batch-dependent", bench.BatchDependentAsync),
    ("payload-scene-info", bench.PayloadAsync),
    ("asset-info-metadata", bench.AssetInfoMetadataAsync),
    ("build-metadata", bench.BuildMetadataAsync),
    ("reload-hold-replay", bench.ReloadAsync),
    ("compile-responsiveness", bench.CompileResponsivenessAsync),
    ("skill-tree", bench.SkillTreeAsync),
    ("scene-query-vs-dump", bench.SceneQueryAsync),
    ("validation-exact-limit", bench.ValidationExactLimitAsync),
    ("physics-settings-2d", bench.PhysicsSettings2DAsync),
    ("physics-bounded-results", bench.PhysicsBoundedResultsAsync),
    ("physics-sync", bench.PhysicsSyncAsync),
    ("navmesh-agent-bounds", bench.NavMeshAgentBoundsAsync),
    ("terrain-bounds", bench.TerrainBoundsAsync),
    ("timeline-director-bounds", bench.TimelineDirectorBoundsAsync),
    ("code-mode", bench.CodeModeAsync),
    ("mirror-latency", bench.MirrorLatencyAsync),
    ("mirror-reconcile", bench.MirrorReconcileAsync),
    ("reads-through-reloads", () => bench.ReadsThroughReloadsAsync(reloads)),
    ("path-confinement", bench.PathConfinementAsync),
    ("dry-run", bench.DryRunAsync),
    ("scene-diff", bench.SceneDiffAsync),
    ("validate-target", bench.ValidateTargetAsync),
    ("cancel-queued", bench.CancelAsync),
    ("blocked-detection", bench.BlockedProbeAsync),
    ("blocked-under-stall", bench.BlockedUnderStallAsync),
    ("blocked-withdrawn", () => bench.BlockedWithdrawalAsync()),
    ("cleanup", bench.CleanupAsync)
};

// The smoke run calls every tool in the catalog once with its own documented example. It is opt-in
// because it is coverage rather than measurement: nothing in it is a number worth tracking, and a
// failure in it is a defect rather than a regression in a figure.
if (smoke)
{
    all = new (string name, Func<Task<JsonObject>> run)[]
    {
        ("smoke-catalog", bench.SmokeAsync),
        ("action-contracts", bench.ActionContractsAsync),
        ("cleanup", bench.CleanupAsync)
    };
}

// The fleet run is opt-in: it launches real Editors, which costs minutes and gigabytes. It
// replaces the single-editor suite rather than joining it, because half of it is about killing
// an Editor and the other half assumes one that is alive.
if (fleet && !smoke)
{
    var defaultProject = await bench.DefaultProjectIdAsync();
    all = new (string name, Func<Task<JsonObject>> run)[]
    {
        ("fleet-discovery", bench.FleetDiscoveryAsync),
        ("fleet-prepare", bench.FleetPrepareAsync),
        ("fleet-open", bench.FleetOpenAsync),
        ("fleet-isolation", () => bench.FleetIsolationAsync()),
        ("fleet-crash-restart", bench.FleetCrashRestartAsync),
        ("fleet-cleanup", () => bench.FleetCleanupAsync(defaultProject))
    };
}

// The soak is opt-in: it is the proxy for "no drift over a long editing session",
// and it takes as long as you give it.
if (soakSeconds > 0 && !fleet && !smoke)
    all = all.Take(all.Length - 1)
             .Append(("mirror-soak", (Func<Task<JsonObject>>)(() => bench.SoakAsync(soakSeconds))))
             .Append(("cleanup", (Func<Task<JsonObject>>)bench.CleanupAsync))
             .ToArray();

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

static string? ReadTokenFile() => Umcp.UmcpPaths.ReadToken(ArgInt("--port", 8730));

sealed partial class Bench(McpClient client)
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

    /// <summary>The values required to use tag, layer and project-aware tools without guessing.</summary>
    public async Task<JsonObject> ProjectMetadataAsync()
    {
        var result = await CallAsync("unity_run", new() { ["tool"] = "project.info" });
        var data = result["data"];
        var tags = data?["tags"] as JsonArray ?? new JsonArray();
        var layers = data?["layers"] as JsonArray ?? new JsonArray();
        var sortingLayers = data?["sortingLayers"] as JsonArray ?? new JsonArray();
        var qualityLevels = data?["qualityLevels"] as JsonArray ?? new JsonArray();
        var packages = data?["packages"] as JsonArray ?? new JsonArray();
        var packageCount = (int?)data?["packageCount"] ?? -1;

        var hasUntagged = tags.Any(value => (string?)value == "Untagged");
        var hasDefaultLayer = layers.Any(value => (string?)value == "Default");
        var hasDefaultSortingLayer = sortingLayers.Any(value => (string?)value?["name"] == "Default");
        var hasUrp = packages.Any(value => (string?)value?["name"] == "com.unity.render-pipelines.universal");
        var packageInventoryComplete = packages.Count == Math.Min(packageCount, 200);

        return new JsonObject
        {
            ["packageCount"] = packageCount,
            ["packagesReturned"] = packages.Count,
            ["packageInventoryComplete"] = packageInventoryComplete,
            ["hasUrp"] = hasUrp,
            ["tags"] = tags.Count,
            ["layers"] = layers.Count,
            ["sortingLayers"] = sortingLayers.Count,
            ["qualityLevels"] = qualityLevels.Count,
            ["hasUntagged"] = hasUntagged,
            ["hasDefaultLayer"] = hasDefaultLayer,
            ["hasDefaultSortingLayer"] = hasDefaultSortingLayer,
            ["pass"] = (bool?)result["ok"] == true && packageCount > 0 && packageInventoryComplete &&
                       hasUrp && hasUntagged && hasDefaultLayer && hasDefaultSortingLayer &&
                       qualityLevels.Count > 0
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

    /// <summary>2D projects need their own solver and collision settings; they are not Physics aliases.</summary>
    public async Task<JsonObject> PhysicsSettings2DAsync()
    {
        var result = await CallAsync("unity_run", new()
        {
            ["tool"] = "physics.settings",
            ["args"] = new JsonObject { ["layerMatrix"] = true }
        });
        var settings = result["data"]?["physics2D"];
        var gravity = settings?["gravity"] as JsonArray ?? new JsonArray();
        var ignored = settings?["ignoredLayerPairs"] as JsonArray;
        var simulationMode = (string?)settings?["simulationMode"];

        return new JsonObject
        {
            ["gravity"] = gravity.DeepClone(),
            ["velocityIterations"] = settings?["velocityIterations"]?.DeepClone(),
            ["positionIterations"] = settings?["positionIterations"]?.DeepClone(),
            ["simulationMode"] = simulationMode,
            ["ignoredLayerPairs"] = ignored?.Count ?? -1,
            ["pass"] = (bool?)result["ok"] == true && gravity.Count == 2 &&
                       (int?)settings?["velocityIterations"] > 0 &&
                       (int?)settings?["positionIterations"] > 0 &&
                       !string.IsNullOrEmpty(simulationMode) && ignored is not null
        };
    }

    /// <summary>Exactly N findings at limit N is complete, not truncated.</summary>
    public async Task<JsonObject> ValidationExactLimitAsync()
    {
        await CleanupAsync();
        var name = Prefix + "validation_exact";
        JsonObject validation = new();
        JsonObject overflow = new();
        JsonObject cleanup = new();
        try
        {
            var created = await CallAsync("unity_script", new()
            {
                ["code"] = $$"""
                    var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
                    go.name = "{{name}}";
                    go.GetComponent<Renderer>().sharedMaterials = new Material[3];
                    Undo.RegisterCreatedObjectUndo(go, "umcp-bench validation boundary");
                    return go.GetInstanceID();
                    """
            });
            if ((bool?)created["ok"] != true)
                return new JsonObject { ["error"] = "failed to create validation fixture", ["pass"] = false };

            validation = await CallAsync("unity_run", new()
            {
                ["tool"] = "scene.validate",
                ["args"] = new JsonObject
                {
                    ["root"] = name,
                    ["checks"] = new JsonArray("materials"),
                    ["limit"] = 3
                }
            });

            var changed = await CallAsync("unity_script", new()
            {
                ["code"] = $$"""
                    var go = GameObject.Find("{{name}}");
                    go.GetComponent<Renderer>().sharedMaterials = new Material[4];
                    return 4;
                    """
            });
            if ((bool?)changed["ok"] != true)
                return new JsonObject { ["error"] = "failed to update validation fixture", ["pass"] = false };

            overflow = await CallAsync("unity_run", new()
            {
                ["tool"] = "scene.validate",
                ["args"] = new JsonObject
                {
                    ["root"] = name,
                    ["checks"] = new JsonArray("materials"),
                    ["limit"] = 3
                }
            });
        }
        finally
        {
            cleanup = await CleanupAsync();
        }

        var data = validation["data"];
        var count = (int?)data?["count"] ?? -1;
        var truncated = (bool?)data?["_truncated"] ?? false;
        var overflowData = overflow["data"];
        var overflowCount = (int?)overflowData?["count"] ?? -1;
        var overflowTruncated = (bool?)overflowData?["_truncated"] ?? false;
        return new JsonObject
        {
            ["exactCount"] = count,
            ["exactTruncated"] = truncated,
            ["overflowCount"] = overflowCount,
            ["overflowTruncated"] = overflowTruncated,
            ["cleanup"] = (bool?)cleanup["pass"] ?? false,
            ["pass"] = (bool?)validation["ok"] == true && count == 3 && !truncated &&
                       (bool?)overflow["ok"] == true && overflowCount == 3 && overflowTruncated &&
                       (bool?)cleanup["pass"] == true
        };
    }

    /// <summary>A limited all-hit raycast must disclose the real total and truncation.</summary>
    public async Task<JsonObject> PhysicsBoundedResultsAsync()
    {
        var ops = new JsonArray();
        for (var i = 0; i < 8; i++)
            ops.Add(new JsonObject
            {
                ["op"] = "gameobject.create",
                ["args"] = new JsonObject
                {
                    ["name"] = Prefix + "ray_" + i,
                    ["primitive"] = "Cube",
                    ["position"] = new JsonArray(0, 1000, i * 2)
                }
            });

        JsonObject ray = new();
        JsonObject cleanup = new();
        try
        {
            var created = await CallAsync("unity_batch", new()
            {
                ["ops"] = ops,
                ["returns"] = "summary",
                ["undoName"] = "umcp-bench bounded physics"
            });
            if ((bool?)created["ok"] != true)
                return new JsonObject { ["error"] = "failed to create raycast fixtures", ["pass"] = false };

            ray = await CallAsync("unity_run", new()
            {
                ["tool"] = "physics.raycast",
                ["args"] = new JsonObject
                {
                    ["origin"] = new JsonArray(0, 1000, -5),
                    ["direction"] = new JsonArray(0, 0, 1),
                    ["maxDistance"] = 100,
                    ["all"] = true,
                    ["limit"] = 3
                }
            });
        }
        finally
        {
            cleanup = await CleanupAsync();
        }

        var data = ray["data"];
        var hits = data?["hits"] as JsonArray;
        var total = (int?)data?["total"] ?? -1;
        var returned = (int?)data?["returned"] ?? hits?.Count ?? -1;
        var truncated = (bool?)data?["_truncated"] ?? false;

        return new JsonObject
        {
            ["total"] = total,
            ["returned"] = returned,
            ["truncated"] = truncated,
            ["cleanup"] = (bool?)cleanup["pass"] ?? false,
            ["pass"] = (bool?)ray["ok"] == true && total >= 8 && returned == 3 &&
                       truncated && (bool?)cleanup["pass"] == true
        };
    }

    /// <summary>NavMesh agent inventory must distinguish its total from its bounded sample.</summary>
    public async Task<JsonObject> NavMeshAgentBoundsAsync()
    {
        await CleanupAsync();
        JsonObject info = new();
        JsonObject cleanup = new();
        try
        {
            var created = await CallAsync("unity_script", new()
            {
                ["code"] = $$"""
                    for (var i = 0; i < 55; i++) {
                        var go = new GameObject("{{Prefix}}nav_agent_" + i);
                        go.AddComponent<UnityEngine.AI.NavMeshAgent>();
                        Undo.RegisterCreatedObjectUndo(go, "umcp-bench navmesh bounds");
                    }
                    return 55;
                    """
            });
            if ((bool?)created["ok"] != true)
                return new JsonObject { ["error"] = "failed to create NavMeshAgent fixtures", ["pass"] = false };

            info = await CallAsync("unity_run", new() { ["tool"] = "navmesh.info" });
        }
        finally
        {
            cleanup = await CleanupAsync();
        }

        var agents = info["data"]?["agents"];
        var sample = agents?["sample"] as JsonArray;
        var count = (int?)agents?["count"] ?? -1;
        var returned = (int?)agents?["returned"] ?? sample?.Count ?? -1;
        var truncated = (bool?)agents?["_truncated"] ?? false;
        return new JsonObject
        {
            ["count"] = count,
            ["returned"] = returned,
            ["truncated"] = truncated,
            ["cleanup"] = (bool?)cleanup["pass"] ?? false,
            ["pass"] = (bool?)info["ok"] == true && count >= 55 && returned == 50 && truncated &&
                       (bool?)cleanup["pass"] == true
        };
    }

    /// <summary>Terrain inventory must identify when its ten-row payload is a sample.</summary>
    public async Task<JsonObject> TerrainBoundsAsync()
    {
        await CleanupAsync();
        JsonObject info = new();
        JsonObject cleanup = new();
        try
        {
            var created = await CallAsync("unity_script", new()
            {
                ["code"] = $$"""
                    for (var i = 0; i < 11; i++) {
                        var go = new GameObject("{{Prefix}}terrain_" + i);
                        go.AddComponent<Terrain>();
                        Undo.RegisterCreatedObjectUndo(go, "umcp-bench terrain bounds");
                    }
                    return 11;
                    """
            });
            if ((bool?)created["ok"] != true)
                return new JsonObject { ["error"] = "failed to create terrain fixtures", ["pass"] = false };

            info = await CallAsync("unity_run", new() { ["tool"] = "terrain.info" });
        }
        finally
        {
            cleanup = await CleanupAsync();
        }

        var data = info["data"];
        var rows = data?["terrains"] as JsonArray;
        var count = (int?)data?["count"] ?? -1;
        var returned = (int?)data?["returned"] ?? rows?.Count ?? -1;
        var truncated = (bool?)data?["_truncated"] ?? false;
        return new JsonObject
        {
            ["count"] = count,
            ["returned"] = returned,
            ["truncated"] = truncated,
            ["cleanup"] = (bool?)cleanup["pass"] ?? false,
            ["pass"] = (bool?)info["ok"] == true && count >= 11 && returned == 10 && truncated &&
                       (bool?)cleanup["pass"] == true
        };
    }

    /// <summary>Timeline director inventory must distinguish its total from its 20-row sample.</summary>
    public async Task<JsonObject> TimelineDirectorBoundsAsync()
    {
        await CleanupAsync();
        JsonObject info = new();
        JsonObject cleanup = new();
        try
        {
            var created = await CallAsync("unity_script", new()
            {
                ["code"] = $$"""
                    for (var i = 0; i < 21; i++) {
                        var go = new GameObject("{{Prefix}}director_" + i);
                        go.AddComponent<UnityEngine.Playables.PlayableDirector>();
                        Undo.RegisterCreatedObjectUndo(go, "umcp-bench timeline bounds");
                    }
                    return 21;
                    """
            });
            if ((bool?)created["ok"] != true)
                return new JsonObject { ["error"] = "failed to create PlayableDirector fixtures", ["pass"] = false };

            info = await CallAsync("unity_run", new() { ["tool"] = "timeline.info" });
        }
        finally
        {
            cleanup = await CleanupAsync();
        }

        var data = info["data"];
        var rows = data?["directors"] as JsonArray;
        var count = (int?)data?["count"] ?? -1;
        var returned = (int?)data?["returned"] ?? rows?.Count ?? -1;
        var truncated = (bool?)data?["_truncated"] ?? false;
        return new JsonObject
        {
            ["count"] = count,
            ["returned"] = returned,
            ["truncated"] = truncated,
            ["cleanup"] = (bool?)cleanup["pass"] ?? false,
            ["pass"] = (bool?)info["ok"] == true && count >= 21 && returned == 20 && truncated &&
                       (bool?)cleanup["pass"] == true
        };
    }

    /// <summary>Asset metadata is bounded and says exactly when dependencies or sub-assets were cut.</summary>
    public async Task<JsonObject> AssetInfoMetadataAsync()
    {
        var find = await CallAsync("unity_run", new()
        {
            ["tool"] = "assets.find",
            ["args"] = new JsonObject { ["filter"] = "t:Scene", ["limit"] = 1 }
        });
        var path = (string?)find["data"]?["items"]?[0]?["path"];
        if (path is null)
            return new JsonObject { ["error"] = "no scene asset found", ["pass"] = false };

        var result = await CallAsync("unity_run", new()
        {
            ["tool"] = "assets.info",
            ["args"] = new JsonObject { ["path"] = path, ["dependencies"] = true }
        });
        var data = result["data"];
        var labels = data?["labels"] as JsonArray;
        var subAssets = data?["subAssets"] as JsonArray;
        var dependencies = data?["dependencies"] as JsonArray;
        var subAssetCount = (int?)data?["subAssetCount"] ?? -1;
        var dependencyCount = (int?)data?["dependencyCount"] ?? -1;

        return new JsonObject
        {
            ["path"] = path,
            ["type"] = data?["type"]?.DeepClone(),
            ["importer"] = data?["importer"]?["type"]?.DeepClone(),
            ["labels"] = labels?.Count ?? -1,
            ["subAssetCount"] = subAssetCount,
            ["subAssetsReturned"] = subAssets?.Count ?? -1,
            ["dependencyCount"] = dependencyCount,
            ["dependenciesReturned"] = dependencies?.Count ?? -1,
            ["pass"] = (bool?)result["ok"] == true && !string.IsNullOrEmpty((string?)data?["guid"]) &&
                       labels is not null && subAssets is not null && dependencies is not null &&
                       data?["importer"] is not null && subAssetCount >= subAssets.Count &&
                       dependencyCount >= dependencies.Count
        };
    }

    /// <summary>Build automation needs target identity and compiler/linker configuration.</summary>
    public async Task<JsonObject> BuildMetadataAsync()
    {
        var result = await CallAsync("unity_run", new() { ["tool"] = "build.settings" });
        var data = result["data"];
        var defines = data?["scriptingDefineSymbols"] as JsonArray;
        var identifier = (string?)data?["applicationIdentifier"];
        var api = (string?)data?["apiCompatibilityLevel"];
        var stripping = (string?)data?["managedStrippingLevel"];

        return new JsonObject
        {
            ["applicationIdentifier"] = identifier,
            ["apiCompatibilityLevel"] = api,
            ["defines"] = defines?.Count ?? -1,
            ["managedStrippingLevel"] = stripping,
            ["allowUnsafeCode"] = data?["allowUnsafeCode"]?.DeepClone(),
            ["pass"] = (bool?)result["ok"] == true && !string.IsNullOrEmpty(identifier) &&
                       !string.IsNullOrEmpty(api) && defines is not null &&
                       !string.IsNullOrEmpty(stripping) && data?["allowUnsafeCode"] is JsonValue
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
    /// A compile must be visible promptly without making the MCP endpoint wait for the Editor.
    /// Unity is deliberately left unfocused by the harness: requiring a click to begin or finish
    /// compilation is a failure, as is dropping the Editor from unity_projects during reload.
    /// </summary>
    public async Task<JsonObject> CompileResponsivenessAsync()
    {
        var initial = await CallAsync("unity_projects", new());
        var firstEditor = initial["data"]?["editors"]?[0];
        var projectId = (string?)firstEditor?["projectId"];
        var initialEpoch = (int?)firstEditor?["epoch"] ?? -1;
        if (projectId is null)
            return new JsonObject { ["error"] = "no connected Editor", ["pass"] = false };

        var compileClock = Stopwatch.StartNew();
        var compile = await CallAsync("unity_run", new()
        {
            ["tool"] = "editor.compile",
            ["project"] = projectId
        });
        var compileRequestMs = compileClock.ElapsedMilliseconds;

        var statusFailures = new JsonArray();
        var readFailures = new JsonArray();
        var statusTimes = new List<long>();
        var readTimes = new List<long>();
        var missingEditorSamples = 0;
        var busySamples = 0;
        long? busyDetectedMs = null;
        var finalEpoch = initialEpoch;
        var postReloadSamples = 0;
        var pollClock = Stopwatch.StartNew();

        while (pollClock.Elapsed < TimeSpan.FromSeconds(30))
        {
            var statusClock = Stopwatch.StartNew();
            var status = await CallAsync("unity_projects", new());
            statusTimes.Add(statusClock.ElapsedMilliseconds);

            if ((bool?)status["ok"] != true)
            {
                statusFailures.Add($"{(string?)status["code"]}: {(string?)status["message"]}");
            }
            else
            {
                var editors = status["data"]?["editors"] as JsonArray;
                var editor = editors?.FirstOrDefault(e => (string?)e?["projectId"] == projectId);
                if (editor is null)
                {
                    missingEditorSamples++;
                }
                else
                {
                    var compiling = (bool?)editor["compiling"] == true;
                    var reloading = (bool?)editor["reloading"] == true ||
                                    (string?)editor["health"] == "reloading";
                    if (compiling || reloading)
                    {
                        busySamples++;
                        busyDetectedMs ??= compileClock.ElapsedMilliseconds;
                    }

                    finalEpoch = (int?)editor["epoch"] ?? finalEpoch;
                    if (finalEpoch > initialEpoch && !compiling && !reloading)
                        postReloadSamples++;
                }
            }

            var readClock = Stopwatch.StartNew();
            var read = await CallAsync("unity_run", new()
            {
                ["tool"] = "scene.count",
                ["project"] = projectId,
                ["args"] = new JsonObject { ["select"] = "//*" }
            });
            readTimes.Add(readClock.ElapsedMilliseconds);
            if ((bool?)read["ok"] != true)
                readFailures.Add($"{(string?)read["code"]}: {(string?)read["message"]}");

            if (postReloadSamples >= 3) break;
            await Task.Delay(75);
        }

        var reloaded = finalEpoch > initialEpoch;
        return new JsonObject
        {
            ["compileRequestMs"] = compileRequestMs,
            ["busyDetectedMs"] = busyDetectedMs,
            ["busySamples"] = busySamples,
            ["missingEditorSamples"] = missingEditorSamples,
            ["statusCalls"] = statusTimes.Count,
            ["statusFailures"] = statusFailures.Count,
            ["statusMaxMs"] = statusTimes.Count == 0 ? -1 : statusTimes.Max(),
            ["readCalls"] = readTimes.Count,
            ["readFailures"] = readFailures.Count,
            ["readMaxMs"] = readTimes.Count == 0 ? -1 : readTimes.Max(),
            ["initialEpoch"] = initialEpoch,
            ["finalEpoch"] = finalEpoch,
            ["focusRequired"] = !reloaded,
            ["failures"] = new JsonObject
            {
                ["status"] = statusFailures,
                ["reads"] = readFailures
            },
            ["target"] = "busy visible within 1000ms; no missing row or failed/stalled reads; reload without focus",
            ["pass"] = (bool?)compile["ok"] == true && busyDetectedMs <= 1000 &&
                       missingEditorSamples == 0 && statusFailures.Count == 0 &&
                       readFailures.Count == 0 && statusTimes.Max() < 1000 &&
                       readTimes.Max() < 1000 && reloaded
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
    /// Read latency with the mirror answering, against the same read forced live. The mirror is
    /// the reason a read no longer costs an Editor tick.
    /// </summary>
    public async Task<JsonObject> MirrorLatencyAsync()
    {
        // Warm both paths so neither number includes a first-call cost.
        await QueryAsync(false);
        await QueryAsync(true);

        var mirrorTimes = new List<long>();
        JsonObject last = new();
        for (var i = 0; i < 20; i++)
        {
            var sw = Stopwatch.StartNew();
            last = await QueryAsync(false);
            mirrorTimes.Add(sw.ElapsedMilliseconds);
        }

        var liveTimes = new List<long>();
        JsonObject live = new();
        for (var i = 0; i < 10; i++)
        {
            var sw = Stopwatch.StartNew();
            live = await QueryAsync(true);
            liveTimes.Add(sw.ElapsedMilliseconds);
        }

        var source = (string?)last["meta"]?["source"];
        var liveSource = (string?)live["meta"]?["source"];

        // The two paths must agree, or "source: mirror" is a lie.
        var mirrorCount = (int?)last["data"]?["_total"] ?? -1;
        var liveCount = (int?)live["data"]?["_total"] ?? -2;

        return new JsonObject
        {
            ["source"] = source,
            ["verifySource"] = liveSource,
            ["mirrorMsMean"] = Math.Round(mirrorTimes.Average(), 2),
            ["mirrorMsMax"] = mirrorTimes.Max(),
            ["liveMsMean"] = Math.Round(liveTimes.Average(), 1),
            ["staleMs"] = last["meta"]?["staleMs"]?.DeepClone(),
            ["mirrorMatches"] = mirrorCount,
            ["liveMatches"] = liveCount,
            ["agree"] = mirrorCount == liveCount,
            ["target"] = "served from mirror, agrees with live, mean < 10 ms",
            ["pass"] = source == "mirror" && liveSource == "live" && mirrorCount == liveCount
                       && mirrorTimes.Average() < 10
        };
    }

    Task<JsonObject> QueryAsync(bool verify) => CallAsync("unity_run", new()
    {
        ["tool"] = "scene.query",
        ["args"] = new JsonObject
        {
            ["select"] = "//*[has:Transform]",
            ["fields"] = new JsonArray("name", "path"),
            ["limit"] = 100
        },
        ["verify"] = verify
    });

    /// <summary>
    /// Churn the hierarchy in every way the mirror models, then ask the Editor for hashes and
    /// compare. "Zero drift" only means something if drift was actually looked for.
    /// </summary>
    public async Task<JsonObject> MirrorReconcileAsync()
    {
        var mutations = 0;

        // Create a tree, reparent it, rename it, toggle it, add components, reorder, delete some.
        var ops = new JsonArray();
        for (var i = 0; i < 12; i++)
            ops.Add(new JsonObject
            {
                ["op"] = "gameobject.create",
                ["args"] = new JsonObject { ["name"] = Prefix + "rec_" + i, ["primitive"] = i % 3 == 0 ? "Cube" : null }
            });
        var created = await CallAsync("unity_batch", new() { ["ops"] = ops, ["returns"] = "ids" });
        mutations += 12;

        var ids = (created["data"]?["results"] as JsonArray)?.Select(n => (int?)n ?? 0).Where(n => n != 0).ToArray()
                  ?? Array.Empty<int>();

        if (ids.Length >= 6)
        {
            var second = new JsonArray();
            // reparent
            for (var i = 1; i < 4; i++)
                second.Add(new JsonObject
                {
                    ["op"] = "gameobject.setParent",
                    ["args"] = new JsonObject { ["target"] = "#" + ids[i], ["parent"] = "#" + ids[0] }
                });
            // rename
            second.Add(new JsonObject
            {
                ["op"] = "gameobject.rename",
                ["args"] = new JsonObject { ["target"] = "#" + ids[4], ["name"] = Prefix + "rec_renamed" }
            });
            // deactivate
            second.Add(new JsonObject
            {
                ["op"] = "gameobject.setActive",
                ["args"] = new JsonObject { ["target"] = "#" + ids[5], ["active"] = false }
            });
            // add a component (changes the component list the hash covers)
            second.Add(new JsonObject
            {
                ["op"] = "component.add",
                ["args"] = new JsonObject { ["target"] = "#" + ids[0], ["type"] = "Rigidbody" }
            });
            // tag and layer
            second.Add(new JsonObject
            {
                ["op"] = "gameobject.setLayer",
                ["args"] = new JsonObject { ["target"] = "#" + ids[2], ["layer"] = "Water" }
            });
            await CallAsync("unity_batch", new() { ["ops"] = second, ["returns"] = "none" });
            mutations += second.Count;

            // delete two, including one with children
            var third = new JsonArray
            {
                new JsonObject { ["op"] = "gameobject.delete", ["args"] = new JsonObject { ["target"] = "#" + ids[6] } },
                new JsonObject { ["op"] = "gameobject.delete", ["args"] = new JsonObject { ["target"] = "#" + ids[0] } }
            };
            await CallAsync("unity_batch", new() { ["ops"] = third, ["returns"] = "none" });
            mutations += 2;
        }

        // Let the Editor publish its change stream and the daemon apply it.
        await Task.Delay(600);

        var projects = await CallAsync("unity_projects", new() { ["reconcile"] = true });
        var editor = projects["data"]?["editors"]?[0];
        var reconcile = editor?["reconcile"];

        return new JsonObject
        {
            ["mutations"] = mutations,
            ["drift"] = reconcile?["drift"]?.DeepClone(),
            ["liveNodes"] = reconcile?["liveNodes"]?.DeepClone(),
            ["mirrorNodes"] = reconcile?["mirrorNodes"]?.DeepClone(),
            ["details"] = reconcile?["details"]?.DeepClone(),
            ["resyncs"] = editor?["mirror"]?["resyncs"]?.DeepClone(),
            ["target"] = "zero drift after a mixed mutation workload",
            ["pass"] = (bool?)reconcile?["drift"] == false
        };
    }

    /// <summary>
    /// Reads must keep working across domain reloads. Without a mirror every read during a
    /// recompile either fails or stalls; with one, only mutations wait.
    /// </summary>
    public async Task<JsonObject> ReadsThroughReloadsAsync(int reloads)
    {
        var readFailures = new JsonArray();
        var mirrorServed = 0;
        var liveServed = 0;
        var staleServed = 0;
        var totalReads = 0;
        var sw = Stopwatch.StartNew();

        for (var r = 0; r < reloads; r++)
        {
            var compile = await CallAsync("unity_run", new() { ["tool"] = "editor.compile" });
            if ((bool?)compile["ok"] != true)
                readFailures.Add($"compile {r}: {(string?)compile["code"]}");

            // Hammer reads straight through the reload window.
            for (var i = 0; i < 12; i++)
            {
                var read = await CallAsync("unity_run", new()
                {
                    ["tool"] = "scene.count",
                    ["args"] = new JsonObject { ["select"] = "//*" }
                });
                totalReads++;

                if ((bool?)read["ok"] != true)
                {
                    readFailures.Add($"reload {r} read {i}: {(string?)read["code"]} {(string?)read["message"]}");
                    continue;
                }
                var source = (string?)read["meta"]?["source"];
                if (source == "mirror") mirrorServed++; else liveServed++;
                if ((bool?)read["meta"]?["stale"] == true) staleServed++;

                await Task.Delay(120);
            }

            await WaitUntilHealthyAsync(TimeSpan.FromSeconds(90));
        }

        return new JsonObject
        {
            ["reloads"] = reloads,
            ["reads"] = totalReads,
            ["readFailures"] = readFailures.Count,
            ["fromMirror"] = mirrorServed,
            ["fromLive"] = liveServed,
            ["flaggedStale"] = staleServed,
            ["seconds"] = sw.ElapsedMilliseconds / 1000,
            ["failures"] = readFailures,
            ["target"] = "zero read failures across every reload",
            ["pass"] = readFailures.Count == 0
        };
    }

    /// <summary>
    /// Sustained churn with repeated reconciles. This is the practical stand-in for "zero drift
    /// over an 8-hour editing session": the plan's criterion is about accumulated divergence, and
    /// what accumulates divergence is mutations and reconciles, not wall-clock hours. Reported as
    /// what it is — a churn soak of a stated length — not as eight hours.
    /// </summary>
    public async Task<JsonObject> SoakAsync(int seconds)
    {
        var rng = new Random(20260905);
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        var mutations = 0;
        var reconciles = 0;
        var driftEvents = new JsonArray();
        var live = new List<int>();
        var round = 0;

        while (DateTime.UtcNow < deadline)
        {
            round++;
            var ops = new JsonArray();

            // Create a handful.
            var creates = rng.Next(3, 10);
            for (var i = 0; i < creates; i++)
                ops.Add(new JsonObject
                {
                    ["op"] = "gameobject.create",
                    ["args"] = new JsonObject
                    {
                        ["name"] = Prefix + "soak_" + round + "_" + i,
                        ["primitive"] = rng.Next(4) == 0 ? "Cube" : null
                    }
                });

            var created = await CallAsync("unity_batch", new() { ["ops"] = ops, ["returns"] = "ids" });
            mutations += creates;
            foreach (var n in (created["data"]?["results"] as JsonArray) ?? new JsonArray())
                if ((int?)n is int id && id != 0) live.Add(id);

            // Mutate some of what exists, in every way the mirror models.
            if (live.Count > 4)
            {
                var second = new JsonArray();
                for (var i = 0; i < Math.Min(6, live.Count / 2); i++)
                {
                    var target = "#" + live[rng.Next(live.Count)];
                    switch (rng.Next(5))
                    {
                        case 0:
                            second.Add(new JsonObject { ["op"] = "gameobject.rename",
                                ["args"] = new JsonObject { ["target"] = target, ["name"] = Prefix + "soak_r" + round + "_" + i } });
                            break;
                        case 1:
                            second.Add(new JsonObject { ["op"] = "gameobject.setActive",
                                ["args"] = new JsonObject { ["target"] = target, ["active"] = rng.Next(2) == 0 } });
                            break;
                        case 2:
                            second.Add(new JsonObject { ["op"] = "gameobject.setParent",
                                ["args"] = new JsonObject { ["target"] = target, ["parent"] = "#" + live[rng.Next(live.Count)] } });
                            break;
                        case 3:
                            second.Add(new JsonObject { ["op"] = "component.add",
                                ["args"] = new JsonObject { ["target"] = target, ["type"] = rng.Next(2) == 0 ? "Rigidbody" : "BoxCollider" } });
                            break;
                        default:
                            second.Add(new JsonObject { ["op"] = "gameobject.setLayer",
                                ["args"] = new JsonObject { ["target"] = target, ["layer"] = rng.Next(2) == 0 ? "Water" : "Default" } });
                            break;
                    }
                }
                if (second.Count > 0)
                {
                    await CallAsync("unity_batch", new() { ["ops"] = second, ["returns"] = "none" });
                    mutations += second.Count;
                }
            }

            // Delete a few, so removal and root reordering are exercised too.
            if (live.Count > 20)
            {
                var third = new JsonArray();
                for (var i = 0; i < 5 && live.Count > 0; i++)
                {
                    var idx = rng.Next(live.Count);
                    third.Add(new JsonObject { ["op"] = "gameobject.delete",
                        ["args"] = new JsonObject { ["target"] = "#" + live[idx] } });
                    live.RemoveAt(idx);
                }
                await CallAsync("unity_batch", new() { ["ops"] = third, ["returns"] = "none" });
                mutations += third.Count;
            }

            await Task.Delay(400);

            var projects = await CallAsync("unity_projects", new() { ["reconcile"] = true });
            var reconcile = projects["data"]?["editors"]?[0]?["reconcile"];
            reconciles++;

            if ((bool?)reconcile?["drift"] == true)
                driftEvents.Add(new JsonObject
                {
                    ["round"] = round,
                    ["mutationsSoFar"] = mutations,
                    ["liveNodes"] = reconcile?["liveNodes"]?.DeepClone(),
                    ["mirrorNodes"] = reconcile?["mirrorNodes"]?.DeepClone(),
                    ["details"] = reconcile?["details"]?.DeepClone()
                });
        }

        return new JsonObject
        {
            ["seconds"] = seconds,
            ["rounds"] = round,
            ["mutations"] = mutations,
            ["reconciles"] = reconciles,
            ["driftEvents"] = driftEvents.Count,
            ["drift"] = driftEvents,
            ["target"] = "zero drift across the whole soak",
            ["pass"] = driftEvents.Count == 0
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

    /// <summary>
    /// Delete everything this harness created, and verify it. The scene is never saved.
    ///
    /// Deliberately iterative: the soak reparents objects under each other, so deleting a parent
    /// takes its children with it and the ids queued behind it are already gone. A single batch
    /// stops at the first such failure, which is how an earlier version of this reported
    /// "deleted 0, remaining 720" and called it a day.
    /// </summary>
    public async Task<JsonObject> CleanupAsync()
    {
        var deleted = 0;
        var rounds = 0;
        var initial = -1;

        while (rounds++ < 40)
        {
            // Ask for ids only, so the page is small enough never to hit the response cap, and
            // take roots first: deleting a root removes its subtree in one operation.
            var find = await CallAsync("unity_run", new()
            {
                ["tool"] = "scene.query",
                ["args"] = new JsonObject
                {
                    ["select"] = $"//*[name^:{Prefix}]",
                    ["fields"] = new JsonArray("id"),
                    ["limit"] = 200
                },
                ["verify"] = true
            });

            if ((bool?)find["ok"] != true)
                return new JsonObject
                {
                    ["deleted"] = deleted,
                    ["remaining"] = "unknown",
                    ["queryFailed"] = (string?)find["code"] ?? "unknown",
                    ["pass"] = false
                };

            var total = (int?)find["data"]?["_total"] ?? 0;
            if (initial < 0) initial = total;
            if (total == 0) break;

            var items = find["data"]?["items"] as JsonArray ?? new JsonArray();
            if (items.Count == 0) break;

            // One op per call would be slow and one batch aborts at the first stale id, so send
            // small batches and let a failed one cost only its own remainder.
            foreach (var chunk in items.Chunk(20))
            {
                var ops = new JsonArray();
                foreach (var item in chunk)
                    ops.Add(new JsonObject
                    {
                        ["op"] = "gameobject.delete",
                        ["args"] = new JsonObject { ["target"] = "#" + (int?)item?["id"] }
                    });

                var result = await CallAsync("unity_batch", new()
                {
                    ["ops"] = ops,
                    ["returns"] = "none",
                    ["undoName"] = "umcp-bench cleanup"
                });
                deleted += (int?)result["data"]?["count"] ?? 0;
            }
        }

        var check = await CallAsync("unity_run", new()
        {
            ["tool"] = "scene.query",
            ["args"] = new JsonObject
            {
                ["select"] = $"//*[name^:{Prefix}]",
                ["fields"] = new JsonArray("id"),
                ["limit"] = 1
            },
            ["verify"] = true
        });

        var remaining = (bool?)check["ok"] == true ? (int?)check["data"]?["_total"] ?? -1 : -1;

        return new JsonObject
        {
            ["found"] = initial,
            ["deleted"] = deleted,
            ["rounds"] = rounds,
            ["remaining"] = remaining,
            ["pass"] = remaining == 0
        };
    }

    Task<JsonObject> CallAsync(string tool, JsonObject args) => CallAsync(tool, args, CancellationToken.None);

    async Task<JsonObject> CallAsync(string tool, JsonObject args, CancellationToken ct)
    {
        var dict = args.ToDictionary(kv => kv.Key, kv => (object?)JsonSerializer.Deserialize<JsonElement>(kv.Value!.ToJsonString()));
        var result = await client.CallToolAsync(tool, dict, cancellationToken: ct);
        var text = result.Content.OfType<TextContentBlock>().FirstOrDefault()?.Text ?? "{}";
        return JsonNode.Parse(text) as JsonObject ?? new JsonObject { ["ok"] = false, ["message"] = text };
    }

    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hWnd, out int pid);
}
