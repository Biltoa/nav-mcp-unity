using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Umcp.Daemon;
using Umcp.Daemon.Agent;
using Umcp.Daemon.Generated;
using Umcp.Daemon.Mcp;
using Umcp.Daemon.Security;

// umcpd — the daemon.
//
// Long-lived, started at login or on first use, never owned by a Unity process. Killing Unity does
// not kill it; that is the entire design. The durable state — the request queue, the catalog, the
// retry logic — lives on this side of the boundary, because everything on the Unity side dies on
// every recompile.

Paths.EnsureCreated();
var options = DaemonOptions.Parse(args);

// The Tier-0 map. ~200 tokens in the server instructions so the model can route without searching.
const string Instructions = """
    Unity Editor control. Six tools; the full catalog of Editor operations is reached through them.

    Route by domain:
      scene · gameobject · transform · component | assets · prefabs | rendering (material,
      lighting, effects, ui) | physics · navmesh · animation · audio · terrain · cinematics
      build (validateTarget checks a platform before you build it) | diagnostics | script

    How to choose:
      one discrete change            -> unity_run
      several known changes          -> unity_batch   (one Editor tick, one undo group; ~30x faster
                                                       than the same ops sent one at a time)
      a loop, filter or aggregate    -> unity_script  (returns its conclusion, not its working)
      reading the hierarchy          -> unity_run "scene.query" with a selector and a field list;
                                        never dump a scene

    unity_skill("<domain>") loads guidance plus that domain's tool schemas, including caveats for
    the render pipeline this project actually uses. unity_find searches tools and skills.
    unity_projects reports editor health: "blocked" means a modal dialog is open in Unity and a
    human has to dismiss it.
    """;

var tokens = new TokenStore(options.Token ?? Environment.GetEnvironmentVariable("UMCP_TOKEN"));

if (args.Contains("--version") || args.Contains("-v"))
{
    Console.WriteLine($"umcpd {BuildInfo.Version}");
    return 0;
}

if (args.Contains("--help") || args.Contains("-h"))
{
    Console.WriteLine("""
        umcpd — Unity MCP Tool daemon

          --port <n>                HTTP/MCP port          (default 8730, loopback only)
          --agent-port <n>          Unity agent channel    (default 8731, loopback only)
          --stdio                   also serve MCP on this process's stdio
          --tray                    accepted and ignored; the tray lives in the GUI app now
          --token <s>               use this bearer token instead of minting one
          --profile <p>             readonly | standard | full   (default standard)
          --max-response-bytes <n>  response cap           (default 32768)
          --version                 print the version and exit
          --auto-restart            reopen an Editor whose process dies (bounded: 2 per 10 min)
          --package-path <dir>      com.umcp.agent source, when not next to this binary
          --open-timeout <sec>      how long to wait for a launched Editor's handshake (default 300)

        The token is written next to the logs: %LOCALAPPDATA%/UnityMCP on Windows,
        ~/Library/Application Support/UnityMCP on macOS.
        """);
    return 0;
}

if (options.Stdio)
{
    // stdout is the MCP transport in this mode: every log line must go to stderr.
    var stdio = Host.CreateApplicationBuilder(args);
    stdio.Logging.ClearProviders();
    stdio.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);
    AddCore(stdio.Services, options);
    stdio.Services.AddMcpServer(o => { o.ServerInfo = new() { Name = BuildInfo.ServerName, Version = BuildInfo.Version }; o.ServerInstructions = Instructions; })
        .WithStdioServerTransport()
        .WithTools<UnityMcpTools>();
    try
    {
        await stdio.Build().RunAsync();
    }
    catch (Exception e) when (e is System.Net.Sockets.SocketException ||
                              e.InnerException is System.Net.Sockets.SocketException)
    {
        Console.Error.WriteLine($"[umcpd] could not start: {e.Message}");
        return 3;
    }
    return 0;
}


var builder = WebApplication.CreateBuilder(args);
builder.Logging.ClearProviders();
builder.Logging.AddSimpleConsole(o => { o.SingleLine = true; o.TimestampFormat = "HH:mm:ss "; });

// Loopback, explicitly, always. Never listen(port) with no host — the implementation being
// replaced bound 0.0.0.0 with no auth while exposing script creation and arbitrary C#.
builder.WebHost.ConfigureKestrel(k =>
{
    // HTTP/1.1 only. Without TLS, HTTP/2 needs prior knowledge and Kestrel warns about it on
    // every start; there is no TLS here on purpose, because this listener never leaves loopback.
    k.Listen(System.Net.IPAddress.Loopback, options.HttpPort, l => l.Protocols = HttpProtocols.Http1);
});

AddCore(builder.Services, options);
builder.Services.AddSingleton(tokens);
builder.Services.AddMcpServer(o => { o.ServerInfo = new() { Name = BuildInfo.ServerName, Version = BuildInfo.Version }; o.ServerInstructions = Instructions; })
    .WithHttpTransport()
    .WithTools<UnityMcpTools>();

var app = builder.Build();

// Token auth on everything except /health. Loopback binding alone is not enough: any local
// process, and any page that resolves a name to 127.0.0.1, can reach a loopback listener.
app.Use(async (ctx, next) =>
{
    // A client that hangs up must be able to stop work it started. The SDK's tool token does not
    // see a transport abort in this version, so it is made reachable to the tools here.
    Umcp.Daemon.Mcp.RequestAbort.Set(ctx.RequestAborted);

    if (ctx.Request.Path.StartsWithSegments("/health"))
    {
        await next();
        return;
    }

    var header = ctx.Request.Headers.Authorization.ToString();
    var presented = header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
        ? header[7..].Trim()
        : ctx.Request.Query["token"].ToString();

    if (!CryptographicEquals(presented, tokens.Token))
    {
        ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
        await ctx.Response.WriteAsync("unauthorized");
        return;
    }
    await next();
});

app.MapMcp("/mcp");

// Health is the last completed round trip, never "the socket is open".
app.MapGet("/health", async (EditorRegistry registry, DaemonOptions opts) =>
{
    var editors = new JsonArray();
    foreach (var s in registry.Sessions)
    {
        var o = s.StatusJson();
        var probe = await s.ProbeControlAsync();
        var tickAge = (long?)probe?["msSinceTick"];
        o["msSinceTick"] = tickAge;
        o["health"] = tickAge is null
            ? (s.MsSinceLastResponse < 5000 ? "ok" : "unknown")
            : tickAge >= opts.BlockedTickAge.TotalMilliseconds ? "blocked"
            : tickAge >= 5000 ? "degraded" : "ok";
        editors.Add(o);
    }

    return Results.Json(new JsonObject
    {
        ["ok"] = true,
        ["daemon"] = new JsonObject
        {
            ["pid"] = Environment.ProcessId,
            ["version"] = BuildInfo.Version,
            ["uptimeSec"] = (long)(DateTime.UtcNow - DaemonInfo.StartedUtc).TotalSeconds,
            // Memory is reported because this process is expected to run for weeks. A number
            // nobody can see is a leak nobody finds.
            ["memory"] = new JsonObject
            {
                ["workingSetMB"] = Environment.WorkingSet / (1024 * 1024),
                ["privateMB"] = System.Diagnostics.Process.GetCurrentProcess().PrivateMemorySize64 / (1024 * 1024),
                ["managedHeapMB"] = GC.GetTotalMemory(false) / (1024 * 1024),
                ["gen2Collections"] = GC.CollectionCount(2)
            },
            ["httpPort"] = opts.HttpPort,
            ["agentPort"] = opts.AgentPort,
            ["tools"] = ToolCatalog.All.Length,
            ["profile"] = Umcp.Daemon.Security.Profiles.Name(opts.Profile)
        },
        ["editors"] = editors
    });
});

try
{
    // Start first, announce second: a banner printed before the listeners bind claims a service
    // that may be about to fail, and the first thing a user does with that line is trust it.
    await app.StartAsync();

    Console.Error.WriteLine($"[umcpd] {BuildInfo.Version} · http 127.0.0.1:{options.HttpPort}/mcp · agents 127.0.0.1:{options.AgentPort} · " +
                            $"{ToolCatalog.All.Length} tools · profile {Umcp.Daemon.Security.Profiles.Name(options.Profile)} · " +
                            $"token in {Paths.TokenFile}");

    await app.WaitForShutdownAsync();
}
catch (Exception e) when (e is System.Net.Sockets.SocketException ||
                          e.InnerException is System.Net.Sockets.SocketException)
{
    // Either listener can hit this, and the overwhelmingly likely cause is a second daemon. Say
    // so, and say which one, rather than printing a socket error and a stack trace at somebody
    // who just wanted to start the tool.
    var busy = Taken(options.HttpPort) ? options.HttpPort : Taken(options.AgentPort) ? options.AgentPort : 0;
    var existing = await DescribeExistingAsync(options.HttpPort);

    Console.Error.WriteLine(busy == 0
        ? $"[umcpd] could not bind a listener: {e.Message}"
        : $"[umcpd] port {busy} is already in use.");
    Console.Error.WriteLine(existing is not null
        ? $"[umcpd] {existing} is already serving 127.0.0.1:{options.HttpPort} — use that one, or start this with --port <n> --agent-port <n>."
        : "[umcpd] stop whatever holds the port, or start this with --port <n> --agent-port <n>.");
    return 3;
}

return 0;

/// <summary>Is anything listening on this loopback port?</summary>
static bool Taken(int port)
{
    try
    {
        using var probe = new System.Net.Sockets.TcpClient();
        return probe.ConnectAsync(System.Net.IPAddress.Loopback, port).Wait(TimeSpan.FromMilliseconds(400));
    }
    catch { return false; }
}

/// <summary>Ask whatever holds the port whether it is one of ours.</summary>
static async Task<string?> DescribeExistingAsync(int port)
{
    try
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
        var json = await http.GetStringAsync($"http://127.0.0.1:{port}/health");
        var node = JsonNode.Parse(json);
        var pid = (int?)node?["daemon"]?["pid"];
        var version = (string?)node?["daemon"]?["version"];
        var editors = (node?["editors"] as JsonArray)?.Count ?? 0;
        return pid is null ? null : $"umcpd {version} (pid {pid}, {editors} editor(s) connected)";
    }
    catch
    {
        return null;
    }
}

static void AddCore(IServiceCollection services, DaemonOptions options)
{
    services.AddSingleton(options);
    services.AddSingleton<EditorRegistry>();
    services.AddSingleton<AuditLog>();
    services.AddSingleton<Dispatcher>();
    services.AddSingleton<UnityMcpTools>();
    services.AddHostedService<AgentServer>();
    services.AddSingleton<DaemonState>();
    services.AddSingleton<Umcp.Daemon.Script.ScriptCompiler>();
    services.AddHostedService<Umcp.Daemon.Script.ScriptCacheJanitor>();
    services.AddSingleton<SkillTree>();
    services.AddSingleton<Umcp.Daemon.Mirror.MirrorService>();
    services.AddHostedService<Umcp.Daemon.Mirror.MirrorReconciler>();
    services.AddSingleton<Umcp.Daemon.Fleet.FleetService>();
    services.AddHostedService(sp => sp.GetRequiredService<Umcp.Daemon.Fleet.FleetService>());
}

static bool CryptographicEquals(string a, string b)
{
    if (a.Length != b.Length) return false;
    var diff = 0;
    for (var i = 0; i < a.Length; i++) diff |= a[i] ^ b[i];
    return diff == 0;
}
