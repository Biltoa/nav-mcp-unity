using System.Text;
using System.Text.Json.Nodes;
using Umcp.Daemon.Agent;
using Umcp.Daemon.Generated;
using Xunit;

namespace Umcp.Tests;

public class EnvelopeTests
{
    [Fact]
    public void Small_payloads_pass_through_untouched()
    {
        var data = new JsonObject { ["a"] = 1, ["b"] = "two" };
        var (node, truncated, bytes) = Envelope.Cap(data, 32 * 1024);
        Assert.False(truncated);
        Assert.Equal(data.ToJsonString(), node!.ToJsonString());
        Assert.True(bytes > 0);
    }

    [Fact]
    public void Oversized_lists_truncate_honestly()
    {
        var items = new JsonArray();
        for (var i = 0; i < 5000; i++)
            items.Add(new JsonObject { ["id"] = i, ["name"] = "Object_with_a_fairly_long_name_" + i });

        var data = new JsonObject { ["items"] = items, ["_total"] = 5000 };
        var (node, truncated, bytes) = Envelope.Cap(data, 4096);

        Assert.True(truncated);
        Assert.True(bytes > 4096);
        Assert.True((bool?)node!["_truncated"]);
        Assert.Equal(5000, (int?)node["_total"]);
        Assert.True((int?)node["_returned"] < 5000);
        Assert.NotNull(node["_hint"]);
        // The truncated response must itself be within budget.
        Assert.True(Encoding.UTF8.GetByteCount(node.ToJsonString()) <= 4096);
    }

    [Fact]
    public void Oversized_non_list_payloads_say_so_rather_than_silently_cutting()
    {
        var data = new JsonObject { ["blob"] = new string('x', 100_000) };
        var (node, truncated, _) = Envelope.Cap(data, 2048);
        Assert.True(truncated);
        Assert.True((bool?)node!["_truncated"]);
        Assert.NotNull(node["_hint"]);
    }

    [Fact]
    public void Errors_carry_machine_actionable_fields()
    {
        var e = Envelope.Error("E_TARGET_NOT_FOUND", "No GameObject matched 'Palyer'.",
            param: "target", value: "Palyer",
            didYouMean: new[] { "Player", "PlayerCamera" },
            hint: "Names are case-sensitive.");

        Assert.False((bool?)e["ok"]);
        Assert.Equal("E_TARGET_NOT_FOUND", (string?)e["code"]);
        Assert.Equal("target", (string?)e["param"]);
        Assert.Equal("Palyer", (string?)e["value"]);
        Assert.Equal(2, (e["didYouMean"] as JsonArray)!.Count);
        Assert.NotNull(e["hint"]);
        Assert.NotNull(e["meta"]);
    }

    [Fact]
    public void Fuzzy_suggests_the_near_miss_and_not_the_whole_catalog()
    {
        var hits = Fuzzy.Closest("Palyer", new[] { "Player", "PlayerCamera", "Terrain", "Cube" }, 3);
        Assert.Equal("Player", hits[0]);
        Assert.DoesNotContain("Terrain", hits);
    }
}

public class SchemaCheckTests
{
    static ToolEntry Tool(string id) => ToolCatalog.ById[id];

    [Fact]
    public void Missing_required_argument_is_rejected_before_dispatch()
    {
        var error = SchemaCheck.Validate(Tool("gameobject.create"), new JsonObject());
        Assert.NotNull(error);
        Assert.Equal("E_ARG_REQUIRED", (string?)error!["code"]);
        Assert.Equal("name", (string?)error["param"]);
    }

    [Fact]
    public void Unknown_argument_is_rejected_with_a_suggestion()
    {
        var error = SchemaCheck.Validate(Tool("gameobject.create"),
            new JsonObject { ["name"] = "A", ["primitve"] = "Cube" });

        Assert.NotNull(error);
        Assert.Equal("E_ARG_UNKNOWN", (string?)error!["code"]);
        Assert.Contains("primitive", (error["didYouMean"] as JsonArray)!.Select(n => (string)n!));
    }

    [Fact]
    public void Wrong_argument_type_is_rejected_with_the_expected_shape()
    {
        var error = SchemaCheck.Validate(Tool("gameobject.create"),
            new JsonObject { ["name"] = "A", ["position"] = "0,1,0" });

        Assert.NotNull(error);
        Assert.Equal("E_ARG_TYPE", (string?)error!["code"]);
        Assert.Contains("array of numbers", (string?)error["message"]);
    }

    [Fact]
    public void A_valid_call_passes()
    {
        var ok = SchemaCheck.Validate(Tool("gameobject.create"), new JsonObject
        {
            ["name"] = "Enemy",
            ["primitive"] = "Capsule",
            ["position"] = new JsonArray(0, 1, 0)
        });
        Assert.Null(ok);
    }
}
