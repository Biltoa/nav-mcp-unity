using System.Text.Json.Nodes;

namespace Umcp.Gui.Services;

/// <summary>One AI client this machine might have, and where its MCP config lives.</summary>
public sealed record McpClient(string Name, string ConfigPath, string Hint)
{
    public bool ConfigExists => File.Exists(ConfigPath);
    public bool DirectoryExists => Directory.Exists(Path.GetDirectoryName(ConfigPath) ?? ".");
}

/// <summary>
/// Registering this server with the AI clients on the machine — the step a non-technical user
/// cannot be asked to do by hand, because it means editing JSON in a hidden folder.
///
/// The registration points at <c>umcp-stdio</c>, not at the HTTP port, and that is deliberate:
///
///   * the shim reads the bearer token from disk **at spawn time**, so a written config never
///     carries a token that goes stale the next time the server restarts and mints a new one;
///   * the shim starts the daemon if it is not running, so a client opened before this app still
///     works;
///   * nothing secret is written into a config file that people paste into bug reports.
///
/// Every write backs the original up first. These are the user's files, not ours.
/// </summary>
public static class ClientRegistrations
{
    public const string ServerKey = "unity";

    public static IReadOnlyList<McpClient> Known()
    {
        var home = UmcpPaths.Home();
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

        var claudeDesktop = OperatingSystem.IsMacOS()
            ? Path.Combine(home, "Library", "Application Support", "Claude", "claude_desktop_config.json")
            : Path.Combine(appData, "Claude", "claude_desktop_config.json");

        return new[]
        {
            new McpClient("Claude Desktop", claudeDesktop, "Restart Claude Desktop after connecting."),
            new McpClient("Claude Code", Path.Combine(home, ".claude.json"), "Applies to new Claude Code sessions."),
            new McpClient("Cursor", Path.Combine(home, ".cursor", "mcp.json"), "Restart Cursor after connecting.")
        };
    }

    /// <summary>The server entry itself: the shim, and the port this app is serving on.</summary>
    public static JsonObject Entry(string shimPath, int port) => new()
    {
        ["command"] = shimPath,
        ["args"] = new JsonArray("--port", port.ToString())
    };

    /// <summary>The whole snippet, for the Copy button and for clients not listed here.</summary>
    public static string Snippet(string shimPath, int port) =>
        new JsonObject { ["mcpServers"] = new JsonObject { [ServerKey] = Entry(shimPath, port) } }
            .ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true });

    public static string? LocateShim()
    {
        var name = UmcpPaths.ExeName("umcp-stdio");
        var candidates = new List<string>
        {
            Path.Combine(AppContext.BaseDirectory, name),
            Path.Combine(AppContext.BaseDirectory, "..", "Resources", name)
        };
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
            foreach (var configuration in new[] { "Debug", "Release" })
                candidates.Add(Path.Combine(d.FullName, "src", "Umcp.Stdio", "bin", configuration, "net8.0", name));

        foreach (var candidate in candidates)
        {
            try { if (File.Exists(candidate)) return Path.GetFullPath(candidate); } catch { }
        }
        return null;
    }

    /// <summary>Is this client already pointed at this port, through this shim?</summary>
    public static bool IsRegistered(McpClient client, string shimPath, int port)
    {
        try
        {
            if (!File.Exists(client.ConfigPath)) return false;
            if (JsonNode.Parse(File.ReadAllText(client.ConfigPath)) is not JsonObject root) return false;
            if (root["mcpServers"]?[ServerKey] is not JsonObject entry) return false;

            var command = (string?)entry["command"] ?? "";
            var args = (entry["args"] as JsonArray)?.Select(a => (string?)a ?? "").ToArray() ?? Array.Empty<string>();
            return string.Equals(Path.GetFullPath(command), Path.GetFullPath(shimPath), StringComparison.OrdinalIgnoreCase)
                   && args.Contains(port.ToString());
        }
        catch { return false; }
    }

    /// <summary>
    /// Add or update the entry, preserving every other server the user has configured. Returns a
    /// sentence to show them.
    /// </summary>
    public static (bool Ok, string Message) Register(McpClient client, string shimPath, int port)
    {
        try
        {
            var directory = Path.GetDirectoryName(client.ConfigPath);
            if (directory is not null) Directory.CreateDirectory(directory);

            JsonObject root;
            if (File.Exists(client.ConfigPath))
            {
                var text = File.ReadAllText(client.ConfigPath);
                try { root = JsonNode.Parse(text) as JsonObject ?? new JsonObject(); }
                catch (Exception e)
                {
                    // Refusing is right. Rewriting a config we could not parse would silently
                    // delete every other MCP server the user has.
                    return (false, $"{client.Name}'s config file is not valid JSON, so it was left alone: {e.Message}");
                }

                var backup = client.ConfigPath + ".umcp-backup";
                if (!File.Exists(backup)) File.Copy(client.ConfigPath, backup);
            }
            else root = new JsonObject();

            if (root["mcpServers"] is not JsonObject servers)
            {
                servers = new JsonObject();
                root["mcpServers"] = servers;
            }

            var existed = servers[ServerKey] is not null;
            servers[ServerKey] = Entry(shimPath, port);

            var temp = client.ConfigPath + ".tmp";
            File.WriteAllText(temp, root.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }) + "\n");
            File.Move(temp, client.ConfigPath, overwrite: true);

            return (true, existed
                ? $"Updated the Unity server in {client.Name}. {client.Hint}"
                : $"Added the Unity server to {client.Name}. {client.Hint}");
        }
        catch (Exception e)
        {
            return (false, $"Could not write {client.Name}'s config: {e.Message}");
        }
    }

    /// <summary>Remove our entry and nothing else.</summary>
    public static (bool Ok, string Message) Unregister(McpClient client)
    {
        try
        {
            if (!File.Exists(client.ConfigPath)) return (true, $"{client.Name} was not configured.");
            if (JsonNode.Parse(File.ReadAllText(client.ConfigPath)) is not JsonObject root ||
                root["mcpServers"] is not JsonObject servers || servers[ServerKey] is null)
                return (true, $"{client.Name} was not configured.");

            servers.Remove(ServerKey);
            var temp = client.ConfigPath + ".tmp";
            File.WriteAllText(temp, root.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }) + "\n");
            File.Move(temp, client.ConfigPath, overwrite: true);
            return (true, $"Removed the Unity server from {client.Name}. {client.Hint}");
        }
        catch (Exception e)
        {
            return (false, $"Could not update {client.Name}'s config: {e.Message}");
        }
    }
}
