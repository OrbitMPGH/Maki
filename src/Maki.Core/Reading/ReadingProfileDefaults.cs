using Maki.Core.Entities;

namespace Maki.Core.Reading;

/// <summary>The profiles every account starts with. Ordinary rows once seeded: editable, renameable, deletable.</summary>
public record ReadingProfileSeed(string Name, ReaderPrefsSpec Prefs, string[] Types);

/// <summary>
/// What a fresh account's reading profiles look like. Three, one per reading direction the library
/// actually contains: Japanese manga right-to-left a page at a time, Korean/Chinese webtoons as a
/// continuous strip at their native width, western-drawn (OEL) comics left-to-right.
/// <para>
/// Deliberately duplicated as literal SQL in the <c>ReadingProfiles</c> migration rather than called
/// from it. A migration is a snapshot of what happened at one version; editing these values later
/// must change what a <em>new</em> account gets, never rewrite the history of an upgrade that has
/// already run somewhere.
/// </para>
/// </summary>
public static class ReadingProfileDefaults
{
    public static readonly ReadingProfileSeed[] All =
    [
        new("Manga", new ReaderPrefsSpec(
                Mode: ReaderPrefsSpec.ModePaged,
                Direction: ReaderPrefsSpec.DirectionRtl,
                Fit: ReaderPrefsSpec.FitHeight),
            [SeriesTypes.Manga]),

        // 1:1 rather than fit-width: a webtoon panel is authored at a fixed pixel width, and
        // stretching it to a desktop viewport is upscaling a JPEG to twice its size.
        new("Webtoon", new ReaderPrefsSpec(
                Mode: ReaderPrefsSpec.ModeVertical,
                Direction: ReaderPrefsSpec.DirectionLtr,
                Fit: ReaderPrefsSpec.FitOriginal),
            [SeriesTypes.Manhwa, SeriesTypes.Manhua]),

        new("Comic", new ReaderPrefsSpec(
                Mode: ReaderPrefsSpec.ModePaged,
                Direction: ReaderPrefsSpec.DirectionLtr,
                Fit: ReaderPrefsSpec.FitHeight),
            [SeriesTypes.Oel]),
    ];
}
