using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;

namespace Umcp.Daemon.Script;

public sealed record CompileResult(
    bool Ok,
    byte[]? Assembly,
    string? TypeName,
    IReadOnlyList<ScriptDiagnostic> Diagnostics,
    long CompileMs,
    bool FromCache);

public sealed record ScriptDiagnostic(string Severity, string Id, int Line, int Column, string Message);

/// <summary>
/// Code mode's compiler. Roslyn, on the daemon side.
///
/// Two decisions worth stating.
///
/// **Roslyn, not <c>Mono.CSharp.Evaluator</c>.** The evaluator cannot parse generic type
/// instantiation — <c>new List&lt;int&gt;()</c> returns null, silently — which makes it useless for
/// exactly the queries code mode exists to answer.
///
/// **Compiled here, not in the Editor.** The plan puts Roslyn inside Unity in a collectible
/// <c>AssemblyLoadContext</c>. Collectible load contexts are a .NET Core feature; the Editor runs
/// Mono, so that unload never happens and the only thing shipping Roslyn into the package buys is
/// ~15 MB of DLLs that collide with Unity's own System.Collections.Immutable. Compiling on the
/// daemon keeps the agent thin — the architecture's whole thesis — costs nothing on the Editor
/// tick, and lets the compile cache survive domain reloads, which an in-Editor cache cannot.
/// </summary>
public sealed class ScriptCompiler
{
    // Cache compiled assemblies by source hash: the same query re-run costs no compile at all,
    // and this cache lives on the daemon, so a domain reload does not empty it.
    readonly ConcurrentDictionary<string, (byte[] asm, string type)> _cache = new();
    readonly ConcurrentDictionary<string, MetadataReference> _references = new();

    public int CacheCount => _cache.Count;

    /// <summary>
    /// Namespaces every script gets for free. Kept as data rather than baked into one string so
    /// the compiler can be exercised without a Unity install, and so a narrower profile could
    /// one day hand out a smaller set.
    /// </summary>
    public static readonly string[] UnityUsings =
    {
        "System", "System.Collections", "System.Collections.Generic", "System.Linq",
        "UnityEngine", "UnityEditor", "UnityEditor.SceneManagement", "UnityEngine.SceneManagement"
    };

    static string Preamble(string typeName, IReadOnlyList<string> usings)
    {
        var sb = new StringBuilder();
        foreach (var ns in usings) sb.Append("using ").Append(ns).Append(";\n");

        // Object and Debug are ambiguous between System and UnityEngine, and in an Editor script
        // the Unity ones are always what was meant.
        if (usings.Contains("UnityEngine"))
            sb.Append("using Object = UnityEngine.Object;\nusing Debug = UnityEngine.Debug;\n");

        sb.Append("\npublic static class ").Append(typeName)
          .Append("\n{\n    public static object Run()\n    {\n")
          // #line makes every diagnostic report the caller's own line numbers rather than the
          // wrapper's. It is a preprocessor directive, so what follows must start on a new line.
          .Append("#line 1 \"script\"\n");
        return sb.ToString();
    }

    const string Postamble = "\n#line hidden\n    }\n}\n";

    public CompileResult Compile(string body, IReadOnlyList<string> referencePaths,
                                 IReadOnlyList<string>? usings = null)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        usings ??= UnityUsings;
        var hash = Hash(body + "|" + string.Join(",", usings));
        var typeName = "UmcpScript_" + hash;

        if (_cache.TryGetValue(hash, out var hit))
            return new CompileResult(true, hit.asm, hit.type, Array.Empty<ScriptDiagnostic>(), sw.ElapsedMilliseconds, true);

        var source = Preamble(typeName, usings) + Wrap(body) + Postamble;

        var tree = CSharpSyntaxTree.ParseText(
            SourceText.From(source, Encoding.UTF8),
            new CSharpParseOptions(LanguageVersion.CSharp9));

        var compilation = CSharpCompilation.Create(
            typeName,
            new[] { tree },
            referencePaths.Select(Reference).Where(r => r is not null)!,
            new CSharpCompilationOptions(
                OutputKind.DynamicallyLinkedLibrary,
                optimizationLevel: OptimizationLevel.Release,
                allowUnsafe: false,
                // The Editor's own assemblies are full of obsolete members; a script that touches
                // one should get a warning, not a failed compile.
                generalDiagnosticOption: ReportDiagnostic.Default));

        using var ms = new MemoryStream();
        var emit = compilation.Emit(ms);

        var diagnostics = emit.Diagnostics
            .Where(d => d.Severity >= DiagnosticSeverity.Warning)
            .Select(ToDiagnostic)
            .OrderByDescending(d => d.Severity)
            .ThenBy(d => d.Line)
            .Take(25)
            .ToArray();

        if (!emit.Success)
            return new CompileResult(false, null, null, diagnostics, sw.ElapsedMilliseconds, false);

        var bytes = ms.ToArray();
        _cache[hash] = (bytes, typeName);
        return new CompileResult(true, bytes, typeName, diagnostics, sw.ElapsedMilliseconds, false);
    }

    /// <summary>
    /// A bare expression becomes its own return; a statement block gets a trailing
    /// <c>return null;</c> so "do this, tell me nothing" needs no ceremony.
    /// </summary>
    static string Wrap(string body)
    {
        var trimmed = body.Trim();
        if (trimmed.Length == 0) return "return null;";
        if (!trimmed.Contains(';') && !trimmed.StartsWith("return", StringComparison.Ordinal))
            return "return (" + trimmed + ");";
        return trimmed + "\n#line hidden\nreturn null;";
    }

    static ScriptDiagnostic ToDiagnostic(Diagnostic d)
    {
        // Mapped, not raw: #line in the preamble is what turns wrapper line numbers back into the
        // caller's own. GetLineSpan() would report the generated file's lines and quietly send
        // people looking at the wrong line of their own code.
        var span = d.Location.GetMappedLineSpan();
        return new ScriptDiagnostic(
            d.Severity == DiagnosticSeverity.Error ? "error" : "warning",
            d.Id,
            span.StartLinePosition.Line + 1,
            span.StartLinePosition.Character + 1,
            d.GetMessage());
    }

    MetadataReference? Reference(string path)
    {
        if (_references.TryGetValue(path, out var cached)) return cached;
        try
        {
            if (!File.Exists(path)) return null;
            var r = MetadataReference.CreateFromFile(path);
            _references[path] = r;
            return r;
        }
        catch
        {
            return null;   // a reference we cannot read is not worth failing the compile over
        }
    }

    static string Hash(string s)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(s));
        return Convert.ToHexString(bytes, 0, 8).ToLowerInvariant();
    }
}
