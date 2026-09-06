using System.Text.Json.Nodes;
using Umcp.Daemon.Api;
using Umcp.Gui.Services;
using Umcp.Gui.ViewModels;
using Xunit;

namespace Umcp.Tests;

/// <summary>
/// The desktop app's own logic: the list it remembers, the config files it edits on the user's
/// behalf, and the sentences it shows instead of protocol words.
///
/// Everything here runs without a UI thread on purpose — the parts that can be wrong while looking
/// right (a rewritten config that drops someone's other MCP servers, a green dot over an Editor
/// showing a modal dialog) are all decisions, not drawing.
/// </summary>
public class LinkedProjectsTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), "umcp-tests-" + Guid.NewGuid().ToString("N"));

    string File_ => Path.Combine(_dir, "settings.json");

    public void Dispose()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); } catch { }
    }

    [Fact]
    public void A_linked_project_survives_a_restart()
    {
        new LinkedProjects(File_).Add(@"D:\Games\Alpha");
        Assert.Single(new LinkedProjects(File_).All());
    }

    [Fact]
    public void Linking_the_same_project_twice_does_not_duplicate_it()
    {
        var store = new LinkedProjects(File_);
        store.Add(@"D:\Games\Alpha");
        store.Add(@"D:\Games\Alpha");
        Assert.Single(store.All());
    }

    [Fact]
    public void Other_keys_in_the_settings_file_are_left_alone()
    {
        // The GUI and the daemon share this file. Either one erasing the other's keys would lose
        // the user's ports or the user's project list, silently.
        Directory.CreateDirectory(_dir);
        System.IO.File.WriteAllText(File_, """{ "gui": { "httpPort": 8999 } }""");

        new LinkedProjects(File_).Add(@"D:\Games\Alpha");

        var root = JsonNode.Parse(System.IO.File.ReadAllText(File_))!;
        Assert.Equal(8999, (int?)root["gui"]!["httpPort"]);
        Assert.Single((root["projects"] as JsonArray)!);
    }

    [Fact]
    public void A_corrupt_settings_file_costs_the_list_and_not_the_daemon()
    {
        Directory.CreateDirectory(_dir);
        System.IO.File.WriteAllText(File_, "{ this is not json");

        var store = new LinkedProjects(File_);
        Assert.Empty(store.All());
        store.Add(@"D:\Games\Alpha");          // must not throw
        Assert.Single(store.All());
    }

    [Fact]
    public void Unlinking_removes_only_that_project()
    {
        var store = new LinkedProjects(File_);
        store.Add(@"D:\Games\Alpha");
        store.Add(@"D:\Games\Beta");

        Assert.True(store.Remove(@"D:\Games\Alpha"));
        Assert.Equal(@"D:/Games/Beta", store.All().Single().Path);
    }
}

public class ClientRegistrationTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), "umcp-tests-" + Guid.NewGuid().ToString("N"));

    McpClient Client => new("Test Client", Path.Combine(_dir, "mcp.json"), "Restart it.");
    const string Shim = @"C:\Program Files\UnityMCP\umcp-stdio.exe";

    public void Dispose()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); } catch { }
    }

    [Fact]
    public void Connecting_writes_an_entry_that_reads_back_as_connected()
    {
        var (ok, _) = ClientRegistrations.Register(Client, Shim, 8730);
        Assert.True(ok);
        Assert.True(ClientRegistrations.IsRegistered(Client, Shim, 8730));
    }

    [Fact]
    public void A_different_port_is_not_reported_as_connected()
    {
        ClientRegistrations.Register(Client, Shim, 8730);
        Assert.False(ClientRegistrations.IsRegistered(Client, Shim, 8999));
    }

    [Fact]
    public void The_users_other_servers_are_preserved()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Client.ConfigPath, """
            { "mcpServers": { "github": { "command": "gh-mcp" } }, "theme": "dark" }
            """);

        ClientRegistrations.Register(Client, Shim, 8730);

        var root = JsonNode.Parse(File.ReadAllText(Client.ConfigPath))!;
        Assert.Equal("gh-mcp", (string?)root["mcpServers"]!["github"]!["command"]);
        Assert.Equal("dark", (string?)root["theme"]);
    }

    [Fact]
    public void The_original_config_is_backed_up_before_the_first_edit()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Client.ConfigPath, """{ "mcpServers": { "github": { "command": "gh-mcp" } } }""");

        ClientRegistrations.Register(Client, Shim, 8730);

        Assert.True(File.Exists(Client.ConfigPath + ".umcp-backup"));
        Assert.Contains("gh-mcp", File.ReadAllText(Client.ConfigPath + ".umcp-backup"));
    }

    [Fact]
    public void A_config_that_is_not_valid_json_is_refused_rather_than_rewritten()
    {
        // Rewriting it would silently delete every MCP server the user has configured. Refusing
        // and saying so is the only safe answer.
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Client.ConfigPath, "{ not json at all");

        var (ok, message) = ClientRegistrations.Register(Client, Shim, 8730);

        Assert.False(ok);
        Assert.Contains("left alone", message);
        Assert.Equal("{ not json at all", File.ReadAllText(Client.ConfigPath));
    }

    [Fact]
    public void Disconnecting_removes_our_entry_and_nothing_else()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Client.ConfigPath, """{ "mcpServers": { "github": { "command": "gh-mcp" } } }""");
        ClientRegistrations.Register(Client, Shim, 8730);

        ClientRegistrations.Unregister(Client);

        var servers = JsonNode.Parse(File.ReadAllText(Client.ConfigPath))!["mcpServers"]!;
        Assert.Null(servers[ClientRegistrations.ServerKey]);
        Assert.NotNull(servers["github"]);
    }

    [Fact]
    public void The_written_entry_carries_no_token()
    {
        // The registration points at the shim, which reads the token from disk at spawn time.
        // A token in a config file goes stale on the next server restart, and config files get
        // pasted into bug reports.
        ClientRegistrations.Register(Client, Shim, 8730);
        Assert.DoesNotContain("token", File.ReadAllText(Client.ConfigPath), StringComparison.OrdinalIgnoreCase);
    }
}

public class ProjectRowTests
{
    static JsonObject Project(bool exists = true, bool isProject = true, bool referenced = true,
                              bool resolved = true, bool open = false, bool connected = false,
                              string? projectId = "abc") => new()
    {
        ["path"] = @"D:/Games/Alpha",
        ["exists"] = exists,
        ["isProject"] = isProject,
        ["agentInstalled"] = referenced,
        ["agentResolved"] = resolved,
        ["open"] = open,
        ["connected"] = connected,
        ["projectId"] = projectId,
        ["editorVersion"] = "6000.0.58f2"
    };

    static JsonObject Editor(string health = "ok", bool reloading = false, bool compiling = false,
                             string? dialog = null)
    {
        var editor = new JsonObject { ["health"] = health, ["reloading"] = reloading, ["compiling"] = compiling };
        if (dialog is not null) editor["blockingDialog"] = dialog;
        return editor;
    }

    [Fact]
    public void A_connected_healthy_editor_is_ready()
    {
        var row = new ProjectRow(@"D:/Games/Alpha");
        row.Apply(Project(open: true, connected: true), Editor());
        Assert.Equal("Ready", row.Status);
    }

    [Fact]
    public void A_modal_dialog_is_named_and_says_who_has_to_act()
    {
        // "E_EDITOR_BLOCKED" is accurate and useless. The dialog has to be dismissed by a human,
        // and the window is where that human is looking.
        var row = new ProjectRow(@"D:/Games/Alpha");
        row.Apply(Project(open: true, connected: true), Editor("blocked", dialog: "Recovering Scene Backups"));

        Assert.Equal("Waiting on you", row.Status);
        Assert.Contains("Recovering Scene Backups", row.Detail);
    }

    [Fact]
    public void An_open_editor_that_has_not_resolved_the_package_says_to_click_Unity()
    {
        // Unity re-reads its package list at startup, or when its window regains focus. Nobody
        // guesses that, and without it the link looks broken.
        var row = new ProjectRow(@"D:/Games/Alpha");
        row.Apply(Project(open: true, resolved: false), editor: null);

        Assert.Equal("Click the Unity window", row.Status);
        Assert.Contains("click its window once", row.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_linked_project_with_no_editor_can_be_opened()
    {
        var row = new ProjectRow(@"D:/Games/Alpha");
        row.Apply(Project(), editor: null);

        Assert.Equal("Unity not open", row.Status);
        Assert.True(row.CanOpen);
    }

    [Fact]
    public void A_project_whose_folder_is_gone_says_so_in_red()
    {
        var row = new ProjectRow(@"D:/Games/Alpha");
        row.Apply(Project(exists: false), editor: null);

        Assert.Equal("Folder missing", row.Status);
        Assert.Equal("#EF4444", row.Badge);
        Assert.False(row.CanOpen);
    }

    [Fact]
    public void A_project_whose_manifest_no_longer_names_the_package_is_not_linked()
    {
        var row = new ProjectRow(@"D:/Games/Alpha");
        row.Apply(Project(referenced: false), editor: null);
        Assert.Equal("Not linked", row.Status);
    }

    [Fact]
    public void When_the_server_stops_no_project_keeps_a_green_dot()
    {
        var row = new ProjectRow(@"D:/Games/Alpha");
        row.Apply(Project(open: true, connected: true), Editor());
        row.ApplyServerDown();

        Assert.Equal("Server stopped", row.Status);
        Assert.False(row.Connected);
        Assert.Equal("#6B7280", row.Badge);
    }
}
