using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Umcp.ToolGen;

internal static class Names
{
    public static bool Is(AttributeSyntax a, string name)
    {
        var n = a.Name.ToString();
        return n == name || n == name + "Attribute" || n.EndsWith("." + name) || n.EndsWith("." + name + "Attribute");
    }
}

internal sealed class ParamModel
{
    public required string Name { get; init; }
    public required string CsType { get; init; }
    public required bool Required { get; init; }
    public string? Doc { get; init; }
    public string? Default { get; init; }

    /// <summary>The JSON Schema fragment for this parameter.</summary>
    public string Schema()
    {
        var (type, items) = CsType switch
        {
            "string" => ("string", null),
            "int" or "int?" => ("integer", null),
            "float" or "double" or "float?" or "double?" => ("number", null),
            "bool" or "bool?" => ("boolean", null),
            "string[]" => ("array", "string"),
            "float[]" or "double[]" => ("array", "number"),
            "int[]" => ("array", "integer"),
            "JObject" or "Newtonsoft.Json.Linq.JObject" => ("object", null),
            _ => ("string", null)
        };

        var parts = new List<string> { $"\"type\":\"{type}\"" };
        if (items is not null) parts.Add($"\"items\":{{\"type\":\"{items}\"}}");
        if (!string.IsNullOrEmpty(Doc)) parts.Add($"\"description\":{Json.Str(Doc)}");
        if (Default is not null && Default != "null") parts.Add($"\"default\":{JsonDefault(type)}");
        return "{" + string.Join(",", parts) + "}";
    }

    /// <summary>
    /// The default as JSON rather than as C#. A C# float literal is <c>0f</c>, and <c>0f</c> in a
    /// JSON Schema is not a number — it is a parse error two layers away from the code that wrote
    /// it, which is exactly the kind of drift this generator exists to make impossible.
    /// </summary>
    string JsonDefault(string jsonType)
    {
        var d = Default!;
        if (jsonType is "number" or "integer")
            return d.TrimEnd('f', 'F', 'd', 'D', 'm', 'M');
        if (jsonType == "boolean") return d;
        if (jsonType == "string") return d.StartsWith('"') ? d : Json.Str(d);
        return d;
    }

    /// <summary>The binder call as a statement whose value is discarded — validation only.</summary>
    public string BindDiscard() => "var __" + Name + " = " + Binder();

    /// <summary>The binder call the generated dispatch uses for this parameter.</summary>
    public string Binder()
    {
        var req = Required ? "true" : "false";
        var d = Default ?? "";
        return CsType switch
        {
            "string" => $"Bind.Str(a, \"{Name}\", {req}{(Default is null or "null" ? "" : ", " + d)})",
            "int" => $"Bind.Int(a, \"{Name}\", {req}{(Default is null ? "" : ", " + d)})",
            "float" => $"Bind.Flt(a, \"{Name}\", {req}{(Default is null ? "" : ", " + FloatLit(d))})",
            "double" => $"(double)Bind.Flt(a, \"{Name}\", {req}{(Default is null ? "" : ", " + FloatLit(d))})",
            "bool" => $"Bind.Bool(a, \"{Name}\", {req}{(Default is null ? "" : ", " + d)})",
            // A nullable parameter has no default to pass: null is the default, and it means
            // "the caller did not say", which is a different thing from any value.
            "int?" => $"Bind.IntOpt(a, \"{Name}\", {req})",
            "float?" or "double?" => $"Bind.FltOpt(a, \"{Name}\", {req})",
            "bool?" => $"Bind.BoolOpt(a, \"{Name}\", {req})",
            "string[]" => $"Bind.StrArr(a, \"{Name}\", {req})",
            "float[]" or "double[]" => $"Bind.FltArr(a, \"{Name}\", {req})",
            "int[]" => $"Bind.IntArr(a, \"{Name}\", {req})",
            _ => $"Bind.Obj(a, \"{Name}\", {req})"
        };
    }

    static string FloatLit(string d) => d.EndsWith("f") || d.EndsWith("F") ? d : d + "f";
}

internal sealed class ToolModel
{
    public required string Id { get; init; }
    public required string Skill { get; init; }
    public required string Summary { get; init; }
    public required string Owner { get; init; }
    public required string Method { get; init; }
    public required bool Mutating { get; init; }
    public required string Retry { get; init; }
    public required string Cost { get; init; }
    public string? Undo { get; init; }
    public string? NoUndoReason { get; init; }
    public required List<ParamModel> Params { get; init; }
    public required List<string> Examples { get; init; }

    public string InputSchema()
    {
        var props = string.Join(",", Params.Select(p => $"{Json.Str(p.Name)}:{p.Schema()}"));
        var required = Params.Where(p => p.Required).Select(p => Json.Str(p.Name)).ToArray();
        var req = required.Length == 0 ? "" : $",\"required\":[{string.Join(",", required)}]";
        return $"{{\"type\":\"object\",\"properties\":{{{props}}}{req},\"additionalProperties\":false}}";
    }

    public static ToolModel? From(AttributeSyntax attr, MethodDeclarationSyntax method, string owner, string file, List<string> errors)
    {
        var named = new Dictionary<string, string>();
        foreach (var arg in attr.ArgumentList?.Arguments ?? default)
        {
            var name = arg.NameEquals?.Name.Identifier.Text;
            if (name is null) continue;
            named[name] = arg.Expression.ToString();
        }

        var id = Json.Unquote(named.GetValueOrDefault("Id"));
        var summary = Json.Unquote(named.GetValueOrDefault("Summary"));
        var where = $"{Path.GetFileName(file)}:{owner}.{method.Identifier.Text}";

        if (string.IsNullOrEmpty(id)) { errors.Add($"{where}: [UnityTool] needs an Id."); return null; }
        if (string.IsNullOrEmpty(summary)) { errors.Add($"{where}: tool '{id}' needs a Summary."); return null; }

        var ps = new List<ParamModel>();
        foreach (var p in method.ParameterList.Parameters)
        {
            var doc = p.AttributeLists.SelectMany(l => l.Attributes).FirstOrDefault(a => Names.Is(a, "Doc"));
            var docText = doc?.ArgumentList?.Arguments.FirstOrDefault()?.Expression.ToString();
            var type = p.Type!.ToString();
            if (type.StartsWith("Newtonsoft.Json.Linq.")) type = type["Newtonsoft.Json.Linq.".Length..];

            ps.Add(new ParamModel
            {
                Name = p.Identifier.Text,
                CsType = type,
                Required = p.Default is null,
                Doc = Json.Unquote(docText),
                Default = p.Default?.Value.ToString()
            });
        }

        var examples = method.AttributeLists.SelectMany(l => l.Attributes)
            .Where(a => Names.Is(a, "Example"))
            .Select(a => Json.Unquote(a.ArgumentList?.Arguments.FirstOrDefault()?.Expression.ToString()))
            .Where(s => !string.IsNullOrEmpty(s))
            .Select(s => s!)
            .ToList();

        return new ToolModel
        {
            Id = id!,
            Skill = Json.Unquote(named.GetValueOrDefault("Skill")) ?? id!.Split('.')[0],
            Summary = summary!,
            Owner = owner,
            Method = method.Identifier.Text,
            Mutating = named.GetValueOrDefault("Mutating") == "true",
            Retry = Enum(named.GetValueOrDefault("Retry"), "Read"),
            Cost = Enum(named.GetValueOrDefault("Cost"), "Cheap"),
            Undo = Json.Unquote(named.GetValueOrDefault("Undo")),
            NoUndoReason = Json.Unquote(named.GetValueOrDefault("NoUndoReason")),
            Params = ps,
            Examples = examples
        };
    }

    static string Enum(string? expr, string dflt)
    {
        if (string.IsNullOrEmpty(expr)) return dflt;
        var dot = expr.LastIndexOf('.');
        return dot < 0 ? expr : expr[(dot + 1)..];
    }
}
