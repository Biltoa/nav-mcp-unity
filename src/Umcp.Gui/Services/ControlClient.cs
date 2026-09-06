using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;

namespace Umcp.Gui.Services;

/// <summary>
/// Talks to whichever daemon is on the port — the one this app started, or one already running.
///
/// The bearer token is re-read from disk on every 401 rather than cached for the life of the
/// window. A daemon mints a fresh token each start, so a cached token is stale the moment the
/// server is restarted from anywhere, and the symptom of that is a GUI that says "unauthorized"
/// about a server working perfectly.
/// </summary>
public sealed class ControlClient
{
    readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(20) };
    string? _token;

    public int Port { get; set; } = 8730;

    string Base => $"http://127.0.0.1:{Port}";

    /// <summary>Is anything of ours answering on this port? Unauthenticated by design.</summary>
    public async Task<JsonObject?> HealthAsync(CancellationToken ct = default)
    {
        try
        {
            using var response = await _http.GetAsync($"{Base}/health", ct);
            if (!response.IsSuccessStatusCode) return null;
            return JsonNode.Parse(await response.Content.ReadAsStringAsync(ct)) as JsonObject;
        }
        catch { return null; }
    }

    public Task<JsonObject?> StatusAsync(CancellationToken ct = default) => GetAsync("/api/status", ct);

    public Task<JsonObject?> LogsAsync(int tail = 200, CancellationToken ct = default) =>
        GetAsync($"/api/logs?tail={tail}", ct);

    public Task<JsonObject?> LinkAsync(string path, CancellationToken ct = default) =>
        PostAsync("/api/projects/link", new JsonObject { ["path"] = path }, ct);

    public Task<JsonObject?> UnlinkAsync(string path, CancellationToken ct = default) =>
        PostAsync("/api/projects/unlink", new JsonObject { ["path"] = path }, ct);

    public Task<JsonObject?> OpenAsync(string path, CancellationToken ct = default) =>
        PostAsync("/api/projects/open", new JsonObject { ["path"] = path, ["wait"] = false }, ct);

    public Task<JsonObject?> CloseAsync(string project, CancellationToken ct = default) =>
        PostAsync("/api/projects/close", new JsonObject { ["project"] = project, ["save"] = true }, ct);

    public Task<JsonObject?> RestartAsync(string project, CancellationToken ct = default) =>
        PostAsync("/api/projects/restart", new JsonObject { ["project"] = project, ["save"] = true }, ct);

    public Task<JsonObject?> AutoRestartAsync(string project, bool on, CancellationToken ct = default) =>
        PostAsync("/api/projects/autorestart", new JsonObject { ["project"] = project, ["on"] = on }, ct);

    public Task<JsonObject?> PauseAsync(bool on, CancellationToken ct = default) =>
        PostAsync("/api/pause", new JsonObject { ["on"] = on }, ct);

    public Task<JsonObject?> QuitAsync(CancellationToken ct = default) =>
        PostAsync("/api/quit", new JsonObject(), ct);

    public string? Token => _token ??= ReadToken();

    static string? ReadToken()
    {
        try { return File.Exists(UmcpPaths.TokenFile) ? File.ReadAllText(UmcpPaths.TokenFile).Trim() : null; }
        catch { return null; }
    }

    async Task<JsonObject?> GetAsync(string path, CancellationToken ct)
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, Base + path);
                Authorise(request);
                using var response = await _http.SendAsync(request, ct);
                if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized && attempt == 0) { _token = ReadToken(); continue; }
                return JsonNode.Parse(await response.Content.ReadAsStringAsync(ct)) as JsonObject;
            }
            catch (OperationCanceledException) { throw; }
            catch { return null; }
        }
        return null;
    }

    async Task<JsonObject?> PostAsync(string path, JsonObject body, CancellationToken ct)
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, Base + path)
                {
                    Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json")
                };
                Authorise(request);
                using var response = await _http.SendAsync(request, ct);
                if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized && attempt == 0) { _token = ReadToken(); continue; }

                var text = await response.Content.ReadAsStringAsync(ct);
                return JsonNode.Parse(text) as JsonObject
                       ?? new JsonObject { ["ok"] = false, ["message"] = text };
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception e) { return new JsonObject { ["ok"] = false, ["message"] = e.Message }; }
        }
        return null;
    }

    void Authorise(HttpRequestMessage request)
    {
        var token = Token;
        if (token is not null)
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
    }
}
