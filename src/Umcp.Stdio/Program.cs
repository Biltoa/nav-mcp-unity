using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

// umcp-stdio — the shim.
//
// Claude Code (or any stdio MCP client) spawns this; it proxies stdio to the daemon's loopback
// HTTP endpoint, starting the daemon if it isn't up. That is what makes the daemon's independence
// invisible to the client: the client still gets a normal stdio MCP server, but killing that
// process kills nothing. Unity crashing kills nothing either.
//
// Everything here is transport plumbing. It holds no state that matters, so it is free to die.

var port = ArgInt("--port", 8730);
var baseUrl = $"http://127.0.0.1:{port}";
var token = Arg("--token")
            ?? Environment.GetEnvironmentVariable("UMCP_TOKEN")
            ?? TryReadTokenFile();

var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
if (!string.IsNullOrEmpty(token))
    http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

if (!await WaitForDaemonAsync(TimeSpan.FromSeconds(2)))
{
    StartDaemon(port);
    if (!await WaitForDaemonAsync(TimeSpan.FromSeconds(30)))
    {
        Console.Error.WriteLine($"[umcp-stdio] daemon did not come up on {baseUrl}");
        return 1;
    }
    // A freshly started daemon mints a new token.
    token = TryReadTokenFile();
    if (!string.IsNullOrEmpty(token))
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
}

string? sessionId = null;
var stdout = Console.Out;
var writeLock = new SemaphoreSlim(1, 1);

var stdin = Console.OpenStandardInput();
using var reader = new StreamReader(stdin, Encoding.UTF8);

while (true)
{
    var line = await reader.ReadLineAsync();
    if (line is null) break;
    if (string.IsNullOrWhiteSpace(line)) continue;

    try { await ForwardAsync(line); }
    catch (Exception e)
    {
        Console.Error.WriteLine($"[umcp-stdio] {e.Message}");
        await EmitTransportErrorAsync(line, e.Message);
    }
}

return 0;

async Task ForwardAsync(string jsonRpc)
{
    using var request = new HttpRequestMessage(HttpMethod.Post, baseUrl + "/mcp")
    {
        Content = new StringContent(jsonRpc, Encoding.UTF8, "application/json")
    };
    if (sessionId is not null) request.Headers.TryAddWithoutValidation("Mcp-Session-Id", sessionId);

    using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);

    if (response.Headers.TryGetValues("Mcp-Session-Id", out var ids))
        sessionId = ids.FirstOrDefault() ?? sessionId;

    if (!response.IsSuccessStatusCode)
    {
        var body = await response.Content.ReadAsStringAsync();
        await EmitTransportErrorAsync(jsonRpc, $"daemon returned {(int)response.StatusCode}: {body.Trim()}");
        return;
    }

    var mediaType = response.Content.Headers.ContentType?.MediaType;
    if (mediaType == "text/event-stream")
    {
        await using var stream = await response.Content.ReadAsStreamAsync();
        using var sse = new StreamReader(stream, Encoding.UTF8);
        var data = new StringBuilder();
        while (true)
        {
            var evLine = await sse.ReadLineAsync();
            if (evLine is null) break;
            if (evLine.StartsWith("data:", StringComparison.Ordinal))
            {
                data.Append(evLine[5..].TrimStart());
            }
            else if (evLine.Length == 0 && data.Length > 0)
            {
                await WriteAsync(data.ToString());
                data.Clear();
            }
        }
        if (data.Length > 0) await WriteAsync(data.ToString());
        return;
    }

    var payload = (await response.Content.ReadAsStringAsync()).Trim();
    if (payload.Length > 0) await WriteAsync(payload);
}

async Task WriteAsync(string json)
{
    await writeLock.WaitAsync();
    try
    {
        await stdout.WriteLineAsync(json);
        await stdout.FlushAsync();
    }
    finally { writeLock.Release(); }
}

async Task EmitTransportErrorAsync(string originalRequest, string message)
{
    // Never leave a client-side request hanging: if the transport failed, answer with a JSON-RPC
    // error carrying the original id, or say nothing at all for a notification.
    JsonNode? id = null;
    try { id = JsonNode.Parse(originalRequest)?["id"]?.DeepClone(); } catch { }
    if (id is null) return;

    var error = new JsonObject
    {
        ["jsonrpc"] = "2.0",
        ["id"] = id,
        ["error"] = new JsonObject { ["code"] = -32000, ["message"] = "umcp-stdio: " + message }
    };
    await WriteAsync(error.ToJsonString());
}

async Task<bool> WaitForDaemonAsync(TimeSpan timeout)
{
    using var probe = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
    var deadline = DateTime.UtcNow + timeout;
    while (DateTime.UtcNow < deadline)
    {
        try
        {
            var r = await probe.GetAsync(baseUrl + "/health");
            if (r.IsSuccessStatusCode) return true;
        }
        catch { }
        await Task.Delay(250);
    }
    return false;
}

void StartDaemon(int httpPort)
{
    var exe = FindDaemon();
    if (exe is null)
    {
        Console.Error.WriteLine("[umcp-stdio] umcpd not found next to this shim or on PATH.");
        return;
    }
    Console.Error.WriteLine($"[umcp-stdio] starting daemon: {exe}");
    var psi = new ProcessStartInfo(exe)
    {
        UseShellExecute = false,
        CreateNoWindow = true,
        RedirectStandardOutput = true,
        RedirectStandardError = true
    };
    psi.ArgumentList.Add("--port");
    psi.ArgumentList.Add(httpPort.ToString());
    foreach (var extra in new[] { "--agent-port", "--profile", "--package-path" })
    {
        var v = Arg(extra);
        if (v is not null) { psi.ArgumentList.Add(extra); psi.ArgumentList.Add(v); }
    }
    if (Environment.GetCommandLineArgs().Contains("--tray")) psi.ArgumentList.Add("--tray");
    if (Environment.GetCommandLineArgs().Contains("--auto-restart")) psi.ArgumentList.Add("--auto-restart");

    // Deliberately not tracked: the daemon outliving this shim is the entire point of the design.
    // No parent-PID watchdog anywhere in this system.
    var child = Process.Start(psi);
    if (child is null) return;

    // Both pipes must be drained. A redirected stream nobody reads fills its buffer — 4 KB on
    // Windows — and then every write from the daemon blocks: the daemon stops serving, having
    // logged its way into a deadlock, and the only visible symptom is that nothing answers.
    // Draining into a log file also gives a place to look when startup fails.
    _ = DrainAsync(child.StandardOutput, DaemonLogPath());
    _ = DrainAsync(child.StandardError, DaemonLogPath());
}

static string DaemonLogPath()
{
    Umcp.UmcpPaths.EnsureCreated();
    return Umcp.UmcpPaths.DaemonLog;
}

static async Task DrainAsync(StreamReader stream, string logPath)
{
    const long maxBytes = 8 * 1024 * 1024;
    try
    {
        while (true)
        {
            var line = await stream.ReadLineAsync();
            if (line is null) return;

            try
            {
                // Truncate rather than grow without bound: this is a convenience log, and a
                // daemon that fills a disk with its own chatter is a worse failure than a lost
                // history of it.
                var info = new FileInfo(logPath);
                if (info.Exists && info.Length > maxBytes) File.WriteAllText(logPath, "");
                await File.AppendAllTextAsync(logPath, line + Environment.NewLine);
            }
            catch { /* logging must never take the shim down */ }
        }
    }
    catch { /* the daemon exited; nothing to drain */ }
}

static string? FindDaemon()
{
    var names = new[] { Umcp.UmcpPaths.ExeName("umcpd") };
    var dirs = new List<string> { AppContext.BaseDirectory };

    // Development layout: src/Umcp.Stdio/bin/... alongside src/Umcp.Daemon/bin/...
    var here = new DirectoryInfo(AppContext.BaseDirectory);
    for (var d = here; d is not null; d = d.Parent)
        foreach (var configuration in new[] { "Debug", "Release" })
        {
            var candidate = Path.Combine(d.FullName, "src", "Umcp.Daemon", "bin", configuration, "net8.0");
            if (Directory.Exists(candidate)) dirs.Add(candidate);
        }

    // A macOS drop puts the binaries inside the app bundle; the shim may be invoked from either.
    dirs.Add(Path.Combine(AppContext.BaseDirectory, "..", "Resources"));

    foreach (var dir in dirs)
        foreach (var name in names)
        {
            var p = Path.Combine(dir, name);
            if (File.Exists(p)) return p;
        }

    foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        foreach (var name in names)
        {
            try { var p = Path.Combine(dir, name); if (File.Exists(p)) return p; } catch { }
        }

    return null;
}

static string? TryReadTokenFile()
{
    var path = Umcp.UmcpPaths.TokenFile;
    try { return File.Exists(path) ? File.ReadAllText(path).Trim() : null; } catch { return null; }
}

static string? Arg(string name)
{
    var a = Environment.GetCommandLineArgs();
    for (var i = 0; i < a.Length - 1; i++) if (a[i] == name) return a[i + 1];
    return null;
}

static int ArgInt(string name, int dflt) => int.TryParse(Arg(name), out var v) ? v : dflt;
