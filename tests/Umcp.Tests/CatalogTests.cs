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

    [Fact]
    public void Every_tool_belongs_to_a_skill_node_that_exists()
    {
        var tree = new SkillTree();
        var ids = tree.Nodes.Select(n => n.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var orphans = ToolCatalog.All.Select(t => t.Skill).Distinct()
            .Where(s => !ids.Contains(s)).ToArray();
        Assert.Empty(orphans);
    }
}

public class SkillTreeTests
{
    static readonly SkillTree Tree = new();

    [Fact]
    public void Skill_nodes_load_from_embedded_resources()
    {
        Assert.True(Tree.Nodes.Count >= 6, $"expected at least 6 authored skills, found {Tree.Nodes.Count}");
        Assert.NotNull(Tree.Get("material"));
        Assert.NotNull(Tree.Get("scene.query"));
    }

    [Fact]
    public void Sub_skills_are_parented_by_their_id()
    {
        var children = Tree.ChildrenOf("scene").Select(n => n.Id).ToArray();
        Assert.Contains("scene.query", children);
        Assert.DoesNotContain("scene", Tree.Roots().Select(r => r.Id).Where(id => id.Contains('.')));
    }

    [Fact]
    public void Every_node_maps_to_at_least_one_tool()
    {
        foreach (var node in Tree.Nodes)
        {
            // script.md documents unity_script, which is a Tier-0 tool rather than a catalog entry.
            if (node.Id == "script") continue;
            Assert.NotEmpty(Tree.ToolsFor(node));
        }
    }

    [Fact]
    public void Placeholders_are_filled_from_project_facts()
    {
        var rendered = SkillTree.Render("pipeline is {{pipeline}}", new Dictionary<string, string> { ["pipeline"] = "URP" });
        Assert.Equal("pipeline is URP", rendered);
    }

    [Fact]
    public void Unfilled_placeholders_say_so_rather_than_leaking_the_token()
    {
        var rendered = SkillTree.Render("pipeline is {{pipeline}}", new Dictionary<string, string>());
        Assert.DoesNotContain("{{", rendered);
        Assert.Contains("unknown", rendered);
    }

    [Theory]
    [InlineData("create gameobject", "gameobject.create")]
    [InlineData("add component", "component.add")]
    [InlineData("assign texture to material", "material.set")]
    [InlineData("compile errors", "compile.errors")]
    [InlineData("select objects in the hierarchy", "scene.query")]
    public void Search_surfaces_the_obvious_tool(string query, string expected)
    {
        var hits = Tree.Index.Search(query, 8).Select(h => h.Doc.Id).ToArray();
        Assert.Contains(expected, hits);
    }

    [Fact]
    public void Searching_an_exact_id_ranks_it_first()
    {
        Assert.Equal("transform.set", Tree.Index.Search("transform.set", 5)[0].Doc.Id);
    }

    [Fact]
    public void Search_can_be_restricted_to_skills()
    {
        var hits = Tree.Index.Search("material", 5, kind: "skill");
        Assert.All(hits, h => Assert.Equal("skill", h.Doc.Kind));
        Assert.Contains("material", hits.Select(h => h.Doc.Id));
    }
}
