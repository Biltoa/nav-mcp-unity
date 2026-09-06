using System.Text.Json.Nodes;

namespace Umcp.Gui.Services;

/// <summary>
/// What the window remembers: ports, profile, and whether it should start the server by itself.
///
/// It shares <c>settings.json</c> with the daemon's linked-project list rather than opening a
/// second file, and it never rewrites keys it does not own — two writers, one file, and neither
/// allowed to erase the other's work.
/// </summary>
public sealed class GuiSettings
{
    readonly string _file = UmcpPaths.SettingsFile;

    public int HttpPort { get; set; } = 8730;
    public int AgentPort { get; set; } = 8731;
    public string Profile { get; set; } = "standard";
    public bool StartServerOnLaunch { get; set; } = true;
    public bool StopServerOnExit { get; set; }
    public bool MinimiseToTray { get; set; } = true;

    public static GuiSettings Load()
    {
        var s = new GuiSettings();
        try
        {
            if (!File.Exists(s._file)) return s;
            if (JsonNode.Parse(File.ReadAllText(s._file))?["gui"] is not JsonObject gui) return s;

            s.HttpPort = (int?)gui["httpPort"] ?? s.HttpPort;
            s.AgentPort = (int?)gui["agentPort"] ?? s.AgentPort;
            s.Profile = (string?)gui["profile"] ?? s.Profile;
            s.StartServerOnLaunch = (bool?)gui["startServerOnLaunch"] ?? s.StartServerOnLaunch;
            s.StopServerOnExit = (bool?)gui["stopServerOnExit"] ?? s.StopServerOnExit;
            s.MinimiseToTray = (bool?)gui["minimiseToTray"] ?? s.MinimiseToTray;
        }
        catch { /* a settings file we cannot read costs preferences, never a launch */ }
        return s;
    }

    public void Save()
    {
        try
        {
            UmcpPaths.EnsureCreated();

            JsonObject root;
            try { root = (File.Exists(_file) ? JsonNode.Parse(File.ReadAllText(_file)) as JsonObject : null) ?? new(); }
            catch { root = new(); }

            root["gui"] = new JsonObject
            {
                ["httpPort"] = HttpPort,
                ["agentPort"] = AgentPort,
                ["profile"] = Profile,
                ["startServerOnLaunch"] = StartServerOnLaunch,
                ["stopServerOnExit"] = StopServerOnExit,
                ["minimiseToTray"] = MinimiseToTray
            };

            var temp = _file + ".tmp";
            File.WriteAllText(temp, root.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }) + "\n");
            File.Move(temp, _file, overwrite: true);
        }
        catch { /* saving preferences must never take the app down */ }
    }
}
