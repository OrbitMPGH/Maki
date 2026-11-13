using Maki.Core.Entities;

namespace Maki.Api.Services;

/// <summary>
/// The one total a series' Reading and Downloads progress count against, and the downloaded and read
/// counts that go with it.
/// <para>
/// The total is the locked total, else the largest of MangaBaka's total, the highest whole chapter
/// listed and the number of main chapters listed. Specials (decimal chapter numbers) count on every
/// side only while the reader keeps them: neither the global "unmonitor specials" setting nor the
/// series' Main only mode is on. MangaBaka's total usually counts main chapters, so kept specials are
/// added on top of it. Some entries count the extras too (26 chapters and 3 specials listed against
/// 29): when the main chapters fall short of the total and the specials make up exactly the
/// difference, the specials come off MangaBaka's number.
/// </para>
/// <para>
/// A chapter the user switched off (Wanted off, no file, never had one) is left out of the listed
/// floors and of the kept specials, so a skipped prelude reads 17 of 17. Smart mode never writes
/// Wanted, so this holds there too. A None series is the exception: every chapter it discovers is
/// stamped Wanted off, so an off switch there says nothing about the user.
/// </para>
/// </summary>
public static class SeriesProgressScope
{
    /// <param name="Skipped">Chapters left out of <paramref name="Total"/> because they were switched off.</param>
    /// <param name="IncludedSpecials">Specials inside <paramref name="Total"/>; 0 when specials are not counted.</param>
    public sealed record Totals(
        int Total, int Have, int? Read, bool SpecialsCounted, int ListedSpecials, int Skipped = 0, int IncludedSpecials = 0);

    /// <summary>Chapters with Wanted off and no file, and the highest whole chapter that is not one.</summary>
    public sealed record SwitchedOff(int Chapters, int Specials, double? HighestKeptMain);

    public static bool CountsSpecials(NewChapterMonitorMode mode, bool unmonitorSpecials) =>
        !unmonitorSpecials && mode != NewChapterMonitorMode.MainOnly;

    public static Totals Compute(
        Series series, int known, int had, int specials, int specialsHad, double? highestMain,
        int? read, int readSpecials, bool unmonitorSpecials, SwitchedOff? switchedOff = null)
    {
        var countSpecials = CountsSpecials(series.MonitorNewItems, unmonitorSpecials);
        var off = series.MonitorNewItems == NewChapterMonitorMode.None || switchedOff is null
            ? new SwitchedOff(0, 0, highestMain)
            : switchedOff;
        var offMain = Math.Max(0, off.Chapters - off.Specials);
        var mangaBaka = MangaBakaMain(series.TotalChapters, Math.Max(0, known - specials), highestMain, specials);
        var main = MainTotal(series, mangaBaka, Math.Max(0, known - specials - offMain), off.HighestKeptMain);
        // What the switches actually took off: a MangaBaka total above the rows keeps its floor.
        var mainOff = MainTotal(series, mangaBaka, Math.Max(0, known - specials), highestMain) - main;
        var includedSpecials = countSpecials ? Math.Max(0, specials - off.Specials) : 0;
        var total = main + includedSpecials;

        return new Totals(
            total,
            Math.Max(0, had - (countSpecials ? 0 : specialsHad)),
            read is { } r ? Math.Clamp(r - (countSpecials ? 0 : readSpecials), 0, total) : null,
            countSpecials,
            specials,
            Math.Max(0, mainOff) + (countSpecials ? off.Specials : 0),
            includedSpecials);
    }

    // Every main chapter listed is a floor too: a chapter 0, a one-shot or a volume entry is
    // counted as had, and a total below the rows would read "100 of 99".
    private static int MainTotal(Series series, int mangaBaka, int listedMain, double? highestMain)
    {
        if (series.IsLocked(SeriesMetadataField.TotalChapters) && series.TotalChapters is { } locked)
        {
            return locked;
        }

        var highest = highestMain is { } h ? (int)Math.Floor(h) : 0;
        return Math.Max(Math.Max(mangaBaka, highest), listedMain);
    }

    private static int MangaBakaMain(int? total, int listedMain, double? highestMain, int specials)
    {
        if (total is not { } t)
        {
            return 0;
        }

        var main = Math.Max(listedMain, highestMain is { } h ? (int)Math.Floor(h) : 0);
        return specials > 0 && main < t && main + specials == t ? main : t;
    }
}
