namespace Maki.Core.Http.Proxy;

public enum SourceProxyFailure
{
    /// <summary>Nothing answered at the proxy's address.</summary>
    Unreachable,

    /// <summary>The proxy answered but refused or failed the request (CONNECT error, 407, upstream failure).</summary>
    Rejected,

    /// <summary>The source is assigned a proxy that no longer exists.</summary>
    Misconfigured,
}

/// <summary>
/// A source's traffic could not go through its proxy. Never retried directly: falling back to a
/// direct connection is exactly what the admin set the proxy to avoid. The message is English for
/// logs; callers that word it for a reader use <see cref="Failure"/>.
/// </summary>
public sealed class SourceProxyException(string? sourceName, SourceProxyFailure failure, Exception? inner, string message)
    : HttpRequestException(message, inner)
{
    public string? SourceName { get; } = sourceName;

    public SourceProxyFailure Failure { get; } = failure;
}
