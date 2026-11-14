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
/// is read. Without the OCR models the colour rule judges alone. A candidate that is a notice already
/// judged takes its verdict without a read (<see cref="CreditNoticeStore"/>).
/// </para>
/// <para>
/// Pages are read with <see cref="ReaderArchiveCache.OpenPageUnmarkedAsync"/>: a search is not a
/// reader, and marking seven files as read would hold their page conversion for ten minutes.
/// </para>
/// </summary>
public sealed class ReaderCreditsCache(
    ReaderArchiveCache archives, IPageTextReader text, CreditStore store, CreditStrips strips, CreditBank bank,
    CreditNoticeStore notices, ILogger<ReaderCreditsCache> logger)
{
    private const int MaxResults = 512;

    /// <summary>Chapters whose edges are kept in memory. An edge pair is at most about 49 KB, so this stays near 3 MB.</summary>
    private const int MaxStrips = 64;

    /// <summary>Pages from each end of a volume file whose text decides whether its series is a scanlation pack.</summary>
    private const int ProbePages = 2;

    /// <summary>Bank proposals judged at one edge before it gives up.</summary>
    private const int MaxBankTries = 4;

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
        int parallelism = 0) =>
        (await SearchAsync(chapter, references, ct, parallelism)).Found;

    /// <summary><see cref="FindAsync"/>, also saying whether the search failed (and was stored as failed).</summary>
    public async Task<(CreditPages Found, bool Failed)> SearchAsync(
        ReaderService.ChapterSlice chapter, IReadOnlyList<ReaderService.ChapterSlice> references, CancellationToken ct,
        int parallelism = 0)
    {
        if (!Applies(chapter))
        {
            return (CreditPages.None, false);
        }

        // With no neighbours only the bank can find anything.
        var usable = references.Where(Usable).ToList();
        var entries = await bank.EntriesAsync(ct);
        if (usable.Count == 0 && entries.Count == 0)
        {
            return (CreditPages.None, false);
        }

        // The bank's newest entry is part of the key: a run from before an entry arrived never answered for
        // it. So is the notice store's generation: a notice accepted since sends chapters back to be searched.
        var key = string.Join('|', usable.Select(Key).Prepend(Key(chapter))) +
            $"|bank{CreditBank.NewestId(entries)}|n{notices.Generation}";
        if (_results.Count >= MaxResults)
        {
            _results.Clear();
        }

        var run = _results.GetOrAdd(key, _ => new Lazy<Task<CreditPages>>(() =>
            RunAsync(chapter, usable, parallelism > 0 ? parallelism : ImageWorkGate.Parallelism)));
        try
        {
            return (await run.Value.WaitAsync(ct), false);
        }
        catch (Exception e) when (e is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            _results.TryRemove(new KeyValuePair<string, Lazy<Task<CreditPages>>>(key, run));
            logger.LogDebug(e, "Credit page search failed for chapter {ChapterId}", chapter.Chapter.Id);
            await TryAsync(() => store.SaveFailedAsync(chapter, CancellationToken.None), chapter);
            return (CreditPages.None, true);
        }
    }

    private int _warming;

    /// <summary>
    /// Starts the search for <paramref name="chapter"/> without waiting for it, so the reader finds the
    /// answer ready (or the run under way) when it asks. One warm-up runs at a time and any other is
    /// dropped: cards warm on hover, and a pointer run along a rail would otherwise start a search per
    /// card. A failure is dropped as <see cref="FindAsync"/> drops it.
    /// </summary>
    /// <returns>Whether a search was started.</returns>
    public bool Warm(ReaderService.ChapterSlice chapter, IReadOnlyList<ReaderService.ChapterSlice> references)
    {
        if (Interlocked.CompareExchange(ref _warming, 1, 0) != 0)
        {
            return false;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await FindAsync(chapter, references, CancellationToken.None);
            }
            finally
            {
                Volatile.Write(ref _warming, 0);
            }
        });
        return true;
    }

    /// <summary>
    /// Whether the text on the first and last pages of the file behind <paramref name="slice"/> reads as
    /// a scanlator's: how a volume file tells a scanlation pack from a published volume without a
    /// search. False without the OCR models (the caller stores that as the colour rule's, to probe again
    /// once they load); null when a page could not be read, so it cannot tell.
    /// </summary>
    public async Task<bool?> ProbeAsync(ReaderService.ChapterSlice slice, CancellationToken ct)
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
            if (bytes is null)
            {
                return null;
            }

            var read = await text.ReadAsync(bytes, 1, ct);
            if (read is null && !text.Available)
            {
                return false;
            }

            if (read is not null && CreditText.IsScanlator(read, colour: false))
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
        var repeated = CreditDetector.Candidates(chapter.PageCount, edges[0], edges[1..]);

        // An edge nothing nearby repeats is offered to the bank: an entry from this chapter or one of
        // the neighbours it was just compared with would only find what repetition already ruled on.
        var entries = await bank.EntriesAsync(CancellationToken.None);
        var nearby = references.Select(r => r.Chapter.Id).Append(chapter.Chapter.Id).ToHashSet();
        bool Eligible(CreditBank.Entry e) => e.SourceChapterId is not { } source || !nearby.Contains(source);
        var judged = new List<JudgedCandidate>();

        async Task<(IReadOnlyList<CreditCandidate> Candidates, int Accepted, CreditBank.Match? Bank)> JudgeEdgeAsync(
            bool tail, IReadOnlyList<CreditCandidate> fromRepetition)
        {
            // A tail whose only repeated region is part of its last page asks the bank as it did before tails had
            // partials, and an accepted bank answer wins over the cut: the bank proposes whole tail pages only.
            var lonePartial = tail && OnlyPartial(fromRepetition);
            if (fromRepetition.Count > 0 && !lonePartial)
            {
                return (fromRepetition, await AcceptedAsync(chapter, edges[0], fromRepetition, tail, judged, fromBank: false), null);
            }

            if (await BankAsync(tail) is { } fromBank)
            {
                return fromBank;
            }

            return lonePartial
                ? (fromRepetition, await AcceptedAsync(chapter, edges[0], fromRepetition, tail, judged, fromBank: false), null)
                : ([], 0, null);
        }

        async Task<(IReadOnlyList<CreditCandidate> Candidates, int Accepted, CreditBank.Match? Bank)?> BankAsync(bool tail)
        {
            // Most proposals are margins the judge turns down, so a few are tried in turn.
            var tried = 0;
            await foreach (var match in bank.MatchesAsync(chapter.PageCount, edges[0], tail, entries, Eligible))
            {
                var accepted = await AcceptedAsync(chapter, edges[0], match.Candidates, tail, judged, fromBank: true);
                if (accepted > 0)
                {
                    return (match.Candidates, accepted, match);
                }

                if (++tried >= MaxBankTries)
                {
                    break;
                }
            }

            return null;
        }

        var (headCandidates, head, headBank) = await JudgeEdgeAsync(tail: false, repeated.Head);
        var (tailCandidates, tail, tailBank) = await JudgeEdgeAsync(tail: true, repeated.Tail);
        var candidates = new CreditCandidates(chapter.PageCount, headCandidates, tailCandidates);
        // Read after judging: the models are only known to be missing once a read tried them.
        var judge = text.Available ? CreditJudge.Ocr : CreditJudge.Colour;
        var found = CreditDetector.Settle(candidates, head, tail);
        logger.LogDebug("Credit page search for chapter {ChapterId} took {Ms} ms: {Found}",
            chapter.Chapter.Id, watch.ElapsedMilliseconds, found);
        await TryAsync(() => store.SaveAsync(chapter, CreditStore.ReferencesKey(references), found, judge, judged,
            CancellationToken.None, bankSeen: CreditBank.NewestId(entries)), chapter);
        await TryAsync(() => notices.SaveLinksAsync(chapter.Chapter.Id, chapter.Series.Id, judged, CancellationToken.None), chapter);
        await TryAsync(() => LearnAsync(chapter, edges[0], candidates, judged, (headBank, head), (tailBank, tail)), chapter);
        return found;
    }

    /// <summary>
    /// Keeps what this search accepted for the bank: a region repetition found at an edge whose first
    /// candidate the text judge accepted on read text (now or, through its notice, before). A region the
    /// bank proposed counts a hit for its entry instead. Nothing is kept without the OCR models: the colour
    /// rule is too loose to teach the bank.
    /// </summary>
    private async Task LearnAsync(ReaderService.ChapterSlice chapter, CreditDetector.Edges edges, CreditCandidates candidates,
        List<JudgedCandidate> judged, params (CreditBank.Match? Bank, int Accepted)[] sides)
    {
        for (var side = 0; side < sides.Length; side++)
        {
            var tail = side == 1;
            var (match, accepted) = sides[side];
            if (accepted == 0)
            {
                continue;
            }

            if (match is not null)
            {
                await bank.CountHitAsync(match.EntryId, CancellationToken.None);
                continue;
            }

            var first = (tail ? candidates.Tail : candidates.Head)[0];
            var read = judged.FirstOrDefault(j => j.Tail == tail && j.Page == first.Page && !j.FromBank);
            // Tail partials come from repetition only: the bank route was not measured for them, so none is learned.
            if (first.Page == 0 && (!tail || first.Share >= 1) && read is { Accepted: true, Text: not null })
            {
                await bank.SeedAsync(chapter, edges, tail, CancellationToken.None);
            }
        }
    }

    /// <summary>Whether an edge's candidates are only a partial region of its outermost page.</summary>
    internal static bool OnlyPartial(IReadOnlyList<CreditCandidate> edge) => edge is [{ Page: 0, Share: < 1 }];

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
    /// <param name="fromBank">
    /// Candidates the bank proposed: only a page OCR read and accepted counts, because without the text
    /// a bank match is as likely a blank margin as a credit.
    /// </param>
    private async Task<int> AcceptedAsync(
        ReaderService.ChapterSlice slice, CreditDetector.Edges edges, IReadOnlyList<CreditCandidate> edge, bool tail,
        List<JudgedCandidate> judged, bool fromBank)
    {
        for (var c = 0; c < edge.Count; c++)
        {
            var (accepted, read, notice) = await IsScanlatorAsync(slice, edges, edge[c], tail, fromBank);
            accepted &= !fromBank || read is not null;
            judged.Add(new JudgedCandidate(tail, edge[c].Page, edge[c].Share, edge[c].Colour, read, accepted, fromBank, notice));
            if (!accepted)
            {
                return c;
            }
        }

        return edge.Count;
    }

    /// <summary>
    /// The keyword rule on the candidate's text, with the colour verdict beside it, through the notice the
    /// candidate is when one is known. The colour verdict alone when the reader is not available or the
    /// page could not be read; the notice store then learns nothing.
    /// </summary>
    private async Task<(bool Accepted, string? Text, int? Notice)> IsScanlatorAsync(
        ReaderService.ChapterSlice slice, CreditDetector.Edges edges, CreditCandidate candidate, bool tail, bool fromBank)
    {
        if (!text.Available)
        {
            return (candidate.Colour, null, null);
        }

        var at = slice.StartPage + (tail ? slice.PageCount - 1 - candidate.Page : candidate.Page);
        // A tail's partial region is the bottom of its page.
        var bottom = tail && candidate.Share < 1;

        async Task<string?> ReadTextAsync(CancellationToken ct)
        {
            var bytes = await ReadAsync(slice.ArchivePath, slice.Pages[at], ct);
            if (bytes is null)
            {
                return null;
            }

            return bottom ? await text.ReadBottomAsync(bytes, candidate.Share, ct) : await text.ReadAsync(bytes, candidate.Share, ct);
        }

        if (CreditNotices.RegionOf(edges, tail, candidate) is not { } region || slice.Entries is not { } entries || at >= entries.Count)
        {
            var read = await ReadTextAsync(CancellationToken.None);
            return read is null ? (candidate.Colour, null, null) : (CreditText.IsScanlator(read, candidate.Colour), read, null);
        }

        var entry = entries[at];
        var verdict = await notices.JudgeAsync(
            new CreditNoticeStore.Query(region.Rows, region.Height, candidate.Share >= 1, candidate.Share, entry.Crc, entry.Length,
                candidate.Colour, fromBank, slice.Chapter.Id, bottom),
            ReadTextAsync, CancellationToken.None);
        return verdict.Text is null ? (candidate.Colour, null, null) : (verdict.Accepted, verdict.Text, verdict.NoticeId);
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
