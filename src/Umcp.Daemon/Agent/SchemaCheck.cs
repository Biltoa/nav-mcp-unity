using System.Text.Json;
using System.Text.Json.Nodes;
using Umcp.Daemon.Generated;

namespace Umcp.Daemon.Agent;

/// <summary>
/// Validate arguments against the generated schema <em>on the daemon</em>, before dispatch.
///
/// Validation is free here — it happens off the Editor tick — and it removes a whole class of
/// confusing Unity-side failures. The tool being replaced flattens every parameter into
/// <c>Dictionary&lt;string,string&gt;</c>, turns <c>{x,y,z}</c> into <c>"1,2,3"</c>, and leaves
/// each handler to re-parse by hand.
/// </summary>
public static class SchemaCheck
{
    public static JsonObject? Validate(ToolEntry entry, JsonObject args)
    {
        JsonNode? schema;
        try { schema = JsonNode.Parse(entry.InputSchema); }
        catch { return null; }
        if (schema?["properties"] is not JsonObject props) return null;

        var known = props.Select(p => p.Key).ToArray();

        foreach (var name in (schema["required"] as JsonArray)?.Select(n => (string)n!) ?? Enumerable.Empty<string>())
        {
            if (!args.ContainsKey(name) || args[name] is null)
                return Envelope.Error("E_ARG_REQUIRED",
                    $"'{entry.Id}' requires '{name}'.", param: name,
                    didYouMean: Fuzzy.Closest(name, args.Select(a => a.Key), 3),
                    hint: Example(entry));
        }

        foreach (var (key, value) in args)
        {
            if (!props.TryGetPropertyValue(key, out var spec))
                return Envelope.Error("E_ARG_UNKNOWN",
                    $"'{entry.Id}' has no parameter '{key}'.", param: key,
                    didYouMean: Fuzzy.Closest(key, known, 3),
                    hint: "Accepted: " + string.Join(", ", known));

            if (value is null) continue;
            var expected = (string?)spec?["type"];
            if (expected is null) continue;

            if (!Matches(value, expected))
                return Envelope.Error("E_ARG_TYPE",
                    $"'{key}' should be {Describe(expected, spec)}, got {Kind(value)}.",
                    param: key, value: Short(value),
                    hint: Example(entry));
        }

        return null;
    }

    static string Example(ToolEntry entry)
        => entry.Examples.Length > 0 ? "For example: " + entry.Examples[0] : "See unity.catalog for the schema.";

    static bool Matches(JsonNode value, string expected) => expected switch
    {
        "string" => value is JsonValue v && v.TryGetValue<string>(out _),
        "integer" => value is JsonValue iv && (iv.TryGetValue<long>(out _) || iv.TryGetValue<int>(out _)),
        "number" => value is JsonValue nv && (nv.TryGetValue<double>(out _) || nv.TryGetValue<long>(out _)),
        "boolean" => value is JsonValue bv && bv.TryGetValue<bool>(out _),
        "array" => value is JsonArray,
        "object" => value is JsonObject,
        _ => true
    };

    static string Describe(string expected, JsonNode? spec)
    {
        if (expected != "array") return "a " + expected;
        var itemType = (string?)spec?["items"]?["type"];
        return itemType is null ? "an array" : $"an array of {itemType}s";
    }

    static string Kind(JsonNode n) => n switch
    {
        JsonArray => "an array",
        JsonObject => "an object",
        JsonValue v when v.TryGetValue<bool>(out _) => "a boolean",
        JsonValue v when v.TryGetValue<string>(out _) => "a string",
        _ => "a number"
    };

    static string Short(JsonNode n)
    {
        var s = n.ToJsonString();
        return s.Length <= 120 ? s : s[..120] + "…";
    }
}
