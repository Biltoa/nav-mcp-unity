using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Umcp.ToolGen;

// One source of truth: the [UnityTool] methods in the Unity package.
//
// This walks them with Roslyn and emits, at build time:
//   * the Unity-side dispatch table and argument binding  (no hand-written switch)
//   * the daemon-side catalog with JSON Schema and examples
//   * markdown docs
//
// The tool being replaced maintains each tool in three places and builds its registry by
// regex-scraping JavaScript — a regex that silently drops any tool whose description contains a
// double quote or an apostrophe. Drift between layers is structurally impossible here.

var repoRoot = Args.Get("--root") ?? FindRepoRoot();
var toolsDir = Path.Combine(repoRoot, "unity", "com.umcp.agent", "Editor");
var unityOut = Path.Combine(repoRoot, "unity", "com.umcp.agent", "Editor", "Generated", "ToolDispatch.g.cs");
var daemonOut = Path.Combine(repoRoot, "src", "Umcp.Daemon", "Generated", "ToolCatalog.g.cs");
var docsOut = Path.Combine(repoRoot, "docs", "TOOLS.md");

if (!Directory.Exists(toolsDir))
{
    Console.Error.WriteLine($"toolgen: no tools directory at {toolsDir}");
    return 2;
}

var tools = new List<ToolModel>();
var errors = new List<string>();

foreach (var file in Directory.GetFiles(toolsDir, "*.cs", SearchOption.AllDirectories).OrderBy(f => f))
{
    var tree = CSharpSyntaxTree.ParseText(File.ReadAllText(file), path: file);
    var root = tree.GetCompilationUnitRoot();

    foreach (var method in root.DescendantNodes().OfType<MethodDeclarationSyntax>())
    {
        var attr = method.AttributeLists.SelectMany(l => l.Attributes)
            .FirstOrDefault(a => Names.Is(a, "UnityTool"));
        if (attr is null) continue;

        var owner = method.Ancestors().OfType<ClassDeclarationSyntax>().First().Identifier.Text;
        var model = ToolModel.From(attr, method, owner, file, errors);
        if (model is not null) tools.Add(model);
    }
}

if (errors.Count > 0)
{
    foreach (var e in errors) Console.Error.WriteLine("toolgen: " + e);
    return 1;
}

var dupes = tools.GroupBy(t => t.Id).Where(g => g.Count() > 1).Select(g => g.Key).ToArray();
if (dupes.Length > 0)
{
    Console.Error.WriteLine("toolgen: duplicate tool ids: " + string.Join(", ", dupes));
    return 1;
}

tools = tools.OrderBy(t => t.Id, StringComparer.Ordinal).ToList();

// A parameter default is copied verbatim into generated code and into the JSON schema, so it has
// to be a literal. A named constant compiles in the tool's own file and then fails to resolve in
// the generated dispatch — 200 lines away, in a file nobody edits.
var badDefaults = new List<string>();
foreach (var tool in tools)
    foreach (var p in tool.Params)
    {
        var d = p.Default;
        if (d is null or "null") continue;
        var literal = d.StartsWith('"') || d is "true" or "false" ||
                      d.All(c => char.IsDigit(c) || c is '.' or '-' or '+' or 'f' or 'F' or 'd' or 'D' or 'm' or 'M');
        if (!literal) badDefaults.Add($"{tool.Id}.{p.Name} = {d}");
    }
if (badDefaults.Count > 0)
{
    foreach (var b in badDefaults)
        Console.Error.WriteLine("toolgen: parameter default must be a literal, not a named constant - " + b);
    return 1;
}

// Every generated schema must parse as JSON, checked here rather than discovered by whatever
// tries to read it. A C# `0f` default produced a schema that failed to parse two layers away,
// in the daemon's skill index, as a JsonReaderException with no tool name in it.
var badSchemas = new List<string>();
foreach (var tool in tools)
{
    try { System.Text.Json.JsonDocument.Parse(tool.InputSchema()); }
    catch (Exception e) { badSchemas.Add($"{tool.Id}: {e.Message}"); }
}
if (badSchemas.Count > 0)
{
    foreach (var b in badSchemas) Console.Error.WriteLine("toolgen: invalid input schema - " + b);
    return 1;
}

Write(unityOut, Emit.UnityDispatch(tools));
Write(daemonOut, Emit.DaemonCatalog(tools));
Write(docsOut, Emit.Docs(tools));

Console.WriteLine($"toolgen: {tools.Count} tools -> ToolDispatch.g.cs, ToolCatalog.g.cs, docs/TOOLS.md");

// The quality bar (§7.4) is enforced here rather than in a review checklist.
var noExample = tools.Where(t => t.Examples.Count == 0).Select(t => t.Id).ToArray();
if (noExample.Length > 0)
{
    Console.Error.WriteLine("toolgen: tools without an input example: " + string.Join(", ", noExample));
    return 1;
}
var noUndoStory = tools.Where(t => t.Mutating && t.Undo is null && t.NoUndoReason is null).Select(t => t.Id).ToArray();
if (noUndoStory.Length > 0)
{
    Console.Error.WriteLine("toolgen: mutating tools with neither Undo nor NoUndoReason: " + string.Join(", ", noUndoStory));
    return 1;
}
return 0;

static void Write(string path, string content)
{
    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
    var normalised = content.Replace("\r\n", "\n");
    if (File.Exists(path) && File.ReadAllText(path).Replace("\r\n", "\n") == normalised) return;
    File.WriteAllText(path, normalised, new UTF8Encoding(false));
}

static string FindRepoRoot()
{
    var d = new DirectoryInfo(AppContext.BaseDirectory);
    while (d is not null && !File.Exists(Path.Combine(d.FullName, "UNITY_MCP_TOOL_PLAN.md"))) d = d.Parent;
    return d?.FullName ?? Directory.GetCurrentDirectory();
}

static class Args
{
    public static string? Get(string name)
    {
        var a = Environment.GetCommandLineArgs();
        for (int i = 0; i < a.Length - 1; i++) if (a[i] == name) return a[i + 1];
        return null;
    }
}
