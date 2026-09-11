using Maki.Core.Entities;

namespace Maki.Core.Naming;

/// <summary>
/// The formats used when an admin has never set one, plus the sample series and chapter every
/// preview and token example renders against.
/// </summary>
public static class NamingDefaults
{
    /// <summary>
    /// <c>Berserk (1989)</c>. The year disambiguates the remakes and re-serializations that share
    /// a title; <c>{Series TitleYear}</c> falls back to the bare title when the year is unknown,
    /// so no folder is left with an empty pair of brackets.
    /// </summary>
    public const string SeriesFolderFormat = "{Series TitleYear}";

    /// <summary>
    /// Reproduces the names Maki hardcoded before formats existed, exactly:
    /// <c>Berserk Vol.3 Ch.24</c>, <c>One Punch Man Ch.10.5</c>, <c>Look Back</c>,
    /// <c>Series - One-shot Title</c>. Changing this changes what every future download is called,
    /// so the parity cases in FileNameBuilderTests are the guard on it.
    /// </summary>
    public const string ChapterFormat = "{Series Title} {Chapter VolChap}{Chapter OneShotSuffix}";

    public const string ChapterExtension = ".cbz";

    /// <summary>
    /// Fixed sample behind the settings preview and the token picker's example column, in the
    /// spirit of Sonarr's. Deliberately awkward — an apostrophe, an exclamation mark and a leading
    /// article — so the difference between Title, CleanTitle and SortTitle is visible.
    /// </summary>
    public static NamingContext SampleContext() => new(
        new Series
        {
            Title = "The Series Title's!",
            SortTitle = "Series Title's!, The",
            OriginalTitle = "シリーズタイトル",
            Year = 2010,
            Type = SeriesTypes.Manga,
            MangaBakaId = 12345,
            MalId = 11223,
            AniListId = 54321,
            MangaDexUuid = "a1b2c3d4-e5f6-7890-abcd-ef1234567890",
            MangaUpdatesId = "abcdef12345",
            KitsuId = 998
        },
        new Chapter
        {
            Number = 24m,
            Volume = 3,
            Title = "The Chapter Title",
            Language = "en"
        });
}
