using System.Text.Json.Nodes;
using Umcp.Daemon.Generated;
using Umcp.Daemon.Mcp;
using Xunit;

namespace Umcp.Tests;

/// <summary>
/// The quality bar from the plan, enforced as tests rather than as a review checklist.
/// The generator also fails the build on the same rules; this is the second lock.
/// </summary>
public class CatalogTests
{
    [Fact]
    public void Every_tool_id_is_unique()
    {
        var dupes = ToolCatalog.All.GroupBy(t => t.Id).Where(g => g.Count() > 1).Select(g => g.Key).ToArray();
        Assert.Empty(dupes);
    }

    [Fact]
    public void Every_tool_has_at_least_one_input_example()
    {
        var missing = ToolCatalog.All.Where(t => t.Examples.Length == 0).Select(t => t.Id).ToArray();
        Assert.Empty(missing);
    }

    [Fact]
    public void Every_mutating_tool_states_its_undo_story()
    {
        var missing = ToolCatalog.All
            .Where(t => t.Mutating && t.Undo is null && t.NoUndoReason is null)
            .Select(t => t.Id).ToArray();
        Assert.Empty(missing);
    }

    [Fact]
    public void Every_schema_is_valid_json_with_an_object_root()
    {
        foreach (var t in ToolCatalog.All)
        {
            var node = JsonNode.Parse(t.InputSchema);
            Assert.NotNull(node);
            Assert.Equal("object", (string?)node!["type"]);
            Assert.NotNull(node["properties"]);
        }
    }

    [Fact]
    public void Every_example_is_valid_json()
    {
        foreach (var t in ToolCatalog.All)
            foreach (var ex in t.Examples)
                Assert.NotNull(JsonNode.Parse(ex));
    }

    [Fact]
    public void Every_example_validates_against_its_own_schema()
    {
        foreach (var t in ToolCatalog.All)
            foreach (var ex in t.Examples)
            {
                var args = JsonNode.Parse(ex) as JsonObject;
                Assert.NotNull(args);
                var error = Umcp.Daemon.Agent.SchemaCheck.Validate(t, args!);
                Assert.True(error is null, $"{t.Id} example failed validation: {error?.ToJsonString()}");
            }
    }

    [Fact]
    public void Reads_are_never_classified_as_mutating()
    {
        // "No tool may change Editor state as a side effect of a read" — a read that declares
        // itself mutating is a design smell, and a mutating tool classified Read would be
        // retried automatically.
        var wrong = ToolCatalog.All.Where(t => t.Mutating && t.Retry == "Read").Select(t => t.Id).ToArray();
        Assert.Empty(wrong);
    }

    [Theory]
    [InlineData("create gameobject", "gameobject.create")]
    [InlineData("add component", "component.add")]
    [InlineData("material", "material.create")]
    [InlineData("console errors", "console.read")]
    public void Search_surfaces_the_obvious_tool(string query, string expected)
    {
        var hits = CatalogSearch.Search(query, 5).Select(h => h.entry.Id).ToArray();
        Assert.Contains(expected, hits);
    }

    [Fact]
    public void Search_of_an_exact_id_ranks_it_first()
    {
        var hits = CatalogSearch.Search("transform.set", 5).ToArray();
        Assert.Equal("transform.set", hits[0].entry.Id);
    }
}
