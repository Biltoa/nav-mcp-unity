using System.Text.Json.Nodes;
using Umcp.Daemon.Generated;
using Umcp.Daemon.Security;
using Xunit;

namespace Umcp.Tests;

/// <summary>
/// Per-tool permission. The point of these is that the answer survives a restart and that the
/// stored shape cannot rot: everything else about the feature is one dictionary lookup.
/// </summary>
public class ToolPolicyTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), "umcp-policy-" + Guid.NewGuid().ToString("N"));

    string File_ => Path.Combine(_dir, "settings.json");

    public void Dispose()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); } catch { }
    }

    static string AnyTool => ToolCatalog.All[0].Id;
    static string AnotherTool => ToolCatalog.All[1].Id;

    [Fact]
    public void Everything_is_allowed_until_somebody_says_otherwise()
    {
        var policy = new ToolPolicy(File_);
        Assert.True(policy.IsAllowed(AnyTool));
        Assert.Null(policy.Denies(AnyTool));
    }

    [Fact]
    public void A_disabled_tool_is_refused_with_a_sentence_a_person_wrote()
    {
        var policy = new ToolPolicy(File_);
        policy.Set(new[] { AnyTool });

        var message = policy.Denies(AnyTool);
        Assert.NotNull(message);
        Assert.Contains(AnyTool, message);
        Assert.True(policy.IsAllowed(AnotherTool));
    }

    [Fact]
    public void The_decision_survives_a_restart()
    {
        new ToolPolicy(File_).Set(new[] { AnyTool });
        Assert.False(new ToolPolicy(File_).IsAllowed(AnyTool));
    }

    [Fact]
    public void Ids_that_are_not_in_the_catalog_are_dropped()
    {
        // A stale name left in the file would quietly become a permission for whatever future
        // tool happens to claim that id.
        var policy = new ToolPolicy(File_);
        policy.Set(new[] { AnyTool, "made.up.tool" });

        Assert.Equal(new[] { AnyTool }, policy.Disabled);
    }

    [Fact]
    public void Other_settings_in_the_file_are_left_alone()
    {
        // The daemon and the app share settings.json. Either one erasing the other's keys loses
        // the user's project list or their ports.
        Directory.CreateDirectory(_dir);
        File.WriteAllText(File_, """{ "projects": [ { "path": "D:/Games/Alpha" } ], "gui": { "httpPort": 8999 } }""");

        new ToolPolicy(File_).Set(new[] { AnyTool });

        var root = JsonNode.Parse(File.ReadAllText(File_))!;
        Assert.Single((root["projects"] as JsonArray)!);
        Assert.Equal(8999, (int?)root["gui"]!["httpPort"]);
        Assert.Single((root["disabledTools"] as JsonArray)!);
    }

    [Fact]
    public void An_unreadable_file_allows_everything_rather_than_nothing()
    {
        // Failing open is right here: the profile and the bearer token are the locks that matter,
        // and a corrupt preferences file that silently disabled all 92 tools would look exactly
        // like a broken server.
        Directory.CreateDirectory(_dir);
        File.WriteAllText(File_, "{ not json");

        Assert.True(new ToolPolicy(File_).IsAllowed(AnyTool));
    }

    [Fact]
    public void Clearing_the_set_allows_everything_again()
    {
        var policy = new ToolPolicy(File_);
        policy.Set(new[] { AnyTool });
        policy.Set(Array.Empty<string>());

        Assert.Empty(policy.Disabled);
        Assert.True(policy.IsAllowed(AnyTool));
    }
}
