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

/// <summary>A whole compared with a run of parts by page count.</summary>
public sealed record PageRatio(string WholeMapping, string PartsMapping, double Ratio, string Verdict);

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

    /// <summary>Per mapping, the numbers that are parts, in reading order.</summary>
    public Dictionary<string, IReadOnlyList<decimal>> Parts { get; } = [];

    /// <summary>Mappings whose own whole chapter stands for the floor.</summary>
    public List<string> Wholes { get; } = [];

    /// <summary>Mappings whose listed whole is really part 1 ("part1", or "part1UnderWhole" when it never stands for the chapter).</summary>
    public Dictionary<string, string> Aliases { get; } = [];

    public List<PageRatio> Ratios { get; } = [];
}

/// <summary>
/// Decides, per integer floor of a series, whether a source's sub-chapters (10.1, 10.2, ...) are parts
/// of one chapter that another source, or the same one, lists whole. Pure: it reads listings and page
/// counts and fetches nothing. Folding only happens when page counts back it, because decimals also
/// mean extras, repeats of one upload, epilogues, prologue numbering and wrong source matches; a
/// wrong merge hides content, a leftover duplicate row only looks untidy.
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

            // "f, f.2" with no f.1: the f is part 1 when another source's whole matches the sum, or the titles say so.
            foreach (var (mapping, firstRun) in firstRuns.Items)
            {
                var titled = firstRun.Any(e => IsPartMarker(e.Title));
                var fits = wholes.Items.Any(w =>
                    w.Key != mapping && Verify(w.Value, firstRun, titled ? HighCertified : High).Verdict == Verdict.Merged);
                if (fits || (titled && wholes.Count == 1))
                {
                    runs.Set(mapping, firstRun);
                    wholes.Remove(mapping);
                    d.Aliases[mapping] = "part1";
                }
            }

            var longRuns = new Ordered<List<ReconcileEntry>>();
            foreach (var (mapping, run) in runs.Items.Where(r => r.Value.Count >= 2))
            {
                longRuns.Set(mapping, run);
            }

            if (longRuns.Count == 0)
            {
                Settle(d, FoldState.Keep, "loneSubChapter");
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
            foreach (var (wholeMapping, whole) in wholes.Items)
            {
                var verdicts = new List<(Verdict Verdict, int K)>();
                foreach (var (partsMapping, run) in longRuns.Items)
                {
                    var check = Verify(whole, run, high);
                    if (check.Ratio is { } ratio)
                    {
                        d.Ratios.Add(new PageRatio(wholeMapping, partsMapping, Math.Round(ratio, 2), check.Verdict.ToString()));
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
            }
        }

        return decisions;
    }

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
    }

    /// <summary>Compares one whole against one run by page count, the whole run or nothing.</summary>
    private static (Verdict Verdict, int K, double? Ratio) Verify(ReconcileEntry whole, List<ReconcileEntry> run, double high)
    {
        if (Pages(whole) is not { } wholePages || run.Count < 2 || run.Any(e => Pages(e) is null))
        {
            return (Verdict.Unknown, run.Count, null);
        }

        var pages = run.Select(e => Pages(e)!.Value).ToList();
        var ratio = (double)wholePages / pages.Sum();
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
