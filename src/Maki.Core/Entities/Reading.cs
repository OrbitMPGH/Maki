namespace Maki.Core.Entities;

/// <summary>
/// Per-chapter position for the built-in reader. One row per chapter, created the
/// first time it is opened.
/// <para>
/// Three different progress semantics live in the codebase and must not be conflated:
/// <see cref="PageIndex"/> is a <em>resume position</em> and is free to move backwards;
/// <see cref="Completed"/> is <em>sticky</em> and only clears through an explicit
/// mark-unread; <c>ReadingState.MaxChapter</c> is a <em>forward-only high-water mark</em>
/// that nothing here may ever lower.
/// </para>
/// </summary>
public class ChapterProgress
{
    public int Id { get; set; }
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

    public DateTime StartedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

/// <summary>
/// A page the user marked to come back to. Separate from <see cref="ChapterProgress"/> because a
/// chapter has one position but any number of bookmarks, and because clearing progress
/// (mark-unread) must not throw bookmarks away.
/// </summary>
public class ReaderBookmark
{
    public int Id { get; set; }
    public int SeriesId { get; set; }
    public int ChapterId { get; set; }

    /// <summary>Zero-based page within the chapter's own slice, like ChapterProgress.PageIndex.</summary>
    public int PageIndex { get; set; }

    public DateTime CreatedAt { get; set; }
}
