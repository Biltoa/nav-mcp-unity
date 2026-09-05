using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Umcp.Daemon.Agent;

/// <summary>
/// The uniform response envelope, enforced at the boundary rather than trusted to each handler.
///
/// <code>
/// { "ok": true, "data": { }, "warnings": [], "meta": { "ms": 12, "source": "live", "epoch": 7 } }
/// </code>
///
/// Errors are machine-actionable, because most agent retry loops are caused by typos and stale
/// names rather than by real failures:
///
/// <code>
/// { "ok": false, "code": "E_TARGET_NOT_FOUND", "param": "name", "value": "Palyer",
///   "didYouMean": ["Player", "PlayerCamera"], "hint": "Names are case-sensitive." }
/// </code>
/// </summary>
public static class Envelope
{
    public static JsonObject Error(string code, string message, string? param = null, string? value = null,
                                   string[]? didYouMean = null, string? hint = null, JsonObject? meta = null)
    {
        var o = new JsonObject
        {
            ["ok"] = false,
            ["code"] = code,
            ["message"] = message
        };
        if (param is not null) o["param"] = param;
        if (value is not null) o["value"] = value;
        if (didYouMean is { Length: > 0 }) o["didYouMean"] = new JsonArray(didYouMean.Select(s => (JsonNode)s!).ToArray());
        if (hint is not null) o["hint"] = hint;
        o["meta"] = meta ?? new JsonObject();
        return o;
    }

    public static JsonObject Ok(JsonNode? data, JsonObject? meta = null, string[]? warnings = null)
    {
        var o = new JsonObject { ["ok"] = true, ["data"] = data?.DeepClone() };
        if (warnings is { Length: > 0 }) o["warnings"] = new JsonArray(warnings.Select(s => (JsonNode)s!).ToArray());
        o["meta"] = meta ?? new JsonObject();
        return o;
    }

    /// <summary>Wrap a raw agent result frame, applying the response byte cap.</summary>
    public static JsonObject FromAgentResult(JsonNode agentResult, AgentSession session, long heldMs, int attempts, int maxBytes)
    {
        var ok = (bool?)agentResult["ok"] ?? false;
        var meta = new JsonObject
        {
            ["ms"] = (long?)agentResult["ms"] ?? 0,
            ["source"] = "live",
            ["epoch"] = session.Epoch,
            ["project"] = session.ProjectName
        };
        if (heldMs > 0) meta["heldMs"] = heldMs;
        if (attempts > 1) meta["attempts"] = attempts;
        if ((bool?)agentResult["replayed"] == true) meta["replayed"] = true;

        if (!ok)
        {
            var err = agentResult["error"];
            return Error(
                (string?)err?["code"] ?? "E_TOOL_FAILED",
                (string?)err?["message"] ?? "The tool failed.",
                (string?)err?["param"],
                (string?)err?["value"],
                (err?["didYouMean"] as JsonArray)?.Select(n => (string)n!).ToArray(),
                (string?)err?["hint"],
                meta);
        }

        var data = agentResult["data"];
        var (capped, truncated, bytes) = Cap(data, maxBytes);
        meta["truncated"] = truncated;
        if (truncated) meta["bytes"] = bytes;

        return Ok(capped, meta);
    }

    /// <summary>
    /// No response leaves the daemon larger than the cap without the caller asking for it. One
    /// scene read in the tool being replaced returned 138,205 bytes — about 34,500 tokens, or
    /// ~17% of a 200 k context window, for a single call.
    /// </summary>
    public static (JsonNode? node, bool truncated, int bytes) Cap(JsonNode? data, int maxBytes)
    {
        if (data is null) return (null, false, 0);
        var json = data.ToJsonString();
        var bytes = Encoding.UTF8.GetByteCount(json);
        if (bytes <= maxBytes) return (data.DeepClone(), false, bytes);

        // Truncate honestly: say what was dropped and how to get it.
        if (data is JsonObject obj && obj["items"] is JsonArray items)
        {
            var kept = new JsonArray();
            var running = 0;
            var n = 0;
            foreach (var item in items)
            {
                var size = Encoding.UTF8.GetByteCount(item?.ToJsonString() ?? "null") + 1;
                if (running + size > maxBytes / 2) break;
                kept.Add(item?.DeepClone());
                running += size;
                n++;
            }
            var clone = new JsonObject
            {
                ["items"] = kept,
                ["_total"] = (JsonNode?)obj["_total"]?.DeepClone() ?? items.Count,
                ["_returned"] = n,
                ["_truncated"] = true,
                ["_bytes"] = bytes,
                ["_hint"] = $"response was {bytes} B, capped at {maxBytes} B. Re-query with offset={n}, " +
                            "narrow the filter, or pass maxResponseBytes to override."
            };
            return (clone, true, bytes);
        }

        return (new JsonObject
        {
            ["_truncated"] = true,
            ["_bytes"] = bytes,
            ["_preview"] = json[..Math.Min(json.Length, Math.Max(0, maxBytes / 2))],
            ["_hint"] = $"response was {bytes} B, capped at {maxBytes} B. Narrow the request or pass " +
                        "maxResponseBytes to override."
        }, true, bytes);
    }
}

public static class Fuzzy
{
    public static string[] Closest(string needle, IEnumerable<string> haystack, int take)
    {
        var n = needle.ToLowerInvariant();
        return haystack
            .Select(s => (s, d: Distance(n, s.ToLowerInvariant())))
            .Where(x => x.d <= Math.Max(2, needle.Length / 2))
            .OrderBy(x => x.d)
            .Take(take)
            .Select(x => x.s)
            .ToArray();
    }

    static int Distance(string a, string b)
    {
        if (a == b) return 0;
        if (a.Length == 0) return b.Length;
        if (b.Length == 0) return a.Length;
        var prev = new int[b.Length + 1];
        var cur = new int[b.Length + 1];
        for (var j = 0; j <= b.Length; j++) prev[j] = j;
        for (var i = 1; i <= a.Length; i++)
        {
            cur[0] = i;
            for (var j = 1; j <= b.Length; j++)
            {
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                cur[j] = Math.Min(Math.Min(cur[j - 1] + 1, prev[j] + 1), prev[j - 1] + cost);
            }
            (prev, cur) = (cur, prev);
        }
        return prev[b.Length];
    }
}
