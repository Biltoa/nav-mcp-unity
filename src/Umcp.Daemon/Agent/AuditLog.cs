using System.Text.Json.Nodes;
using System.Threading.Channels;

namespace Umcp.Daemon.Agent;

/// <summary>
/// Append-only record of every mutating operation, on disk, outside any Unity project.
/// Written from a single background writer so no caller ever blocks on IO.
/// </summary>
public sealed class AuditLog : IAsyncDisposable
{
    readonly Channel<string> _lines = Channel.CreateUnbounded<string>();
    readonly Task _writer;

    public AuditLog()
    {
        Paths.EnsureCreated();
        _writer = Task.Run(WriteLoopAsync);
    }

    public void Write(string tool, string projectId, JsonObject request, JsonObject response)
    {
        var entry = new JsonObject
        {
            ["ts"] = DateTimeOffset.Now.ToString("O"),
            ["tool"] = tool,
            ["projectId"] = projectId,
            ["ok"] = (bool?)response["ok"] ?? false,
            ["ms"] = response["meta"]?["ms"]?.DeepClone(),
            ["args"] = request["args"]?.DeepClone() ?? request["ops"]?.DeepClone(),
            ["code"] = response["code"]?.DeepClone()
        };
        _lines.Writer.TryWrite(entry.ToJsonString());
    }

    async Task WriteLoopAsync()
    {
        await foreach (var line in _lines.Reader.ReadAllAsync())
        {
            try { await File.AppendAllTextAsync(Paths.AuditLog, line + Environment.NewLine); }
            catch { /* auditing must never take the daemon down */ }
        }
    }

    public async ValueTask DisposeAsync()
    {
        _lines.Writer.TryComplete();
        try { await _writer; } catch { }
    }
}
