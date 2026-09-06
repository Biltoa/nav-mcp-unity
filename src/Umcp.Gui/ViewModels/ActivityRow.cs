using System.Text.Json.Nodes;

namespace Umcp.Gui.ViewModels;

/// <summary>
/// One thing the AI did, as a person would read it.
///
/// The audit log is written for forensics — tool id, project GUID, milliseconds. This turns a
/// line of it into a sentence, because "gameobject.create in 3643906f… ok 41ms" answers a
/// question nobody asked.
/// </summary>
public sealed record ActivityRow(string Tool, string Project, string When, string Duration, bool Ok, string? Code)
{
    public string Dot => Ok ? "#3FB950" : "#F85149";
    public string Detail => Ok
        ? string.IsNullOrEmpty(Project) ? When : $"{Project} · {When}"
        : $"{(string.IsNullOrEmpty(Project) ? "" : Project + " · ")}{When} · failed{(Code is null ? "" : $" ({Code})")}";

    public static ActivityRow From(JsonObject row)
    {
        var tool = (string?)row["tool"] ?? "unknown";
        var ok = (bool?)row["ok"] ?? false;
        var project = (string?)row["project"] ?? "";
        var when = DateTimeOffset.TryParse((string?)row["ts"], out var t) ? Ago(t) : "";
        double? ms = row["ms"] is not null && double.TryParse(row["ms"]!.ToString(), out var v) ? v : null;

        return new ActivityRow(
            Tool: tool,
            Project: project,
            When: when,
            Duration: ms is null ? "" : ms >= 1000 ? $"{ms.Value / 1000:0.0} s" : $"{ms:0} ms",
            Ok: ok,
            Code: (string?)row["code"]);
    }

    /// <summary>Relative time, because "4 minutes ago" is the only form anyone reads at a glance.</summary>
    static string Ago(DateTimeOffset then)
    {
        var span = DateTimeOffset.Now - then;
        if (span < TimeSpan.FromSeconds(45)) return "just now";
        if (span < TimeSpan.FromMinutes(2)) return "a minute ago";
        if (span < TimeSpan.FromHours(1)) return $"{(int)span.TotalMinutes} minutes ago";
        if (span < TimeSpan.FromHours(2)) return "an hour ago";
        if (span < TimeSpan.FromDays(1)) return $"{(int)span.TotalHours} hours ago";
        if (span < TimeSpan.FromDays(2)) return "yesterday";
        return then.ToString("d MMM HH:mm");
    }
}

/// <summary>
/// Something that wants a human. Either a step that has not been done yet, or a state that has
/// gone wrong — the same card serves both, because to a new user they are the same question:
/// what do I do next.
/// </summary>
public sealed record AttentionRow(string Title, string Detail, string Dot, bool Done);
