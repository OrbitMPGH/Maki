namespace Maki.Core.Mihon;

/// <summary>The library half of a Mihon backup: favourite manga only, the rest is counted.</summary>
public sealed record MihonBackup(IReadOnlyList<MihonManga> Manga, int SkippedNonLibrary, int CategoryCount);

/// <param name="Index">Position among every manga in the backup, favourite or not.</param>
/// <param name="Categories">Category names, empty for Mihon's implicit Default category.</param>
/// <param name="ReadNumbers">Distinct, sorted numbers of read chapters; unknown (-1) numbers are left out.</param>
/// <param name="TrackerLastRead">Furthest chapter a tracker reports, set only when the manga has no chapter rows.</param>
/// <param name="ReadCount">Every read chapter, numbered or not.</param>
public sealed record MihonManga(
    int Index,
    string Title,
    string? Author,
    IReadOnlyList<string> Categories,
    int? AniListId,
    int? MalId,
    int? KitsuId,
    string? MangaBakaId,
    IReadOnlyList<decimal> ReadNumbers,
    decimal? TrackerLastRead,
    int ChapterCount,
    int ReadCount);
