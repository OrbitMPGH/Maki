using System.Text.Json;
using Maki.Core.Configuration;
using Maki.Core.Download;
using Maki.Core.Entities;
using Maki.Core.Indexers;
using Maki.Data;
using Microsoft.EntityFrameworkCore;

namespace Maki.Api.Services;

public record ReleaseDto(
    string Guid,
    string Title,
    long Size,
    string Indexer,
    int? Seeders,
    int? Leechers,
    string Protocol,
    string? DownloadUrl,
    string? MagnetUrl,
    string? InfoUrl,
    string? InfoHash = null)
{
    /// <summary>
    /// How the release reads against the series' upgrade profile. Filled only on search results, and
    /// only when the series resolves to a profile.
    /// </summary>
    public Dtos.ReleaseParsedDto? Parsed { get; init; }
}

/// <summary>
/// A release as a client sees it. The download link is the indexer's proxy URL and carries its API key,
/// so it stays on the server; a grab names the release by guid and the server resolves the link.
/// </summary>
public record ReleaseResultDto(
    string Guid,
    string Title,
    long Size,
    string Indexer,
    int? Seeders,
    int? Leechers,
    string Protocol,
    string? MagnetUrl,
    string? InfoUrl,
    string? InfoHash,
    Dtos.ReleaseParsedDto? Parsed)
{
    public static ReleaseResultDto From(ReleaseDto release, Dtos.ReleaseParsedDto? parsed) => new(
        IndexerLinks.WithoutApiKey(release.Guid)!, release.Title, release.Size, release.Indexer, release.Seeders,
        release.Leechers, release.Protocol, release.MagnetUrl, IndexerLinks.WithoutApiKey(release.InfoUrl),
        release.InfoHash, parsed);
}

public record ReleaseSearchResponse(string Query, IReadOnlyList<ReleaseResultDto> Releases);

/// <summary>Payload persisted on torrent queue items (DownloadQueueItem.ReleaseInfoJson).</summary>
public record ReleaseInfo(
    string Guid,
    string Title,
    string Indexer,
    string? TorrentHash);

public record ReleaseSearchResult(string Query, IReadOnlyList<ReleaseDto> Releases)
{
    /// <summary>The backend that answered; release guids and links only mean something to it.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public IndexerBackend Backend { get; init; }
}

/// <summary>A release search or grab refused for a reason the caller can show, with its catalogue key.</summary>
public sealed class ReleaseRefusedException(string key, string message) : InvalidOperationException(message)
{
    public const string SeriesNotFound = "error.release.seriesNotFound";
    public const string NoDownloadLink = "error.release.noDownloadLink";
    public const string IndexerNotConfigured = "error.release.indexerNotConfigured";
    public const string DownloadClientNotConfigured = "error.release.downloadClientNotConfigured";

    public string Key { get; } = key;
}

public class ReleaseService(
    MakiDbContext db,
    IndexerRegistry indexers,
    TorrentClientRegistry torrentClients,
    SettingsService settings,
    ILogger<ReleaseService> logger)
{
    public virtual async Task<ReleaseSearchResult> SearchAsync(int seriesId, string? query = null, CancellationToken ct = default)
    {
        var series = await db.Series.FindAsync([seriesId], ct)
            ?? throw new ReleaseRefusedException(ReleaseRefusedException.SeriesNotFound, "Series not found");

        var indexerSettings = await GetIndexerSettingsAsync(ct);
        var indexer = indexers.For(indexerSettings.Backend);

        var candidates = string.IsNullOrWhiteSpace(query)
            ? SearchQuery.WithFallbacks(series.Title, series.OriginalTitle, series.AltTitles).ToList()
            : [query.Trim()];

        var attempted = candidates[0];
        List<ReleaseDto> results = [];
        foreach (var candidate in candidates)
        {
            attempted = candidate;
            var releases = await indexer.SearchAsync(indexerSettings, candidate, ct);
            results = releases
                .Where(r => r.Protocol.Equals("torrent", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(r => r.Seeders ?? 0)
                .Select(r => new ReleaseDto(
                    r.Guid, r.Title, r.Size, r.Indexer, r.Seeders, r.Leechers,
                    r.Protocol, r.DownloadUrl, r.MagnetUrl, r.InfoUrl, r.InfoHash))
                .ToList();
            if (results.Count > 0)
            {
                break;
            }

            logger.LogInformation("No releases for '{Query}'", candidate);
        }

        return new ReleaseSearchResult(attempted, results) { Backend = indexerSettings.Backend };
    }

    /// <param name="origin">What asked for it; an unattended grab notifies, a click does not.</param>
    /// <param name="queuedByUserId">Who clicked, or null for the volume search.</param>
    /// <param name="upgradeInfoJson">A serialised <c>TorrentUpgradeInfo</c> when the grab replaces files.</param>
    public virtual async Task<DownloadQueueItem> GrabAsync(
        int seriesId, ReleaseDto release, DownloadOrigin origin, int? queuedByUserId, string? upgradeInfoJson,
        CancellationToken ct = default)
    {
        if (!await db.Series.AnyAsync(s => s.Id == seriesId, ct))
        {
            throw new ReleaseRefusedException(ReleaseRefusedException.SeriesNotFound, "Series not found");
        }

        var client = await GetClientSettingsAsync(ct);
        if (release.MagnetUrl is null && release.DownloadUrl is null)
        {
            throw new ReleaseRefusedException(ReleaseRefusedException.NoDownloadLink, "Release has no download link");
        }

        var hash = await AddToClientAsync(release, client, ct);

        var item = new DownloadQueueItem
        {
            SeriesId = seriesId,
            Protocol = AcquisitionProtocol.Torrent,
            Status = QueueStatus.Downloading,
            Title = release.Title,
            ReleaseInfoJson = JsonSerializer.Serialize(new ReleaseInfo(release.Guid, release.Title, release.Indexer, hash)),
            QueuedAt = DateTime.UtcNow,
            SortOrder = await DownloadQueueService.NextSortOrderAsync(db, ct),
            Origin = origin,
            QueuedByUserId = queuedByUserId,
            UpgradeInfoJson = upgradeInfoJson
        };
        db.DownloadQueue.Add(item);
        await db.SaveChangesAsync(ct);

        logger.LogInformation("Grabbed '{Title}' from {Indexer} for series {SeriesId}",
            release.Title, release.Indexer, seriesId);
        return item;
    }

    /// <summary>
    /// Adds the release and returns its infohash, so the completed-download job can find the torrent by
    /// hash. A .torrent link is fetched and uploaded rather than handed to the client as a URL, because
    /// a URL may leave the hash unknown and the job then has to guess which torrent is this one by when
    /// it was added, which fails when the client already had it. Only when the fetch fails does the URL
    /// go over as before, with whatever hash the client reports for it.
    /// </summary>
    private async Task<string?> AddToClientAsync(ReleaseDto release, TorrentClientSettings client, CancellationToken ct)
    {
        var driver = torrentClients.For(client.Kind);
        if (release.MagnetUrl is { } magnet)
        {
            return await driver.AddAsync(client, magnet, ct) ?? TorrentInfoHash.FromMagnet(magnet);
        }

        var url = release.DownloadUrl!;
        TorrentLink? link = null;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            var indexer = indexers.For(await ActiveBackendAsync(ct));
            link = await indexer.FetchTorrentAsync(url, timeout.Token);
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidDataException ||
                                   (ex is TaskCanceledException && !ct.IsCancellationRequested))
        {
            logger.LogWarning("Could not fetch the torrent file for '{Title}' ({Error}); handing {Client} the link instead",
                release.Title, ex.Message, client.Kind.DisplayName());
        }

        if (link?.Magnet is { } redirected)
        {
            return await driver.AddAsync(client, redirected, ct) ?? TorrentInfoHash.FromMagnet(redirected);
        }

        if (link?.File is { } file && TorrentInfoHash.FromTorrentFile(file) is { } hash)
        {
            await driver.AddFileAsync(client, file, hash, ct);
            return hash;
        }

        if (link is not null)
        {
            logger.LogWarning("The download link for '{Title}' did not serve a torrent file; handing {Client} the link instead",
                release.Title, client.Kind.DisplayName());
        }

        return await driver.AddAsync(client, url, ct) ?? NormalizedInfoHash(release.InfoHash);
    }

    /// <summary>The indexer's own hash, for a URL add the client reported nothing for, in the form the clients list.</summary>
    private static string? NormalizedInfoHash(string? hash) =>
        string.IsNullOrWhiteSpace(hash) ? null : TorrentInfoHash.FromMagnet($"xt=urn:btih:{hash.Trim()}");

    public virtual Task<IndexerBackend> ActiveBackendAsync(CancellationToken ct) =>
        IndexerSettingsStore.ActiveBackendAsync(settings, ct);

    /// <summary>The active backend's settings; refused when its URL or API key is missing.</summary>
    public async Task<IndexerSettings> GetIndexerSettingsAsync(CancellationToken ct)
    {
        var indexerSettings = await IndexerSettingsStore.LoadAsync(settings, ct);
        if (!indexerSettings.IsConfigured)
        {
            throw new ReleaseRefusedException(
                ReleaseRefusedException.IndexerNotConfigured,
                $"{indexerSettings.Backend.DisplayName()} is not configured (Settings, Downloads, Indexer)");
        }

        return indexerSettings;
    }

    public async Task<TorrentClientSettings> GetClientSettingsAsync(CancellationToken ct)
    {
        var client = await DownloadClientSettingsStore.LoadAsync(settings, ct);
        if (string.IsNullOrWhiteSpace(client.Url))
        {
            throw new ReleaseRefusedException(
                ReleaseRefusedException.DownloadClientNotConfigured, "No download client is configured (Settings, Downloads)");
        }

        return client;
    }

    /// <summary>
    /// Optional prefix rewrite from the download path the client reports to the one Maki can
    /// read, for when the client runs in a container with different mounts.
    /// </summary>
    public async Task<(string? From, string? To)> GetClientPathMapAsync(CancellationToken ct) => (
        await settings.GetAsync(SettingKeys.DownloadClientPathMapFrom, ct),
        await settings.GetAsync(SettingKeys.DownloadClientPathMapTo, ct));
}
