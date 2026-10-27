using Maki.Core.Configuration;
using Maki.Core.Http;
using Microsoft.Extensions.Logging;

namespace Maki.Sources.Custom;

/// <summary>
/// <see cref="ChallengeAwareFetcher"/> over the guarded <see cref="HttpClientName"/> client, which
/// only connects to public addresses (checked at connect time, so redirects and DNS changes are
/// covered). The shared fetcher's client has no such guard because its hosts are fixed in code; a
/// custom source's are typed by an admin. FlareSolverr fetches from its own network, so the host is
/// checked before a handoff too.
/// </summary>
public sealed class CustomSourceFetcher(
    IHttpClientFactory httpClientFactory,
    FlareSolverrClient flareSolverr,
    IAppSettings settings,
    ILogger<ChallengeAwareFetcher> logger) : ChallengeAwareFetcher(httpClientFactory, flareSolverr, settings, logger)
{
    public new const string HttpClientName = "custom-sources";

    /// <summary>The same guards without <see cref="SameSiteRedirectHandler"/>: a repository may legitimately redirect elsewhere.</summary>
    public const string RepositoryHttpClientName = "custom-source-repositories";

    /// <summary>Largest document read; a definition pointed at something huge fails instead of filling memory.</summary>
    public const long MaxDocumentBytes = 8 * 1024 * 1024;

    protected override string ClientName => HttpClientName;

    protected override async Task BeforeFlareSolverrAsync(Uri url, CancellationToken ct)
    {
        if (!PublicAddressGuard.IsAllowedUrl(url) || !await PublicAddressGuard.IsPublicHostAsync(url, ct))
        {
            throw new BlockedDestinationException($"Refusing to hand '{url.Host}' to FlareSolverr: not a public address");
        }
    }

    /// <summary>
    /// FlareSolverr follows redirects inside its own network, where the connect-time guard can't
    /// follow it, so a page it ended up on elsewhere is thrown away rather than parsed.
    /// </summary>
    protected override async Task AfterFlareSolverrAsync(Uri requested, Uri? landed, CancellationToken ct)
    {
        if (landed is null)
        {
            return;
        }

        if (!SameSiteRedirectHandler.SameSite(requested.Host, landed.Host)
            || !PublicAddressGuard.IsAllowedUrl(landed) || !await PublicAddressGuard.IsPublicHostAsync(landed, ct))
        {
            throw new BlockedDestinationException($"FlareSolverr was redirected from '{requested.Host}' to '{landed.Host}'; refusing the page");
        }
    }
}
