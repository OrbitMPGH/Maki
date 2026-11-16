namespace Maki.Core.Sources;

/// <summary>
/// A source that lists the same chapter once per scanlation group's upload (MangaDex). It hands over
/// every upload, and <see cref="ScanlationGroupChoice"/> picks one per chapter under the user's
/// preferred and blocked groups, so the choice works the same for any such source.
/// <see cref="ISource.ListChaptersAsync"/> stays the pick with no preference.
/// </summary>
public interface IScanlationGroupSource : ISource
{
    /// <summary>Every upload, one entry per group's copy, not deduplicated.</summary>
    Task<IReadOnlyList<SourceChapter>> ListUploadsAsync(
        string sourceSeriesId, string? languageFilter, CancellationToken ct = default);
}

/// <summary>
/// Preferred groups, in order, and blocked groups. Names compare case-insensitively and match a
/// member of a joint upload ("A, B") as well as the whole credit.
/// </summary>
public sealed record ScanlationGroupPreference(IReadOnlyList<string> Preferred, IReadOnlyList<string> Blocked)
{
    public const int MaxNames = 50;
    public const int MaxNameLength = 100;

    public static readonly ScanlationGroupPreference None = new([], []);

    public bool IsEmpty => Preferred.Count == 0 && Blocked.Count == 0;

    /// <summary>Stable text for a cache key; empty for <see cref="None"/>.</summary>
    public string Key => IsEmpty
        ? string.Empty
        : $"p:{string.Join('|', Preferred.Select(Fold))};b:{string.Join('|', Blocked.Select(Fold).Order(StringComparer.Ordinal))}";

    /// <summary>Trimmed, blanks dropped, duplicates dropped (first kept), capped.</summary>
    public static ScanlationGroupPreference Of(IEnumerable<string?>? preferred, IEnumerable<string?>? blocked) =>
        new(Clean(preferred), Clean(blocked));

    /// <summary>
    /// What applies to one series: its own preferred list replaces the global one when it has any,
    /// blocked lists add up, and on a conflict the series wins (a name it prefers is never blocked by
    /// the global list, and a name it blocks is dropped from an inherited preferred list).
    /// </summary>
    public static ScanlationGroupPreference Combine(ScanlationGroupPreference global, ScanlationGroupPreference series)
    {
        var preferred = series.Preferred.Count > 0
            ? series.Preferred.ToList()
            : global.Preferred.Where(name => !Contains(series.Blocked, name)).ToList();
        var blocked = global.Blocked.Where(name => !Contains(series.Preferred, name))
            .Concat(series.Blocked)
            .DistinctBy(Fold)
            .ToList();
        return new ScanlationGroupPreference(preferred, blocked);
    }

    /// <summary>The members of a group credit: "A, B" for a joint release is two groups.</summary>
    public static IEnumerable<string> Members(string? credit) =>
        string.IsNullOrWhiteSpace(credit)
            ? []
            : credit.Split(", ", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    public bool IsBlocked(string? credit) =>
        Blocked.Count > 0 && credit is not null &&
        (Contains(Blocked, credit) || Members(credit).Any(m => Contains(Blocked, m)));

    /// <summary>Index of the first preferred group the credit names, or null when it names none.</summary>
    public int? Rank(string? credit)
    {
        if (credit is null)
        {
            return null;
        }

        for (var i = 0; i < Preferred.Count; i++)
        {
            if (Same(Preferred[i], credit) || Members(credit).Any(m => Same(Preferred[i], m)))
            {
                return i;
            }
        }

        return null;
    }

    private static bool Same(string a, string b) => string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);

    private static bool Contains(IEnumerable<string> names, string name) => names.Any(n => Same(n, name));

    private static string Fold(string name) => name.Trim().ToUpperInvariant();

    private static List<string> Clean(IEnumerable<string?>? names) =>
        (names ?? [])
            .Select(n => n?.Trim())
            .Where(n => !string.IsNullOrEmpty(n))
            .Select(n => n!.Length > MaxNameLength ? n[..MaxNameLength] : n)
            .DistinctBy(Fold)
            .Take(MaxNames)
            .ToList();
}

/// <summary>Which upload of a chapter a source lists, given the uploads and a group preference.</summary>
public static class ScanlationGroupChoice
{
    /// <summary>
    /// One upload per chapter: the one from the first preferred group that has one, else the earliest
    /// published upload not from a blocked group. A chapter whose every upload is blocked is left out,
    /// so another source can supply it. With <see cref="ScanlationGroupPreference.None"/> this is the
    /// earliest upload, which keeps the choice stable across refreshes.
    /// </summary>
    public static List<SourceChapter> Pick(IEnumerable<SourceChapter> uploads, ScanlationGroupPreference groups) =>
        SourceChapterList.Normalize(
            uploads.Where(u => !groups.IsBlocked(u.Group)),
            g => g.OrderBy(u => groups.Rank(u.Group) ?? int.MaxValue)
                .ThenBy(u => u.ReleaseDate ?? DateTime.MaxValue)
                .First());

    /// <summary>Chapters the source lists only through blocked groups, so <see cref="Pick"/> left them out.</summary>
    public static List<SourceChapter> Withheld(IReadOnlyCollection<SourceChapter> uploads, ScanlationGroupPreference groups)
    {
        if (groups.Blocked.Count == 0)
        {
            return [];
        }

        var kept = SourceChapterList.Normalize(uploads.Where(u => !groups.IsBlocked(u.Group)))
            .Select(Identity)
            .ToHashSet();
        return SourceChapterList.Normalize(uploads.Where(u => groups.IsBlocked(u.Group)))
            .Where(c => !kept.Contains(Identity(c)))
            .ToList();
    }

    /// <summary>The source's listing under <paramref name="groups"/>; any other source lists as it always does.</summary>
    public static async Task<IReadOnlyList<SourceChapter>> ListAsync(
        ISource source, string sourceSeriesId, string? languageFilter, ScanlationGroupPreference? groups,
        CancellationToken ct = default) =>
        source is IScanlationGroupSource multi && groups is { IsEmpty: false }
            ? Pick(await multi.ListUploadsAsync(sourceSeriesId, languageFilter, ct), groups)
            : await source.ListChaptersAsync(sourceSeriesId, languageFilter, ct);

    /// <summary>The group names a series' uploads carry, joint credits split, with how many chapters each one has.</summary>
    public static List<(string Name, int Chapters)> Seen(IEnumerable<SourceChapter> uploads) =>
        uploads
            .SelectMany(u => ScanlationGroupPreference.Members(u.Group).Select(name => (name, chapter: Identity(u))))
            .GroupBy(x => x.name, StringComparer.OrdinalIgnoreCase)
            .Select(g => (g.First().name, g.Select(x => x.chapter).Distinct().Count()))
            .OrderByDescending(x => x.Item2)
            .ThenBy(x => x.Item1, StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static (decimal? Number, int? Volume, string Language, string? Title) Identity(SourceChapter c) =>
        (c.Number, c.Volume, SourceLanguages.Canonical(c.Language), c.Number is null ? c.Title?.ToUpperInvariant() : null);
}
