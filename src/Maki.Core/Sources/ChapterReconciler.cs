using System.Text.RegularExpressions;

namespace Maki.Core.Sources;

/// <summary>One chapter as one mapping lists it: what reconciliation reads.</summary>
public sealed record ReconcileEntry(decimal Number, string? Title, int? Pages);

/// <summary>One mapping's whole chapter list for a series and language, one entry per number.</summary>
public sealed record ReconcileListing(string Mapping, IReadOnlyList<ReconcileEntry> Entries);

public enum FoldState
{
    /// <summary>No sub-chapters on this floor; nothing to decide.</summary>
    Plain,

    /// <summary>The sub-chapters stay rows of their own.</summary>
    Keep,

    /// <summary>Every mapping that lists the floor lists it in parts and none the whole.</summary>
    AgreedSplit,

    /// <summary>A whole chapter elsewhere is these parts put together.</summary>
    Merged,

    /// <summary>Could be either; a page count would settle it.</summary>
    Pending,
}

/// <summary>A whole compared with a run of parts by page count, after dividing the whole by the pair's page scale.</summary>
public sealed record PageRatio(string WholeMapping, string PartsMapping, double Ratio, string Verdict, double Scale);

/// <summary>What reconciliation decided for one integer floor (chapter 10 and its 10.1, 10.2, ...).</summary>
public sealed class FloorDecision(decimal floor)
{
    public decimal Floor { get; } = floor;
    public FoldState State { get; internal set; } = FoldState.Plain;

    /// <summary>The parts become one chapter. Agreed splits only fold below the settled floor.</summary>
    public bool Fold { get; internal set; }

    /// <summary>A short code for why, for logs and the preview; not user text.</summary>
    public string Reason { get; internal set; } = string.Empty;

    /// <summary>The certification behind a looser ceiling: publisher numbering, part titles or a shared title.</summary>
    public string? Certified { get; internal set; }

    /// <summary>Per mapping, the numbers that are parts, in reading order. On a pending floor, the candidates.</summary>
    public Dictionary<string, IReadOnlyList<decimal>> Parts { get; } = [];

    /// <summary>Mappings whose own whole chapter stands for the floor. On a pending floor, the candidates.</summary>
    public List<string> Wholes { get; } = [];

    /// <summary>Mappings whose listed whole is really part 1 ("part1", or "part1UnderWhole" when it never stands for the chapter).</summary>
    public Dictionary<string, string> Aliases { get; } = [];

    public List<PageRatio> Ratios { get; } = [];

    /// <summary>Plain chapters to count before the page scale between two of the floor's mappings is known.</summary>
    public List<(string Mapping, decimal Number)> Calibration { get; } = [];

    /// <summary>What page counts alone decided, before <see cref="ChapterReconciler.CheckPages"/> looked at the pages.</summary>
    public string? Nominated { get; internal set; }

    public List<PageCheck> PageChecks { get; } = [];

    /// <summary>Parts whose pages no whole on the floor holds: they stay rows of their own whatever else folds.</summary>
    public Dictionary<string, IReadOnlyList<decimal>> Held { get; } = [];

    /// <summary>Parts still waiting on page hashes while another mapping's parts on the floor already folded.</summary>
    public Dictionary<string, IReadOnlyList<decimal>> Waiting { get; } = [];

    /// <summary>Chapters whose page hashes the page check waits on.</summary>
    public List<(string Mapping, decimal Number)> MissingPages { get; } = [];
}

/// <summary>One whole upload compared with one mapping's parts, page by page or (<paramref name="Method"/> "strip") as strips.</summary>
public sealed record PageCheck(
    string WholeMapping, string PartsMapping, string Verdict, IReadOnlyList<double> Shares, double Cover, string Method = "pages");

/// <summary>One upload's stored hashes: one per page, and its strip (<c>StripHash</c>) when it has one.</summary>
public sealed record PageEvidence(IReadOnlyList<ulong> Pages, IReadOnlyList<ulong>? Strip);

/// <summary>
/// Decides, per integer floor of a series, whether a source's sub-chapters (10.1, 10.2, ...) are parts
/// of one chapter that another source, or the same one, lists whole. Pure: it reads listings and page
/// counts and fetches nothing. Page counts only nominate a merge, because decimals also mean extras,
/// repeats of one upload, epilogues, prologue numbering and wrong source matches, and those add up by
/// chance; <see cref="CheckPages"/> then holds it to the pages. A wrong merge hides content, a leftover
/// duplicate row only looks untidy.
/// <para>
/// A port of <c>reconcile.py</c> from the 2026-11-17 reconciliation research. The tests hold it to that
/// prototype's answer on every floor of the labelled corpus, so change both or neither.
/// </para>
/// </summary>
public static partial class ChapterReconciler
{
    /// <summary>whole / sum(parts) below this: the whole does not contain the parts.</summary>
    public const double Low = 0.75;

    /// <summary>Above this the whole holds more than the parts account for.</summary>
    public const double High = 1.45;

    /// <summary>The looser ceiling when the parts are known to be parts (publisher numbering, part titles).</summary>
    public const double HighCertified = 1.75;

    /// <summary>A merged whole has to be larger than any one of its parts.</summary>
    private const double WholeOverPart = 1.15;

    /// <summary>A "whole" within this many pages (or 12 percent) of part 1 is part 1 under another number.</summary>
    private const int AliasPages = 2;

    /// <summary>Floors one (whole source, parts source) pair must verify before the rest follow unprobed.</summary>
    private const int PairFloors = 3;

    /// <summary>A third or later sub-chapter this small against the parts before it is an extra.</summary>
    private const double TinyTail = 0.5;

    /// <summary>Plain floors two mappings must both have counted before their page scale is trusted.</summary>
    private const int ScaleFloors = 3;

    /// <summary>Most plain floors counted to calibrate one pair.</summary>
    private const int ScaleFloorsMax = 6;

    /// <summary>A plain floor whose ratio is within this factor of the median agrees with it.</summary>
    private const double ScaleSpread = 1.35;

    /// <summary>A learnt scale this close to 1 is 1: two sources of one chapter differ by a credits page or two.</summary>
    private const double ScaleSame = 1.15;

    [GeneratedRegex(
        @"\b(omake|extras?|special|bonus|side[- ]?story|gaiden|twitter|afterword|illustrations?|4-?koma|interlude|preview|pilot|prototype)\b|番外|おまけ|特別|卷末",
        RegexOptions.IgnoreCase)]
    private static partial Regex ExtraTitle();

    [GeneratedRegex(@"\b(?:part|pt)\.?\s*(\d+)\b|\((\d)\)\s*$|\[(\d)\]\s*$|[-–]\s*(\d)\s*$|前編|中編|後編", RegexOptions.IgnoreCase)]
    private static partial Regex PartMarker();

    [GeneratedRegex(@"^\s*(?:chapter|ch\.?|episode|ep\.?)\s*\d+(?:\.\d+)?\s*[:\-–]?\s*", RegexOptions.IgnoreCase)]
    private static partial Regex ChapterLabel();

    [GeneratedRegex(@"^[(\[][^()\[\]]*[)\]]\z")]
    private static partial Regex BracketTag();

    private static readonly char[] StemTrim = [' ', '-', '–', ':', '(', ')', '[', ']'];

    public static IReadOnlyDictionary<decimal, FloorDecision> Decide(
        IReadOnlyList<ReconcileListing> listings, IReadOnlyCollection<decimal>? publisherNumbers = null,
        decimal? settledBelow = null)
    {
        var publisher = (publisherNumbers ?? [])
            .GroupBy(decimal.Floor)
            .ToDictionary(g => g.Key, g => g.Distinct().Count());
        var extras = ExtraNumbers(listings);
        var floors = listings.SelectMany(l => l.Entries).Select(e => decimal.Floor(e.Number)).Distinct().Order().ToList();
        var lastFloor = floors.Count > 0 ? floors[^1] : 0m;
        var settled = settledBelow ?? lastFloor;
        var decisions = new Dictionary<decimal, FloorDecision>();
        var scales = new Scales(listings, lastFloor);
        var pairVotes = new Dictionary<(string Whole, string Parts), int[]>();
        var pending = new List<(FloorDecision Decision, List<string> Wholes, Ordered<List<ReconcileEntry>> Runs)>();

        foreach (var floor in floors)
        {
            var d = new FloorDecision(floor);
            decisions[floor] = d;
            var wholes = new Ordered<ReconcileEntry>();
            var runs = new Ordered<List<ReconcileEntry>>();
            var firstRuns = new Ordered<List<ReconcileEntry>>();
            foreach (var listing in listings)
            {
                var (whole, run, firstRun) = RunOf(listing.Entries, floor, extras, keepTail: false);
                if (whole is not null)
                {
                    wholes.Set(listing.Mapping, whole);
                }

                if (run.Count > 0)
                {
                    runs.Set(listing.Mapping, run);
                }

                if (firstRun.Count > 0)
                {
                    firstRuns.Set(listing.Mapping, firstRun);
                }
            }

            if (runs.Count == 0 && firstRuns.Count == 0)
            {
                continue;
            }

            if (floor == 0)
            {
                Settle(d, FoldState.Keep, "prologueNumbering");
                continue;
            }

            (Verdict Verdict, int K, double? Ratio, double Scale) Check(
                string wholeMapping, ReconcileEntry whole, string partsMapping, List<ReconcileEntry> run, double high)
            {
                var scale = scales.Of(wholeMapping, partsMapping);
                switch (scale.State)
                {
                    case ScaleState.Unstable:
                        return (Verdict.Unscaled, run.Count, null, 1);
                    case ScaleState.Pending:
                        foreach (var needed in scale.Needed.Where(n => !d.Calibration.Contains(n)))
                        {
                            d.Calibration.Add(needed);
                        }

                        return (Verdict.Unknown, run.Count, null, 1);
                    default:
                        var (verdict, k, ratio) = Verify(whole, run, high, scale.Factor);
                        return (verdict, k, ratio, scale.Factor);
                }
            }

            // "f, f.2" with no f.1: the f is part 1 when another source's whole matches the sum, or the titles say so.
            var aliasPending = new Ordered<List<ReconcileEntry>>();
            foreach (var (mapping, firstRun) in firstRuns.Items)
            {
                var titled = firstRun.Any(e => IsPartMarker(e.Title));
                var checks = wholes.Items
                    .Where(w => w.Key != mapping)
                    .Select(w => Check(w.Key, w.Value, mapping, firstRun, titled ? HighCertified : High).Verdict)
                    .ToList();
                if (checks.Contains(Verdict.Merged) || (titled && wholes.Count == 1))
                {
                    runs.Set(mapping, firstRun);
                    wholes.Remove(mapping);
                    d.Aliases[mapping] = "part1";
                }
                else if (checks.Contains(Verdict.Unknown))
                {
                    // Another source's whole could settle whether this f is part 1, once it has a page count.
                    aliasPending.Set(mapping, firstRun);
                }
            }

            var longRuns = new Ordered<List<ReconcileEntry>>();
            foreach (var (mapping, run) in runs.Items.Where(r => r.Value.Count >= 2))
            {
                longRuns.Set(mapping, run);
            }

            if (longRuns.Count == 0)
            {
                if (aliasPending.Count > 0)
                {
                    Settle(d, FoldState.Pending, "needsPageCountPart1");
                    d.Wholes.AddRange(wholes.Items.Select(w => w.Key).Where(m => aliasPending.Items.All(a => a.Key != m)));
                    AddParts(d, aliasPending, int.MaxValue);
                }
                else
                {
                    Settle(d, FoldState.Keep, "loneSubChapter");
                }

                continue;
            }

            if (wholes.Count == 0)
            {
                Settle(d, FoldState.AgreedSplit, floor < settled ? "agreedSplit" : "agreedSplitNewest");
                d.Fold = floor < settled;
                AddParts(d, longRuns, int.MaxValue);
                continue;
            }

            var certified = Certified(publisher, floor, longRuns);
            var high = certified is null ? High : HighCertified;
            if (floor >= lastFloor && certified is null)
            {
                // x.1, x.2 after the last chapter are as often epilogues and specials as parts.
                Settle(d, FoldState.Keep, "lastChapter");
                continue;
            }

            var merged = new List<(string Mapping, int K)>();
            var against = new List<string>();
            var repeats = new List<string>();
            int unscaled = 0, waits = 0;
            foreach (var (wholeMapping, whole) in wholes.Items)
            {
                var verdicts = new List<(Verdict Verdict, int K)>();
                foreach (var (partsMapping, run) in longRuns.Items)
                {
                    var check = Check(wholeMapping, whole, partsMapping, run, high);
                    if (check.Verdict == Verdict.Unscaled)
                    {
                        unscaled++;
                        continue;
                    }

                    if (check.Verdict == Verdict.Unknown)
                    {
                        waits++;
                    }

                    if (check.Ratio is { } ratio)
                    {
                        d.Ratios.Add(new PageRatio(
                            wholeMapping, partsMapping, Math.Round(ratio, 2), check.Verdict.ToString(), Math.Round(check.Scale, 2)));
                    }

                    verdicts.Add((check.Verdict, check.K));
                    if (check.Verdict == Verdict.Merged)
                    {
                        Votes(pairVotes, wholeMapping, partsMapping)[0]++;
                    }
                    else if (check.Verdict != Verdict.Unknown)
                    {
                        Votes(pairVotes, wholeMapping, partsMapping)[1]++;
                    }
                }

                if (verdicts.Any(v => v.Verdict == Verdict.Merged))
                {
                    merged.Add((wholeMapping, verdicts.Where(v => v.Verdict == Verdict.Merged).Max(v => v.K)));
                }
                else if (verdicts.Any(v => v.Verdict == Verdict.Alias) &&
                         verdicts.All(v => v.Verdict is Verdict.Alias or Verdict.Unknown or Verdict.Mismatch))
                {
                    // This "whole" is part 1 again; it never stands for the chapter.
                    repeats.Add(wholeMapping);
                }
                else if (verdicts.Any(v => v.Verdict != Verdict.Unknown))
                {
                    against.Add(wholeMapping);
                }
            }

            if (against.Count > 0 || (repeats.Count > 0 && merged.Count == 0))
            {
                Settle(d, FoldState.Keep, merged.Count == 0 ? "wholeIsNotTheParts" : "wholesDisagree");
                continue;
            }

            if (merged.Count > 0 && repeats.Count(m => longRuns.Items.All(r => r.Key != m)) > merged.Count)
            {
                // More sources list the whole as part 1 alone than as the parts put together.
                Settle(d, FoldState.Keep, "wholesDisagree");
                continue;
            }

            if (merged.Count == 0 && unscaled > 0 && waits == 0)
            {
                Settle(d, FoldState.Keep, "pageScalesDisagree");
                continue;
            }

            if (merged.Count > 0)
            {
                Settle(d, FoldState.Merged, certified is null ? "pageCounts" : $"pageCounts+{certified}");
                d.Fold = true;
                d.Wholes.AddRange(wholes.Items.Select(w => w.Key).Where(m => !repeats.Contains(m)));
                foreach (var mapping in repeats)
                {
                    d.Aliases[mapping] = "part1UnderWhole";
                }

                AddParts(d, longRuns, merged.Max(m => m.K));
                continue;
            }

            // Titles and publisher numbering say the parts are parts. They say nothing about the whole:
            // Atsumaru's "20" beside 20.1 and 20.2 of Second Prettiest Girl is 20.1 again. So a whole
            // always waits for a page count.
            d.Certified = certified;
            pending.Add((d, [.. wholes.Items.Select(w => w.Key)], longRuns));
        }

        foreach (var (d, realWholes, longRuns) in pending)
        {
            var votes = realWholes
                .SelectMany(w => longRuns.Items.Select(p => (w, p.Key)))
                .Where(pairVotes.ContainsKey)
                .Select(k => pairVotes[k])
                .ToList();
            var yes = votes.Sum(v => v[0]);
            var no = votes.Sum(v => v[1]);
            if (yes >= PairFloors && no == 0)
            {
                Settle(d, FoldState.Merged, "pairVerified");
                d.Fold = true;
                d.Wholes.AddRange(realWholes);
                AddParts(d, longRuns, int.MaxValue);
            }
            else
            {
                Settle(d, FoldState.Pending, d.Certified is null ? "needsPageCount" : $"needsPageCount+{d.Certified}");
                d.Wholes.AddRange(realWholes);
                AddParts(d, longRuns, int.MaxValue);
            }
        }

        return decisions;
    }

    /// <summary>
    /// The chapters a pending floor waits on: each candidate whole and part that has no page count yet,
    /// and the plain chapters that teach the page scale between its sources.
    /// </summary>
    public static IReadOnlyList<(string Mapping, decimal Number)> MissingCounts(
        FloorDecision decision, IReadOnlyList<ReconcileListing> listings)
    {
        if (decision.State != FoldState.Pending || decision.Nominated is not null)
        {
            return [];
        }

        var entries = listings.ToDictionary(l => l.Mapping, l => l.Entries);
        var wanted = decision.Wholes.Select(m => (m, decision.Floor))
            .Concat(decision.Parts.SelectMany(p => p.Value.Select(n => (p.Key, n))))
            .Concat(decision.Calibration);
        return [.. wanted
            .Distinct()
            .Where(w => entries.TryGetValue(w.Item1, out var list) &&
                        list.FirstOrDefault(e => e.Number == w.Item2) is { } entry && Pages(entry) is null)];
    }

    /// <summary>
    /// Holds a merge to the pages: each mapping's parts fold only once a whole on the floor holds them
    /// (<see cref="ChapterPageMatch"/>), and parts no whole holds stay rows of their own. Every whole with
    /// evidence is tried in the floor's order; only the first whole without it is asked for, so a check
    /// fetches one whole at a time. Pages that differ get a second look as strips
    /// (<see cref="ChapterPageMatch.CompareStrips"/>), for a source that cuts or pads its pages; hashes
    /// stored before strips existed are fetched again. A mapping still waiting on evidence never holds up
    /// another mapping whose parts already matched: those fold, and the waiting ones move to
    /// <see cref="FloorDecision.Waiting"/>. <paramref name="hashesOf"/> gives a chapter's stored hashes, or
    /// null when none are stored. Only a merged floor is touched: page counts and the pair rule nominate,
    /// the pages decide.
    /// </summary>
    public static void CheckPages(FloorDecision d, Func<string, decimal, PageEvidence?> hashesOf)
    {
        if (d.State != FoldState.Merged)
        {
            return;
        }

        d.Nominated = d.Reason;
        var waiting = new List<string>();
        var folds = 0;
        foreach (var (mapping, numbers) in d.Parts.ToList())
        {
            var parts = numbers.Select(n => hashesOf(mapping, n)).ToList();
            if (parts.Any(p => p is null))
            {
                d.MissingPages.AddRange(numbers.Where((_, i) => parts[i] is null).Select(n => (mapping, n)));
                if (d.Wholes.FirstOrDefault(w => hashesOf(w, d.Floor) is null) is { } whole &&
                    d.Wholes.All(w => hashesOf(w, d.Floor) is null))
                {
                    d.MissingPages.Add((whole, d.Floor));
                }

                waiting.Add(mapping);
                continue;
            }

            var matched = false;
            var unhashed = false;
            void Want(string whole)
            {
                if (!unhashed)
                {
                    d.MissingPages.Add((whole, d.Floor));
                }

                unhashed = true;
            }

            foreach (var whole in d.Wholes)
            {
                if (hashesOf(whole, d.Floor) is not { } wholeEvidence)
                {
                    Want(whole);
                    continue;
                }

                var result = ChapterPageMatch.Compare(wholeEvidence.Pages, [.. parts.Select(p => p!.Pages)]);
                d.PageChecks.Add(Check(whole, mapping, result, "pages"));
                if (result.Verdict == PageMatchVerdict.Match)
                {
                    matched = true;
                    break;
                }

                var partsStripless = parts.Any(p => p!.Strip is null);
                if (wholeEvidence.Strip is null || partsStripless)
                {
                    if (wholeEvidence.Strip is null)
                    {
                        Want(whole);
                    }

                    d.MissingPages.AddRange(numbers.Where((_, i) => parts[i]!.Strip is null).Select(n => (mapping, n)));
                    unhashed = true;
                    continue;
                }

                var strips = ChapterPageMatch.CompareStrips(wholeEvidence.Strip, [.. parts.Select(p => p!.Strip!)]);
                d.PageChecks.Add(Check(whole, mapping, strips, "strip"));
                if (strips.Verdict == PageMatchVerdict.Match)
                {
                    matched = true;
                    break;
                }
            }

            if (matched)
            {
                folds++;
            }
            else if (unhashed)
            {
                waiting.Add(mapping);
            }
            else
            {
                d.Held[mapping] = numbers;
                d.Parts.Remove(mapping);
            }
        }

        var missing = d.MissingPages.Distinct().ToList();
        d.MissingPages.Clear();
        d.MissingPages.AddRange(missing);
        if (folds > 0)
        {
            foreach (var mapping in waiting)
            {
                d.Waiting[mapping] = d.Parts[mapping];
                d.Parts.Remove(mapping);
            }

            Settle(d, FoldState.Merged, "pagesMatch");
        }
        else if (waiting.Count > 0)
        {
            Settle(d, FoldState.Pending, "needsPageCheck");
            d.Fold = false;
        }
        else
        {
            Settle(d, FoldState.Keep, d.PageChecks.All(c => c.Verdict == nameof(PageMatchVerdict.Unsure)) ? "pagesUnsure" : "pagesDiffer");
            d.Fold = false;
        }
    }

    private static PageCheck Check(string whole, string mapping, PageMatchResult result, string method) =>
        new(whole, mapping, result.Verdict.ToString(), [.. result.Shares.Select(s => Math.Round(s, 2))], Math.Round(result.Cover, 2), method);

    private static void Settle(FloorDecision d, FoldState state, string reason)
    {
        d.State = state;
        d.Reason = reason;
    }

    private static void AddParts(FloorDecision d, Ordered<List<ReconcileEntry>> runs, int k)
    {
        foreach (var (mapping, run) in runs.Items)
        {
            d.Parts[mapping] = [.. run.Take(k).Select(e => e.Number)];
        }
    }

    private static int[] Votes(Dictionary<(string, string), int[]> votes, string whole, string parts)
    {
        if (!votes.TryGetValue((whole, parts), out var counts))
        {
            counts = new int[2];
            votes[(whole, parts)] = counts;
        }

        return counts;
    }

    private enum Verdict
    {
        Unknown,
        Junk,
        Merged,
        Alias,
        Mismatch,

        /// <summary>The two sources' page counts keep no steady scale, so comparing them says nothing.</summary>
        Unscaled,
    }

    /// <summary>
    /// Compares one whole against one run by page count, the whole run or nothing. <paramref name="factor"/> is
    /// the whole's source's pages per page of the run's source (<see cref="Scales"/>).
    /// </summary>
    private static (Verdict Verdict, int K, double? Ratio) Verify(
        ReconcileEntry whole, List<ReconcileEntry> run, double high, double factor)
    {
        if (Pages(whole) is not { } counted || run.Count < 2 || run.Any(e => Pages(e) is null))
        {
            return (Verdict.Unknown, run.Count, null);
        }

        var pages = run.Select(e => Pages(e)!.Value).ToList();
        var wholePages = counted / factor;
        var ratio = wholePages / pages.Sum();
        if (pages.Count >= 3 && pages.Distinct().Count() == 1)
        {
            // One upload repeated under x.1, x.2, x.3.
            return (Verdict.Junk, run.Count, ratio);
        }

        // The best-fitting prefix folded "200 + 200.1 + 200.2" on a finale followed by eight epilogue
        // chapters (Call of the Night, held-out check), so it is the whole run or nothing.
        if (ratio is >= Low && ratio <= high && wholePages >= WholeOverPart * pages.Max())
        {
            return (Verdict.Merged, run.Count, ratio);
        }

        if (ratio < Low && Math.Abs(wholePages - pages[0]) <= Math.Max(AliasPages, 0.12 * pages[0]))
        {
            return (Verdict.Alias, run.Count, ratio);
        }

        return (Verdict.Mismatch, run.Count, ratio);
    }

    private static int? Pages(ReconcileEntry entry) => entry.Pages is > 0 ? entry.Pages : null;

    private enum ScaleState
    {
        Same,
        Assumed,
        Known,
        Pending,
        Unstable,
    }

    private sealed record Scale(ScaleState State, double Factor, IReadOnlyList<(string Mapping, decimal Number)> Needed);

    /// <summary>
    /// Page counts only compare within one source: some cut long pages into strips or upload a chapter
    /// twice over (MangaKakalot and TopManhua run at about twice everyone else in some series). So a whole
    /// from one mapping meets parts from another after dividing by the pair's scale, the median
    /// pages-per-page on floors both list as one plain chapter. With fewer than <see cref="ScaleFloors"/>
    /// shared plain floors there is nothing to learn from, and the scale is taken as 1.
    /// </summary>
    private sealed class Scales(IReadOnlyList<ReconcileListing> listings, decimal lastFloor)
    {
        private readonly Dictionary<string, Dictionary<decimal, ReconcileEntry>> _plain = listings.ToDictionary(
            l => l.Mapping,
            l => l.Entries.GroupBy(e => decimal.Floor(e.Number))
                .Where(g => g.Count() == 1 && g.Single().Number == g.Key)
                .ToDictionary(g => g.Key, g => g.Single()));

        private readonly Dictionary<(string, string), Scale> _learnt = [];

        public Scale Of(string a, string b)
        {
            if (a == b)
            {
                return new Scale(ScaleState.Same, 1, []);
            }

            if (string.CompareOrdinal(a, b) > 0)
            {
                var other = Of(b, a);
                return other with { Factor = 1 / other.Factor };
            }

            if (!_learnt.TryGetValue((a, b), out var scale))
            {
                scale = Learn(a, b);
                _learnt[(a, b)] = scale;
            }

            return scale;
        }

        private Scale Learn(string a, string b)
        {
            var pa = _plain[a];
            var pb = _plain[b];
            var shared = pa.Keys.Where(f => pb.ContainsKey(f) && f != lastFloor).OrderDescending().ToList();
            if (shared.Count < ScaleFloors)
            {
                return new Scale(ScaleState.Assumed, 1, []);
            }

            var counted = shared.Where(f => Pages(pa[f]) is not null && Pages(pb[f]) is not null).Take(ScaleFloorsMax).ToList();
            var ratios = counted.Select(f => (double)Pages(pa[f])!.Value / Pages(pb[f])!.Value).ToList();
            var want = ScaleFloors;
            if (ratios.Count >= ScaleFloors)
            {
                var middle = Median(ratios);
                var agree = ratios.Where(r => r >= middle / ScaleSpread && r <= middle * ScaleSpread).ToList();
                if (agree.Count >= ScaleFloors && 2 * agree.Count >= ratios.Count)
                {
                    var factor = Median(agree);
                    return new Scale(ScaleState.Known, factor >= 1 / ScaleSame && factor <= ScaleSame ? 1 : factor, []);
                }

                if (ratios.Count >= Math.Min(ScaleFloorsMax, shared.Count))
                {
                    return new Scale(ScaleState.Unstable, 1, []);
                }

                want = Math.Min(ScaleFloorsMax, ratios.Count + ScaleFloors);
            }

            var needed = new List<(string, decimal)>();
            foreach (var f in shared.Where(f => !counted.Contains(f))
                         .OrderByDescending(f => (Pages(pa[f]) is null ? 0 : 1) + (Pages(pb[f]) is null ? 0 : 1))
                         .ThenByDescending(f => f)
                         .Take(want - ratios.Count))
            {
                if (Pages(pa[f]) is null)
                {
                    needed.Add((a, f));
                }

                if (Pages(pb[f]) is null)
                {
                    needed.Add((b, f));
                }
            }

            return new Scale(ScaleState.Pending, 1, needed);
        }

        private static double Median(List<double> values)
        {
            var sorted = values.Order().ToList();
            var half = sorted.Count / 2;
            return sorted.Count % 2 == 1 ? sorted[half] : (sorted[half - 1] + sorted[half]) / 2;
        }
    }

    /// <summary>
    /// A number is an extra everywhere once any mapping says so: by its title, or because it is a third
    /// or later sub-chapter no more than half the size of the parts before it.
    /// </summary>
    private static HashSet<decimal> ExtraNumbers(IReadOnlyList<ReconcileListing> listings)
    {
        var extras = new HashSet<decimal>();
        foreach (var entry in listings.SelectMany(l => l.Entries).Where(e => IsExtraTitle(e.Title)))
        {
            extras.Add(Key(entry.Number));
        }

        foreach (var listing in listings)
        {
            foreach (var floor in listing.Entries.Select(e => decimal.Floor(e.Number)).Distinct())
            {
                var (_, run, _) = RunOf(listing.Entries, floor, extras, keepTail: true);
                var sized = new List<int>();
                for (var i = 0; i < run.Count; i++)
                {
                    if (Pages(run[i]) is not { } pages)
                    {
                        break;
                    }

                    if (i >= 2 && pages <= TinyTail * Middle(sized))
                    {
                        extras.Add(Key(run[i].Number));
                        break;
                    }

                    sized.Add(pages);
                }
            }
        }

        return extras;
    }

    private static int Middle(List<int> values)
    {
        var sorted = values.Order().ToList();
        return sorted[sorted.Count / 2];
    }

    /// <summary>
    /// The floor's whole, its run of consecutive .1, .2, ... sub-chapters that can be parts, and the
    /// other reading of a floor listed as f, f.2, f.3 with no f.1, where the f is part 1. That last one
    /// is only a candidate; <see cref="Decide"/> uses it when page counts or part titles back it.
    /// </summary>
    private static (ReconcileEntry? Whole, List<ReconcileEntry> Run, List<ReconcileEntry> FirstRun) RunOf(
        IReadOnlyList<ReconcileEntry> entries, decimal floor, HashSet<decimal> extras, bool keepTail)
    {
        var byTenth = new Dictionary<int, ReconcileEntry>();
        ReconcileEntry? whole = null;
        foreach (var e in entries)
        {
            if (decimal.Floor(e.Number) != floor)
            {
                continue;
            }

            if (e.Number == floor)
            {
                whole = e;
            }
            else if (Tenth(e.Number) is { } tenth && !extras.Contains(Key(e.Number)))
            {
                byTenth[tenth] = e;
            }
        }

        List<ReconcileEntry> Consecutive(int start, IEnumerable<ReconcileEntry> head)
        {
            var run = head.ToList();
            for (var i = start; i < 10; i++)
            {
                if (!byTenth.TryGetValue(i, out var e))
                {
                    break;
                }

                var sized = run.Select(Pages).OfType<int>().ToList();
                if (!keepTail && run.Count >= 2 && Pages(e) is { } pages && sized.Count == run.Count &&
                    pages <= TinyTail * Middle(sized))
                {
                    break;
                }

                run.Add(e);
            }

            return run;
        }

        var main = Consecutive(1, []);
        var firstRun = whole is not null && !byTenth.ContainsKey(1) && byTenth.ContainsKey(2)
            ? Consecutive(2, [whole])
            : [];
        return (whole, main, firstRun);
    }

    /// <summary>1 to 9 when the number is f + k/10 exactly (one decimal digit), else null.</summary>
    private static int? Tenth(decimal number)
    {
        var hundredths = (int)decimal.Round((number - decimal.Floor(number)) * 100, MidpointRounding.ToEven);
        return hundredths % 10 == 0 && hundredths > 0 ? hundredths / 10 : null;
    }

    private static decimal Key(decimal number) => decimal.Round(number, 2, MidpointRounding.ToEven);

    /// <summary>
    /// Parts known to be parts without page counts: the publisher numbers them that way, or the run's
    /// titles carry part markers or one shared title.
    /// </summary>
    private static string? Certified(Dictionary<decimal, int> publisher, decimal floor, Ordered<List<ReconcileEntry>> runs)
    {
        if (publisher.GetValueOrDefault(floor) >= 2)
        {
            return "publisherNumbering";
        }

        foreach (var (_, run) in runs.Items)
        {
            if (run.Count < 2)
            {
                continue;
            }

            var titles = run.Select(e => CleanTitle(e.Title)).ToList();
            if (titles.Count(IsPartMarker) >= 2)
            {
                return "partTitles";
            }

            var stems = titles.OfType<string>().Select(TitleStem).ToHashSet(StringComparer.Ordinal);
            if (titles.All(t => t is not null) && stems.Count == 1 && !stems.Contains(string.Empty))
            {
                return "sharedTitle";
            }
        }

        return null;
    }

    private static bool IsExtraTitle(string? title) => !string.IsNullOrEmpty(title) && ExtraTitle().IsMatch(title);

    private static bool IsPartMarker(string? title) => !string.IsNullOrEmpty(title) && PartMarker().IsMatch(title);

    /// <summary>
    /// The title with its "Chapter 4.1:" label removed. Null for a title that is only a group tag in
    /// brackets, which is how MangaFire fills the field ("(lfp-DCP)").
    /// </summary>
    private static string? CleanTitle(string? title)
    {
        if (string.IsNullOrEmpty(title))
        {
            return null;
        }

        var cleaned = ChapterLabel().Replace(title, string.Empty, 1).Trim();
        return cleaned.Length == 0 || BracketTag().IsMatch(cleaned) ? null : cleaned;
    }

    private static string TitleStem(string title) =>
        PartMarker().Replace(CleanTitle(title) ?? string.Empty, string.Empty).Trim(StemTrim).ToLowerInvariant();

    /// <summary>Insertion-ordered map: a new key goes to the end, an existing one keeps its place.</summary>
    private sealed class Ordered<T>
    {
        private readonly List<KeyValuePair<string, T>> _items = [];

        public IReadOnlyList<KeyValuePair<string, T>> Items => _items;

        public int Count => _items.Count;

        public void Set(string key, T value)
        {
            var at = _items.FindIndex(i => i.Key == key);
            if (at >= 0)
            {
                _items[at] = new(key, value);
            }
            else
            {
                _items.Add(new(key, value));
            }
        }

        public void Remove(string key) => _items.RemoveAll(i => i.Key == key);
    }
}
