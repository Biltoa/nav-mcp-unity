using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;

namespace Umcp.Daemon.Agent;

public enum SendOutcome { Completed, Disconnected, Blocked, TimedOut }

public sealed record OpResult(SendOutcome Outcome, JsonNode? Result, long ElapsedMs, string? Detail = null);

/// <summary>
/// One connected Unity editor. Owns the socket, the in-flight request map, and the liveness
/// numbers.
///
/// Health here means <em>last completed round trip</em>, never "the socket is open". The tool
/// being replaced reported <c>"unityConnected": true</c> throughout a total deadlock, because a
/// modal dialog was blocking the Editor's message pump while TCP stayed ESTABLISHED.
/// </summary>
public sealed class AgentSession : IAsyncDisposable
{
    const int MaxFrame = 64 * 1024 * 1024;

    readonly TcpClient _client;
    readonly NetworkStream _stream;
    readonly Channel<string> _outbox = Channel.CreateUnbounded<string>();
    readonly ConcurrentDictionary<string, TaskCompletionSource<JsonNode>> _pending = new();
    readonly CancellationTokenSource _cts = new();
    readonly Stopwatch _clock = Stopwatch.StartNew();
    readonly SemaphoreSlim _writeLock = new(1, 1);

    long _lastResponseMs;
    long _lastRoundTripMs = -1;
    int _opsSent, _opsCompleted;

    public string ProjectId { get; private set; } = "";
    public string ProjectPath { get; private set; } = "";
    public string ProjectName { get; private set; } = "";
    public string UnityVersion { get; private set; } = "";
    public int UnityPid { get; private set; }
    public int Epoch { get; private set; }
    public int ControlPort { get; private set; }
    public int ToolCount { get; private set; }
    public HashSet<string> AppliedKeys { get; } = new();

    public DateTimeOffset ConnectedAt { get; } = DateTimeOffset.Now;
    public bool Alive { get; private set; } = true;
    public bool Handshook { get; private set; }
    public bool Compiling { get; private set; }
    public bool Reloading { get; private set; }

    public long MsSinceLastResponse => _clock.ElapsedMilliseconds - Interlocked.Read(ref _lastResponseMs);
    public long LastRoundTripMs => Interlocked.Read(ref _lastRoundTripMs);
    public int OpsSent => _opsSent;
    public int OpsCompleted => _opsCompleted;
    public int InFlight => _pending.Count;

    public event Action<AgentSession>? Handshake;
    public event Action<AgentSession>? Closed;
    public event Action<AgentSession, string, JsonNode>? Event;

    public AgentSession(TcpClient client)
    {
        _client = client;
        _client.NoDelay = true;
        _stream = client.GetStream();
        Interlocked.Exchange(ref _lastResponseMs, _clock.ElapsedMilliseconds);
    }

    public void Start()
    {
        _ = Task.Run(ReadLoopAsync);
        _ = Task.Run(WriteLoopAsync);
    }

    // ------------------------------------------------------------------ dispatch

    public async Task<OpResult> SendAsync(JsonObject message, TimeSpan timeout, CancellationToken ct)
    {
        var id = Guid.NewGuid().ToString("N");
        message["id"] = id;

        var tcs = new TaskCompletionSource<JsonNode>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = tcs;
        Interlocked.Increment(ref _opsSent);

        var sw = Stopwatch.StartNew();
        try
        {
            if (!_outbox.Writer.TryWrite(message.ToJsonString()))
                return new OpResult(SendOutcome.Disconnected, null, sw.ElapsedMilliseconds);

            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _cts.Token);
            var completed = await Task.WhenAny(tcs.Task, Task.Delay(timeout, linked.Token)).ConfigureAwait(false);

            if (completed == tcs.Task)
            {
                var node = await tcs.Task.ConfigureAwait(false);
                Interlocked.Increment(ref _opsCompleted);
                Interlocked.Exchange(ref _lastRoundTripMs, sw.ElapsedMilliseconds);
                return new OpResult(SendOutcome.Completed, node, sw.ElapsedMilliseconds);
            }

            if (!Alive) return new OpResult(SendOutcome.Disconnected, null, sw.ElapsedMilliseconds);
            return new OpResult(SendOutcome.TimedOut, null, sw.ElapsedMilliseconds);
        }
        catch (OperationCanceledException)
        {
            return new OpResult(Alive ? SendOutcome.TimedOut : SendOutcome.Disconnected, null, sw.ElapsedMilliseconds);
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    /// <summary>
    /// Tell the Editor to drop an operation it has not started yet.
    ///
    /// Fire-and-forget by design: there is no reply, because the only honest answer to "did you
    /// cancel it" arrives as the operation's own result — either the value, or E_CANCELLED.
    /// </summary>
    public void SendCancel(string key)
    {
        if (string.IsNullOrEmpty(key) || !Alive) return;
        _outbox.Writer.TryWrite(new JsonObject { ["t"] = "cancel", ["key"] = key }.ToJsonString());
    }

    // ------------------------------------------------------------------ out-of-band control

    /// <summary>
    /// Ask the Editor's background control listener how long ago the main thread last ticked.
    ///
    /// The recovery path must not travel the path that breaks: this connection is serviced by a
    /// background thread inside Unity and needs no Editor tick, so it still answers when a modal
    /// dialog has wedged the message pump.
    /// </summary>
    public async Task<JsonNode?> ProbeControlAsync(string command = "status", int timeoutMs = 2000, CancellationToken ct = default)
    {
        if (ControlPort <= 0) return null;
        try
        {
            using var c = new TcpClient();
            var connect = c.ConnectAsync(IPAddress.Loopback, ControlPort, ct).AsTask();
            if (await Task.WhenAny(connect, Task.Delay(timeoutMs, ct)).ConfigureAwait(false) != connect) return null;
            await connect.ConfigureAwait(false);

            using var s = c.GetStream();
            s.ReadTimeout = timeoutMs;
            s.WriteTimeout = timeoutMs;
            var payload = Encoding.UTF8.GetBytes("{\"cmd\":\"" + command + "\"}\n");
            await s.WriteAsync(payload, ct).ConfigureAwait(false);
            await s.FlushAsync(ct).ConfigureAwait(false);

            var buf = new byte[8192];
            var read = await s.ReadAsync(buf, ct).ConfigureAwait(false);
            if (read <= 0) return null;
            return JsonNode.Parse(Encoding.UTF8.GetString(buf, 0, read).Trim());
        }
        catch
        {
            return null;
        }
    }

    // ------------------------------------------------------------------ io

    async Task ReadLoopAsync()
    {
        var header = new byte[4];
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                if (!await ReadExactlyAsync(header, 4).ConfigureAwait(false)) break;
                var len = BitConverter.ToInt32(header, 0);
                if (len <= 0 || len > MaxFrame) break;
                var body = new byte[len];
                if (!await ReadExactlyAsync(body, len).ConfigureAwait(false)) break;

                Interlocked.Exchange(ref _lastResponseMs, _clock.ElapsedMilliseconds);
                Handle(Encoding.UTF8.GetString(body));
            }
        }
        catch { /* torn down */ }
        finally
        {
            Alive = false;
            foreach (var kv in _pending)
                kv.Value.TrySetCanceled();
            _pending.Clear();
            Closed?.Invoke(this);
        }
    }

    void Handle(string json)
    {
        JsonNode? node;
        try { node = JsonNode.Parse(json); }
        catch { return; }
        if (node is null) return;

        var t = (string?)node["t"];
        switch (t)
        {
            case "hello":
                ProjectId = (string?)node["projectId"] ?? "";
                ProjectPath = (string?)node["projectPath"] ?? "";
                ProjectName = (string?)node["projectName"] ?? "";
                UnityVersion = (string?)node["unityVersion"] ?? "";
                UnityPid = (int?)node["pid"] ?? 0;
                Epoch = (int?)node["epoch"] ?? 0;
                ControlPort = (int?)node["controlPort"] ?? 0;
                ToolCount = (int?)node["toolCount"] ?? 0;
                AppliedKeys.Clear();
                if (node["appliedKeys"] is JsonArray keys)
                    foreach (var k in keys) { var s = (string?)k; if (s is not null) AppliedKeys.Add(s); }
                Reloading = false;
                Handshook = true;
                Handshake?.Invoke(this);
                break;

            case "result":
                {
                    var id = (string?)node["id"];
                    if (id is not null && _pending.TryRemove(id, out var tcs)) tcs.TrySetResult(node);
                    break;
                }

            case "mirror":
                // Scene deltas travel as their own frame type rather than as a generic event,
                // because they are high-volume and must not be confused with lifecycle.
                Event?.Invoke(this, "mirror", node);
                break;

            case "event":
                {
                    var kind = (string?)node["kind"] ?? "";
                    if (kind == "compile.begin") Compiling = true;
                    else if (kind == "compile.end") Compiling = false;
                    else if (kind == "reload.begin") Reloading = true;
                    Event?.Invoke(this, kind, node);
                    break;
                }
        }
    }

    async Task WriteLoopAsync()
    {
        try
        {
            await foreach (var json in _outbox.Reader.ReadAllAsync(_cts.Token).ConfigureAwait(false))
            {
                var payload = Encoding.UTF8.GetBytes(json);
                var header = BitConverter.GetBytes(payload.Length);
                await _writeLock.WaitAsync(_cts.Token).ConfigureAwait(false);
                try
                {
                    await _stream.WriteAsync(header).ConfigureAwait(false);
                    await _stream.WriteAsync(payload).ConfigureAwait(false);
                    await _stream.FlushAsync().ConfigureAwait(false);
                }
                finally { _writeLock.Release(); }
            }
        }
        catch { /* torn down */ }
    }

    async Task<bool> ReadExactlyAsync(byte[] buf, int count)
    {
        var read = 0;
        while (read < count)
        {
            var n = await _stream.ReadAsync(buf.AsMemory(read, count - read), _cts.Token).ConfigureAwait(false);
            if (n <= 0) return false;
            read += n;
        }
        return true;
    }

    public JsonObject StatusJson() => new()
    {
        ["projectId"] = ProjectId,
        ["project"] = ProjectName,
        ["path"] = ProjectPath,
        ["unityVersion"] = UnityVersion,
        ["pid"] = UnityPid,
        ["epoch"] = Epoch,
        ["tools"] = ToolCount,
        ["connectedAt"] = ConnectedAt.ToString("O"),
        ["compiling"] = Compiling,
        ["reloading"] = Reloading,
        ["inFlight"] = InFlight,
        ["opsSent"] = OpsSent,
        ["opsCompleted"] = OpsCompleted,
        ["lastRoundTripMs"] = LastRoundTripMs,
        ["msSinceLastResponse"] = MsSinceLastResponse
    };

    public async ValueTask DisposeAsync()
    {
        Alive = false;
        _cts.Cancel();
        _outbox.Writer.TryComplete();
        try { await _stream.DisposeAsync().ConfigureAwait(false); } catch { }
        try { _client.Close(); } catch { }
        _cts.Dispose();
    }
}
