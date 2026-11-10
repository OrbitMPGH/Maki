using Maki.Core.Entities;

namespace Maki.Api.Services;

/// <summary>
/// The one total a series' Reading and Downloads progress count against, and the downloaded and read
/// counts that go with it.
/// <para>
/// Not the Wanted switches: Smart mode wants a few chapters at a time, so a wanted total sits far
/// below the series until the reader is done with it. The total is the locked total, else the largest
/// of MangaBaka's total, the highest whole chapter listed and the number of main chapters listed.
/// Specials (decimal chapter numbers) count on every side only while the reader keeps them: neither
/// the global "unmonitor specials" setting nor the series' Main only mode is on. MangaBaka's total
/// counts main chapters, so kept specials are added on top of it.
/// </para>
/// </summary>
public static class SeriesProgressScope
{
    public sealed record Totals(int Total, int Have, int? Read, bool SpecialsCounted, int ListedSpecials);

    public static bool CountsSpecials(NewChapterMonitorMode mode, bool unmonitorSpecials) =>
        !unmonitorSpecials && mode != NewChapterMonitorMode.MainOnly;

    public static Totals Compute(
        Series series, int known, int had, int specials, int specialsHad, double? highestMain,
        int? read, int readSpecials, bool unmonitorSpecials)
    {
        var countSpecials = CountsSpecials(series.MonitorNewItems, unmonitorSpecials);
        var listedMain = Math.Max(0, known - specials);
        var highest = highestMain is { } h ? (int)Math.Floor(h) : 0;
        // Every main chapter listed is a floor too: a chapter 0, a one-shot or a volume entry is
        // counted as had, and a total below the rows would read "100 of 99".
        var main = series.IsLocked(SeriesMetadataField.TotalChapters) && series.TotalChapters is { } locked
            ? locked
            : Math.Max(Math.Max(series.TotalChapters ?? 0, highest), listedMain);

        return new Totals(
            main + (countSpecials ? specials : 0),
            Math.Max(0, had - (countSpecials ? 0 : specialsHad)),
            read is { } r ? Math.Max(0, r - (countSpecials ? 0 : readSpecials)) : null,
            countSpecials,
            specials);
    }
}
