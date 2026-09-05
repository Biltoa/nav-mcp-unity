using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Umcp.Daemon;
using Umcp.Daemon.Agent;
using Umcp.Daemon.Generated;
using Umcp.Daemon.Mcp;
using Umcp.Daemon.Security;
using Umcp.Daemon.Tray;

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
      scene · gameobject · transform · component | assets · prefabs | material · shaders
      diagnostics (console, compile errors, editor health) | script (code mode)

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

if (args.Contains("--help") || args.Contains("-h"))
{
    Console.WriteLine("""
        umcpd — Unity MCP Tool daemon

          --port <n>                HTTP/MCP port          (default 8730, loopback only)
          --agent-port <n>          Unity agent channel    (default 8731, loopback only)
          --stdio                   also serve MCP on this process's stdio
          --tray                    show the Windows tray UI
          --token <s>               use this bearer token instead of minting one
          --profile <p>             readonly | standard | full   (default standard)
          --max-response-bytes <n>  response cap           (default 32768)
          --auto-restart            reopen an Editor whose process dies (bounded: 2 per 10 min)
          --package-path <dir>      com.umcp.agent source, when not next to this binary
          --open-timeout <sec>      how long to wait for a launched Editor's handshake (default 300)

        The token is written to %LOCALAPPDATA%/UnityMCP/token.
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
    stdio.Services.AddMcpServer(o => { o.ServerInfo = new() { Name = "unity-mcp-tool", Version = "0.2.0" }; o.ServerInstructions = Instructions; })
        .WithStdioServerTransport()
        .WithTools<UnityMcpTools>();
    await stdio.Build().RunAsync();
    return 0;
}


var builder = WebApplication.CreateBuilder(args);
builder.Logging.ClearProviders();
builder.Logging.AddSimpleConsole(o => { o.SingleLine = true; o.TimestampFormat = "HH:mm:ss "; });

// Loopback, explicitly, always. Never listen(port) with no host — the implementation being
// replaced bound 0.0.0.0 with no auth while exposing script creation and arbitrary C#.
builder.WebHost.ConfigureKestrel(k =>
{
    k.Listen(System.Net.IPAddress.Loopback, options.HttpPort, l => l.Protocols = HttpProtocols.Http1AndHttp2);
});

AddCore(builder.Services, options);
builder.Services.AddSingleton(tokens);
builder.Services.AddMcpServer(o => { o.ServerInfo = new() { Name = "unity-mcp-tool", Version = "0.2.0" }; o.ServerInstructions = Instructions; })
    .WithHttpTransport()
    .WithTools<UnityMcpTools>();

var app = builder.Build();

// Token auth on everything except /health. Loopback binding alone is not enough: any local
// process, and any page that resolves a name to 127.0.0.1, can reach a loopback listener.
app.Use(async (ctx, next) =>
{
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
            ["version"] = "0.2.0",
            ["uptimeSec"] = (long)(DateTime.UtcNow - DaemonInfo.StartedUtc).TotalSeconds,
            ["httpPort"] = opts.HttpPort,
            ["agentPort"] = opts.AgentPort,
            ["tools"] = ToolCatalog.All.Length,
            ["profile"] = Umcp.Daemon.Security.Profiles.Name(opts.Profile)
        },
        ["editors"] = editors
    });
});

if (options.Tray && OperatingSystem.IsWindows())
    TrayHost.Start(app.Services, options, tokens);

Console.Error.WriteLine($"[umcpd] http 127.0.0.1:{options.HttpPort}/mcp · agents 127.0.0.1:{options.AgentPort} · " +
                        $"{ToolCatalog.All.Length} tools · profile {Umcp.Daemon.Security.Profiles.Name(options.Profile)} · " +
                        $"token in {Paths.TokenFile}");

await app.RunAsync();
return 0;

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
