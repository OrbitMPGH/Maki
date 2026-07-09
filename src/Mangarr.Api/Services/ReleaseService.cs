using System.Text.Json;
using System.Text.RegularExpressions;
using Mangarr.Core.Configuration;
using Mangarr.Core.Download;
using Mangarr.Core.Entities;
using Mangarr.Core.Indexers;
using Mangarr.Data;
using Microsoft.EntityFrameworkCore;

namespace Mangarr.Api.Services;

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

public partial class ReleaseService(
    MangarrDbContext db,
    ProwlarrClient prowlarr,
    QBittorrentClient qbittorrent,
    SettingsService settings,
    ILogger<ReleaseService> logger)
{
    [GeneratedRegex(@"xt=urn:btih:([0-9a-fA-F]{40}|[a-zA-Z2-7]{32})")]
    private static partial Regex MagnetHash();

    public async Task<IReadOnlyList<ReleaseDto>> SearchAsync(int seriesId, CancellationToken ct = default)
    {
        var series = await db.Series.FindAsync([seriesId], ct)
            ?? throw new InvalidOperationException("Series not found");

        var (url, apiKey) = await GetProwlarrConfigAsync(ct);
        var releases = await prowlarr.SearchAsync(url, apiKey, series.Title, ct);

        return releases
            .Where(r => r.Protocol.Equals("torrent", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(r => r.Seeders ?? 0)
            .Select(r => new ReleaseDto(
                r.Guid, r.Title, r.Size, r.Indexer, r.Seeders, r.Leechers,
                r.Protocol, r.DownloadUrl, r.MagnetUrl, r.InfoUrl))
            .ToList();
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
            await settings.GetAsync(SettingKeys.QBittorrentCategory, ct) ?? "mangarr");
    }
}
