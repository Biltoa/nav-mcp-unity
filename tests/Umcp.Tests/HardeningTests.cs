using System.Text.Json.Nodes;
using Umcp.Daemon;
using Umcp.Daemon.Agent;
using Umcp.Daemon.Security;
using Xunit;

namespace Umcp.Tests;

/// <summary>
/// The properties that only matter after the tool has been running for a month: logs that do not
/// grow forever, queues that refuse rather than swell, and a profile that says no.
/// </summary>
public class AuditLogTests
{
    [Fact]
    public async Task The_audit_log_rotates_instead_of_growing_forever()
    {
        var dir = Path.Combine(Path.GetTempPath(), "umcp-audit-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "audit.jsonl");

        try
        {
            // Start it already over the limit: rotation is checked before each append, so one
            // more line is enough to prove the roll happens.
            await File.WriteAllTextAsync(path, new string('x', (int)AuditLog.MaxBytes + 1));

            await using (var log = new AuditLog(path))
            {
                log.Write("gameobject.create", "p1",
                    new JsonObject { ["args"] = new JsonObject { ["name"] = "A" } },
                    new JsonObject { ["ok"] = true, ["meta"] = new JsonObject { ["ms"] = 3 } });
                await log.DrainAsync();
            }

            Assert.True(File.Exists(Path.Combine(dir, "audit.1.jsonl")), "the full file should have been rolled");
            var live = await File.ReadAllTextAsync(path);
            Assert.Contains("gameobject.create", live);
            Assert.True(live.Length < 4096, "the live file should hold only the new entry");
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task Rotation_keeps_a_bounded_number_of_generations()
    {
        var dir = Path.Combine(Path.GetTempPath(), "umcp-audit-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "audit.jsonl");

        try
        {
            await using var log = new AuditLog(path);
            for (var round = 0; round < AuditLog.Generations + 2; round++)
            {
                await File.WriteAllTextAsync(path, new string('x', (int)AuditLog.MaxBytes + 1));
                log.Write("t", "p", new JsonObject(), new JsonObject { ["ok"] = true });
                await log.DrainAsync();
            }

            var rolled = Directory.GetFiles(dir, "audit.*.jsonl").Length;
            Assert.True(rolled <= AuditLog.Generations, $"kept {rolled} generations, limit is {AuditLog.Generations}");
        }
        finally { Directory.Delete(dir, true); }
    }
}

public class VersionTests
{
    [Fact]
    public void The_daemon_and_the_unity_package_report_the_same_version()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "UNITY_MCP_TOOL_PLAN.md"))) root = root.Parent;
        Assert.NotNull(root);

        var packageJson = JsonNode.Parse(File.ReadAllText(
            Path.Combine(root!.FullName, "unity", "com.umcp.agent", "package.json")));
        var packageVersion = (string?)packageJson?["version"];

        Assert.Equal(packageVersion, BuildInfo.Version);
    }
}

/// <summary>
/// The response boundary: what the Editor says reaches the caller, and what it must not send
/// reaches nobody.
/// </summary>
public class EnvelopeBoundaryTests
{
    static JsonObject Wrap(JsonNode agentResult, int maxBytes = 32 * 1024) =>
        Envelope.FromAgentResult(agentResult, epoch: 7, projectName: "P", heldMs: 0, attempts: 1, maxBytes: maxBytes);

    [Fact]
    public void A_warning_from_the_editor_reaches_the_caller()
    {
        // The Play-mode warning is the one that matters: a scene change made during play is
        // discarded when play stops, and a result that does not say so is a successful lie.
        var result = Wrap(new JsonObject
        {
            ["ok"] = true,
            ["data"] = new JsonObject { ["created"] = 1 },
            ["warnings"] = new JsonArray("The Editor is in Play mode: scene changes made now are discarded when Play stops."),
            ["ms"] = 4
        });

        Assert.True((bool?)result["ok"]);
        var warnings = result["warnings"] as JsonArray;
        Assert.NotNull(warnings);
        Assert.Contains("Play mode", (string?)warnings![0]);
    }

    [Fact]
    public void A_result_with_no_warnings_carries_no_warnings_field()
    {
        var result = Wrap(new JsonObject { ["ok"] = true, ["data"] = new JsonObject(), ["ms"] = 1 });
        Assert.Null(result["warnings"]);
    }

    [Fact]
    public void A_structured_tool_error_survives_the_boundary()
    {
        var result = Wrap(new JsonObject
        {
            ["ok"] = false,
            ["error"] = new JsonObject
            {
                ["code"] = "E_TARGET_NOT_FOUND",
                ["message"] = "No GameObject matched 'Palyer'.",
                ["param"] = "target",
                ["value"] = "Palyer",
                ["didYouMean"] = new JsonArray("Player"),
                ["hint"] = "Names are case-sensitive."
            }
        });

        Assert.False((bool?)result["ok"]);
        Assert.Equal("E_TARGET_NOT_FOUND", (string?)result["code"]);
        Assert.Equal("Player", (string?)(result["didYouMean"] as JsonArray)?[0]);
        Assert.Equal("target", (string?)result["param"]);
    }

    [Fact]
    public void An_oversized_payload_is_truncated_and_says_so()
    {
        var items = new JsonArray();
        for (var i = 0; i < 5000; i++) items.Add(new JsonObject { ["name"] = "Object_" + i, ["path"] = "/Level/Object_" + i });

        var result = Wrap(new JsonObject { ["ok"] = true, ["data"] = new JsonObject { ["items"] = items }, ["ms"] = 9 }, maxBytes: 4096);

        Assert.True((bool?)result["meta"]?["truncated"]);
        var kept = (result["data"]?["items"] as JsonArray)?.Count ?? 0;
        Assert.True(kept is > 0 and < 5000, $"kept {kept} of 5000");
    }
}
