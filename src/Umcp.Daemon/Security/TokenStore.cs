using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Runtime.Versioning;

namespace Umcp.Daemon.Security;

/// <summary>
/// Bearer token minted at daemon start, written with an ACL that grants the current user only.
///
/// Loopback binding alone is not enough: any local process, and any web page that can resolve a
/// hostname to 127.0.0.1, can reach a loopback listener. The token is what stops that. The tool
/// being replaced called <c>server.listen(port)</c> with no host at all — binding 0.0.0.0 with no
/// auth while exposing script creation, menu execution and arbitrary C#.
/// </summary>
public sealed class TokenStore
{
    public string Token { get; }

    public TokenStore(string? explicitToken = null)
    {
        Paths.EnsureCreated();
        Token = explicitToken ?? Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        Write();
    }

    void Write()
    {
        File.WriteAllText(Paths.TokenFile, Token);
        if (OperatingSystem.IsWindows()) Restrict(Paths.TokenFile);
    }

    [SupportedOSPlatform("windows")]
    static void Restrict(string path)
    {
        try
        {
            var info = new FileInfo(path);
            var security = info.GetAccessControl();
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            var me = WindowsIdentity.GetCurrent().User;
            if (me is not null)
                security.AddAccessRule(new FileSystemAccessRule(me, FileSystemRights.FullControl, AccessControlType.Allow));
            info.SetAccessControl(security);
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"[umcpd] warning: could not restrict ACL on {path}: {e.Message}");
        }
    }

    public static string? ReadExisting()
        => File.Exists(Paths.TokenFile) ? File.ReadAllText(Paths.TokenFile).Trim() : null;
}
