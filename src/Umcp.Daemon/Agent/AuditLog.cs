using System.Text.Json.Nodes;
using System.Threading.Channels;

namespace Umcp.Daemon.Agent;

/// <summary>
/// Append-only record of every mutating operation, on disk, outside any Unity project.
/// Written from a single background writer so no caller ever blocks on IO.
///
/// It rotates. A daemon that runs for months at a few hundred mutations an hour writes a file
/// nobody can open and a disk nobody expected to fill; "append forever" is a defect with a long
/// fuse. At <see cref="MaxBytes"/> the current file becomes <c>audit.1.jsonl</c>, the older
/// generations shift down, and the oldest is dropped.
/// </summary>
public sealed class AuditLog : IAsyncDisposable
{
    sealed record Item(string? Line, TaskCompletionSource? Barrier = null);

    /// <summary>Roll at 8 MB, which is roughly 40,000 operations.</summary>
    public const long MaxBytes = 8 * 1024 * 1024;
    /// <summary>Keep this many rolled generations.</summary>
    public const int Generations = 3;

    readonly Channel<Item> _lines = Channel.CreateUnbounded<Item>();
    readonly Task _writer;
    readonly string _path;

    public AuditLog() : this(Paths.AuditLog) { }

    public AuditLog(string path)
    {
        Paths.EnsureCreated();
        _path = path;
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
        _lines.Writer.TryWrite(new Item(entry.ToJsonString()));
    }

    async Task WriteLoopAsync()
    {
        await foreach (var item in _lines.Reader.ReadAllAsync())
        {
            if (item.Barrier is not null)
            {
                item.Barrier.TrySetResult();
                continue;
            }

            try
            {
                RollIfNeeded();
                await File.AppendAllTextAsync(_path, item.Line + Environment.NewLine).ConfigureAwait(false);
            }
            catch { /* auditing must never take the daemon down */ }
        }
    }

    /// <summary>Shift the generations along when the live file is full. Called on the writer thread only.</summary>
    void RollIfNeeded()
    {
        var info = new FileInfo(_path);
        if (!info.Exists || info.Length < MaxBytes) return;

        var directory = Path.GetDirectoryName(_path)!;
        var stem = Path.GetFileNameWithoutExtension(_path);
        var extension = Path.GetExtension(_path);
        string Generation(int n) => Path.Combine(directory, $"{stem}.{n}{extension}");

        try { if (File.Exists(Generation(Generations))) File.Delete(Generation(Generations)); } catch { }
        for (var n = Generations - 1; n >= 1; n--)
        {
            try { if (File.Exists(Generation(n))) File.Move(Generation(n), Generation(n + 1), overwrite: true); }
            catch { }
        }
        try { File.Move(_path, Generation(1), overwrite: true); } catch { }
    }

    /// <summary>Flush everything queued. Tests need it; nothing in the daemon's hot path does.</summary>
    public async Task DrainAsync()
    {
        var barrier = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_lines.Writer.TryWrite(new Item(null, barrier))) return;
        await barrier.Task.ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        _lines.Writer.TryComplete();
        try { await _writer; } catch { }
    }
}
