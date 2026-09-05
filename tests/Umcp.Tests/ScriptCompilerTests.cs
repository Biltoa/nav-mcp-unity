using Umcp.Daemon.Script;
using Xunit;

namespace Umcp.Tests;

/// <summary>
/// Code mode's compiler, exercised against this test process's own assemblies rather than a
/// Unity install — the wrapping, the line mapping and the cache are what these tests are about,
/// and none of them care which assemblies are referenced.
/// </summary>
public class ScriptCompilerTests
{
    static readonly string[] References = AppDomain.CurrentDomain.GetAssemblies()
        .Where(a => !a.IsDynamic && !string.IsNullOrEmpty(a.Location))
        .Select(a => a.Location)
        .Distinct()
        .ToArray();

    // The Unity namespaces obviously are not loadable in a test process, so these tests pass a
    // plain-BCL using set. Wrapping, line mapping and caching are what is under test, and none of
    // them care which namespaces are imported.
    static readonly string[] Usings = { "System", "System.Collections.Generic", "System.Linq" };

    static CompileResult Compile(string body) => new ScriptCompiler().Compile(body, References, Usings);

    [Fact]
    public void A_bare_expression_is_returned_automatically()
    {
        var r = Compile("1 + 1");
        Assert.True(r.Ok, Describe(r));
        Assert.NotNull(r.Assembly);
        Assert.StartsWith("UmcpScript_", r.TypeName);
    }

    [Fact]
    public void A_statement_block_without_a_return_yields_null_rather_than_failing()
    {
        var r = Compile("var x = 2; var y = x * 3;");
        Assert.True(r.Ok, Describe(r));
    }

    [Fact]
    public void An_explicit_return_works()
    {
        var r = Compile("var xs = new List<int>{1,2,3}; return xs.Count;");
        Assert.True(r.Ok, Describe(r));
    }

    [Fact]
    public void Generic_type_instantiation_compiles()
    {
        // The specific thing Mono.CSharp.Evaluator cannot do: it returns null silently for
        // `new List<int>()`, which is why this project uses Roslyn.
        var r = Compile("new List<int>().Count");
        Assert.True(r.Ok, Describe(r));
    }

    [Fact]
    public void Errors_report_line_numbers_relative_to_the_users_code()
    {
        var r = Compile("var a = 1;\nvar b = ;\nreturn a;");
        Assert.False(r.Ok);
        var error = r.Diagnostics.First(d => d.Severity == "error");
        Assert.Equal(2, error.Line);
    }

    [Fact]
    public void An_empty_script_is_valid_and_returns_null()
    {
        Assert.True(Compile("").Ok);
    }

    [Fact]
    public void Identical_sources_hit_the_cache_and_reuse_the_same_type()
    {
        var compiler = new ScriptCompiler();
        var first = compiler.Compile("40 + 2", References, Usings);
        var second = compiler.Compile("40 + 2", References, Usings);

        Assert.True(first.Ok);
        Assert.False(first.FromCache);
        Assert.True(second.FromCache);
        Assert.Equal(first.TypeName, second.TypeName);
        Assert.Equal(1, compiler.CacheCount);
    }

    [Fact]
    public void Different_sources_get_different_types()
    {
        var compiler = new ScriptCompiler();
        var a = compiler.Compile("1", References, Usings);
        var b = compiler.Compile("2", References, Usings);
        Assert.NotEqual(a.TypeName, b.TypeName);
        Assert.Equal(2, compiler.CacheCount);
    }

    [Fact]
    public void Unreadable_references_are_skipped_rather_than_failing_the_compile()
    {
        var r = new ScriptCompiler().Compile("1 + 1",
            References.Concat(new[] { @"C:\definitely\not\here.dll" }).ToArray(), Usings);
        Assert.True(r.Ok, Describe(r));
    }

    static string Describe(CompileResult r) =>
        string.Join("; ", r.Diagnostics.Select(d => $"{d.Severity} {d.Id} line {d.Line}: {d.Message}"));
}

public class ProfileTests
{
    [Theory]
    [InlineData("unity.script")]
    [InlineData("assets.delete")]
    [InlineData("scene.save")]
    [InlineData("editor.stall")]
    public void Dangerous_operations_need_the_full_profile(string toolId)
    {
        Assert.NotNull(Umcp.Daemon.Security.Profiles.Denies(Umcp.Daemon.Security.Profile.Standard, toolId, true));
        Assert.NotNull(Umcp.Daemon.Security.Profiles.Denies(Umcp.Daemon.Security.Profile.ReadOnly, toolId, true));
        Assert.Null(Umcp.Daemon.Security.Profiles.Denies(Umcp.Daemon.Security.Profile.Full, toolId, true));
    }

    [Fact]
    public void Readonly_blocks_every_mutation_but_allows_reads()
    {
        Assert.NotNull(Umcp.Daemon.Security.Profiles.Denies(Umcp.Daemon.Security.Profile.ReadOnly, "gameobject.create", true));
        Assert.Null(Umcp.Daemon.Security.Profiles.Denies(Umcp.Daemon.Security.Profile.ReadOnly, "scene.query", false));
    }

    [Fact]
    public void Standard_allows_ordinary_mutations()
    {
        Assert.Null(Umcp.Daemon.Security.Profiles.Denies(Umcp.Daemon.Security.Profile.Standard, "gameobject.create", true));
    }

    [Theory]
    [InlineData("readonly", Umcp.Daemon.Security.Profile.ReadOnly)]
    [InlineData("full", Umcp.Daemon.Security.Profile.Full)]
    [InlineData("standard", Umcp.Daemon.Security.Profile.Standard)]
    [InlineData(null, Umcp.Daemon.Security.Profile.Standard)]
    [InlineData("nonsense", Umcp.Daemon.Security.Profile.Standard)]
    public void Profile_parsing_defaults_to_standard(string? input, Umcp.Daemon.Security.Profile expected)
    {
        Assert.Equal(expected, Umcp.Daemon.Security.Profiles.Parse(input));
    }
}
