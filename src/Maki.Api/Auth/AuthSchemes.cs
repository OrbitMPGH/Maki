namespace Maki.Api.Auth;

public static class AuthSchemes
{
    /// <summary>
    /// The default scheme: a policy scheme that forwards to <see cref="ApiKey"/> when the request
    /// carries an <c>X-Api-Key</c> header, and to the Identity application cookie otherwise. One
    /// endpoint set therefore serves both the SPA and third-party clients without either having to
    /// know the other exists.
    /// </summary>
    public const string Adaptive = "Adaptive";

    /// <summary>
    /// Per-user API key in the <c>X-Api-Key</c> header. Header only — never a query parameter: a
    /// query string lands in browser history, <c>Referer</c> headers and every reverse-proxy access
    /// log, which is exactly how the old instance-wide key leaked.
    /// </summary>
    public const string ApiKey = "ApiKey";

    /// <summary>
    /// OpenID Connect. Never the default scheme and never forwarded to by <see cref="Adaptive"/>:
    /// it is only ever reached explicitly, by the challenge endpoint and by the handler's own
    /// callback path. That is what lets the scheme be registered unconditionally — an instance with
    /// no provider configured never materializes its options and so never tries to fetch a discovery
    /// document from an empty authority.
    /// </summary>
    public const string Oidc = "oidc";
}
