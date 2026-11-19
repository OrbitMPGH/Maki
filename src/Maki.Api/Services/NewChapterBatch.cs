namespace Maki.Api.Services;

/// <summary>
/// What "N new" means, on Home's Recently added rail and on Library cards alike: the chapters in
/// a series' latest batch of files that the caller has not read. A batch is the newest run of the
/// series' files where each arrived within <see cref="BatchGap"/> of the next.
/// </summary>
public static class NewChapterBatch
{
    /// <summary>A backlog download lands minutes apart; weekly or daily releases fall outside it.</summary>
    public static readonly TimeSpan BatchGap = TimeSpan.FromHours(12);

    public readonly record struct File(int Id, DateTime DateAdded);

    public readonly record struct Chapter(int Id, decimal? Number, int? Volume);

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
    /// The chapters the batch files back that are still unread, in batch order, one per
    /// <see cref="ContinueReadingService.SlotOf"/>: a volume file counts every chapter in it, and
    /// two languages' copies of one chapter count once.
    /// </summary>
    /// <param name="unreadChapterIds">From <see cref="NextChapter.UnreadChapterIds"/>; null when nothing is left to read.</param>
    public static List<Chapter> UnreadChapters(
        IEnumerable<int> batch, Func<int, IEnumerable<Chapter>> chaptersOf, IReadOnlySet<int>? unreadChapterIds) =>
        unreadChapterIds is null
            ? []
            : [.. batch.SelectMany(chaptersOf)
                .Where(c => unreadChapterIds.Contains(c.Id))
                .DistinctBy(c => (Slot: ContinueReadingService.SlotOf(c.Number, c.Volume), Id: c.Number is null ? c.Id : 0))];
}
