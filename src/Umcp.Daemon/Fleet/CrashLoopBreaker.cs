namespace Umcp.Daemon.Fleet;

/// <summary>
/// "Restart it" is only a good idea a bounded number of times.
///
/// An Editor that dies on a corrupt Library, a broken script, or a missing licence dies again the
/// moment it is restarted, and an unbounded supervisor turns one failure into a machine-wide
/// launch loop that also spins the disk and rewrites the Library each time. The rule from §6.4:
/// at most <see cref="MaxRestarts"/> restarts within <see cref="Window"/>, then stop and say so.
///
/// The breaker is deliberately per-project rather than global: one bad project must not stop the
/// daemon from recovering a different one.
/// </summary>
public sealed class CrashLoopBreaker
{
    readonly Dictionary<string, List<DateTime>> _restarts = new(StringComparer.OrdinalIgnoreCase);
    readonly object _gate = new();

    public int MaxRestarts { get; init; } = 2;
    public TimeSpan Window { get; init; } = TimeSpan.FromMinutes(10);

    public Func<DateTime> Now { get; init; } = () => DateTime.UtcNow;

    /// <summary>Record an attempt and say whether it is allowed. Call once per intended restart.</summary>
    public bool TryRestart(string key, out string? refusal)
    {
        lock (_gate)
        {
            var now = Now();
            var list = _restarts.TryGetValue(key, out var l) ? l : _restarts[key] = new List<DateTime>();
            list.RemoveAll(t => now - t > Window);

            if (list.Count >= MaxRestarts)
            {
                var oldest = list.Min();
                var clearsIn = Window - (now - oldest);
                refusal = $"Crash-loop breaker: already restarted {list.Count} time(s) in the last " +
                          $"{Window.TotalMinutes:0} minutes. Not restarting again for " +
                          $"{clearsIn.TotalMinutes:0.0} more minutes — the Editor is failing at startup, " +
                          $"not crashing at random.";
                return false;
            }

            list.Add(now);
            refusal = null;
            return true;
        }
    }

    /// <summary>Attempts still available inside the window.</summary>
    public int Remaining(string key)
    {
        lock (_gate)
        {
            if (!_restarts.TryGetValue(key, out var list)) return MaxRestarts;
            var now = Now();
            list.RemoveAll(t => now - t > Window);
            return Math.Max(0, MaxRestarts - list.Count);
        }
    }

    /// <summary>A clean, deliberate start clears the history: the project is evidently healthy again.</summary>
    public void Reset(string key)
    {
        lock (_gate) _restarts.Remove(key);
    }
}
