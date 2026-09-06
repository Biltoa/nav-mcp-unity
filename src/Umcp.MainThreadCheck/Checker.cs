using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Umcp.MainThreadCheck;

public sealed record Finding(string File, int Line, int Column, string Method, string Expression, string Reason)
{
    public override string ToString() => $"{File}({Line},{Column}): {Method} calls {Expression} — {Reason}";
}

/// <summary>
/// "No Unity API off the main thread", enforced instead of remembered.
///
/// This is the rule that produced the most expensive bug of Phase 1. The hello frame was built on
/// the socket thread, where it read <c>Application.productName</c> and the project settings — both
/// main-thread-only. It threw silently inside the reader thread, tore the connection down, and
/// reconnected forever, with no log line and no visible cause. Nothing about that failure was
/// obvious from reading the method.
///
/// The check is syntactic, and works in three steps:
///
///   1. **Seeds.** A method handed to <c>new Thread(...)</c>, <c>Task.Run</c>,
///      <c>ThreadPool.QueueUserWorkItem</c>, or subscribed to Unity's threaded log callback.
///   2. **Callbacks into threaded types.** A method group passed to the constructor of a type that
///      itself owns a thread is a seed too — that is how this agent's own
///      <c>UmcpConnection(port, inbox, OnSocketConnected)</c> callback reaches the socket thread,
///      and a check that missed it would miss the shape of the original bug.
///   3. **Closure.** From every seed, calls to other methods of the same type.
///
/// Inside that closure, any use of a known Unity API root is a finding. It will not catch a call
/// reached through an interface or an event added at runtime; it catches the class of mistake that
/// has actually happened here, at zero runtime cost, and it fails the build.
/// </summary>
public static class Checker
{
    /// <summary>Unity API roots. A member access whose leftmost name is one of these is a Unity call.</summary>
    static readonly HashSet<string> UnityRoots = new(StringComparer.Ordinal)
    {
        "Application", "AssetDatabase", "AssetImporter", "AudioSettings", "Camera", "Component",
        "Debug", "EditorApplication", "EditorBuildSettings", "EditorSceneManager", "EditorUserBuildSettings",
        "EditorUtility", "GameObject", "Graphics", "GraphicsSettings", "Lightmapping", "LightmapSettings",
        "LayerMask", "Object", "PlayerSettings", "PrefabUtility", "Physics", "RenderSettings", "Resources",
        "SceneManager", "Selection", "SessionState", "Shader", "Undo", "UnityEditor", "UnityEngine",
        "CompilationPipeline", "AssetPostprocessor", "EditorPrefs", "EditorGUIUtility", "Time", "NavMesh"
    };

    /// <summary>
    /// Members that are documented thread-safe, or that are only ever *subscribed to* rather than
    /// called. Each entry needs a reason, because an allowlist is where a rule like this rots.
    /// </summary>
    static readonly Dictionary<string, string> Allowed = new(StringComparer.Ordinal)
    {
        ["Application.logMessageReceivedThreaded"] = "Unity's own threaded log callback; the whole point of it is that it is raised off the main thread.",
        ["Debug.unityLogger"] = "The logger object itself is thread-safe to read."
    };

    sealed class TypeModel
    {
        public required string Name { get; init; }
        public required string File { get; init; }
        public required TypeDeclarationSyntax Syntax { get; init; }
        public required Dictionary<string, MethodDeclarationSyntax> Methods { get; init; }
        public HashSet<string> Seeds { get; } = new(StringComparer.Ordinal);
    }

    public static IReadOnlyList<Finding> Run(string sourceDirectory)
    {
        var types = Parse(sourceDirectory);

        foreach (var type in types.Values)
            foreach (var seed in LocalSeeds(type))
                type.Seeds.Add(seed);

        // A type that owns a thread makes every callback handed to it off-thread as well. Repeat
        // until nothing new appears: a threaded type can be constructed by another one.
        var threaded = new HashSet<string>(types.Values.Where(t => t.Seeds.Count > 0).Select(t => t.Name), StringComparer.Ordinal);
        for (var pass = 0; pass < 4; pass++)
        {
            var added = false;
            foreach (var type in types.Values)
                foreach (var seed in CallbackSeeds(type, threaded))
                    added |= type.Seeds.Add(seed);
            if (!added) break;
        }

        var findings = new List<Finding>();
        foreach (var type in types.Values.OrderBy(t => t.File, StringComparer.Ordinal))
        {
            if (type.Seeds.Count == 0) continue;
            foreach (var name in Closure(type).OrderBy(n => n, StringComparer.Ordinal))
            {
                if (!type.Methods.TryGetValue(name, out var method)) continue;
                findings.AddRange(UnityCallsIn(method, type.File, type.Name + "." + name));
            }
        }

        return findings;
    }

    static Dictionary<string, TypeModel> Parse(string sourceDirectory)
    {
        var types = new Dictionary<string, TypeModel>(StringComparer.Ordinal);

        var files = Directory.GetFiles(sourceDirectory, "*.cs", SearchOption.AllDirectories)
                             .Where(f => !f.Contains("Generated", StringComparison.OrdinalIgnoreCase))
                             .OrderBy(f => f, StringComparer.Ordinal);

        foreach (var file in files)
        {
            var tree = CSharpSyntaxTree.ParseText(File.ReadAllText(file), path: file);
            foreach (var syntax in tree.GetCompilationUnitRoot().DescendantNodes().OfType<TypeDeclarationSyntax>())
            {
                // Overloads share a name; the first declaration is enough for a reachability walk,
                // because the question is which *names* run off the main thread.
                var methods = new Dictionary<string, MethodDeclarationSyntax>(StringComparer.Ordinal);
                foreach (var method in syntax.Members.OfType<MethodDeclarationSyntax>())
                    if (!methods.ContainsKey(method.Identifier.Text)) methods[method.Identifier.Text] = method;

                var name = syntax.Identifier.Text;
                if (!types.ContainsKey(name))
                    types[name] = new TypeModel { Name = name, File = file, Syntax = syntax, Methods = methods };
            }
        }

        return types;
    }

    static IEnumerable<string> LocalSeeds(TypeModel type)
    {
        foreach (var creation in type.Syntax.DescendantNodes().OfType<ObjectCreationExpressionSyntax>())
        {
            if (NameOf(creation.Type) is not ("Thread" or "System.Threading.Thread")) continue;
            foreach (var argument in creation.ArgumentList?.Arguments ?? default)
                if (argument.Expression is IdentifierNameSyntax id && type.Methods.ContainsKey(id.Identifier.Text))
                    yield return id.Identifier.Text;
        }

        foreach (var invocation in type.Syntax.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            if (invocation.Expression.ToString() is not ("Task.Run" or "ThreadPool.QueueUserWorkItem" or "Task.Factory.StartNew"))
                continue;
            foreach (var argument in invocation.ArgumentList.Arguments)
                if (argument.Expression is IdentifierNameSyntax id && type.Methods.ContainsKey(id.Identifier.Text))
                    yield return id.Identifier.Text;
        }

        // Unity raises this one off the main thread by design, which is exactly why a handler for
        // it must not touch the Unity API.
        foreach (var assignment in type.Syntax.DescendantNodes().OfType<AssignmentExpressionSyntax>())
        {
            if (!assignment.Left.ToString().EndsWith("logMessageReceivedThreaded", StringComparison.Ordinal)) continue;
            if (assignment.Right is IdentifierNameSyntax handler && type.Methods.ContainsKey(handler.Identifier.Text))
                yield return handler.Identifier.Text;
        }
    }

    /// <summary>Methods handed as callbacks to a type that owns a thread.</summary>
    static IEnumerable<string> CallbackSeeds(TypeModel type, IReadOnlySet<string> threadedTypes)
    {
        foreach (var creation in type.Syntax.DescendantNodes().OfType<ObjectCreationExpressionSyntax>())
        {
            var created = NameOf(creation.Type);
            var simple = created.Contains('.') ? created[(created.LastIndexOf('.') + 1)..] : created;
            if (!threadedTypes.Contains(simple)) continue;

            foreach (var argument in creation.ArgumentList?.Arguments ?? default)
                if (argument.Expression is IdentifierNameSyntax id && type.Methods.ContainsKey(id.Identifier.Text))
                    yield return id.Identifier.Text;
        }
    }

    static HashSet<string> Closure(TypeModel type)
    {
        var closure = new HashSet<string>(type.Seeds, StringComparer.Ordinal);
        var queue = new Queue<string>(type.Seeds);

        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            if (!type.Methods.TryGetValue(current, out var method)) continue;

            foreach (var invocation in method.DescendantNodes().OfType<InvocationExpressionSyntax>())
            {
                if (invocation.Expression is not IdentifierNameSyntax called) continue;
                if (!type.Methods.ContainsKey(called.Identifier.Text)) continue;
                if (closure.Add(called.Identifier.Text)) queue.Enqueue(called.Identifier.Text);
            }
        }

        return closure;
    }

    static IEnumerable<Finding> UnityCallsIn(MethodDeclarationSyntax method, string file, string qualifiedName)
    {
        foreach (var access in method.DescendantNodes().OfType<MemberAccessExpressionSyntax>())
        {
            var leftmost = Leftmost(access);
            if (leftmost is null || !UnityRoots.Contains(leftmost)) continue;

            var expression = access.ToString();
            var member = leftmost + "." + access.Name.Identifier.Text;
            if (Allowed.ContainsKey(member)) continue;
            if (Allowed.Keys.Any(k => expression.StartsWith(k, StringComparison.Ordinal))) continue;

            var position = access.GetLocation().GetLineSpan().StartLinePosition;
            yield return new Finding(
                file, position.Line + 1, position.Character + 1, qualifiedName, expression,
                "this method runs off the main thread, and Unity APIs are main-thread only; " +
                "set a flag and let the main-thread pump do the work");
        }
    }

    static string? Leftmost(MemberAccessExpressionSyntax access)
    {
        ExpressionSyntax current = access;
        while (true)
        {
            switch (current)
            {
                case MemberAccessExpressionSyntax member: current = member.Expression; break;
                case InvocationExpressionSyntax invocation: current = invocation.Expression; break;
                case IdentifierNameSyntax id: return id.Identifier.Text;
                default: return null;
            }
        }
    }

    static string NameOf(TypeSyntax type) => type switch
    {
        IdentifierNameSyntax id => id.Identifier.Text,
        QualifiedNameSyntax qualified => qualified.ToString(),
        _ => type.ToString()
    };
}
