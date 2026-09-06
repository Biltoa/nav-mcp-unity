using System.Security.Cryptography;

namespace Umcp.Daemon.Security;

/// <summary>
/// Bearer token minted at daemon start, written so that only the current user can read it — an
/// ACL on Windows, mode 0600 on macOS and Linux.
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
        var problem = Umcp.UmcpPaths.RestrictToOwner(Paths.TokenFile);
        if (problem is not null)
            Console.Error.WriteLine($"[umcpd] warning: could not restrict permissions on {Paths.TokenFile}: {problem}");
    }

    public static string? ReadExisting()
        => File.Exists(Paths.TokenFile) ? File.ReadAllText(Paths.TokenFile).Trim() : null;
}
