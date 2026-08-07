using Maki.Core.Security;

namespace Maki.Core.Entities;

public enum StatsEventType
{
    SeriesAdded,
    SeriesRemoved,
    ChapterDownloaded,
    ChaptersRead,
    VolumesRead,
    SeriesFinished,

    /// <summary>
    /// Seconds spent reading, in <see cref="StatsEvent.Value"/> like every other type — the one
    /// event whose value is not a count of things. Emitted by the built-in reader only, in chunks
    /// (see <c>ChapterProgress.ReportedSeconds</c>), so a series accumulates several per sitting.
    /// </summary>
    ReadingTime
}

/// <summary>
/// Append-only activity log driving the Rewind feature. Rows are never updated or
/// purged: the series link is severed (not cascaded) on delete, and the denormalized
/// title plus <see cref="PayloadJson"/> snapshot keep the row meaningful afterward.
/// </summary>
public class StatsEvent
{
    public long Id { get; set; }
    public StatsEventType Type { get; set; }
    public DateTime Timestamp { get; set; }

    /// <summary>
    /// Who the event belongs to, or <c>null</c> for a <em>library-wide</em> event that has no reader:
    /// <see cref="StatsEventType.SeriesAdded"/>, <see cref="StatsEventType.SeriesRemoved"/> and
    /// <see cref="StatsEventType.ChapterDownloaded"/> describe the instance, not a person, and
    /// <c>StatsBackfillService</c> seeds them from <c>Series.Added</c>/<c>ChapterFile.DateAdded</c>
    /// where no reader is recorded at all. Rewind shows null rows to everyone and non-null rows only
    /// to their owner. Forcing this non-null would attribute the entire back catalogue's downloads
    /// to whoever happened to be user 1.
    /// </summary>
    public int? UserId { get; set; }

    public int? SeriesId { get; set; }
    public Series? Series { get; set; }

    /// <summary>Set on read events so unmatched Kavita series still aggregate.</summary>
    public int? KavitaSeriesId { get; set; }

    public string SeriesTitle { get; set; } = string.Empty;

    /// <summary>Chapters-read delta / files downloaded in one operation; 1 for lifecycle events.</summary>
    public int Value { get; set; } = 1;

    /// <summary>SeriesRemoved carries {"genres":[...],"tags":[...]} so tag stats survive deletion.</summary>
    public string? PayloadJson { get; set; }
}

/// <summary>
/// Forward-only reading high-water mark, one row per series, fed by <em>both</em> progress
/// sources: the Kavita scan and the built-in reader. Merging them here rather than keeping
/// two tables is what makes double-counting impossible — read deltas are computed against
/// the stored mark, so a chapter read in the reader and later re-reported by Kavita yields
/// a delta of zero. Kept separate from ScrobbleSyncState (which is per tracker service):
/// with two trackers that table would double-count read deltas, and with zero trackers it
/// records nothing.
/// </summary>
public class ReadingState : IUserOwned
{
    public int Id { get; set; }

    /// <summary>
    /// Whose mark this is. Every unique index on this table is prefixed with it, so two users read
    /// the same series through two independent high-water marks and one person's Kavita history can
    /// never advance another's baseline.
    /// </summary>
    public int UserId { get; set; }

    /// <summary>
    /// Null on a <em>native</em> row — one the built-in reader created for a series Kavita
    /// has never reported. The Kavita scan adopts such a row (sets this) the first time it
    /// sees the series, silently, so pre-existing Kavita history is never emitted as stats.
    /// Both keys null means the local series was hard-deleted; the row survives as a
    /// tombstone so Rewind's dropped-series computation stays correct for the year.
    /// </summary>
    public int? KavitaSeriesId { get; set; }

    public int? SeriesId { get; set; }
    public string Title { get; set; } = string.Empty;
    public double MaxChapter { get; set; }
    public double MaxVolume { get; set; }
    public bool Finished { get; set; }

    /// <summary>Last time the mark advanced — drives the "dropped series" computation.</summary>
    public DateTime LastProgressAt { get; set; }

    public DateTime UpdatedAt { get; set; }
}
