using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Maki.Api.Dtos;
using Maki.Metadata.MangaBaka;

namespace Maki.Api.Services;

/// <summary>What a <see cref="LibraryFilterEvaluator"/> run needs besides the rows and the spec.</summary>
/// <param name="ReadTracking">
/// Whether anything tracks reading (the built-in reader has been used, or Kavita is connected), the
/// client's <c>useReadTracking</c>. Without it read progress counts as nothing.
/// </param>
/// <param name="MaxContentRating">The caller's ceiling; a saved rating filter is narrowed to it.</param>
/// <param name="Now">The clock "added within" counts back from.</param>
public sealed record LibraryFilterContext(bool ReadTracking, string? MaxContentRating, DateTime Now);

/// <summary>
/// A saved Library filter (<see cref="LibraryFilterSpec"/>) applied on the server, for Home rails
/// built from one. The Library page evaluates the same spec in the browser (the <c>visible</c> memo
/// in <c>frontend/src/pages/LibraryPage.tsx</c> and the helpers it calls), and the two must stay in
/// step: a filter change on one side needs the same change here, and <c>LibraryFilterEvaluatorTests</c>
/// pins the semantics. Rows are the Library list's own (<see cref="LibraryListRows"/>), so every
/// count means what the page means by it.
/// <para>
/// Known differences: the title search keeps only plain matches (substring, all words as word
/// prefixes, spaces ignored); the page's typo-tolerant fallback, used when nothing matches plainly,
/// is not ported. "On this device" and the offline ordering are per browser and have no meaning here.
/// </para>
/// </summary>
public static class LibraryFilterEvaluator
{
    private const string All = "all";

    /// <summary>The rows that pass, in the filter's own sort. Archived series are never included.</summary>
    public static List<SeriesDto> Apply(IEnumerable<SeriesDto> rows, LibraryFilterSpec spec, LibraryFilterContext context)
    {
        var matching = rows.Where(s => s.ArchivedAt is null && Matches(s, spec, context)).ToList();
        var sort = string.IsNullOrEmpty(spec.Sort) || spec.Sort == "archived" ? "added" : spec.Sort;
        var descending = spec.SortDir is "asc" or "desc" ? spec.SortDir == "desc" : DefaultDescending(sort);
        // A stable sort, like Array.prototype.sort, so ties keep the list's own (title) order.
        var ordered = matching.Order(Comparer(sort, descending)).ToList();
        return Search(ordered, spec.Query);
    }

    public static bool Matches(SeriesDto s, LibraryFilterSpec f, LibraryFilterContext context)
    {
        if (f.Status != All && s.Status != f.Status)
        {
            return false;
        }

        if (f.Types is { Count: > 0 } types && (s.Type is null || !types.Contains(s.Type)))
        {
            return false;
        }

        if (f.ReadingStatuses is { Count: > 0 } statuses && !statuses.Contains(s.ReadingStatus))
        {
            return false;
        }

        if (f.UntaggedOnly)
        {
            if (s.TagIds.Count > 0)
            {
                return false;
            }
        }
        else
        {
            if (f.TagIds is { Count: > 0 } tagIds && !MatchesSet(tagIds, s.TagIds, f.TagMatch))
            {
                return false;
            }

            if (f.ExcludeTagIds is { Count: > 0 } excluded && excluded.Any(s.TagIds.Contains))
            {
                return false;
            }
        }

        if (f.Genres is { Count: > 0 } genres && !MatchesSet(genres, s.Genres, f.GenreMatch))
        {
            return false;
        }

        if (f.ExcludeGenres is { Count: > 0 } excludedGenres && excludedGenres.Any(s.Genres.Contains))
        {
            return false;
        }

        if (f.MetadataTags is { Count: > 0 } metaTags && !MatchesSet(metaTags, s.MetadataTags, f.MetadataTagMatch))
        {
            return false;
        }

        // Narrowed to the caller's ceiling first, as the page does when it applies a preset; nothing
        // left means no rating filter at all.
        var allowed = ContentRating.Allowed(context.MaxContentRating);
        if (f.ContentRatings?.Where(allowed.Contains).ToList() is { Count: > 0 } ratings &&
            (s.ContentRating is null || !ratings.Contains(s.ContentRating)))
        {
            return false;
        }

        var shown = s.Rating ?? s.ProvisionalRating;
        if (f.RatingState == "unrated" && shown is not null)
        {
            return false;
        }

        if (f.RatingState == "rated" && (shown is not int r || r < f.RatingMin || r > f.RatingMax))
        {
            return false;
        }

        if ((f.CommunityRatingMin > 0 || f.CommunityRatingMax < 10) &&
            (s.CommunityRating is not double score || score < f.CommunityRatingMin || score > f.CommunityRatingMax))
        {
            return false;
        }

        if (f.Monitored != All && s.Monitored != (f.Monitored == "monitored"))
        {
            return false;
        }

        if (f.QualityProfile != All &&
            (f.QualityProfile == "default"
                ? s.UpgradeProfileId is not null
                : s.UpgradeProfileId?.ToString(CultureInfo.InvariantCulture) != f.QualityProfile))
        {
            return false;
        }

        if (f.Completeness != All && (f.Completeness == "behind" ? Missing(s) <= 0 : Missing(s) > 0))
        {
            return false;
        }

        if ((f.YearMin is not null || f.YearMax is not null) &&
            (s.Year is not int year || year < f.YearMin || year > f.YearMax))
        {
            return false;
        }

        if (f.Credits is { Count: > 0 } credits && !CreditsOf(s).Any(credits.Contains))
        {
            return false;
        }

        if (f.Publishers is { Count: > 0 } publishers && (s.Publisher is null || !publishers.Contains(s.Publisher)))
        {
            return false;
        }

        if (f.RootFolderIds is { Count: > 0 } folders && !folders.Contains(s.RootFolderId))
        {
            return false;
        }

        if (f.Incognito != All && (s.Incognito != "Off") != (f.Incognito == "on"))
        {
            return false;
        }

        if (f.Muted != All && (s.NotificationMode == "Muted") != (f.Muted == "muted"))
        {
            return false;
        }

        if (f.Anime != All && s.HasAnime != (f.Anime == "has"))
        {
            return false;
        }

        if (f.NumberingClash != All && (s.NumberingClash is not null) != (f.NumberingClash == "clash"))
        {
            return false;
        }

        if (f.Overrides is { Count: > 0 } overrides &&
            !((overrides.Contains("readCleanup") && s.ReadFileCleanup != "Default") ||
              (overrides.Contains("pageConversion") && s.PageConversion != "Default")))
        {
            return false;
        }

        if (f.AddedWithinDays is int days && s.Added < context.Now.AddDays(-days))
        {
            return false;
        }

        if (f.SourceState != All && !MatchesSourceState(s, f.SourceState))
        {
            return false;
        }

        if (f.Sources is { Count: > 0 } sources && !MatchesSet(sources, s.Sources, f.SourceMatch))
        {
            return false;
        }

        if (f.FileSources is { Count: > 0 } fileSources && !MatchesSet(fileSources, s.FileSources, f.FileSourceMatch))
        {
            return false;
        }

        if (f.ChapterMin is not null || f.ChapterMax is not null)
        {
            var n = ChapterCount(s, f.ChapterMode);
            if (n < f.ChapterMin || n > f.ChapterMax)
            {
                return false;
            }
        }

        if (f.ReadMin > 0 || f.ReadMax < 100)
        {
            var pct = (int)Math.Round(Progress(s, context.ReadTracking).ReadPct ?? 0, MidpointRounding.AwayFromZero);
            if (pct < f.ReadMin || pct > f.ReadMax)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>`any` is OR (carries at least one), `all` is AND (carries every one).</summary>
    private static bool MatchesSet<T>(IReadOnlyCollection<T> wanted, IReadOnlyCollection<T> has, string mode) =>
        mode == All ? wanted.All(has.Contains) : wanted.Any(has.Contains);

    private static bool MatchesSourceState(SeriesDto s, string state) => state switch
    {
        "none" => s.Sources.Count == 0,
        "hasDisabled" => s.Sources.Count > s.EnabledSources.Count,
        "noneEnabled" => s.Sources.Count > 0 && s.EnabledSources.Count == 0,
        "hasEnabled" => s.EnabledSources.Count > 0,
        _ => true,
    };

    private static int Obtained(SeriesDto s) => s.ChapterFileCount + s.RemovedChapterCount;

    /// <summary>The client's <c>missingCount</c>.</summary>
    private static int Missing(SeriesDto s) =>
        (s.WantedChapterCount != 0 ? s.WantedChapterCount : s.KnownChapterCount) - Obtained(s);

    private static int ChapterCount(SeriesDto s, string mode) =>
        mode == "total" ? (s.WantedChapterCount != 0 ? s.WantedChapterCount : s.KnownChapterCount) : s.ChapterFileCount;

    private static IEnumerable<string> CreditsOf(SeriesDto s) =>
        new[] { s.AuthorStory, s.AuthorArt }
            .SelectMany(c => (c ?? "").Split(','))
            .Select(n => n.Trim())
            .Where(n => n.Length > 0)
            .Distinct();

    private readonly record struct ProgressShape(int Have, double? ReadPct, int? Unread);

    /// <summary>The client's <c>seriesProgressVisual</c>, the parts a filter or sort reads.</summary>
    private static ProgressShape Progress(SeriesDto s, bool readTracking)
    {
        var scoped = s.ProgressTotal > 0;
        var have = scoped ? s.ProgressHave : Obtained(s);
        var read = scoped ? s.ProgressRead : s.ReadChapterCount;
        var tracked = readTracking && read is not null && have > 0;
        return new ProgressShape(
            have,
            tracked ? Math.Min(100, (double)read!.Value / have * 100) : null,
            tracked ? Math.Max(0, have - read!.Value) : null);
    }

    private static int Unread(SeriesDto s) => Math.Max(0, s.ChapterFileCount - (s.ReadChapterCount ?? 0));

    private static readonly string[] StatusOrder = ["Ongoing", "Hiatus", "Completed", "Cancelled", "Unknown"];

    private static int StatusRank(string status)
    {
        var i = Array.IndexOf(StatusOrder, status);
        return i == -1 ? StatusOrder.Length : i;
    }

    /// <summary>The direction each key starts in: names read A to Z, dates and scores read biggest first.</summary>
    private static bool DefaultDescending(string sort) => sort is not ("title" or "status" or "author");

    private static string TitleSortKey(SeriesDto s)
    {
        if (s.DisplayTitle == s.Title)
        {
            return s.SortTitle;
        }

        var lowered = s.DisplayTitle.ToLowerInvariant();
        var article = new[] { "the ", "a ", "an " }.FirstOrDefault(lowered.StartsWith);
        return article is null ? lowered : lowered[article.Length..];
    }

    private static string? AuthorOf(SeriesDto s)
    {
        var name = (string.IsNullOrEmpty(s.AuthorStory) ? s.AuthorArt ?? "" : s.AuthorStory).Trim();
        return name.Length == 0 ? null : name.ToLowerInvariant();
    }

    private static double? Ticks(DateTime? at) => at?.Ticks;

    /// <summary>The client's <c>compareSeries</c>: a missing value sorts last whichever way the list faces.</summary>
    private static IComparer<SeriesDto> Comparer(string sort, bool descending)
    {
        var sign = descending ? -1 : 1;
        Comparison<SeriesDto> nullable(Func<SeriesDto, double?> pick) => (a, b) =>
        {
            var (x, y) = (pick(a), pick(b));
            if (x is null || y is null)
            {
                return x is null ? (y is null ? 0 : 1) : -1;
            }

            return x.Value.CompareTo(y.Value) * sign;
        };
        Comparison<SeriesDto> by(Func<SeriesDto, double> pick) => (a, b) => pick(a).CompareTo(pick(b)) * sign;
        var text = StringComparer.Create(CultureInfo.InvariantCulture, CompareOptions.None);

        Comparison<SeriesDto> comparison = sort switch
        {
            "title" => (a, b) => text.Compare(TitleSortKey(a), TitleSortKey(b)) * sign,
            "incomplete" => by(s => Missing(s)),
            "status" => by(s => StatusRank(s.Status)),
            "read" => nullable(s => Ticks(s.LastReadAt)),
            "rating" => nullable(s => s.Rating ?? s.ProvisionalRating),
            "downloaded" => nullable(s => Ticks(s.LastDownloadedAt)),
            "year" => nullable(s => s.Year),
            "score" => nullable(s => s.CommunityRating),
            "unread" => by(s => Unread(s)),
            "released" => nullable(s => Ticks(s.LatestChapterAt)),
            "chapters" => by(s => ChapterCount(s, "total")),
            "author" => (a, b) =>
            {
                var (x, y) = (AuthorOf(a), AuthorOf(b));
                if (x is null || y is null)
                {
                    return x is null ? (y is null ? 0 : 1) : -1;
                }

                return text.Compare(x, y) * sign;
            },
            _ => by(s => s.Added.Ticks),
        };
        return System.Collections.Generic.Comparer<SeriesDto>.Create(comparison);
    }

    // Title search: the plain-match half of the client's fuzzySearch.ts.

    private static readonly Regex NonWord = new(@"[^\p{L}\p{N}]+", RegexOptions.Compiled);

    private static string Normalize(string text)
    {
        var decomposed = text.Normalize(NormalizationForm.FormKD);
        var stripped = new StringBuilder(decomposed.Length);
        foreach (var c in decomposed)
        {
            var category = CharUnicodeInfo.GetUnicodeCategory(c);
            if (category is not (UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark or UnicodeCategory.EnclosingMark))
            {
                stripped.Append(c);
            }
        }

        return NonWord.Replace(stripped.ToString().ToLowerInvariant(), " ").Trim();
    }

    private static bool TitleMatches(string query, string[] queryWords, string querySpaced, string queryCompact, string title)
    {
        if (title.ToLowerInvariant().Contains(query, StringComparison.Ordinal))
        {
            return true;
        }

        var spaced = Normalize(title);
        var words = spaced.Length == 0 ? [] : spaced.Split(' ');
        var compact = string.Concat(words);
        if ((querySpaced.Length > 0 && spaced.Contains(querySpaced, StringComparison.Ordinal)) ||
            (queryCompact.Length > 0 && compact.Contains(queryCompact, StringComparison.Ordinal)))
        {
            return true;
        }

        return queryWords.Length > 0 && queryWords.All(word =>
            words.Any(w => w.StartsWith(word, StringComparison.Ordinal) || (word.Length >= 3 && w.Contains(word, StringComparison.Ordinal))));
    }

    private static List<SeriesDto> Search(List<SeriesDto> rows, string? query)
    {
        var raw = (query ?? "").Trim().ToLowerInvariant();
        if (raw.Length == 0)
        {
            return rows;
        }

        var spaced = Normalize(query!);
        var words = spaced.Length == 0 ? [] : spaced.Split(' ');
        var compact = string.Concat(words);
        return rows
            .Where(s => new[] { s.Title, s.DisplayTitle, s.OriginalTitle }
                .Concat(s.AltTitleNames ?? [])
                .Any(t => !string.IsNullOrEmpty(t) && TitleMatches(raw, words, spaced, compact, t)))
            .ToList();
    }
}
