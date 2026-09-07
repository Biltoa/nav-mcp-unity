using Umcp.MainThreadCheck;
using Xunit;

namespace Umcp.Tests;

/// <summary>
/// The check that enforces "no Unity API off the main thread" — and, first, proof that the check
/// itself can fail. A gate nobody has seen fail is not a gate.
/// </summary>
public class MainThreadCheckTests
{
    static string WriteSource(string code)
    {
        var dir = Path.Combine(Path.GetTempPath(), "umcp-mtc-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "Subject.cs"), code);
        return dir;
    }

    [Fact]
    public void A_unity_call_on_a_thread_entry_point_is_a_finding()
    {
        // This is Phase 1's actual bug, reduced: a socket thread reading Application.productName.
        var dir = WriteSource(@"
namespace X {
  class Subject {
    System.Threading.Thread _t;
    void Start() { _t = new Thread(ReadLoop); _t.Start(); }
    void ReadLoop() { var name = Application.productName; }
  }
}");
        try
        {
            var findings = Checker.Run(dir);
            Assert.Single(findings);
            Assert.Contains("Application.productName", findings[0].Expression);
            Assert.Contains("ReadLoop", findings[0].Method);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void A_helper_called_from_the_thread_is_covered_too()
    {
        // Moving the call into a helper is the obvious way to defeat a shallower check.
        var dir = WriteSource(@"
namespace X {
  class Subject {
    void Start() { var t = new Thread(ReadLoop); }
    void ReadLoop() { Build(); }
    void Build() { var n = Application.dataPath; }
  }
}");
        try
        {
            var findings = Checker.Run(dir);
            Assert.Contains(findings, f => f.Method.EndsWith("Build") && f.Expression.Contains("Application.dataPath"));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Setting_a_flag_for_the_main_thread_pump_is_clean()
    {
        // The fix that was actually applied: the socket thread sets a flag, and the main-thread
        // pump builds the frame.
        var dir = WriteSource(@"
namespace X {
  class Subject {
    volatile bool _helloPending;
    void Start() { var t = new Thread(ReadLoop); }
    void ReadLoop() { _helloPending = true; }
  }
}");
        try { Assert.Empty(Checker.Run(dir)); }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void The_agent_package_itself_is_clean()
    {
        var source = Path.Combine(RepoRoot(), "unity", "com.umcp.agent", "Editor");
        Assert.True(Directory.Exists(source), source);

        var findings = Checker.Run(source);
        Assert.True(findings.Count == 0,
            "Unity API on an off-main-thread path:\n" + string.Join("\n", findings.Select(f => f.ToString())));
    }

    static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "UnityMcpTool.sln"))) dir = dir.Parent;
        return dir?.FullName ?? Directory.GetCurrentDirectory();
    }
}

/// <summary>
/// The cross-type case: a callback handed to a type that owns a thread runs on that thread, and
/// the check has to know it. This is the exact shape of the agent's own
/// UmcpConnection(port, inbox, OnSocketConnected).
/// </summary>
public class MainThreadCallbackTests
{
    static string WriteSources(params (string name, string code)[] files)
    {
        var dir = Path.Combine(Path.GetTempPath(), "umcp-mtc-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        foreach (var (name, code) in files) File.WriteAllText(Path.Combine(dir, name + ".cs"), code);
        return dir;
    }

    const string ThreadedType = @"
namespace X {
  class Pump {
    System.Action _onReady;
    public Pump(System.Action onReady) { _onReady = onReady; }
    public void Start() { var t = new Thread(Loop); t.Start(); }
    void Loop() { _onReady(); }
  }
}";

    [Fact]
    public void A_callback_given_to_a_threaded_type_is_treated_as_off_thread()
    {
        var dir = WriteSources(
            ("Pump", ThreadedType),
            ("Owner", @"
namespace X {
  class Owner {
    void Start() { var p = new Pump(OnReady); p.Start(); }
    void OnReady() { var name = Application.productName; }
  }
}"));
        try
        {
            var findings = Checker.Run(dir);
            Assert.Contains(findings, f => f.Method == "Owner.OnReady" && f.Expression.Contains("Application.productName"));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void The_same_callback_setting_a_flag_is_clean()
    {
        var dir = WriteSources(
            ("Pump", ThreadedType),
            ("Owner", @"
namespace X {
  class Owner {
    volatile bool _pending;
    void Start() { var p = new Pump(OnReady); }
    void OnReady() { _pending = true; }
  }
}"));
        try { Assert.Empty(Checker.Run(dir)); }
        finally { Directory.Delete(dir, true); }
    }
}
