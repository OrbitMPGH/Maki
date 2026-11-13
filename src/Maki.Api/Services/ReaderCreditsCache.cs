using System.Collections.Concurrent;
using System.Diagnostics;
using Maki.Api.Services.Ocr;
using Maki.Core.Entities;
using Maki.Core.Images;
using Maki.Core.Reading;

namespace Maki.Api.Services;

/// <summary>
/// Which credit pages each chapter's reader leaves out (<see cref="CreditDetector"/>). Every answer is
/// stored (<see cref="CreditStore"/>), which is where the reader looks first; this runs the search
/// for whatever has no current answer yet, for the reader and for <see cref="CreditScanWorker"/>.
/// Runs are shared per set of edge keys, and each chapter's two edge strips are kept in memory for
/// the last few chapters and on disk (<see cref="CreditStrips"/>), so moving to the next chapter
/// decodes one chapter's edges at most.
/// <para>
/// The detector's repeated pages are only candidates. Each is judged from the edge inward by the
/// text on it (<see cref="IPageTextReader"/>, <see cref="CreditText"/>), so publisher pages, title
/// pages and covers stay; the first one that is not a scanlator's ends the run, and nothing behind it
/// is read. Without the OCR models the colour rule judges alone.
/// </para>
/// <para>
/// Pages are read with <see cref="ReaderArchiveCache.OpenPageUnmarkedAsync"/>: a search is not a
/// reader, and marking seven files as read would hold their page conversion for ten minutes.
/// </para>
/// </summary>
public sealed class ReaderCreditsCache(
    ReaderArchiveCache archives, IPageTextReader text, CreditStore store, CreditStrips strips,
    ILogger<ReaderCreditsCache> logger)
{
    private const int MaxResults = 512;

    /// <summary>Chapters whose edges are kept in memory. An edge pair is at most about 49 KB, so this stays near 3 MB.</summary>
    private const int MaxStrips = 64;

    /// <summary>Pages from each end of a volume file whose text decides whether its series is a scanlation pack.</summary>
    private const int ProbePages = 2;

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
    /// nearby chapters), and stored. The run itself is shared and not cancelled with the request that
    /// started it. A failed search answers <see cref="CreditPages.None"/> and is stored as failed.
    /// <paramref name="parallelism"/> caps how many pages decode at once (0: the image gate's share).
    /// </summary>
    public async Task<CreditPages> FindAsync(
        ReaderService.ChapterSlice chapter, IReadOnlyList<ReaderService.ChapterSlice> references, CancellationToken ct,
        int parallelism = 0)
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

        var key = string.Join('|', usable.Select(Key).Prepend(Key(chapter)));
        if (_results.Count >= MaxResults)
        {
            _results.Clear();
        }

        var run = _results.GetOrAdd(key, _ => new Lazy<Task<CreditPages>>(() =>
            RunAsync(chapter, usable, parallelism > 0 ? parallelism : ImageWorkGate.Parallelism)));
        try
        {
            return await run.Value.WaitAsync(ct);
        }
        catch (Exception e) when (e is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            _results.TryRemove(new KeyValuePair<string, Lazy<Task<CreditPages>>>(key, run));
            logger.LogDebug(e, "Credit page search failed for chapter {ChapterId}", chapter.Chapter.Id);
            await TryAsync(() => store.SaveFailedAsync(chapter, CancellationToken.None), chapter);
            return CreditPages.None;
        }
    }

    /// <summary>
    /// Starts the search for <paramref name="chapter"/> without waiting for it, so the reader finds the
    /// answer ready (or the run under way) when it asks. A failure is dropped as <see cref="FindAsync"/> drops it.
    /// </summary>
    public void Warm(ReaderService.ChapterSlice chapter, IReadOnlyList<ReaderService.ChapterSlice> references) =>
        _ = Task.Run(() => FindAsync(chapter, references, CancellationToken.None));

    /// <summary>
    /// Whether the text on the first and last pages of the file behind <paramref name="slice"/> reads as
    /// a scanlator's: how a volume file tells a scanlation pack from a published volume without a
    /// search. False when the OCR models are not available.
    /// </summary>
    public async Task<bool> ProbeAsync(ReaderService.ChapterSlice slice, CancellationToken ct)
    {
        if (!text.Available || ComicFile.IsPdf(slice.ArchivePath))
        {
            return false;
        }

        var count = slice.Pages.Count;
        var pages = Enumerable.Range(0, Math.Min(ProbePages, count))
            .Concat(Enumerable.Range(Math.Max(0, count - ProbePages), Math.Min(ProbePages, count)))
            .Distinct();
        foreach (var index in pages)
        {
            ct.ThrowIfCancellationRequested();
            var bytes = await ReadAsync(slice.ArchivePath, slice.Pages[index], ct);
            if (bytes is not null && await text.ReadAsync(bytes, 1, ct) is { } read && CreditText.IsScanlator(read, colour: false))
            {
                return true;
            }
        }

        return false;
    }

    private static string Key(ReaderService.ChapterSlice slice) => slice.EdgeKey ?? ReaderRecapCache.Key(slice);

    private async Task<CreditPages> RunAsync(
        ReaderService.ChapterSlice chapter, IReadOnlyList<ReaderService.ChapterSlice> references, int parallelism)
    {
        var watch = Stopwatch.StartNew();
        // The models take a few hundred milliseconds to load; that happens while the edges decode.
        _ = Task.Run(text.PrepareAsync);
        var edges = await Task.WhenAll(references.Prepend(chapter).Select(s => EdgesAsync(s, parallelism)));
        var candidates = CreditDetector.Candidates(chapter.PageCount, edges[0], edges[1..]);
        var judged = new List<JudgedCandidate>();
        var head = await AcceptedAsync(chapter, candidates.Head, tail: false, judged);
        var tail = await AcceptedAsync(chapter, candidates.Tail, tail: true, judged);
        // Read after judging: the models are only known to be missing once a read tried them.
        var judge = text.Available ? CreditJudge.Ocr : CreditJudge.Colour;
        var found = CreditDetector.Settle(candidates, head, tail);
        logger.LogDebug("Credit page search for chapter {ChapterId} took {Ms} ms: {Found}",
            chapter.Chapter.Id, watch.ElapsedMilliseconds, found);
        await TryAsync(() => store.SaveAsync(chapter, CreditStore.ReferencesKey(references), found, judge, judged,
            CancellationToken.None), chapter);
        return found;
    }

    private async Task TryAsync(Func<Task> write, ReaderService.ChapterSlice chapter)
    {
        try
        {
            await write();
        }
        catch (Exception e)
        {
            // The answer still reaches the reader; only the stored copy is lost.
            logger.LogDebug(e, "Could not store the credit answer for chapter {ChapterId}", chapter.Chapter.Id);
        }
    }

    /// <summary>How many candidates from the edge are a scanlator's before the first that is not.</summary>
    private async Task<int> AcceptedAsync(
        ReaderService.ChapterSlice slice, IReadOnlyList<CreditCandidate> edge, bool tail, List<JudgedCandidate> judged)
    {
        for (var c = 0; c < edge.Count; c++)
        {
            var (accepted, read) = await IsScanlatorAsync(slice, edge[c], tail);
            judged.Add(new JudgedCandidate(tail, edge[c].Page, edge[c].Share, edge[c].Colour, read, accepted));
            if (!accepted)
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
    private async Task<(bool Accepted, string? Text)> IsScanlatorAsync(
        ReaderService.ChapterSlice slice, CreditCandidate candidate, bool tail)
    {
        if (!text.Available)
        {
            return (candidate.Colour, null);
        }

        var index = tail ? slice.PageCount - 1 - candidate.Page : candidate.Page;
        var bytes = await ReadAsync(slice.ArchivePath, slice.Pages[slice.StartPage + index], CancellationToken.None);
        if (bytes is null)
        {
            return (candidate.Colour, null);
        }

        var read = await text.ReadAsync(bytes, candidate.Share, CancellationToken.None);
        return read is null ? (candidate.Colour, null) : (CreditText.IsScanlator(read, candidate.Colour), read);
    }

    private async Task<byte[]?> ReadAsync(string archivePath, string entry, CancellationToken ct)
    {
        await using var source = await archives.OpenPageUnmarkedAsync(archivePath, entry, ct);
        if (source is null)
        {
            return null;
        }

        using var copy = new MemoryStream();
        await source.CopyToAsync(copy, ct);
        return copy.ToArray();
    }

    private async Task<CreditDetector.Edges> EdgesAsync(ReaderService.ChapterSlice slice, int parallelism)
    {
        var key = Key(slice);
        if (!_strips.TryGetValue(key, out var strip))
        {
            var fresh = new Lazy<Task<CreditDetector.Edges>>(() => LoadEdgesAsync(slice, parallelism));
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

    /// <summary>The strips stored on disk for the slice's edge key, else decoded and stored.</summary>
    private async Task<CreditDetector.Edges> LoadEdgesAsync(ReaderService.ChapterSlice slice, int parallelism)
    {
        if (slice.EdgeKey is { } edgeKey && strips.Read(edgeKey) is { } stored)
        {
            return stored;
        }

        var head = ShrinkEdgeAsync(slice, tail: false, parallelism);
        var tail = ShrinkEdgeAsync(slice, tail: true, parallelism);
        var edges = new CreditDetector.Edges(await head, await tail);
        if (slice.EdgeKey is { } key)
        {
            strips.Write(key, edges);
        }

        return edges;
    }

    /// <summary>
    /// The pages an edge strip needs, from the edge inward. The first page alone often fills the strip
    /// (a webtoon slice), so it is shrunk first, and only as many more as its height says are needed.
    /// </summary>
    private async Task<CreditDetector.Edge> ShrinkEdgeAsync(ReaderService.ChapterSlice slice, bool tail, int parallelism)
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
            await Parallel.ForAsync(0, want, new ParallelOptions { MaxDegreeOfParallelism = parallelism },
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
            var bytes = await ReadAsync(slice.ArchivePath, entry, ct);
            if (bytes is null)
            {
                return null;
            }

            try
            {
                return CreditDetector.Shrink(bytes);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                return null;
            }
        }, ct);
    }
}
