using System.Text.RegularExpressions;

namespace Maki.Core.Sources;

/// <summary>
/// Keys used in the cross-site id maps a source can expose (<see cref="SourceSeriesResult.ExternalIds"/>
/// and <see cref="ISource.GetExternalIdsAsync"/>). These are in-memory only — nothing persists them —
/// so the set can be extended freely.
/// </summary>
public static class ExternalIdService
{
    public const string MangaBaka = "mangabaka";
    public const string Mal = "mal";
    public const string AniList = "anilist";
    public const string Kitsu = "kitsu";
    public const string MangaUpdates = "mangaupdates";
    public const string MangaDex = "mangadex";

    /// <summary>
    /// WeebCentral's own series id (a ULID). Not a tracker: Atsumaru's search index records which
    /// WeebCentral entry each of its titles corresponds to, which is a ready-made mapping for a
    /// second source. See <see cref="SourceSeriesIdServices"/>.
    /// </summary>
    public const string WeebCentral = "weebcentral";

    /// <summary>
    /// The services whose id *is* a registered source's series id, so holding one is the same as
    /// having found that source's entry. The key equals the source's <see cref="ISource.Name"/>,
    /// which is how <c>SourceMatchService</c> resolves one to the other — a service added here must
    /// be named exactly after the source it addresses.
    /// <para>
    /// MangaDex earns its place from the other direction too: it is a tracker other sites link to,
    /// *and* a source we can download from, so a site's MangaDex link is both evidence about identity
    /// and a mapping we would otherwise have to search for.
    /// </para>
    /// </summary>
    public static readonly IReadOnlyList<string> SourceSeriesIdServices = [MangaDex, WeebCentral];
}

/// <summary>How a candidate's external ids line up with the ones we already hold for a series.</summary>
public enum ExternalIdVerdict
{
    /// <summary>No service is named on both sides, so the ids say nothing either way.</summary>
    NoEvidence,

    /// <summary>At least one shared service agrees. Treated as proof the candidate is the same work.</summary>
    Match,

    /// <summary>Every shared service disagrees. Treated as proof it is a different work.</summary>
    Mismatch
}

/// <summary>
/// Extracts tracker ids from the outbound links a source's series page carries, and compares two
/// id maps.
/// <para>
/// Several sites link their entries to MyAnimeList/AniList/MangaUpdates/Kitsu/MangaDex, and MangaBaka
/// records the same ids for the series in our library — so where both sides name the same service,
/// a title match can be replaced with an identity check. That is the only way to separate two works
/// whose titles are near-identical, which fuzzy matching cannot do by construction.
/// </para>
/// <para>
/// Only id forms that match what MangaBaka stores are captured. MangaUpdates' legacy numeric URLs
/// (<c>series.html?id=12345</c>) and Kitsu's slug URLs (<c>kitsu.app/manga/one-piece</c>) are skipped
/// rather than recorded: recording an id in a form the other side never uses cannot produce a match,
/// but it can produce a false <see cref="ExternalIdVerdict.Mismatch"/> that discards the right result.
/// </para>
/// </summary>
public static partial class SourceExternalIds
{
    [GeneratedRegex(@"myanimelist\.net/manga/(\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex MalLink();

    [GeneratedRegex(@"anilist\.co/manga/(\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex AniListLink();

    [GeneratedRegex(@"kitsu\.(?:io|app)/manga/(\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex KitsuLink();

    // The base36 slug form only ("/series/pb8uwds/one-piece"), which is what MangaBaka stores.
    [GeneratedRegex(@"mangaupdates\.com/series/([a-z0-9]+)(?:/|$)", RegexOptions.IgnoreCase)]
    private static partial Regex MangaUpdatesLink();

    [GeneratedRegex(@"mangadex\.org/title/([0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12})",
        RegexOptions.IgnoreCase)]
    private static partial Regex MangaDexLink();

    [GeneratedRegex(@"mangabaka\.(?:org|dev)/(?:series/)?(\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex MangaBakaLink();

    private static readonly (string Service, Regex Pattern)[] LinkPatterns =
    [
        (ExternalIdService.Mal, MalLink()),
        (ExternalIdService.AniList, AniListLink()),
        (ExternalIdService.Kitsu, KitsuLink()),
        (ExternalIdService.MangaUpdates, MangaUpdatesLink()),
        (ExternalIdService.MangaDex, MangaDexLink()),
        (ExternalIdService.MangaBaka, MangaBakaLink()),
    ];

    /// <summary>Reads {service: id} out of a series page's outbound links (first mention per service wins).</summary>
    public static Dictionary<string, string> FromUrls(IEnumerable<string?> urls)
    {
        var found = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var url in urls)
        {
            if (string.IsNullOrWhiteSpace(url))
            {
                continue;
            }

            foreach (var (service, pattern) in LinkPatterns)
            {
                if (!found.ContainsKey(service) && pattern.Match(url) is { Success: true } m)
                {
                    Set(found, service, m.Groups[1].Value);
                }
            }
        }

        return found;
    }

    /// <summary>
    /// Builds a map from raw values, dropping blanks and anything that fails
    /// <see cref="IsComparable"/>.
    /// </summary>
    public static Dictionary<string, string> From(params (string Service, string? Id)[] pairs)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (service, id) in pairs)
        {
            Set(map, service, id);
        }

        return map;
    }

    /// <summary>Records an id when it is present and in a comparable form; a no-op otherwise.</summary>
    public static void Set(IDictionary<string, string> map, string service, string? id)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return;
        }

        var trimmed = id.Trim();
        if (IsComparable(service, trimmed))
        {
            map[service] = trimmed;
        }
    }

    /// <summary>
    /// Whether an id is in the same form MangaBaka records for that service, and can therefore be
    /// compared at all.
    /// <para>
    /// Two services publish ids in more than one form. MangaUpdates moved from numeric ids to base36
    /// slugs and old links still carry the numeric form; Kitsu addresses a series by either its numeric
    /// id or its slug. MangaBaka stores the base36 slug and the numeric id respectively, so the other
    /// form of each can never produce a match — but it *can* produce a false
    /// <see cref="ExternalIdVerdict.Mismatch"/> that throws away the right result. Dropping it costs
    /// nothing and is the only safe reading.
    /// </para>
    /// </summary>
    public static bool IsComparable(string service, string id) => service switch
    {
        ExternalIdService.MangaUpdates => id.All(char.IsLetterOrDigit) && id.Any(char.IsLetter),
        ExternalIdService.MangaDex => Guid.TryParse(id, out _),
        ExternalIdService.WeebCentral => id.Length > 0 && id.All(char.IsLetterOrDigit),
        _ => id.All(char.IsAsciiDigit),
    };

    /// <summary>
    /// Everything both maps know, with <paramref name="extra"/> winning a disagreement.
    /// <para>
    /// A source can publish ids in two places at once and they need not overlap: Atsumaru's search
    /// index carries the WeebCentral id and nothing else, while its series page carries the trackers
    /// and not that. Whichever of the two confirmed the match, the other half is still worth keeping.
    /// </para>
    /// </summary>
    public static Dictionary<string, string> Merge(
        IReadOnlyDictionary<string, string>? first, IReadOnlyDictionary<string, string>? extra)
    {
        var merged = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var map in new[] { first, extra })
        {
            if (map is null)
            {
                continue;
            }

            foreach (var (service, id) in map)
            {
                Set(merged, service, id);
            }
        }

        return merged;
    }

    /// <summary>
    /// Compares the ids we hold against a candidate's.
    /// <para>
    /// One agreeing service is enough for <see cref="ExternalIdVerdict.Match"/> even when another
    /// disagrees: these ids are scraped, and a site that has quietly gone stale on one tracker is far
    /// more likely than two different works sharing a tracker id. <see cref="ExternalIdVerdict.Mismatch"/>
    /// therefore needs *every* shared service to disagree.
    /// </para>
    /// </summary>
    public static ExternalIdVerdict Compare(
        IReadOnlyDictionary<string, string>? ours, IReadOnlyDictionary<string, string>? theirs)
    {
        if (ours is null || theirs is null || ours.Count == 0 || theirs.Count == 0)
        {
            return ExternalIdVerdict.NoEvidence;
        }

        var shared = 0;
        foreach (var (service, id) in ours)
        {
            if (!theirs.TryGetValue(service, out var other) || string.IsNullOrWhiteSpace(other))
            {
                continue;
            }

            shared++;
            if (string.Equals(id.Trim(), other.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                return ExternalIdVerdict.Match;
            }
        }

        return shared > 0 ? ExternalIdVerdict.Mismatch : ExternalIdVerdict.NoEvidence;
    }
}
