using Umcp.MainThreadCheck;

// umcp-mainthread-check — the one hard rule the compiler cannot enforce.
//
//   Usage: umcp-mainthread-check [--root <repo>] [--source <dir>]
//
// Exit code 1 on any finding, so CI fails on it rather than logging it.

var root = Arg("--root") ?? FindRepoRoot();
var source = Arg("--source") ?? Path.Combine(root, "unity", "com.umcp.agent", "Editor");

if (!Directory.Exists(source))
{
    Console.Error.WriteLine($"mainthread-check: no source directory at {source}");
    return 2;
}

var findings = Checker.Run(source);
foreach (var finding in findings) Console.Error.WriteLine("mainthread-check: " + finding);

Console.WriteLine(findings.Count == 0
    ? $"mainthread-check: clean — no Unity API calls on off-main-thread paths under {Path.GetFileName(source)}"
    : $"mainthread-check: {findings.Count} finding(s)");

return findings.Count == 0 ? 0 : 1;

static string? Arg(string name)
{
    var args = Environment.GetCommandLineArgs();
    for (var i = 0; i < args.Length - 1; i++) if (args[i] == name) return args[i + 1];
    return null;
}

static string FindRepoRoot()
{
    var dir = new DirectoryInfo(AppContext.BaseDirectory);
    while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "UNITY_MCP_TOOL_PLAN.md"))) dir = dir.Parent;
    return dir?.FullName ?? Directory.GetCurrentDirectory();
}
