using System.Text.Json.Nodes;
using Umcp.Agent;

namespace Umcp.Daemon.Mirror;

/// <summary>
/// Evaluate a <see cref="SceneSelector"/> against the mirror instead of the Editor.
///
/// The selector grammar is the shared file both sides compile, so "the same query" really is the
/// same query. What differs is what each side can see: the mirror holds identity, parentage,
/// active state, tag, layer and the component <em>type</em> list, but not component property
/// values. A query that asks for <c>Rigidbody.mass</c> is therefore not serviceable here and goes
/// live — and says so, rather than returning a confident subset.
/// </summary>
public static class MirrorQuery
{
    public sealed record Servability(bool CanServe, string? Reason);

    public static Servability CanServe(SceneMirror mirror, List<SceneSelector.Step> steps, string[] fields)
    {
        if (!mirror.Seeded) return new Servability(false, "mirror not seeded");

        foreach (var field in fields)
        {
            if (field.Contains('.'))
                return new Servability(false, $"field '{field}' is a component property, which the mirror does not hold");
            if (!SceneSelector.IsMirrorServiceableField(field))
                return new Servability(false, $"field '{field}' is not mirrored");
        }

        foreach (var step in steps)
            foreach (var p in step.Predicates)
            {
                if (!SceneSelector.IsMirrorServiceablePredicate(p.Key))
                    return new Servability(false, $"predicate '{p.Key}' is not mirrored");

                // An unknown component name must produce the Editor's E_TYPE_NOT_FOUND with
                // suggestions, not a silent empty result that looks like a true answer.
                if ((p.Key is "has" or "missing") && !KnownComponent(mirror, p.Arg!))
                    return new Servability(false, $"component type '{p.Arg}' is not present anywhere in the mirror");
            }

        return new Servability(true, null);
    }

    static bool KnownComponent(SceneMirror mirror, string typeName)
    {
        var shortName = typeName.Contains('.') ? typeName[(typeName.LastIndexOf('.') + 1)..] : typeName;
        foreach (var node in mirror.Snapshot())
            if (node.HasComponent(shortName)) return true;
        return false;
    }

    public static List<MirrorNode> Evaluate(SceneMirror mirror, List<SceneSelector.Step> steps, int maxDepth)
    {
        IEnumerable<MirrorNode> current = mirror.Roots();

        for (var s = 0; s < steps.Count; s++)
        {
            var step = steps[s];
            var next = new List<MirrorNode>();
            var seen = new HashSet<int>();

            foreach (var node in current)
            {
                if (s == 0)
                {
                    Collect(mirror, node, step, step.Descendant, maxDepth, 0, next, seen);
                }
                else
                {
                    foreach (var childId in node.ChildrenInOrder(mirror))
                        if (mirror.TryGet(childId, out var child))
                            Collect(mirror, child, step, step.Descendant, maxDepth, 0, next, seen);
                }
            }
            current = next;
            if (next.Count == 0) break;
        }
        return current.ToList();
    }

    static void Collect(SceneMirror mirror, MirrorNode node, SceneSelector.Step step, bool descend,
                        int maxDepth, int depth, List<MirrorNode> into, HashSet<int> seen)
    {
        if (maxDepth >= 0 && depth > maxDepth) return;

        if (Matches(mirror, node, step) && seen.Add(node.Id)) into.Add(node);

        if (!descend) return;
        foreach (var childId in node.ChildrenInOrder(mirror))
            if (mirror.TryGet(childId, out var child))
                Collect(mirror, child, step, true, maxDepth, depth + 1, into, seen);
    }

    static bool Matches(SceneMirror mirror, MirrorNode node, SceneSelector.Step step)
    {
        if (step.Name != "*" && node.Name != step.Name) return false;

        foreach (var p in step.Predicates)
        {
            var arg = p.Arg ?? "";
            var shortArg = arg.Contains('.') ? arg[(arg.LastIndexOf('.') + 1)..] : arg;

            var ok = p.Key switch
            {
                "active" => node.ActiveInHierarchy(mirror),
                "inactive" => !node.ActiveInHierarchy(mirror),
                "root" => node.Parent == 0 || !mirror.TryGet(node.Parent, out _),
                "leaf" => node.Children.Count == 0,
                "has" => node.HasComponent(shortArg),
                "missing" => !node.HasComponent(shortArg),
                "tag" => string.Equals(node.Tag, arg, StringComparison.Ordinal),
                "layer" => string.Equals(node.Layer, arg, StringComparison.Ordinal),
                "name*" => node.Name.Contains(arg, StringComparison.OrdinalIgnoreCase),
                "name^" => node.Name.StartsWith(arg, StringComparison.OrdinalIgnoreCase),
                "name$" => node.Name.EndsWith(arg, StringComparison.OrdinalIgnoreCase),
                _ => false
            };
            if (!ok) return false;
        }
        return true;
    }

    public static JsonObject Project(SceneMirror mirror, MirrorNode node, string[] fields)
    {
        var o = new JsonObject();
        foreach (var field in fields)
        {
            JsonNode? value = field switch
            {
                "id" => node.Id,
                "name" => node.Name,
                "path" => mirror.Path(node),
                "active" => node.ActiveSelf,
                "activeInHierarchy" => node.ActiveInHierarchy(mirror),
                "tag" => node.Tag,
                "layer" => node.Layer,
                "parent" => mirror.TryGet(node.Parent, out var parent) ? parent.Name : null,
                "childCount" => node.Children.Count,
                "components" => new JsonArray(node.ComponentList.Select(c => (JsonNode)c!).ToArray()),
                _ => null
            };
            if (value is not null) o[field] = value;
        }
        return o;
    }
}
