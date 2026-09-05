using System.Diagnostics;
using System.Text.Json.Nodes;
using Umcp.Agent;

namespace Umcp.Daemon.Mirror;

public sealed class MirrorNode
{
    public required int Id { get; init; }
    public string Name { get; set; } = "";
    public int Parent { get; set; }
    public int SiblingIndex { get; set; }
    public int Flags { get; set; }
    public string Tag { get; set; } = "";
    public string Layer { get; set; } = "";
    /// <summary>Pipe-separated component type names, exactly as the Editor serialised them.</summary>
    public string Components { get; set; } = "";
    public string? Scene { get; set; }
    public long Revision { get; set; }

    public bool ActiveSelf => (Flags & 1) != 0;

    /// <summary>
    /// Derived, not mirrored. Deactivating a parent changes this for every descendant, and Unity
    /// publishes no event for those descendants — so the only way to hold it correctly is to
    /// compute it from the ancestor chain on demand.
    /// </summary>
    public bool ActiveInHierarchy(SceneMirror mirror)
    {
        var node = this;
        var guard = 0;
        while (guard++ < 512)
        {
            if (!node.ActiveSelf) return false;
            if (node.Parent == 0 || !mirror.TryGet(node.Parent, out var parent)) return true;
            node = parent;
        }
        return true;
    }

    public readonly List<int> Children = new();

    public bool HasComponent(string typeName)
    {
        if (Components.Length == 0) return false;
        var span = Components.AsSpan();
        var start = 0;
        while (start <= span.Length)
        {
            var next = span[start..].IndexOf('|');
            var end = next < 0 ? span.Length : start + next;
            if (span[start..end].Equals(typeName, StringComparison.OrdinalIgnoreCase)) return true;
            if (next < 0) break;
            start = end + 1;
        }
        return false;
    }

    public string[] ComponentList =>
        Components.Length == 0 ? Array.Empty<string>() : Components.Split('|');

    public uint Hash(SceneMirror mirror)
    {
        var h = MirrorHash.Node(Name, SiblingIndex, Flags, Tag, Layer, Components);
        foreach (var childId in ChildrenInOrder(mirror))
            if (mirror.TryGet(childId, out var child)) h = MirrorHash.Fold(h, child.Hash(mirror));
        return h;
    }

    public IEnumerable<int> ChildrenInOrder(SceneMirror mirror) =>
        Children.Select(id => mirror.TryGet(id, out var n) ? n : null)
                .Where(n => n is not null)
                .OrderBy(n => n!.SiblingIndex)
                .Select(n => n!.Id);
}

/// <summary>
/// The daemon's live model of one project's scene hierarchy.
///
/// The point of it: reads are answered here, so they never wait for an Editor tick and never
/// serialise a whole scene. Half of an agent's calls are reads, and without a mirror every one of
/// them fails during a recompile.
///
/// A cache that silently lies is worse than no cache, so every answer carries its provenance —
/// <c>source</c>, <c>staleMs</c>, <c>epoch</c>, <c>revision</c> — a periodic hash reconcile
/// compares the model against the live hierarchy, and any change kind the Editor does not model
/// triggers a resync rather than being treated as "nothing happened".
/// </summary>
public sealed class SceneMirror
{
    readonly Dictionary<int, MirrorNode> _nodes = new();
    readonly Stopwatch _clock = Stopwatch.StartNew();
    readonly object _gate = new();

    long _revision;
    long _lastUpdateMs;

    public int Epoch { get; private set; } = -1;
    public int Seq { get; private set; }
    public long Revision => Interlocked.Read(ref _revision);
    public int Count { get { lock (_gate) return _nodes.Count; } }
    public bool Seeded { get; private set; }
    public string? ActiveScene { get; private set; }

    /// <summary>How long since anything changed the model. Reported to callers as staleMs.</summary>
    public long StaleMs => _clock.ElapsedMilliseconds - Interlocked.Read(ref _lastUpdateMs);

    public int ResyncCount { get; private set; }
    public int ReconcileCount { get; private set; }
    public int DriftRepairs { get; private set; }

    public bool TryGet(int id, out MirrorNode node)
    {
        lock (_gate) return _nodes.TryGetValue(id, out node!);
    }

    public void Invalidate(string reason)
    {
        lock (_gate)
        {
            _nodes.Clear();
            Seeded = false;
            ResyncCount++;
        }
    }

    /// <summary>
    /// Replace the whole model from a snapshot. Instance ids do not survive a domain reload, so
    /// this is what happens after every reconnect — the mirror is rebuilt, not patched.
    /// </summary>
    public void Seed(JsonNode data, int epoch)
    {
        lock (_gate)
        {
            _nodes.Clear();
            Epoch = epoch;
            Seq = (int?)data["seq"] ?? 0;
            ActiveScene = (string?)data["activeScene"];

            if (data["nodes"] is JsonArray nodes)
                foreach (var record in nodes)
                    Upsert(record as JsonArray);

            Relink();
            Seeded = true;
            Touch();
        }
    }

    /// <summary>Apply one delta frame.</summary>
    public void Apply(JsonNode delta)
    {
        lock (_gate)
        {
            Seq = (int?)delta["seq"] ?? Seq;

            if (delta["removed"] is JsonArray removed)
                foreach (var idNode in removed)
                    RemoveSubtree((int?)idNode ?? 0);

            if (delta["nodes"] is JsonArray nodes)
                foreach (var record in nodes)
                    Upsert(record as JsonArray);

            Relink();
            Touch();
        }
    }

    void Touch()
    {
        Interlocked.Increment(ref _revision);
        Interlocked.Exchange(ref _lastUpdateMs, _clock.ElapsedMilliseconds);
    }

    void Upsert(JsonArray? r)
    {
        // [id, name, parent, sibling, flags, tag, layer, components, scene]
        if (r is null || r.Count < 8) return;
        var id = (int?)r[0] ?? 0;
        if (id == 0) return;

        if (!_nodes.TryGetValue(id, out var node))
        {
            node = new MirrorNode { Id = id };
            _nodes[id] = node;
        }

        node.Name = (string?)r[1] ?? "";
        node.Parent = (int?)r[2] ?? 0;
        node.SiblingIndex = (int?)r[3] ?? 0;
        node.Flags = (int?)r[4] ?? 0;
        node.Tag = (string?)r[5] ?? "";
        node.Layer = (string?)r[6] ?? "";
        node.Components = (string?)r[7] ?? "";
        node.Scene = r.Count > 8 ? (string?)r[8] : null;
        node.Revision = _revision + 1;
    }

    void RemoveSubtree(int id)
    {
        if (id == 0 || !_nodes.TryGetValue(id, out var node)) return;
        foreach (var childId in node.Children.ToArray()) RemoveSubtree(childId);
        _nodes.Remove(id);
    }

    void Relink()
    {
        foreach (var node in _nodes.Values) node.Children.Clear();
        foreach (var node in _nodes.Values)
        {
            if (node.Parent == 0) continue;
            if (_nodes.TryGetValue(node.Parent, out var parent)) parent.Children.Add(node.Id);
        }
        foreach (var node in _nodes.Values)
            node.Children.Sort((a, b) =>
                _nodes[a].SiblingIndex.CompareTo(_nodes[b].SiblingIndex));
    }

    public IReadOnlyList<MirrorNode> Roots()
    {
        lock (_gate)
            return _nodes.Values.Where(n => n.Parent == 0 || !_nodes.ContainsKey(n.Parent))
                                .OrderBy(n => n.SiblingIndex).ToArray();
    }

    public string Path(MirrorNode node)
    {
        lock (_gate)
        {
            var parts = new List<string> { node.Name };
            var current = node;
            var guard = 0;
            while (current.Parent != 0 && _nodes.TryGetValue(current.Parent, out var parent) && guard++ < 512)
            {
                parts.Insert(0, parent.Name);
                current = parent;
            }
            return string.Join("/", parts);
        }
    }

    public IReadOnlyList<MirrorNode> Snapshot()
    {
        lock (_gate) return _nodes.Values.ToArray();
    }

    /// <summary>Root-level hashes, in the shape the Editor's <c>mirror.hashes</c> returns.</summary>
    public (uint all, int count, Dictionary<string, (uint hash, int count)> roots) Hashes()
    {
        lock (_gate)
        {
            var roots = new Dictionary<string, (uint, int)>();
            var all = MirrorHash.Start();
            var total = 0;

            foreach (var root in Roots())
            {
                var h = root.Hash(this);
                var n = CountSubtree(root);
                total += n;
                all = MirrorHash.Fold(all, h);
                // Keyed by name+sibling rather than instance id: ids are not stable across a
                // domain reload, and a reconcile that only works before the first recompile is
                // not a reconcile.
                roots[root.Name + "#" + root.SiblingIndex] = (h, n);
            }
            return (all, total, roots);
        }
    }

    int CountSubtree(MirrorNode node)
    {
        var n = 1;
        foreach (var childId in node.Children)
            if (_nodes.TryGetValue(childId, out var child)) n += CountSubtree(child);
        return n;
    }


    /// <summary>
    /// Compare this model against a fresh snapshot, field by field, and report what differs.
    ///
    /// This exists so that "zero drift" is an auditable claim rather than an assertion. A hash
    /// mismatch says only that something is wrong; this says which node and which field, which is
    /// the difference between fixing a bug and guessing at one.
    /// </summary>
    public JsonArray DiffAgainst(JsonNode snapshot, int max = 8)
    {
        var report = new JsonArray();
        lock (_gate)
        {
            var live = new Dictionary<int, JsonArray>();
            if (snapshot["nodes"] is JsonArray nodes)
                foreach (var r in nodes)
                    if (r is JsonArray a && a.Count >= 8 && (int?)a[0] is int id) live[id] = a;

            foreach (var (id, r) in live)
            {
                if (report.Count >= max) break;
                if (!_nodes.TryGetValue(id, out var mine))
                {
                    report.Add(new JsonObject
                    {
                        ["id"] = id, ["name"] = (string?)r[1], ["issue"] = "missing from mirror"
                    });
                    continue;
                }

                var differences = new JsonArray();
                void Check(string field, object? liveValue, object? mineValue)
                {
                    if (Equals(liveValue?.ToString(), mineValue?.ToString())) return;
                    differences.Add(new JsonObject
                    {
                        ["field"] = field,
                        ["live"] = liveValue?.ToString(),
                        ["mirror"] = mineValue?.ToString()
                    });
                }

                Check("name", (string?)r[1], mine.Name);
                Check("parent", (int?)r[2], mine.Parent);
                Check("siblingIndex", (int?)r[3], mine.SiblingIndex);
                Check("flags", (int?)r[4], mine.Flags);
                Check("tag", (string?)r[5], mine.Tag);
                Check("layer", (string?)r[6], mine.Layer);
                Check("components", (string?)r[7], mine.Components);

                if (differences.Count > 0)
                    report.Add(new JsonObject
                    {
                        ["id"] = id,
                        ["name"] = (string?)r[1],
                        ["issue"] = "field mismatch",
                        ["fields"] = differences
                    });
            }

            foreach (var id in _nodes.Keys)
            {
                if (report.Count >= max) break;
                if (!live.ContainsKey(id))
                    report.Add(new JsonObject
                    {
                        ["id"] = id, ["name"] = _nodes[id].Name, ["issue"] = "stale: no longer in the scene"
                    });
            }
        }
        return report;
    }

    public void NoteReconcile(bool drifted)
    {
        ReconcileCount++;
        if (drifted) DriftRepairs++;
    }

    public JsonObject StatusJson() => new()
    {
        ["seeded"] = Seeded,
        ["epoch"] = Epoch,
        ["seq"] = Seq,
        ["revision"] = Revision,
        ["nodes"] = Count,
        ["staleMs"] = StaleMs,
        ["resyncs"] = ResyncCount,
        ["reconciles"] = ReconcileCount,
        ["driftRepairs"] = DriftRepairs
    };
}
