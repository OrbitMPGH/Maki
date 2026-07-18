using System.Text.Json;
using System.Text.RegularExpressions;
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
    string? InfoUrl);

/// <summary>Payload persisted on torrent queue items (DownloadQueueItem.ReleaseInfoJson).</summary>
public record ReleaseInfo(
    string Guid,
    string Title,
    string Indexer,
    string? TorrentHash);

public record ReleaseSearchResult(string Query, IReadOnlyList<ReleaseDto> Releases);

public partial class ReleaseService(
    MakiDbContext db,
    ProwlarrClient prowlarr,
    QBittorrentClient qbittorrent,
    SettingsService settings,
    ILogger<ReleaseService> logger)
{
    [GeneratedRegex(@"xt=urn:btih:([0-9a-fA-F]{40}|[a-zA-Z2-7]{32})")]
    private static partial Regex MagnetHash();

    public async Task<ReleaseSearchResult> SearchAsync(int seriesId, string? query = null, CancellationToken ct = default)
    {
        var series = await db.Series.FindAsync([seriesId], ct)
            ?? throw new InvalidOperationException("Series not found");

        var (url, apiKey) = await GetProwlarrConfigAsync(ct);
        var indexerIds = ParseIds(await settings.GetAsync(SettingKeys.ProwlarrIndexerIds, ct));
        var categories = ParseIds(await settings.GetAsync(SettingKeys.ProwlarrCategories, ct));

        var candidates = string.IsNullOrWhiteSpace(query)
            ? SearchQuery.Candidates(series.Title).ToList()
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

    public async Task<DownloadQueueItem> GrabAsync(int seriesId, ReleaseDto release, CancellationToken ct = default)
    {
        if (!await db.Series.AnyAsync(s => s.Id == seriesId, ct))
        {
            throw new InvalidOperationException("Series not found");
        }

        var qbt = await GetQbtConfigAsync(ct);
        var link = release.MagnetUrl ?? release.DownloadUrl
            ?? throw new InvalidOperationException("Release has no download link");

        await qbittorrent.AddAsync(qbt.Url, qbt.Username, qbt.Password, link, qbt.Category, ct);

        // Magnets carry their infohash; .torrent URLs get matched later by the
        // completed-download job (newest unclaimed torrent in our category).
        string? hash = null;
        if (release.MagnetUrl != null && MagnetHash().Match(release.MagnetUrl) is { Success: true } m)
        {
            hash = m.Groups[1].Value.ToLowerInvariant();
        }

        var item = new DownloadQueueItem
        {
            SeriesId = seriesId,
            Protocol = AcquisitionProtocol.Torrent,
            Status = QueueStatus.Downloading,
            Title = release.Title,
            ReleaseInfoJson = JsonSerializer.Serialize(new ReleaseInfo(release.Guid, release.Title, release.Indexer, hash)),
            QueuedAt = DateTime.UtcNow
        };
        db.DownloadQueue.Add(item);
        await db.SaveChangesAsync(ct);

        logger.LogInformation("Grabbed '{Title}' from {Indexer} for series {SeriesId}",
            release.Title, release.Indexer, seriesId);
        return item;
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
            throw new InvalidOperationException("Prowlarr is not configured (Settings → Prowlarr)");
        }

        return (url, apiKey);
    }

    public async Task<(string Url, string Username, string Password, string Category)> GetQbtConfigAsync(CancellationToken ct)
    {
        var url = await settings.GetAsync(SettingKeys.QBittorrentUrl, ct);
        if (string.IsNullOrWhiteSpace(url))
        {
            throw new InvalidOperationException("qBittorrent is not configured (Settings → qBittorrent)");
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
