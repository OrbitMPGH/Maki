using Maki.Core.Security;

namespace Maki.Core.Entities;

/// <summary>
/// Per-chapter read state. One row per chapter, created the first time it is opened in the
/// built-in reader or the first time Kavita reports it read.
/// <para>
/// This table is the <b>ground truth for what has been read</b> — every read count the UI shows
/// is a count of rows here. <c>ReadingState.MaxChapter</c> is not: it is a forward-only aggregate
/// kept for Rewind's deltas and for forward-only tracker pushes, and it deliberately cannot be
/// lowered, which makes it wrong to display (a mark of 1 left behind by a since-corrected Kavita
/// read reported "1 chapter read" on a series that had never been opened).
/// </para>
/// <para>
/// Three progress semantics live in the codebase and must not be conflated:
/// <see cref="PageIndex"/> is a <em>resume position</em> and is free to move backwards;
/// <see cref="Completed"/> is <em>sticky</em> and only clears through an explicit
/// mark-unread; <c>ReadingState.MaxChapter</c> is a <em>forward-only high-water mark</em>
/// that nothing here may ever lower.
/// </para>
/// </summary>
public class ChapterProgress : IUserOwned
{
    public int Id { get; set; }

    /// <summary>
    /// Whose read this is. Part of the row's identity — the unique index is
    /// <c>(UserId, ChapterId)</c>, so two people reading the same chapter get two rows and neither
    /// sees the other's position. Never nullable: a progress row with no reader has no meaning, and
    /// a nullable column would need every read path to decide what "everyone's" progress means.
    /// </summary>
    public int UserId { get; set; }

    public int SeriesId { get; set; }
    public int ChapterId { get; set; }

    /// <summary>Zero-based page within the chapter's own slice, not within the archive.</summary>
    public int PageIndex { get; set; }

    /// <summary>Page count of the chapter's slice, snapshotted so "% read" needs no archive open.</summary>
    public int PageCount { get; set; }

    /// <summary>
    /// Sticky. Doubles as the idempotency token for the read events a chapter emits —
    /// only the false → true transition may emit, so a re-read never double-counts.
    /// </summary>
    public bool Completed { get; set; }

    /// <summary>
    /// The read was observed in Kavita rather than in Maki's own reader, so no page position is
    /// known (<see cref="PageCount"/> stays 0 until the chapter is opened here). Display-only: it
    /// separates "you read this here" from "Kavita says you read this", and nothing branches on it
    /// beyond that.
    /// </summary>
    public bool External { get; set; }

    /// <summary>
    /// When the user explicitly marked this chapter unread in Maki, else null. A row that carries
    /// it is a <em>tombstone</em>: the chapter is unread, and the recurring Kavita scan must leave
    /// it alone. Without that, un-reading a chapter Kavita still reports as read would silently
    /// undo itself on the next tick. Cleared when the chapter is read again.
    /// </summary>
    public DateTime? UnreadAt { get; set; }

    public DateTime StartedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

/// <summary>
/// A named set of reader display settings, private to one user, optionally claiming a set of
/// <see cref="SeriesTypes"/> so it is picked automatically.
/// <para>
/// The point of the type claim is that a manhwa opens as a continuous left-to-right strip and a
/// manga stays single-page right-to-left without anybody configuring either. Three profiles are
/// seeded per account (<c>ReadingProfileSeeder</c>); they are ordinary rows, so they can be
/// renamed, retuned, re-pointed at other types or deleted like any profile the user writes.
/// </para>
/// <para>
/// Resolution order for a series, in <c>ReadingProfileService.ResolveAsync</c>: the series'
/// own ad-hoc override, then the profile explicitly pinned to the series, then the profile
/// claiming the series' type, then the user's global <c>reader.prefs</c>. The last step is why
/// nothing had to be migrated: a library whose series have no type yet, or whose types no profile
/// claims, behaves exactly as it did before profiles existed.
/// </para>
/// </summary>
public class ReadingProfile : IUserOwned
{
    public int Id { get; set; }
    public int UserId { get; set; }

    /// <summary>Display name, unique per user (NOCASE), and how the reader's picker labels it.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>A <c>ReaderPrefsSpec</c> blob, same never-rename-a-property discipline as everywhere else it is stored.</summary>
    public string PrefsJson { get; set; } = string.Empty;

    /// <summary>
    /// Comma-separated <see cref="SeriesTypes"/> values this profile is auto-selected for; empty
    /// means "never automatically, only when pinned to a series". A type is claimed by at most one
    /// of a user's profiles — the write path rejects a second claimant rather than picking a winner,
    /// because a silent tie-break is a setting that appears to have been ignored.
    /// </summary>
    public string SeriesTypes { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    public IReadOnlyList<string> Types() =>
        SeriesTypes.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}

/// <summary>
/// A page the user marked to come back to. Separate from <see cref="ChapterProgress"/> because a
/// chapter has one position but any number of bookmarks, and because clearing progress
/// (mark-unread) must not throw bookmarks away.
/// </summary>
public class ReaderBookmark : IUserOwned
{
    public int Id { get; set; }
    public int UserId { get; set; }
    public int SeriesId { get; set; }
    public int ChapterId { get; set; }

    /// <summary>Zero-based page within the chapter's own slice, like ChapterProgress.PageIndex.</summary>
    public int PageIndex { get; set; }

    public DateTime CreatedAt { get; set; }
}
