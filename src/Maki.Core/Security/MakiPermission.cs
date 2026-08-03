namespace Maki.Core.Security;

/// <summary>
/// What a user is allowed to do. Stored as a single int column on the user row and checked
/// through <see cref="MakiPermissions.Grants"/>, never by a bare <c>HasFlag</c> — the
/// <see cref="Admin"/> flag implies every other one and a direct flag test would miss that.
/// <para>
/// Values are persisted, so a member's bit position is part of the schema: append new
/// permissions, never renumber or reuse a retired bit.
/// </para>
/// </summary>
[Flags]
public enum MakiPermission
{
    None = 0,

    /// <summary>
    /// Implies every other permission, plus the surfaces that have no flag of their own —
    /// instance settings, root folders, notifications, backups, user management. Only an
    /// admin may grant it, and an admin may not revoke their own (the last admin standing
    /// would lock the instance out of its own settings).
    /// </summary>
    Admin = 1 << 0,

    AddSeries = 1 << 1,
    DeleteSeries = 1 << 2,

    /// <summary>Enqueue chapter downloads, retry failures, grab torrent releases.</summary>
    DownloadChapters = 1 << 3,

    /// <summary>Cancel or retry queue items, including ones another user queued.</summary>
    ManageDownloadQueue = 1 << 4,

    /// <summary>Per-series source mapping CRUD.</summary>
    ManageSources = 1 << 5,

    /// <summary>Refresh metadata, rewrite ComicInfo, change monitor mode, rescan.</summary>
    EditMetadata = 1 << 6,

    /// <summary>Library-wide tag CRUD and bulk assignment.</summary>
    ManageTags = 1 << 7,

    /// <summary>Edit their <em>own</em> maximum content rating. An admin can always edit anyone's.</summary>
    ChangeContentRating = 1 << 8,

    /// <summary>Connect their own AniList/MAL/Kitsu/MangaBaka account.</summary>
    UseTrackers = 1 << 9,

    /// <summary>Hold an OPDS feed token and read through it.</summary>
    UseOpds = 1 << 10,

    /// <summary>Scan a root folder for unmanaged series and adopt them.</summary>
    ImportLibrary = 1 << 11,
}

public static class MakiPermissions
{
    /// <summary>
    /// Every permission except <see cref="MakiPermission.Admin"/>. What "grant everything but
    /// keep them out of instance settings" means, and the ceiling for a non-admin edit.
    /// </summary>
    public static readonly MakiPermission AllNonAdmin =
        Enum.GetValues<MakiPermission>().Aggregate(MakiPermission.None, (a, p) => a | p)
        & ~MakiPermission.Admin;

    /// <summary>
    /// A sane starting set for a new reader: read the library, keep their own progress, use
    /// OPDS, connect their own trackers. No writes to shared library state.
    /// </summary>
    public const MakiPermission DefaultForNewUser =
        MakiPermission.UseOpds | MakiPermission.UseTrackers;

    /// <summary>
    /// Whether <paramref name="held"/> satisfies <paramref name="required"/>.
    /// <see cref="MakiPermission.Admin"/> satisfies everything.
    /// </summary>
    public static bool Grants(this MakiPermission held, MakiPermission required) =>
        (held & MakiPermission.Admin) != 0 || (held & required) == required;
}
