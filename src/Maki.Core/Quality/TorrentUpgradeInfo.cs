using System.Text.Json;

namespace Maki.Core.Quality;

/// <summary>
/// What a torrent upgrade queue row expects to replace; stored as <c>DownloadQueueItem.UpgradeInfoJson</c>
/// on torrent items. <see cref="Kind"/> tells it apart from a scraper row's <see cref="UpgradeInfo"/>.
/// </summary>
public sealed class TorrentUpgradeInfo
{
    public const string KindName = "torrent";

    public string Kind { get; set; } = KindName;
    public int? ProposalId { get; set; }
    public int ProfileId { get; set; }
    public int ProfileVersion { get; set; }
    public List<int> ReplacedFileIds { get; set; } = [];
    public List<string> SkipFileNames { get; set; } = [];
    public List<string> Reasons { get; set; } = [];

    /// <summary>The chapter language the verdict judged; the import links only chapters of it.</summary>
    public string? Language { get; set; }

    /// <summary>pending, applied, parked or rejected.</summary>
    public string Outcome { get; set; } = TorrentUpgradeOutcomes.Pending;

    public Guid? HistoryGroupId { get; set; }

    public string Serialize() => JsonSerializer.Serialize(this, QualitySnapshot.Json);

    /// <summary>Null for a missing value, an unreadable one, or a scraper row's <see cref="UpgradeInfo"/>.</summary>
    public static TorrentUpgradeInfo? Parse(string? json)
    {
        if (string.IsNullOrEmpty(json)) return null;
        try
        {
            using (var doc = JsonDocument.Parse(json))
            {
                if (doc.RootElement.ValueKind != JsonValueKind.Object ||
                    !doc.RootElement.TryGetProperty("kind", out var kind) ||
                    kind.ValueKind != JsonValueKind.String || kind.GetString() != KindName)
                {
                    return null;
                }
            }

            return JsonSerializer.Deserialize<TorrentUpgradeInfo>(json, QualitySnapshot.Json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Whether this JSON belongs to a torrent row, so the scraper shape must not be read from it.</summary>
    public static bool IsTorrent(string? json) => Parse(json) is not null;
}

public static class TorrentUpgradeOutcomes
{
    public const string Pending = "pending";
    public const string Applied = "applied";
    public const string Parked = "parked";
    public const string Rejected = "rejected";
}
