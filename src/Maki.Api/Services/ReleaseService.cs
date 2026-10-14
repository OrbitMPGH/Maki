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
    string? InfoUrl)
{
    /// <summary>
    /// How the release reads against the series' upgrade profile. Filled only on search results, and
    /// only when the series resolves to a profile.
    /// </summary>
    public Dtos.ReleaseParsedDto? Parsed { get; init; }
}

/// <summary>Payload persisted on torrent queue items (DownloadQueueItem.ReleaseInfoJson).</summary>
public record ReleaseInfo(
    string Guid,
    string Title,
    string Indexer,
    string? TorrentHash);

public record ReleaseSearchResult(string Query, IReadOnlyList<ReleaseDto> Releases);

/// <summary>A release search or grab refused for a reason the caller can show, with its catalogue key.</summary>
public sealed class ReleaseRefusedException(string key, string message) : InvalidOperationException(message)
{
    public const string SeriesNotFound = "error.release.seriesNotFound";
    public const string NoDownloadLink = "error.release.noDownloadLink";
    public const string ProwlarrNotConfigured = "error.release.prowlarrNotConfigured";
    public const string QBittorrentNotConfigured = "error.release.qbittorrentNotConfigured";

    public string Key { get; } = key;
}

public class ReleaseService(
    MakiDbContext db,
    ProwlarrClient prowlarr,
    QBittorrentClient qbittorrent,
    SettingsService settings,
    ILogger<ReleaseService> logger)
{
    public virtual async Task<ReleaseSearchResult> SearchAsync(int seriesId, string? query = null, CancellationToken ct = default)
    {
        var series = await db.Series.FindAsync([seriesId], ct)
            ?? throw new ReleaseRefusedException(ReleaseRefusedException.SeriesNotFound, "Series not found");

        var (url, apiKey) = await GetProwlarrConfigAsync(ct);
        var indexerIds = ParseIds(await settings.GetAsync(SettingKeys.ProwlarrIndexerIds, ct));
        var categories = ParseIds(await settings.GetAsync(SettingKeys.ProwlarrCategories, ct));

        var candidates = string.IsNullOrWhiteSpace(query)
            ? SearchQuery.WithFallbacks(series.Title, series.OriginalTitle, series.AltTitles).ToList()
            : [query.Trim()];

        var attempted = candidates[0];
        List<ReleaseDto> results = [];
        foreach (var candidate in candidates)
        {
            attempted = candidate;
            var releases = await prowlarr.SearchAsync(url, apiKey, candidate, indexerIds, categories, ct);
            results = releases
                .Where(r => r.Protocol.Equals("torrent", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(r => r.Seeders ?? 0)
                .Select(r => new ReleaseDto(
                    r.Guid, r.Title, r.Size, r.Indexer, r.Seeders, r.Leechers,
                    r.Protocol, r.DownloadUrl, r.MagnetUrl, r.InfoUrl))
                .ToList();
            if (results.Count > 0)
            {
                break;
            }

            logger.LogInformation("No releases for '{Query}'", candidate);
        }

        return new ReleaseSearchResult(attempted, results);
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

        var qbt = await GetQbtConfigAsync(ct);
        if (release.MagnetUrl is null && release.DownloadUrl is null)
        {
            throw new ReleaseRefusedException(ReleaseRefusedException.NoDownloadLink, "Release has no download link");
        }

        var hash = await AddToQbittorrentAsync(release, qbt, ct);

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
    /// hash. A .torrent link is fetched and uploaded rather than handed to qBittorrent as a URL, because
    /// a URL leaves the hash unknown and the job then has to guess which torrent is this one by when it
    /// was added, which fails when qBittorrent already had it. Only when the fetch fails does the URL go
    /// over as before, with no hash.
    /// </summary>
    private async Task<string?> AddToQbittorrentAsync(
        ReleaseDto release, (string Url, string Username, string Password, string Category) qbt, CancellationToken ct)
    {
        if (release.MagnetUrl is { } magnet)
        {
            await qbittorrent.AddAsync(qbt.Url, qbt.Username, qbt.Password, magnet, qbt.Category, ct);
            return TorrentInfoHash.FromMagnet(magnet);
        }

        var url = release.DownloadUrl!;
        ProwlarrClient.TorrentLink? link = null;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            link = await prowlarr.FetchTorrentAsync(url, timeout.Token);
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidDataException ||
                                   (ex is TaskCanceledException && !ct.IsCancellationRequested))
        {
            logger.LogWarning("Could not fetch the torrent file for '{Title}' ({Error}); handing qBittorrent the link instead",
                release.Title, ex.Message);
        }

        if (link?.Magnet is { } redirected)
        {
            await qbittorrent.AddAsync(qbt.Url, qbt.Username, qbt.Password, redirected, qbt.Category, ct);
            return TorrentInfoHash.FromMagnet(redirected);
        }

        if (link?.File is { } file && TorrentInfoHash.FromTorrentFile(file) is { } hash)
        {
            await qbittorrent.AddFileAsync(qbt.Url, qbt.Username, qbt.Password, file, hash, qbt.Category, ct);
            return hash;
        }

        if (link is not null)
        {
            logger.LogWarning("The download link for '{Title}' did not serve a torrent file; handing qBittorrent the link instead",
                release.Title);
        }

        await qbittorrent.AddAsync(qbt.Url, qbt.Username, qbt.Password, url, qbt.Category, ct);
        return null;
    }

    /// <summary>Parses a CSV of ids from the settings store; null/blank → null (no restriction).</summary>
    private static List<int>? ParseIds(string? csv)
    {
        if (string.IsNullOrWhiteSpace(csv))
        {
            return null;
        }

        var ids = csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(part => int.TryParse(part, out var id) ? id : (int?)null)
            .OfType<int>()
            .ToList();
        return ids.Count > 0 ? ids : null;
    }

    public async Task<(string Url, string ApiKey)> GetProwlarrConfigAsync(CancellationToken ct)
    {
        var url = await settings.GetAsync(SettingKeys.ProwlarrUrl, ct);
        var apiKey = await settings.GetAsync(SettingKeys.ProwlarrApiKey, ct);
        if (string.IsNullOrWhiteSpace(url) || string.IsNullOrWhiteSpace(apiKey))
        {
            throw new ReleaseRefusedException(
                ReleaseRefusedException.ProwlarrNotConfigured, "Prowlarr is not configured (Settings → Downloads → Prowlarr)");
        }

        return (url, apiKey);
    }

    public async Task<(string Url, string Username, string Password, string Category)> GetQbtConfigAsync(CancellationToken ct)
    {
        var url = await settings.GetAsync(SettingKeys.QBittorrentUrl, ct);
        if (string.IsNullOrWhiteSpace(url))
        {
            throw new ReleaseRefusedException(
                ReleaseRefusedException.QBittorrentNotConfigured, "qBittorrent is not configured (Settings → Downloads → qBittorrent)");
        }

        return (
            url,
            await settings.GetAsync(SettingKeys.QBittorrentUsername, ct) ?? string.Empty,
            await settings.GetAsync(SettingKeys.QBittorrentPassword, ct) ?? string.Empty,
            await settings.GetAsync(SettingKeys.QBittorrentCategory, ct) ?? "maki");
    }

    /// <summary>
    /// Optional prefix rewrite from the download path qBittorrent reports to the one
    /// Maki can read — for when qBittorrent runs in a container with different mounts.
    /// </summary>
    public async Task<(string? From, string? To)> GetQbtPathMapAsync(CancellationToken ct) => (
        await settings.GetAsync(SettingKeys.QBittorrentPathMapFrom, ct),
        await settings.GetAsync(SettingKeys.QBittorrentPathMapTo, ct));
}
