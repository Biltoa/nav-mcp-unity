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
/// The check is deliberately syntactic and deliberately narrow:
///
///   * **Seeds** are the entry points that provably run off the main thread — a method named in
///     <c>new Thread(...)</c>, <c>Task.Run(...)</c>, <c>ThreadPool.QueueUserWorkItem(...)</c>, or
///     subscribed to Unity's own threaded log callback.
///   * From each seed it follows calls to other methods **in the same type**, which is where this
///     agent's threading actually lives.
///   * Inside that closure, any use of a known Unity API root is a finding.
///
/// It will not catch a Unity call made through an interface or a delegate hop across types. It
/// catches the class of mistake that has actually happened here, at zero runtime cost, and it fails
/// the build rather than producing a warning nobody reads.
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

    public static IReadOnlyList<Finding> Run(string sourceDirectory)
    {
        var findings = new List<Finding>();
        var files = Directory.GetFiles(sourceDirectory, "*.cs", SearchOption.AllDirectories)
                             .Where(f => !f.Contains("Generated", StringComparison.OrdinalIgnoreCase))
                             .OrderBy(f => f, StringComparer.Ordinal)
                             .ToArray();

        foreach (var file in files)
        {
            var tree = CSharpSyntaxTree.ParseText(File.ReadAllText(file), path: file);
            var root = tree.GetCompilationUnitRoot();

            foreach (var type in root.DescendantNodes().OfType<TypeDeclarationSyntax>())
            {
                // Overloads share a name, so the first declaration wins. That is enough for a
                // reachability walk: the point is which *names* run off the main thread.
                var methods = new Dictionary<string, MethodDeclarationSyntax>(StringComparer.Ordinal);
                foreach (var m in type.Members.OfType<MethodDeclarationSyntax>())
                    if (!methods.ContainsKey(m.Identifier.Text)) methods[m.Identifier.Text] = m;

                var offThread = SeedMethods(type, methods);
                if (offThread.Count == 0) continue;

                // Follow calls within the type: a seed that delegates its work to a private helper
                // has moved the problem, not solved it.
                var closure = new HashSet<string>(offThread, StringComparer.Ordinal);
                var queue = new Queue<string>(offThread);
                while (queue.Count > 0)
                {
                    var current = queue.Dequeue();
                    if (!methods.TryGetValue(current, out var method) || method.Body is null) continue;

                    foreach (var invocation in method.DescendantNodes().OfType<InvocationExpressionSyntax>())
                    {
                        if (invocation.Expression is not IdentifierNameSyntax called) continue;
                        if (!methods.ContainsKey(called.Identifier.Text)) continue;
                        if (closure.Add(called.Identifier.Text)) queue.Enqueue(called.Identifier.Text);
                    }
                }

                foreach (var name in closure.OrderBy(n => n, StringComparer.Ordinal))
                {
                    if (!methods.TryGetValue(name, out var method)) continue;
                    findings.AddRange(UnityCallsIn(method, file, type.Identifier.Text + "." + name));
                }
            }
        }

        return findings;
    }

    /// <summary>Methods this type provably hands to another thread.</summary>
    static List<string> SeedMethods(TypeDeclarationSyntax type, Dictionary<string, MethodDeclarationSyntax> methods)
    {
        var seeds = new List<string>();

        foreach (var creation in type.DescendantNodes().OfType<ObjectCreationExpressionSyntax>())
        {
            if (NameOf(creation.Type) is not ("Thread" or "System.Threading.Thread")) continue;
            foreach (var argument in creation.ArgumentList?.Arguments ?? default)
                if (argument.Expression is IdentifierNameSyntax id && methods.ContainsKey(id.Identifier.Text))
                    seeds.Add(id.Identifier.Text);
        }

        foreach (var invocation in type.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            var target = invocation.Expression.ToString();
            var isHandoff = target is "Task.Run" or "ThreadPool.QueueUserWorkItem" or "Task.Factory.StartNew";
            if (!isHandoff) continue;

            foreach (var argument in invocation.ArgumentList.Arguments)
                if (argument.Expression is IdentifierNameSyntax id && methods.ContainsKey(id.Identifier.Text))
                    seeds.Add(id.Identifier.Text);
        }

        // Unity raises this one off the main thread by design, which is exactly why a handler for
        // it must not touch the Unity API.
        foreach (var assignment in type.DescendantNodes().OfType<AssignmentExpressionSyntax>())
        {
            if (!assignment.Left.ToString().EndsWith("logMessageReceivedThreaded", StringComparison.Ordinal)) continue;
            if (assignment.Right is IdentifierNameSyntax handler && methods.ContainsKey(handler.Identifier.Text))
                seeds.Add(handler.Identifier.Text);
        }

        return seeds.Distinct(StringComparer.Ordinal).ToList();
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
