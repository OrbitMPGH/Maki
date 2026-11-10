namespace Maki.Api.Services;

/// <summary>
/// What readers said about a series, from every site we can ask.
/// <para>
/// <see cref="Reviews"/> are full reviews from MyAnimeList and AniList, alternating so neither site
/// buries the other. <see cref="QuickTakes"/> are Kitsu's one-line reactions, kept apart because
/// they read differently and rank below the full reviews. <see cref="Unavailable"/> lists the
/// <see cref="ReviewSource"/> keys whose fetch failed, so the UI can say a site didn't answer
/// instead of implying nobody reviewed the series there.
/// </para>
/// </summary>
public record SeriesReviews(
    IReadOnlyList<MangaReview> Reviews,
    IReadOnlyList<MangaReview> QuickTakes,
    IReadOnlyList<string> Unavailable)
{
    public static readonly SeriesReviews Empty = new([], [], []);
}

public class SeriesReviewService(MalReviewClient mal, AniListReviewClient aniList, KitsuReactionClient kitsu)
{
    private static readonly Task<IReadOnlyList<MangaReview>?> None = Task.FromResult<IReadOnlyList<MangaReview>?>([]);

    /// <summary>
    /// Fetches from every site the ids allow, in parallel. AniList is asked by MAL id when there is
    /// no AniList id, so a series known only to MAL still gets both.
    /// </summary>
    public async Task<SeriesReviews> GetAsync(int? malId, int? aniListId, int? kitsuId, CancellationToken ct = default)
    {
        var malTask = malId is int m ? mal.GetReviewsAsync(m, ct) : None;
        var aniListTask = aniListId is not null || malId is not null ? aniList.GetReviewsAsync(aniListId, malId, ct) : None;
        var kitsuTask = kitsuId is int k ? kitsu.GetReactionsAsync(k, ct) : None;
        await Task.WhenAll(malTask, aniListTask, kitsuTask);

        var unavailable = new List<string>();
        if (malTask.Result is null)
        {
            unavailable.Add(ReviewSource.MyAnimeList);
        }

        if (aniListTask.Result is null)
        {
            unavailable.Add(ReviewSource.AniList);
        }

        if (kitsuTask.Result is null)
        {
            unavailable.Add(ReviewSource.Kitsu);
        }

        return new SeriesReviews(
            Interleave(malTask.Result ?? [], aniListTask.Result ?? []),
            kitsuTask.Result ?? [],
            unavailable);
    }

    internal static List<MangaReview> Interleave(IReadOnlyList<MangaReview> first, IReadOnlyList<MangaReview> second)
    {
        var merged = new List<MangaReview>(first.Count + second.Count);
        for (var i = 0; i < Math.Max(first.Count, second.Count); i++)
        {
            if (i < first.Count)
            {
                merged.Add(first[i]);
            }

            if (i < second.Count)
            {
                merged.Add(second[i]);
            }
        }

        return merged;
    }
}
