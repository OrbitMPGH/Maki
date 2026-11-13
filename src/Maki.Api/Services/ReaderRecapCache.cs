using System.Collections.Concurrent;
using System.Diagnostics;
using Maki.Core.Images;
using Maki.Core.Reading;

namespace Maki.Api.Services;

/// <summary>
/// Where each chapter's recap of the previous chapter ends (<see cref="RecapDetector"/>), kept in
/// memory per pair of file versions. The reader asks once when the previous chapter nears its end
/// and again when the chapter opens, so both requests share one run.
/// </summary>
public sealed class ReaderRecapCache(ReaderArchiveCache archives, ILogger<ReaderRecapCache> logger)
{
    private const int MaxEntries = 512;

    private readonly ConcurrentDictionary<string, Lazy<Task<RecapEnd?>>> _results = new();

    /// <summary>
    /// The recap at the top of <paramref name="chapter"/>, matched against <paramref name="previous"/>.
    /// The run itself is shared and not cancelled with the request that started it.
    /// </summary>
    public async Task<RecapEnd?> FindAsync(
        ReaderService.ChapterSlice chapter, ReaderService.ChapterSlice previous, CancellationToken ct)
    {
        if (ComicFile.IsPdf(chapter.ArchivePath) || ComicFile.IsPdf(previous.ArchivePath))
        {
            return null;
        }

        var key = $"{Key(chapter)}|{Key(previous)}";
        if (_results.Count >= MaxEntries)
        {
            _results.Clear();
        }

        var run = _results.GetOrAdd(key, _ => new Lazy<Task<RecapEnd?>>(() => RunAsync(chapter, previous)));
        try
        {
            return await run.Value.WaitAsync(ct);
        }
        catch (Exception e) when (e is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            _results.TryRemove(new KeyValuePair<string, Lazy<Task<RecapEnd?>>>(key, run));
            logger.LogDebug(e, "Recap search failed for chapter {ChapterId}", chapter.Chapter.Id);
            return null;
        }
    }

    internal static string Key(ReaderService.ChapterSlice slice) =>
        $"{slice.ChapterFileId}-{slice.ArchiveVersion}-{slice.StartPage}-{slice.PageCount}";

    private async Task<RecapEnd?> RunAsync(ReaderService.ChapterSlice chapter, ReaderService.ChapterSlice previous)
    {
        var watch = Stopwatch.StartNew();
        var head = await ShrinkAsync(chapter, 0, Math.Min(RecapDetector.HeadPages, chapter.PageCount));
        var tailCount = Math.Min(RecapDetector.TailPages, previous.PageCount);
        var tail = await ShrinkAsync(previous, previous.PageCount - tailCount, tailCount);
        var end = RecapDetector.Find(head, tail);
        logger.LogDebug("Recap search for chapter {ChapterId} took {Ms} ms: {End}",
            chapter.Chapter.Id, watch.ElapsedMilliseconds, end?.ToString() ?? "none");
        return end;
    }

    private async Task<GreyPage?[]> ShrinkAsync(ReaderService.ChapterSlice slice, int from, int count)
    {
        var pages = new GreyPage?[count];
        await Parallel.ForAsync(0, count, new ParallelOptions { MaxDegreeOfParallelism = ImageWorkGate.Parallelism },
            async (index, token) =>
            {
                var entry = slice.Pages[slice.StartPage + from + index];
                pages[index] = await ImageWorkGate.RunAsync<GreyPage?>(async () =>
                {
                    await using var source = await archives.OpenPageAsync(slice.ArchivePath, entry, token);
                    if (source is null)
                    {
                        return null;
                    }

                    using var copy = new MemoryStream();
                    await source.CopyToAsync(copy, token);
                    try
                    {
                        return RecapDetector.Shrink(copy.ToArray());
                    }
                    catch (Exception e) when (e is not OperationCanceledException)
                    {
                        return null;
                    }
                }, token);
            });
        return pages;
    }
}
