namespace Umcp.Daemon.Mcp;

/// <summary>
/// The HTTP request's own abort signal, made reachable from inside a tool.
///
/// Why this exists, precisely. A client that cancels a call over the streamable-HTTP transport
/// aborts the POST — Kestrel logs it as a 499 — but in this SDK version that abort does not reach
/// the tool's <c>CancellationToken</c>: the handler runs to completion against a caller that has
/// gone. For a read that only wastes a round trip. For a mutation it is worse than that: the
/// caller believes it stopped the operation, and the Editor applies it anyway.
///
/// So the middleware stamps <c>HttpContext.RequestAborted</c> here, and the tools link it into the
/// token they pass down. When the SDK propagates <c>notifications/cancelled</c> to the tool token
/// itself, this becomes redundant rather than wrong — both signals mean the same thing, and
/// linking two tokens that agree costs nothing.
/// </summary>
public static class RequestAbort
{
    static readonly AsyncLocal<CancellationToken> _current = new();

    /// <summary>The current request's abort token, or <see cref="CancellationToken.None"/> outside a request.</summary>
    public static CancellationToken Current => _current.Value;

    public static void Set(CancellationToken token) => _current.Value = token;

    /// <summary>
    /// Link the tool's token with the transport's. The caller disposes it; the returned token is
    /// cancelled when either signal fires.
    /// </summary>
    public static CancellationTokenSource Link(CancellationToken ct) =>
        CancellationTokenSource.CreateLinkedTokenSource(ct, Current);
}
