using System.Text.Json.Nodes;
using Umcp.Daemon.Fleet;

namespace Umcp.Daemon.Api;

/// <summary>
/// The projects a human has linked to this daemon, remembered across restarts.
///
/// The fleet knows about projects whose Editor has connected at least once. That is the wrong list
/// for a GUI: someone who links a project on Monday and opens Unity on Wednesday should still see
/// it on Tuesday. So the link is recorded here, in the daemon's own settings file, and the fleet
/// stays the record of what is actually running.
///
/// One writer at a time, and every read tolerates a corrupt file: a settings file that fails to
/// parse must cost the user their list, not their daemon.
/// </summary>
public sealed class LinkedProjects
{
    readonly object _gate = new();
    readonly string _file;

    public LinkedProjects() : this(Umcp.UmcpPaths.SettingsFile) { }

    /// <summary>The file is a parameter so a test can have its own, rather than the user's.</summary>
    public LinkedProjects(string file) => _file = file;

    public sealed record Entry(string Path, bool AutoRestart, DateTimeOffset LinkedAt);

    public IReadOnlyList<Entry> All()
    {
        lock (_gate) return Read();
    }

    public Entry Add(string path, bool autoRestart = false)
    {
        var normalised = EditorInstalls.Normalise(System.IO.Path.GetFullPath(path));
        lock (_gate)
        {
            var list = Read().Where(e => !Same(e.Path, normalised)).ToList();
            var entry = new Entry(normalised, autoRestart, DateTimeOffset.Now);
            list.Add(entry);
            Write(list);
            return entry;
        }
    }

    public bool Remove(string path)
    {
        var normalised = EditorInstalls.Normalise(System.IO.Path.GetFullPath(path));
        lock (_gate)
        {
            var list = Read();
            var kept = list.Where(e => !Same(e.Path, normalised)).ToList();
            if (kept.Count == list.Count) return false;
            Write(kept);
            return true;
        }
    }

    public bool SetAutoRestart(string path, bool on)
    {
        var normalised = EditorInstalls.Normalise(System.IO.Path.GetFullPath(path));
        lock (_gate)
        {
            var list = Read();
            var i = list.FindIndex(e => Same(e.Path, normalised));
            if (i < 0) return false;
            list[i] = list[i] with { AutoRestart = on };
            Write(list);
            return true;
        }
    }

    static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    List<Entry> Read()
    {
        try
        {
            if (!File.Exists(_file)) return new();
            if (JsonNode.Parse(File.ReadAllText(_file))?["projects"] is not JsonArray array) return new();

            var list = new List<Entry>();
            foreach (var node in array)
            {
                var path = (string?)node?["path"];
                if (string.IsNullOrWhiteSpace(path)) continue;
                list.Add(new Entry(
                    path!,
                    (bool?)node?["autoRestart"] ?? false,
                    DateTimeOffset.TryParse((string?)node?["linkedAt"], out var t) ? t : DateTimeOffset.Now));
            }
            return list;
        }
        catch { return new(); }
    }

    void Write(IReadOnlyList<Entry> entries)
    {
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(_file)!);

            // Preserve anything else in the file — the GUI keeps its own keys here.
            JsonObject root;
            try { root = (File.Exists(_file) ? JsonNode.Parse(File.ReadAllText(_file)) as JsonObject : null) ?? new(); }
            catch { root = new(); }

            var array = new JsonArray();
            foreach (var e in entries)
                array.Add(new JsonObject
                {
                    ["path"] = e.Path,
                    ["autoRestart"] = e.AutoRestart,
                    ["linkedAt"] = e.LinkedAt.ToString("O")
                });
            root["projects"] = array;

            // Write beside and move: a half-written settings file is how a tool loses a list it
            // was asked to remember.
            var temp = _file + ".tmp";
            File.WriteAllText(temp, root.ToJsonString(ProjectCatalog.Indented) + "\n");
            File.Move(temp, _file, overwrite: true);
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"[umcpd] warning: could not save {_file}: {e.Message}");
        }
    }
}
