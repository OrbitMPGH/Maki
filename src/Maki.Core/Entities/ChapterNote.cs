namespace Maki.Core.Entities;

/// <summary>
/// Text a user attached to a chapter, optionally pinned to one page. Private notes are seen only by
/// their author, admins included; a shared root is the start of a one-level thread that everyone who
/// can see the series reads and answers.
/// <para>
/// Not <see cref="Security.IUserOwned"/>: the query filter is "mine, or shared on a series I can
/// see", so <see cref="AuthorId"/> is set explicitly on every insert rather than stamped.
/// </para>
/// </summary>
public class ChapterNote
{
    public const int MaxBodyLength = 4000;

    public int Id { get; set; }
    public int SeriesId { get; set; }
    public int ChapterId { get; set; }

    /// <summary>Null once the author's account is deleted and the note stays as part of someone else's thread.</summary>
    public int? AuthorId { get; set; }

    /// <summary>Null for a thread's first note. Replies are one level deep: a reply's parent is always a root.</summary>
    public int? ParentId { get; set; }

    /// <summary>Zero-based page within the chapter's own slice, like ReaderBookmark.PageIndex. Null = the whole chapter.</summary>
    public int? PageIndex { get; set; }

    /// <summary>A reply stores its root's, so the query filter needs no join.</summary>
    public NoteVisibility Visibility { get; set; }

    public string Body { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; }
    public DateTime? EditedAt { get; set; }

    /// <summary>Set when a shared root with other people's replies is deleted: the body is cleared and the thread stays readable.</summary>
    public DateTime? DeletedAt { get; set; }

    /// <summary>The deletion at <see cref="DeletedAt"/> was an admin's, not the author's.</summary>
    public bool DeletedByModerator { get; set; }
}

/// <summary>Persisted as an int: append only, never renumber.</summary>
public enum NoteVisibility
{
    Private = 0,
    Shared = 1,
}
