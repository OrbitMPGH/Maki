namespace Maki.Api.Services;

/// <summary>
/// What "N new" means, on Home's Recently added rail and on Library cards alike: the files in a
/// series' latest batch that the caller has not read. A batch is the newest run of the series'
/// files where each arrived within <see cref="BatchGap"/> of the next.
/// </summary>
public static class NewChapterBatch
{
    /// <summary>A backlog download lands minutes apart; weekly or daily releases fall outside it.</summary>
    public static readonly TimeSpan BatchGap = TimeSpan.FromHours(12);

    public readonly record struct File(int Id, DateTime DateAdded);

    /// <summary>The ids of one series' latest batch, newest first. Empty for no files.</summary>
    public static List<int> Latest(IEnumerable<File> files)
    {
        var ordered = files.OrderByDescending(f => f.DateAdded).ThenByDescending(f => f.Id).ToList();
        return
        [
            .. ordered.Take(1)
                .Concat(ordered.Zip(ordered.Skip(1))
                    .TakeWhile(p => p.First.DateAdded - p.Second.DateAdded <= BatchGap)
                    .Select(p => p.Second))
                .Select(f => f.Id),
        ];
    }

    /// <summary>
    /// The batch files that back at least one chapter still unread, in batch order. A volume file
    /// backs several chapters and stays new until all of them are read.
    /// </summary>
    /// <param name="unreadChapterIds">From <see cref="NextChapter.UnreadChapterIds"/>; null when nothing is left to read.</param>
    public static List<int> UnreadFiles(
        IEnumerable<int> batch, Func<int, IEnumerable<int>> chapterIdsOf, IReadOnlySet<int>? unreadChapterIds) =>
        unreadChapterIds is null ? [] : [.. batch.Where(id => chapterIdsOf(id).Any(unreadChapterIds.Contains))];
}
