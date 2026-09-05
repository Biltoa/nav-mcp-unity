using System.Text.Json.Nodes;
using Umcp.Daemon.Fleet;
using Umcp.Daemon.Generated;
using Umcp.Daemon.Mcp;
using Xunit;

namespace Umcp.Tests;

public class EditorInstallTests
{
    static IReadOnlyList<EditorInstall> Installs(params string[] versions) =>
        versions.Select(v => new EditorInstall(v, $@"E:\Unity Editor\{v}\Editor\Unity.exe", @"E:\Unity Editor")).ToArray();

    [Fact]
    public void An_exact_version_wins()
    {
        var (install, match) = EditorInstalls.Choose(Installs("6000.0.58f2", "6000.3.0f1", "6000.3.10f1"), "6000.3.0f1");
        Assert.Equal("6000.3.0f1", install!.Version);
        Assert.Equal("exact", match);
    }

    [Fact]
    public void Without_an_exact_match_the_newest_of_the_same_minor_is_used()
    {
        var (install, match) = EditorInstalls.Choose(Installs("6000.3.0f1", "6000.3.10f1", "6000.0.58f2"), "6000.3.4f1");
        Assert.Equal("6000.3.10f1", install!.Version);
        Assert.Equal("same-minor", match);
    }

    [Fact]
    public void Patch_numbers_compare_numerically_not_lexically()
    {
        // "6000.3.10f1" sorts before "6000.3.9f1" as text. That is how a fleet ends up opening a
        // project in an older Editor than it asked for.
        var (install, _) = EditorInstalls.Choose(Installs("6000.3.9f1", "6000.3.10f1"), "6000.3.20f1");
        Assert.Equal("6000.3.10f1", install!.Version);
    }

    [Fact]
    public void A_different_major_falls_back_no_further_than_the_same_major()
    {
        var (install, match) = EditorInstalls.Choose(Installs("6000.3.10f1"), "2022.3.1f1");
        Assert.Null(install);
        Assert.Equal("none", match);
    }

    [Fact]
    public void An_unparseable_version_does_not_throw()
    {
        Assert.Equal((0, 0, 0, 'a', 0), EditorInstalls.ParseVersion("not-a-version"));
    }

    [Fact]
    public void Project_version_is_read_from_ProjectVersion_txt()
    {
        var dir = TempProject();
        try
        {
            Assert.Equal("6000.3.10f1", EditorInstalls.ProjectVersion(dir));
        }
        finally { Directory.Delete(dir, true); }
    }

    internal static string TempProject()
    {
        var dir = Path.Combine(Path.GetTempPath(), "umcp-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(dir, "Assets"));
        Directory.CreateDirectory(Path.Combine(dir, "ProjectSettings"));
        Directory.CreateDirectory(Path.Combine(dir, "Packages"));
        File.WriteAllText(Path.Combine(dir, "ProjectSettings", "ProjectVersion.txt"),
            "m_EditorVersion: 6000.3.10f1\nm_EditorVersionWithRevision: 6000.3.10f1 (e35f0c77bd8e)\n");
        File.WriteAllText(Path.Combine(dir, "Packages", "manifest.json"),
            "{\n  \"dependencies\": {\n    \"com.unity.ide.rider\": \"3.0.36\"\n  }\n}\n");
        return dir;
    }
}

public class EditorLauncherTests
{
    [Fact]
    public void A_path_with_spaces_is_quoted()
    {
        // The failure this exists for: '-projectPath', 'E:\Unity Workspaces\Gameplay Portfolio'
        // passed unquoted reached Unity as three arguments and it exited 0 without opening.
        var args = EditorLauncher.BuildArguments(@"E:\Unity Workspaces\Gameplay Portfolio");
        var rendered = EditorLauncher.Render(args);
        Assert.Contains("\"", rendered);
        Assert.Contains("Gameplay Portfolio", rendered);
        Assert.DoesNotContain("-projectPath E:\\Unity Workspaces\\Gameplay", rendered);
    }

    [Fact]
    public void A_path_without_spaces_is_left_alone()
    {
        Assert.Equal(@"E:\Projects\Game", EditorLauncher.Quote(@"E:\Projects\Game"));
    }

    [Fact]
    public void Api_update_prompt_is_suppressed_by_default()
    {
        Assert.Contains("-accept-apiupdate", EditorLauncher.BuildArguments(@"C:\p"));
        Assert.DoesNotContain("-accept-apiupdate", EditorLauncher.BuildArguments(@"C:\p", acceptApiUpdate: false));
    }

    [Theory]
    [InlineData("\"E:\\Unity Editor\\Unity.exe\" -projectPath \"E:\\Unity Workspaces\\Gameplay Portfolio\" -accept-apiupdate",
                "E:/Unity Workspaces/Gameplay Portfolio")]
    [InlineData("Unity.exe -projectPath E:\\Projects\\Game", "E:/Projects/Game")]
    public void The_project_path_is_recoverable_from_a_command_line(string commandLine, string expected)
    {
        Assert.Equal(expected, EditorLauncher.ExtractProjectPath(commandLine));
    }

    [Fact]
    public void A_command_line_without_projectPath_yields_null()
    {
        Assert.Null(EditorLauncher.ExtractProjectPath("Unity.exe -batchmode -quit"));
    }

    [Fact]
    public void Read_back_of_this_process_finds_its_own_command_line()
    {
        // Proves the WMI read-back works at all on this machine; without it, verification would
        // silently return "unverified" forever and the quoting check would be decorative.
        var cmd = EditorLauncher.CommandLineOf(Environment.ProcessId);
        if (cmd is null) return;   // WMI unavailable — an honest skip, not a failure
        Assert.NotEqual("", cmd.Trim());
    }
}

public class AgentPackageTests
{
    [Fact]
    public void Ensure_adds_a_file_dependency_and_backs_the_manifest_up()
    {
        var project = EditorInstallTests.TempProject();
        var package = Path.Combine(Path.GetTempPath(), "umcp-pkg-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(package);
        File.WriteAllText(Path.Combine(package, "package.json"), "{\"name\":\"com.umcp.agent\"}");
        try
        {
            Assert.False(AgentPackage.IsReferenced(project));

            var (changed, detail) = AgentPackage.Ensure(project, package);
            Assert.True(changed);
            Assert.Contains("com.umcp.agent", detail);
            Assert.True(AgentPackage.IsReferenced(project));
            Assert.True(File.Exists(AgentPackage.ManifestPath(project) + AgentPackage.ManifestBackupSuffix));

            // Every pre-existing dependency survives: a manifest we rewrote badly is a broken project.
            var deps = JsonNode.Parse(File.ReadAllText(AgentPackage.ManifestPath(project)))!["dependencies"]!;
            Assert.Equal("3.0.36", (string?)deps["com.unity.ide.rider"]);

            // Idempotent.
            Assert.False(AgentPackage.Ensure(project, package).Changed);

            Assert.True(AgentPackage.Remove(project));
            Assert.False(AgentPackage.IsReferenced(project));
        }
        finally
        {
            Directory.Delete(project, true);
            Directory.Delete(package, true);
        }
    }

    [Fact]
    public void Resolved_means_packages_lock_json_names_it_not_that_we_wrote_the_manifest()
    {
        var project = EditorInstallTests.TempProject();
        var package = Path.Combine(Path.GetTempPath(), "umcp-pkg-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(package);
        File.WriteAllText(Path.Combine(package, "package.json"), "{\"name\":\"com.umcp.agent\"}");
        try
        {
            AgentPackage.Ensure(project, package);
            Assert.True(AgentPackage.IsReferenced(project));
            Assert.False(AgentPackage.IsResolved(project));   // Unity has not run yet

            File.WriteAllText(Path.Combine(project, "Packages", "packages-lock.json"),
                "{\"dependencies\":{\"com.umcp.agent\":{\"version\":\"file:...\"}}}");
            Assert.True(AgentPackage.IsResolved(project));
        }
        finally
        {
            Directory.Delete(project, true);
            Directory.Delete(package, true);
        }
    }

    [Fact]
    public void A_missing_manifest_is_reported_not_created()
    {
        var dir = Path.Combine(Path.GetTempPath(), "umcp-empty-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var (changed, detail) = AgentPackage.Ensure(dir, dir);
            Assert.False(changed);
            Assert.Contains("manifest.json", detail);
        }
        finally { Directory.Delete(dir, true); }
    }
}

public class ProjectCatalogTests
{
    [Fact]
    public void A_directory_without_Assets_is_not_a_project()
    {
        Assert.False(ProjectCatalog.IsProject(Path.GetTempPath()));
        var p = EditorInstallTests.TempProject();
        try { Assert.True(ProjectCatalog.IsProject(p)); }
        finally { Directory.Delete(p, true); }
    }

    [Fact]
    public void Describe_reads_version_identity_and_agent_state()
    {
        var p = EditorInstallTests.TempProject();
        try
        {
            File.WriteAllText(Path.Combine(p, "ProjectSettings", "UnityMCP.json"),
                "{\"projectId\":\"abc123\",\"daemonPort\":8731,\"enabled\":true}");
            var described = ProjectCatalog.Describe(p);
            Assert.Equal("abc123", described.ProjectId);
            Assert.Equal("6000.3.10f1", described.EditorVersion);
            Assert.False(described.AgentInstalled);
            Assert.False(described.LockedByEditor);
        }
        finally { Directory.Delete(p, true); }
    }

    [Fact]
    public void A_stale_lockfile_nobody_holds_does_not_read_as_open()
    {
        var p = EditorInstallTests.TempProject();
        try
        {
            Directory.CreateDirectory(Path.Combine(p, "Temp"));
            File.WriteAllText(Path.Combine(p, "Temp", "UnityLockfile"), "");
            Assert.False(ProjectCatalog.IsLocked(p));   // exists, but unheld: a crash leaves this behind

            using var held = new FileStream(Path.Combine(p, "Temp", "UnityLockfile"),
                FileMode.Open, FileAccess.Write, FileShare.None);
            Assert.True(ProjectCatalog.IsLocked(p));
        }
        finally { Directory.Delete(p, true); }
    }
}

public class CrashLoopBreakerTests
{
    [Fact]
    public void Two_restarts_are_allowed_and_the_third_is_refused()
    {
        var breaker = new CrashLoopBreaker();
        Assert.True(breaker.TryRestart("p", out _));
        Assert.True(breaker.TryRestart("p", out _));
        Assert.False(breaker.TryRestart("p", out var refusal));
        Assert.Contains("Crash-loop breaker", refusal);
        Assert.Equal(0, breaker.Remaining("p"));
    }

    [Fact]
    public void The_window_slides_so_an_editor_that_behaves_for_ten_minutes_is_recoverable_again()
    {
        var now = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        var breaker = new CrashLoopBreaker { Now = () => now };
        Assert.True(breaker.TryRestart("p", out _));
        Assert.True(breaker.TryRestart("p", out _));
        Assert.False(breaker.TryRestart("p", out _));

        now = now.AddMinutes(11);
        Assert.True(breaker.TryRestart("p", out _));
    }

    [Fact]
    public void One_bad_project_does_not_block_recovery_of_another()
    {
        var breaker = new CrashLoopBreaker();
        breaker.TryRestart("bad", out _);
        breaker.TryRestart("bad", out _);
        Assert.False(breaker.TryRestart("bad", out _));
        Assert.True(breaker.TryRestart("good", out _));
    }

    [Fact]
    public void A_clean_start_clears_the_history()
    {
        var breaker = new CrashLoopBreaker();
        breaker.TryRestart("p", out _);
        breaker.TryRestart("p", out _);
        breaker.Reset("p");
        Assert.Equal(2, breaker.Remaining("p"));
        Assert.True(breaker.TryRestart("p", out _));
    }
}

/// <summary>
/// Phase 5 gates: the ones that would have caught the defects Phase 5 found in itself.
/// </summary>
public class CatalogReachabilityTests
{
    static readonly SkillTree Tree = new();

    [Fact]
    public void Every_tool_is_reachable_from_at_least_one_skill_node()
    {
        // The smoke harness reached 60 of 63 tools on its first run: scene.mark, scene.diff and
        // scene.validate existed in the catalog but were missing from scene.md's explicit tool
        // list, so nothing that walks the tree could ever find them. A tool nobody can discover
        // is, for an agent, a tool that does not exist.
        var reachable = Tree.Nodes.SelectMany(n => Tree.ToolsFor(n)).Select(t => t.Id).ToHashSet();
        var missing = ToolCatalog.All.Select(t => t.Id).Where(id => !reachable.Contains(id)).ToArray();

        Assert.True(missing.Length == 0,
            "Not reachable from any skill node: " + string.Join(", ", missing));
    }

    [Fact]
    public void Every_input_schema_is_parseable_json()
    {
        // A C# `0f` default emitted straight into a schema is not valid JSON, and the failure
        // surfaced two layers away as a JsonReaderException with no tool name in it.
        foreach (var tool in ToolCatalog.All)
        {
            var exception = Record.Exception(() => JsonNode.Parse(tool.InputSchema));
            Assert.True(exception is null, tool.Id + ": " + exception?.Message);
        }
    }

    [Fact]
    public void A_guidance_only_node_says_so_explicitly()
    {
        var guidanceOnly = Tree.Nodes.Where(SkillTree.IsGuidanceOnly).Select(n => n.Id).ToArray();
        Assert.Contains("script", guidanceOnly);
        Assert.Contains("fleet", guidanceOnly);
    }
}
