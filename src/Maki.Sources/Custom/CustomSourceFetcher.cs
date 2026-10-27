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
}
