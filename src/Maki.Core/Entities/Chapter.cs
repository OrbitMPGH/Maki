using System.Text.Json.Serialization;

namespace Maki.Core.Entities;

public class Chapter
{
    public int Id { get; set; }
    public int SeriesId { get; set; }
    public Series? Series { get; set; }

    /// <summary>Parsed chapter number; supports decimals like 10.5. Null for one-shots.</summary>
    public decimal? Number { get; set; }

    /// <summary>The original, unparsed chapter identifier from the source. Always preserved.</summary>
    public string? NumberRaw { get; set; }

    /// <summary>Null when the source does not group chapters into volumes.</summary>
    public int? Volume { get; set; }

    public string? Title { get; set; }
    public bool IsOneShot { get; set; }

    /// <summary>BCP-47 language tag, e.g. "en".</summary>
    public string Language { get; set; } = "en";

    public DateTime? ReleaseDate { get; set; }

    /// <summary>
    /// Whether the user wants this chapter at all. Purely their intent: it decides what counts
    /// toward a series' chapter total and what is eligible to download, and nothing else.
    /// <para>
    /// Only three things write it — <c>ChapterSyncService</c> stamps it once when a chapter is first
    /// discovered (see <see cref="WantedUnder"/>), the user flips it, and the duplicate merge ORs it.
    /// The download pipeline must never write it: <c>SmartDownloadJob</c> used to rewrite every flag
    /// on each top-up so only its current window stayed set, which silently undid the user's own
    /// choices and made a held-back series read "10 / 10" instead of "10 / 207".
    /// </para>
    /// </summary>
    public bool Wanted { get; set; } = true;

    public int? ChapterFileId { get; set; }
    public ChapterFile? ChapterFile { get; set; }

    /// <summary>
    /// When Maki deleted this chapter's file on purpose, else null. A chapter carrying it with no
    /// file is <em>removed</em>: it still counts toward the series total and its reads still count,
    /// but it is not missing, so nothing queues it again. Separate from <see cref="Wanted"/> so a
    /// delete never rewrites user intent. <c>MakiDbContext</c> clears it whenever a file is linked,
    /// so a later unlink of that new file reads as missing again rather than as removed.
    /// </summary>
    public DateTime? FileRemovedAt { get; set; }

    /// <summary>The per-source listings that currently support this chapter.</summary>
    [JsonIgnore]
    public List<ChapterSourceLink> SourceLinks { get; set; } = [];

    /// <summary>
    /// Wanted, not on disk, and not removed on purpose: what downloads go looking for. Every
    /// download selector and missing count uses this. The SQL tallies in <c>SeriesController</c> and
    /// <c>ReadCounts</c> spell the same condition out, since EF can't translate a method call.
    /// </summary>
    public static bool IsMissing(Chapter c) => c.Wanted && c.ChapterFileId == null && c.FileRemovedAt == null;

    /// <summary>The file was deleted on purpose and nothing has replaced it.</summary>
    public static bool IsRemoved(Chapter c) => c.ChapterFileId == null && c.FileRemovedAt != null;

    /// <summary>A special is a decimal-numbered chapter (10.5 omake etc.); one-shots are not specials.</summary>
    public static bool IsSpecial(decimal? number) => number is { } n && n % 1 != 0;

    /// <summary>
    /// What <see cref="Wanted"/> is stamped as when a chapter with this number is first discovered.
    /// <para>
    /// <c>All</c> ignores <paramref name="skipSpecials"/> on purpose: the global setting already
    /// downgrades a new series' mode from All to MainOnly in
    /// <c>SeriesCreationService.DefaultedMonitorMode</c>, and that is the only place it should decide
    /// a mode. <c>Smart</c> has to honour it directly, since it can't be combined with MainOnly.
    /// </para>
    /// </summary>
    public static bool WantedUnder(NewChapterMonitorMode mode, decimal? number, bool skipSpecials) => mode switch
    {
        NewChapterMonitorMode.All => true,
        NewChapterMonitorMode.MainOnly => !IsSpecial(number),
        NewChapterMonitorMode.Smart => !skipSpecials || !IsSpecial(number),
        _ => false // None
    };

    /// <summary>
    /// The next <paramref name="count"/> chapters a user would expect to get: wanted, not yet on
    /// disk, in chapter-number order. Shared by Smart top-ups and the series page's "download next N",
    /// so the two can't disagree about what "next" means.
    /// <para>
    /// Ordering is done in memory because <see cref="Number"/> is stored as REAL and EF Core SQLite
    /// can't ORDER BY it. One-shots have no number to sort on and go last.
    /// </para>
    /// </summary>
    /// <param name="skip">
    /// Chapters to leave out before counting, so they free their place in the window rather than
    /// holding it. Smart passes the ones whose last download failed and is not yet due a retry.
    /// </param>
    public static List<int> NextWanted(IEnumerable<Chapter> chapters, int count, IReadOnlySet<int>? skip = null) =>
        chapters
            .Where(c => IsMissing(c) && (skip is null || !skip.Contains(c.Id)))
            .OrderBy(c => c.Number ?? decimal.MaxValue)
            .ThenBy(c => c.Id)
            .Take(count)
            .Select(c => c.Id)
            .ToList();
}
