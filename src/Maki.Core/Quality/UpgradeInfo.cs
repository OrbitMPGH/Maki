using System.Text.Json;

namespace Maki.Core.Quality;

/// <summary>What an upgrade queue row expects to replace and with what; stored as <c>DownloadQueueItem.UpgradeInfoJson</c>.</summary>
public sealed class UpgradeInfo
{
    public int ChapterFileId { get; set; }
    public int AttemptId { get; set; }
    public int ProfileId { get; set; }
    public int ProfileVersion { get; set; }
    public QualitySnapshot Before { get; set; } = new();
    public QualitySnapshot Predicted { get; set; } = new();
    public QualitySnapshot? After { get; set; }
    public string Outcome { get; set; } = UpgradeOutcomes.Pending;
    public string? Reason { get; set; }
    public int? HistoryId { get; set; }

    /// <summary>A user picked this copy: it replaces the file whatever the profile thinks of it.</summary>
    public bool Force { get; set; }

    /// <summary>With <see cref="Force"/>, also skips the page count, width and protect checks.</summary>
    public bool IgnoreGuards { get; set; }

    public string Serialize() => JsonSerializer.Serialize(this, QualitySnapshot.Json);

    /// <summary>Null for a missing or unreadable value.</summary>
    public static UpgradeInfo? Parse(string? json)
    {
        if (string.IsNullOrEmpty(json)) return null;
        try
        {
            return JsonSerializer.Deserialize<UpgradeInfo>(json, QualitySnapshot.Json);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

public static class UpgradeOutcomes
{
    public const string Pending = "pending";
    public const string Applied = "applied";
    public const string Rejected = "rejected";
}

/// <summary>A chapter file's quality attributes at one moment, for the queue and the upgrade history.</summary>
public sealed class QualitySnapshot
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>Lowercase <see cref="QualityTier"/> name.</summary>
    public string Tier { get; set; } = "unknown";
    public string? SourceName { get; set; }
    public string? SourceChapterId { get; set; }
    public string? Group { get; set; }
    public int? PageCount { get; set; }
    public int? MedianWidth { get; set; }
    public int? MedianHeight { get; set; }
    public string? ImageFormat { get; set; }
    public long? SizeBytes { get; set; }
    public int Score { get; set; }

    /// <summary>The torrent release a file came from, kept so a revert can give it back.</summary>
    public string? ReleaseName { get; set; }
    public string? ReleaseHash { get; set; }

    public static string TierName(QualityTier tier) => tier.ToString().ToLowerInvariant();

    public QualityTier ParsedTier() =>
        Enum.TryParse<QualityTier>(Tier, ignoreCase: true, out var tier) && Enum.IsDefined(tier) ? tier : QualityTier.Unknown;

    public string Serialize() => JsonSerializer.Serialize(this, Json);

    public static QualitySnapshot? Parse(string? json)
    {
        if (string.IsNullOrEmpty(json)) return null;
        try
        {
            return JsonSerializer.Deserialize<QualitySnapshot>(json, Json);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
