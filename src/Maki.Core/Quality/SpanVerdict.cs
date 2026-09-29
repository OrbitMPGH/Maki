using Maki.Core.Parsing;

namespace Maki.Core.Quality;

public enum ChapterSpanState { Upgrade, AlreadyMet, Skipped, Missing, Unknown }

public sealed record SpanChapter(decimal Number, int? Volume, ChapterSpanState State, int? ChapterFileId);

public enum SpanOutcome { Ignore, Proposal, AutoGrab }

public sealed record SpanVerdictInput(
    ReleaseSpan Span,
    bool TitleMatched,
    bool VolumeMappingKnown,
    IReadOnlyList<SpanChapter> Chapters,
    long SizeBytes,
    long AutoGrabMaxBytes,
    int MissingTolerancePerVolume,
    bool WholeSeries = false);

public sealed record SpanVerdict(
    SpanOutcome Outcome,
    IReadOnlyList<string> Reasons,
    int UpgradeCount, int AlreadyMetCount, int SkippedCount, int MissingCount, int UnknownCount,
    IReadOnlyList<int> ReplacedFileIds);

/// <summary>Catalogue keys a <see cref="SpanVerdict"/> carries. Persisted on proposals, so never rename one.</summary>
public static class SpanVerdictReasons
{
    public const string TitleUncertain = "title_uncertain";
    public const string NothingToUpgrade = "nothing_to_upgrade";
    public const string NoSpan = "no_span";
    public const string NoProfile = "no_profile";
    public const string UnknownChapters = "unknown_chapters";
    public const string VolumeSpanUnknown = "volume_span_unknown";
    public const string AddsMissingChapters = "adds_missing_chapters";
    public const string OverBudget = "over_budget";
    public const string WholeSeriesPack = "whole_series_pack";

    /// <summary>The only chapter number was a bare number ending the title, which can be part of a name.</summary>
    public const string TrailingNumber = "trailing_number";

    /// <summary>A manual search ran with the instance switches off, so nothing is grabbed on its own.</summary>
    public const string UpgradesDisabled = "upgrades_disabled";
}

public static class SpanVerdictExtensions
{
    /// <summary>The verdict at most a proposal, carrying <paramref name="reason"/> unless it is ignored.</summary>
    public static SpanVerdict AtMostProposal(this SpanVerdict verdict, string reason) => verdict.Outcome switch
    {
        SpanOutcome.AutoGrab => verdict with { Outcome = SpanOutcome.Proposal, Reasons = [reason] },
        SpanOutcome.Proposal when !verdict.Reasons.Contains(reason) => verdict with { Reasons = [.. verdict.Reasons, reason] },
        _ => verdict
    };
}

/// <summary>
/// Decides what a torrent release covering part of a series should do: nothing, wait for a click,
/// or grab on its own. Only a release that improves files already on disk and adds at most a few
/// chapters the user does not have is ever grabbed automatically.
/// </summary>
public static class SpanVerdicts
{
    public static SpanVerdict Evaluate(SpanVerdictInput input)
    {
        var chapters = input.Chapters;
        int Count(ChapterSpanState state) => chapters.Count(c => c.State == state);
        var upgrade = Count(ChapterSpanState.Upgrade);
        var replaced = chapters
            .Where(c => c.State == ChapterSpanState.Upgrade && c.ChapterFileId is not null)
            .Select(c => c.ChapterFileId!.Value)
            .Distinct()
            .ToList();

        SpanVerdict Verdict(SpanOutcome outcome, IReadOnlyList<string> reasons) => new(outcome, reasons,
            upgrade, Count(ChapterSpanState.AlreadyMet), Count(ChapterSpanState.Skipped),
            Count(ChapterSpanState.Missing), Count(ChapterSpanState.Unknown), replaced);

        if (!input.TitleMatched) return Verdict(SpanOutcome.Ignore, [SpanVerdictReasons.TitleUncertain]);
        if (upgrade == 0) return Verdict(SpanOutcome.Ignore, [SpanVerdictReasons.NothingToUpgrade]);
        if (input.Span.IsEmpty && !input.WholeSeries) return Verdict(SpanOutcome.Ignore, [SpanVerdictReasons.NoSpan]);

        // A pack with no span in its name claims the whole series, which nothing can check before the
        // download, so it always waits for a click.
        var reasons = new List<string>();
        if (input.WholeSeries) reasons.Add(SpanVerdictReasons.WholeSeriesPack);
        if (chapters.Any(c => c.State == ChapterSpanState.Unknown)) reasons.Add(SpanVerdictReasons.UnknownChapters);
        if (input.Span.Volumes is not null && !input.VolumeMappingKnown) reasons.Add(SpanVerdictReasons.VolumeSpanUnknown);
        if (AddsMissingChapters(input)) reasons.Add(SpanVerdictReasons.AddsMissingChapters);
        if (input.SizeBytes > input.AutoGrabMaxBytes) reasons.Add(SpanVerdictReasons.OverBudget);

        return reasons.Count > 0 ? Verdict(SpanOutcome.Proposal, reasons) : Verdict(SpanOutcome.AutoGrab, []);
    }

    // A missing chapter in a loose chapter segment always counts; inside a volume only past the
    // tolerance. One that sits in neither can't be shown to belong to a volume, so it counts too.
    private static bool AddsMissingChapters(SpanVerdictInput input)
    {
        var volumes = input.Span.Volumes;
        var perVolume = new Dictionary<int, int>();
        foreach (var chapter in input.Chapters.Where(c => c.State == ChapterSpanState.Missing))
        {
            if (input.Span.ChapterSegments.Any(s => chapter.Number >= s.Start && chapter.Number <= s.End)) return true;

            if (chapter.Volume is not { } volume || volumes is null || volume < volumes.Start || volume > volumes.End)
            {
                return true;
            }

            perVolume[volume] = perVolume.GetValueOrDefault(volume) + 1;
            if (perVolume[volume] > input.MissingTolerancePerVolume) return true;
        }

        return false;
    }
}
