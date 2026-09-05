using System.Text.Json.Nodes;
using Umcp.Agent;
using Umcp.Daemon.Mirror;
using Xunit;

namespace Umcp.Tests;

public class SceneSelectorTests
{
    [Fact]
    public void A_bare_name_is_treated_as_a_descendant_search()
    {
        var steps = SceneSelector.Parse("Button");
        Assert.Single(steps);
        Assert.True(steps[0].Descendant);
        Assert.Equal("Button", steps[0].Name);
    }

    [Fact]
    public void Slash_is_a_direct_child_and_double_slash_is_any_descendant()
    {
        var steps = SceneSelector.Parse("/Level/Props//Crate");
        Assert.Equal(3, steps.Count);
        Assert.False(steps[0].Descendant);
        Assert.False(steps[1].Descendant);
        Assert.True(steps[2].Descendant);
        Assert.Equal("Crate", steps[2].Name);
    }

    [Fact]
    public void Predicates_chain_on_one_step()
    {
        var steps = SceneSelector.Parse("//Button[active][has:Image]");
        Assert.Single(steps);
        Assert.Equal(2, steps[0].Predicates.Count);
        Assert.Equal("active", steps[0].Predicates[0].Key);
        Assert.Equal("has", steps[0].Predicates[1].Key);
        Assert.Equal("Image", steps[0].Predicates[1].Arg);
    }

    [Fact]
    public void An_empty_selector_means_everything()
    {
        var steps = SceneSelector.Parse("");
        Assert.Single(steps);
        Assert.Equal("*", steps[0].Name);
        Assert.True(steps[0].Descendant);
    }

    [Fact]
    public void An_unclosed_predicate_is_a_syntax_error()
    {
        var e = Assert.Throws<SceneSelector.ParseException>(() => SceneSelector.Parse("//Button[active"));
        Assert.Equal(SceneSelector.ErrSyntax, e.Code);
    }

    [Fact]
    public void An_unknown_predicate_suggests_the_near_miss()
    {
        var e = Assert.Throws<SceneSelector.ParseException>(() => SceneSelector.Parse("//Button[activ]"));
        Assert.Equal(SceneSelector.ErrPredicate, e.Code);
        Assert.Contains("active", e.DidYouMean!);
    }

    [Fact]
    public void A_predicate_that_needs_an_argument_says_so()
    {
        var e = Assert.Throws<SceneSelector.ParseException>(() => SceneSelector.Parse("//*[has]"));
        Assert.Equal(SceneSelector.ErrPredicate, e.Code);
        Assert.Contains("needs a value", e.Message);
    }

    [Fact]
    public void Component_property_fields_are_not_mirror_serviceable()
    {
        Assert.True(SceneSelector.IsMirrorServiceableField("path"));
        Assert.False(SceneSelector.IsMirrorServiceableField("Rigidbody.mass"));
        Assert.False(SceneSelector.IsMirrorServiceableField("position"));
    }
}

public class MirrorHashTests
{
    [Fact]
    public void The_same_inputs_hash_the_same()
    {
        var a = MirrorHash.Node("Crate", 2, 3, "Untagged", "Default", "Transform|MeshRenderer");
        var b = MirrorHash.Node("Crate", 2, 3, "Untagged", "Default", "Transform|MeshRenderer");
        Assert.Equal(a, b);
    }

    [Theory]
    [InlineData("Crate2", 2, 3, "Untagged", "Default", "Transform")]
    [InlineData("Crate", 3, 3, "Untagged", "Default", "Transform")]
    [InlineData("Crate", 2, 1, "Untagged", "Default", "Transform")]
    [InlineData("Crate", 2, 3, "Player", "Default", "Transform")]
    [InlineData("Crate", 2, 3, "Untagged", "Water", "Transform")]
    [InlineData("Crate", 2, 3, "Untagged", "Default", "Transform|Rigidbody")]
    public void Any_field_changing_changes_the_hash(string name, int sib, int flags, string tag, string layer, string comps)
    {
        var baseline = MirrorHash.Node("Crate", 2, 3, "Untagged", "Default", "Transform");
        Assert.NotEqual(baseline, MirrorHash.Node(name, sib, flags, tag, layer, comps));
    }

    [Fact]
    public void Folding_is_order_sensitive()
    {
        var a = MirrorHash.Fold(MirrorHash.Fold(MirrorHash.Start(), 1), 2);
        var b = MirrorHash.Fold(MirrorHash.Fold(MirrorHash.Start(), 2), 1);
        Assert.NotEqual(a, b);
    }
}

public class SceneMirrorTests
{
    /// <summary>[id, name, parent, sibling, flags, tag, layer, components, scene]</summary>
    static JsonArray Node(int id, string name, int parent, int sib, int flags,
                          string comps = "Transform", string tag = "Untagged",
                          string layer = "Default", string? scene = null) =>
        new(id, name, parent, sib, flags, tag, layer, comps, scene);

    static SceneMirror Seeded()
    {
        var mirror = new SceneMirror();
        mirror.Seed(new JsonObject
        {
            ["seq"] = 1,
            ["activeScene"] = "Main",
            ["nodes"] = new JsonArray
            {
                Node(1, "Level", 0, 0, 1, scene: "Main"),
                Node(2, "Props", 1, 0, 1),
                Node(3, "Crate", 2, 0, 1, "Transform|MeshRenderer|BoxCollider"),
                Node(4, "Barrel", 2, 1, 0, "Transform|MeshRenderer"),
                Node(5, "UI", 0, 1, 1, "Transform|Canvas", scene: "Main"),
                Node(6, "Button", 5, 0, 1, "Transform|Image|Button")
            }
        }, epoch: 4);
        return mirror;
    }

    [Fact]
    public void Seeding_builds_parentage_and_paths()
    {
        var mirror = Seeded();
        Assert.True(mirror.Seeded);
        Assert.Equal(6, mirror.Count);
        Assert.Equal(2, mirror.Roots().Count);

        Assert.True(mirror.TryGet(3, out var crate));
        Assert.Equal("Level/Props/Crate", mirror.Path(crate));
    }

    [Fact]
    public void A_delta_updates_a_node_in_place_and_bumps_the_revision()
    {
        var mirror = Seeded();
        var before = mirror.Revision;

        mirror.Apply(new JsonObject
        {
            ["seq"] = 2,
            ["nodes"] = new JsonArray { Node(3, "Crate_Renamed", 2, 0, 1, "Transform|MeshRenderer|BoxCollider") }
        });

        Assert.True(mirror.TryGet(3, out var crate));
        Assert.Equal("Crate_Renamed", crate.Name);
        Assert.True(mirror.Revision > before);
        Assert.Equal(6, mirror.Count);
    }

    [Fact]
    public void Removing_a_node_removes_its_whole_subtree()
    {
        var mirror = Seeded();
        mirror.Apply(new JsonObject { ["seq"] = 2, ["removed"] = new JsonArray { 2 } });

        Assert.Equal(3, mirror.Count);          // Props, Crate and Barrel all gone
        Assert.False(mirror.TryGet(3, out _));
        Assert.False(mirror.TryGet(4, out _));
        Assert.True(mirror.TryGet(1, out _));
    }

    [Fact]
    public void Reparenting_moves_the_node_under_its_new_parent()
    {
        var mirror = Seeded();
        mirror.Apply(new JsonObject
        {
            ["seq"] = 2,
            ["nodes"] = new JsonArray { Node(3, "Crate", 5, 1, 1, "Transform|MeshRenderer|BoxCollider") }
        });

        Assert.True(mirror.TryGet(3, out var crate));
        Assert.Equal("UI/Crate", mirror.Path(crate));
        Assert.True(mirror.TryGet(2, out var props));
        Assert.DoesNotContain(3, props.Children);
    }

    [Fact]
    public void Invalidating_unseeds_the_model_rather_than_leaving_a_partial_one()
    {
        var mirror = Seeded();
        mirror.Invalidate("test");
        Assert.False(mirror.Seeded);
        Assert.Equal(0, mirror.Count);
        Assert.Equal(1, mirror.ResyncCount);
    }

    [Fact]
    public void Hashes_change_when_the_hierarchy_changes_and_not_otherwise()
    {
        var mirror = Seeded();
        var (before, countBefore, _) = mirror.Hashes();

        // A delta that changes nothing must not change the hash.
        mirror.Apply(new JsonObject
        {
            ["seq"] = 2,
            ["nodes"] = new JsonArray { Node(4, "Barrel", 2, 1, 0, "Transform|MeshRenderer") }
        });
        var (same, countSame, _) = mirror.Hashes();
        Assert.Equal(before, same);
        Assert.Equal(countBefore, countSame);

        mirror.Apply(new JsonObject
        {
            ["seq"] = 3,
            ["nodes"] = new JsonArray { Node(4, "Barrel", 2, 1, 1, "Transform|MeshRenderer") }
        });
        var (after, _, _) = mirror.Hashes();
        Assert.NotEqual(before, after);
    }
}

public class MirrorQueryTests
{
    static SceneMirror Seeded()
    {
        var mirror = new SceneMirror();
        mirror.Seed(new JsonObject
        {
            ["seq"] = 1,
            ["nodes"] = new JsonArray
            {
                new JsonArray(1, "Level", 0, 0, 1, "Untagged", "Default", "Transform", "Main"),
                new JsonArray(2, "Props", 1, 0, 1, "Untagged", "Default", "Transform", null),
                new JsonArray(3, "Crate", 2, 0, 1, "Untagged", "Default", "Transform|MeshRenderer|Rigidbody", null),
                new JsonArray(4, "Crate", 2, 1, 0, "Untagged", "Water", "Transform|MeshRenderer", null),
                new JsonArray(5, "UI", 0, 1, 1, "Untagged", "UI", "Transform|Canvas", "Main"),
                new JsonArray(6, "Button", 5, 0, 1, "Player", "UI", "Transform|Image", null)
            }
        }, epoch: 1);
        return mirror;
    }

    static List<MirrorNode> Run(SceneMirror m, string selector) =>
        MirrorQuery.Evaluate(m, SceneSelector.Parse(selector), -1);

    [Fact]
    public void Descendant_search_finds_at_any_depth()
    {
        Assert.Equal(2, Run(Seeded(), "//Crate").Count);
    }

    [Fact]
    public void Direct_child_search_does_not_descend()
    {
        Assert.Empty(Run(Seeded(), "/Level/Crate"));
        Assert.Single(Run(Seeded(), "/Level/Props"));
    }

    [Fact]
    public void Predicates_filter()
    {
        var m = Seeded();
        Assert.Single(Run(m, "//Crate[active]"));
        Assert.Single(Run(m, "//Crate[inactive]"));
        Assert.Single(Run(m, "//*[has:Rigidbody]"));
        Assert.Single(Run(m, "//*[layer:Water]"));
        Assert.Single(Run(m, "//*[tag:Player]"));
        Assert.Equal(2, Run(m, "//*[root]").Count);
    }

    [Fact]
    public void Wildcards_and_name_matching_work()
    {
        var m = Seeded();
        Assert.Equal(6, Run(m, "//*").Count);
        Assert.Equal(2, Run(m, "//*[name^:Cra]").Count);
        Assert.Single(Run(m, "//*[name$:ton]"));
    }

    [Fact]
    public void Active_in_hierarchy_is_derived_from_the_ancestor_chain()
    {
        var m = Seeded();
        // Deactivating Props must make Crate inactive in the hierarchy without any event for Crate.
        m.Apply(new JsonObject
        {
            ["seq"] = 2,
            ["nodes"] = new JsonArray { new JsonArray(2, "Props", 1, 0, 0, "Untagged", "Default", "Transform", null) }
        });

        m.TryGet(3, out var crate);
        Assert.True(crate.ActiveSelf);
        Assert.False(crate.ActiveInHierarchy(m));
        Assert.Empty(Run(m, "//Crate[active]"));
        Assert.Equal(2, Run(m, "//Crate[inactive]").Count);
    }

    [Fact]
    public void Projection_returns_only_the_requested_fields()
    {
        var m = Seeded();
        m.TryGet(3, out var crate);
        var o = MirrorQuery.Project(m, crate, new[] { "name", "path", "components" });
        Assert.Equal(3, o.Count);
        Assert.Equal("Level/Props/Crate", (string?)o["path"]);
        Assert.Equal(3, (o["components"] as JsonArray)!.Count);
    }

    [Fact]
    public void A_component_property_field_is_refused_rather_than_approximated()
    {
        var m = Seeded();
        var steps = SceneSelector.Parse("//*");
        var can = MirrorQuery.CanServe(m, steps, new[] { "path", "Rigidbody.mass" });
        Assert.False(can.CanServe);
        Assert.Contains("component property", can.Reason);
    }

    [Fact]
    public void An_unknown_component_name_falls_through_to_the_editor()
    {
        // An empty result would look like a true answer; the Editor can say E_TYPE_NOT_FOUND
        // with suggestions instead.
        var can = MirrorQuery.CanServe(Seeded(), SceneSelector.Parse("//*[has:Rigidbdy]"), Array.Empty<string>());
        Assert.False(can.CanServe);
        Assert.Contains("not present anywhere", can.Reason);
    }

    [Fact]
    public void An_unseeded_mirror_serves_nothing()
    {
        var can = MirrorQuery.CanServe(new SceneMirror(), SceneSelector.Parse("//*"), Array.Empty<string>());
        Assert.False(can.CanServe);
        Assert.Contains("not seeded", can.Reason);
    }

    [Fact]
    public void A_fully_mirrored_query_is_serviceable()
    {
        var can = MirrorQuery.CanServe(Seeded(), SceneSelector.Parse("//*[has:Rigidbody][active]"),
            new[] { "id", "name", "path", "components" });
        Assert.True(can.CanServe, can.Reason);
    }
}
