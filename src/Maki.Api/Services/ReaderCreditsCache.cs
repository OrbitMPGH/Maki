using System.Collections.Concurrent;
using System.Diagnostics;
using Maki.Api.Services.Ocr;
using Maki.Core.Images;
using Maki.Core.Reading;

namespace Maki.Api.Services;

/// <summary>
/// Which credit pages each chapter's reader leaves out (<see cref="CreditDetector"/>), kept in memory
/// per set of file versions. Each chapter's two edge strips are cached on their own as well: moving to
/// the next chapter compares against five of the same six references, so only one new chapter's edges
/// are decoded.
/// <para>
/// The detector's repeated pages are only candidates. Each is judged from the edge inward by the
/// text on it (<see cref="IPageTextReader"/>, <see cref="CreditText"/>), so publisher pages, title
/// pages and covers stay; the first one that is not a scanlator's ends the run, and nothing behind it
/// is read. Without the OCR models the colour rule judges alone.
/// </para>
/// </summary>
public sealed class ReaderCreditsCache(
    ReaderArchiveCache archives, IPageTextReader text, ILogger<ReaderCreditsCache> logger)
{
    private const int MaxResults = 512;

    /// <summary>Chapters whose edges are kept. An edge pair is at most about 49 KB, so this stays near 3 MB.</summary>
    private const int MaxStrips = 64;

    private readonly ConcurrentDictionary<string, Lazy<Task<CreditPages>>> _results = new();
    private readonly ConcurrentDictionary<string, Lazy<Task<CreditDetector.Edges>>> _strips = new();
    /// <summary>Strips in the order they were added, each with the entry it added, so a stale key never evicts a newer entry.</summary>
    private readonly ConcurrentQueue<KeyValuePair<string, Lazy<Task<CreditDetector.Edges>>>> _stripOrder = new();

    /// <summary>Whether a chapter can have credits hidden at all; PDFs and short chapters cannot.</summary>
    public static bool Applies(ReaderService.ChapterSlice slice) =>
        !ComicFile.IsPdf(slice.ArchivePath) && slice.PageCount >= CreditDetector.MinPages;

    /// <summary>Whether a nearby chapter is worth comparing against.</summary>
    public static bool Usable(ReaderService.ChapterSlice slice) =>
        !ComicFile.IsPdf(slice.ArchivePath) && slice.PageCount >= CreditDetector.MinReferencePages;

    /// <summary>
    /// The credit pages of <paramref name="chapter"/>, compared with <paramref name="references"/> (the
    /// nearby chapters). The run itself is shared and not cancelled with the request that started it.
    /// </summary>
    public async Task<CreditPages> FindAsync(
        ReaderService.ChapterSlice chapter, IReadOnlyList<ReaderService.ChapterSlice> references, CancellationToken ct)
    {
        if (!Applies(chapter))
        {
            return CreditPages.None;
        }

        var usable = references.Where(Usable).ToList();
        if (usable.Count == 0)
        {
            return CreditPages.None;
        }

        var key = string.Join('|', usable.Select(ReaderRecapCache.Key).Prepend(ReaderRecapCache.Key(chapter)));
        if (_results.Count >= MaxResults)
        {
            _results.Clear();
        }

        var run = _results.GetOrAdd(key, _ => new Lazy<Task<CreditPages>>(() => RunAsync(chapter, usable)));
        try
        {
            return await run.Value.WaitAsync(ct);
        }
        catch (Exception e) when (e is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            _results.TryRemove(new KeyValuePair<string, Lazy<Task<CreditPages>>>(key, run));
            logger.LogDebug(e, "Credit page search failed for chapter {ChapterId}", chapter.Chapter.Id);
            return CreditPages.None;
        }
    }

    /// <summary>
    /// Starts the search for <paramref name="chapter"/> without waiting for it, so the reader finds the
    /// answer ready (or the run under way) when it asks. A failure is dropped as <see cref="FindAsync"/> drops it.
    /// </summary>
    public void Warm(ReaderService.ChapterSlice chapter, IReadOnlyList<ReaderService.ChapterSlice> references) =>
        _ = Task.Run(() => FindAsync(chapter, references, CancellationToken.None));

    private async Task<CreditPages> RunAsync(
        ReaderService.ChapterSlice chapter, IReadOnlyList<ReaderService.ChapterSlice> references)
    {
        var watch = Stopwatch.StartNew();
        // The models take a few hundred milliseconds to load; that happens while the edges decode.
        _ = Task.Run(text.PrepareAsync);
        var edges = await Task.WhenAll(references.Prepend(chapter).Select(EdgesAsync));
        var candidates = CreditDetector.Candidates(chapter.PageCount, edges[0], edges[1..]);
        var head = await AcceptedAsync(chapter, candidates.Head, tail: false);
        var tail = await AcceptedAsync(chapter, candidates.Tail, tail: true);
        var found = CreditDetector.Settle(candidates, head, tail);
        logger.LogDebug("Credit page search for chapter {ChapterId} took {Ms} ms: {Found}",
            chapter.Chapter.Id, watch.ElapsedMilliseconds, found);
        return found;
    }

    /// <summary>How many candidates from the edge are a scanlator's before the first that is not.</summary>
    private async Task<int> AcceptedAsync(
        ReaderService.ChapterSlice slice, IReadOnlyList<CreditCandidate> edge, bool tail)
    {
        for (var c = 0; c < edge.Count; c++)
        {
            if (!await IsScanlatorAsync(slice, edge[c], tail))
            {
                return c;
            }
        }

        return edge.Count;
    }

    /// <summary>
    /// The keyword rule on the candidate's text, with the colour verdict beside it. The colour verdict
    /// alone when the reader is not available or the page could not be read.
    /// </summary>
    private async Task<bool> IsScanlatorAsync(ReaderService.ChapterSlice slice, CreditCandidate candidate, bool tail)
    {
        if (!text.Available)
        {
            return candidate.Colour;
        }

        var index = tail ? slice.PageCount - 1 - candidate.Page : candidate.Page;
        var entry = slice.Pages[slice.StartPage + index];
        byte[] bytes;
        await using (var source = await archives.OpenPageAsync(slice.ArchivePath, entry, CancellationToken.None))
        {
            if (source is null)
            {
                return candidate.Colour;
            }

            using var copy = new MemoryStream();
            await source.CopyToAsync(copy);
            bytes = copy.ToArray();
        }

        var read = await text.ReadAsync(bytes, candidate.Share, CancellationToken.None);
        return read is null ? candidate.Colour : CreditText.IsScanlator(read, candidate.Colour);
    }

    private async Task<CreditDetector.Edges> EdgesAsync(ReaderService.ChapterSlice slice)
    {
        var key = ReaderRecapCache.Key(slice);
        if (!_strips.TryGetValue(key, out var strip))
        {
            var fresh = new Lazy<Task<CreditDetector.Edges>>(() => ShrinkEdgesAsync(slice));
            strip = _strips.GetOrAdd(key, fresh);
            if (ReferenceEquals(strip, fresh))
            {
                _stripOrder.Enqueue(new(key, fresh));
                while (_strips.Count > MaxStrips && _stripOrder.TryDequeue(out var oldest))
                {
                    _strips.TryRemove(oldest);
                }
            }
        }

        try
        {
            return await strip.Value;
        }
        catch
        {
            _strips.TryRemove(new KeyValuePair<string, Lazy<Task<CreditDetector.Edges>>>(key, strip));
            throw;
        }
    }

    private async Task<CreditDetector.Edges> ShrinkEdgesAsync(ReaderService.ChapterSlice slice)
    {
        var head = ShrinkEdgeAsync(slice, tail: false);
        var tail = ShrinkEdgeAsync(slice, tail: true);
        return new CreditDetector.Edges(await head, await tail);
    }

    /// <summary>
    /// The pages an edge strip needs, from the edge inward. The first page alone often fills the strip
    /// (a webtoon slice), so it is shrunk first, and only as many more as its height says are needed.
    /// </summary>
    private async Task<CreditDetector.Edge> ShrinkEdgeAsync(ReaderService.ChapterSlice slice, bool tail)
    {
        var limit = Math.Min(CreditDetector.EdgePages, slice.PageCount);
        var pages = new List<CreditPage?>();
        var rows = 0;
        while (rows < CreditDetector.EdgeRows && pages.Count < limit)
        {
            var typical = pages.Count == 0 ? 0 : Math.Max(1, rows / pages.Count);
            var want = typical == 0
                ? 1
                : Math.Clamp((CreditDetector.EdgeRows - rows + typical - 1) / typical, 1, limit - pages.Count);
            var batch = new CreditPage?[want];
            var from = pages.Count;
            await Parallel.ForAsync(0, want, new ParallelOptions { MaxDegreeOfParallelism = ImageWorkGate.Parallelism },
                async (index, token) =>
                {
                    var at = from + index;
                    batch[index] = await ShrinkAsync(slice, tail ? slice.PageCount - 1 - at : at, token);
                });

            foreach (var page in batch)
            {
                pages.Add(page);
                if (page is null)
                {
                    return CreditDetector.Edge.Of(pages, tail);
                }

                rows += page.Height;
            }
        }

        return CreditDetector.Edge.Of(pages, tail);
    }

    private async Task<CreditPage?> ShrinkAsync(ReaderService.ChapterSlice slice, int index, CancellationToken ct)
    {
        var entry = slice.Pages[slice.StartPage + index];
        return await ImageWorkGate.RunAsync<CreditPage?>(async () =>
        {
            await using var source = await archives.OpenPageAsync(slice.ArchivePath, entry, ct);
            if (source is null)
            {
                return null;
            }

            using var copy = new MemoryStream();
            await source.CopyToAsync(copy, ct);
            try
            {
                return CreditDetector.Shrink(copy.ToArray());
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                return null;
            }
        }, ct);
    }
}
