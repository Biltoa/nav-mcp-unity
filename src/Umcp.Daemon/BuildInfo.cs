using System.Reflection;

namespace Umcp.Daemon;

/// <summary>
/// The one place the version comes from: the assembly, which comes from Directory.Build.props.
/// A version string typed into three files is a version that eventually disagrees with itself —
/// and the first thing anyone asks about a bug report is which build it came from.
/// </summary>
public static class BuildInfo
{
    public static string Version { get; } =
        typeof(BuildInfo).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            is { Length: > 0 } informational
            ? informational.Split('+')[0]      // strip the source-revision suffix the SDK appends
            : typeof(BuildInfo).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

    public const string ServerName = "unity-mcp-tool";
}
