using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Maki.Core.Entities;
using Maki.Core.Images;
using Maki.Data;
using Microsoft.EntityFrameworkCore;

namespace Maki.Api.Services;

/// <summary>
/// Credit regions the search already accepted (<see cref="CreditBankEntry"/>), so a chapter whose
/// notice nothing near it repeats is still caught: a lone scanlated chapter among volumes, a group's
/// card pasted into one chapter of many.
/// <para>
/// An entry only proposes candidates (<see cref="CreditDetector.BankCandidates"/>, after the cheap
/// <see cref="CreditDetector.Alike"/>), and only a page OCR read and accepted is hidden. Unchecked,
/// bank matches were mostly blank margins: on the 4,942-chapter scan (2026-11-13, git-ignored
/// <c>research/credit-pages/2026-11-13-lone-credits</c>) 34 of 406 passed the judge, and all 34 were
/// credits. Entries come only from regions repetition found and the text judge accepted, never from a
/// bank match, so a mistake cannot feed itself.
/// </para>
/// <para>
/// Only the signatures stay in memory; an entry's strip (about 25 KB) is read from the database the
/// first time its signature passes, which is 0.7% of comparisons, and the last few hundred are kept.
/// </para>
/// </summary>
public sealed class CreditBank(IServiceScopeFactory scopes, ILogger<CreditBank> logger)
{
    /// <summary>Entries kept per edge at most; the scan's library needed 82 head and 143 tail.</summary>
    public const int MaxEntriesPerEdge = 2000;

    /// <summary>Strips kept decoded, from the entries whose signatures passed most recently.</summary>
    private const int MaxStrips = 256;

    public sealed record Entry(int Id, bool Tail, int SeriesId, int? SourceChapterId, BankSignature Signature);

    /// <summary>A bank match: the candidates it proposed at that edge, and the entry that proposed them.</summary>
    public sealed record Match(IReadOnlyList<CreditCandidate> Candidates, int EntryId);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ConcurrentDictionary<int, CreditDetector.Edge> _strips = new();
    private IReadOnlyList<Entry>? _entries;

    public async Task<IReadOnlyList<Entry>> EntriesAsync(CancellationToken ct)
    {
        if (Volatile.Read(ref _entries) is { } loaded)
        {
            return loaded;
        }

        await _gate.WaitAsync(ct);
        try
        {
            return _entries ??= await LoadAsync(ct);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>The newest entry's id, or 0 with none: what a search records it was compared against.</summary>
    public static int NewestId(IReadOnlyList<Entry> entries) => entries.Count == 0 ? 0 : entries[^1].Id;

    /// <summary>
    /// Every entry at <paramref name="tail"/>'s edge, newer than <paramref name="afterId"/> and
    /// <paramref name="eligible"/>, that proposes candidates on <paramref name="chapter"/>, oldest first.
    /// Most proposals are blank margins the judge turns down, so a caller tries more than the first.
    /// </summary>
    public async IAsyncEnumerable<Match> MatchesAsync(int pageCount, CreditDetector.Edges chapter, bool tail,
        IReadOnlyList<Entry> entries, Func<Entry, bool> eligible, int afterId = 0, [EnumeratorCancellation] CancellationToken ct = default)
    {
        if (CreditDetector.SignatureOf(tail ? chapter.Tail : chapter.Head) is not { } mine)
        {
            yield break;
        }

        foreach (var entry in entries)
        {
            if (entry.Tail != tail || entry.Id <= afterId || !CreditDetector.Alike(mine, entry.Signature) || !eligible(entry))
            {
                continue;
            }

            if (await StripAsync(entry.Id, ct) is not { } strip)
            {
                continue;
            }

            var candidates = CreditDetector.BankCandidates(pageCount, chapter, strip, tail);
            if (candidates.Count > 0)
            {
                yield return new Match(candidates, entry.Id);
            }
        }
    }

    /// <summary>The first of <see cref="MatchesAsync"/>, or null.</summary>
    public async Task<Match?> FindAsync(int pageCount, CreditDetector.Edges chapter, bool tail, IReadOnlyList<Entry> entries,
        Func<Entry, bool> eligible, int afterId, CancellationToken ct)
    {
        await foreach (var match in MatchesAsync(pageCount, chapter, tail, entries, eligible, afterId, ct))
        {
            return match;
        }

        return null;
    }

    /// <summary>
    /// Adds the region at <paramref name="tail"/>'s edge of <paramref name="slice"/>, which repetition
    /// found and the text judge accepted from the edge itself, unless an entry already covers it (that
    /// entry counts a hit instead).
    /// </summary>
    public async Task SeedAsync(ReaderService.ChapterSlice slice, CreditDetector.Edges edges, bool tail, CancellationToken ct)
    {
        var edge = tail ? edges.Tail : edges.Head;
        if (CreditDetector.SignatureOf(edge) is not { } signature)
        {
            return;
        }

        await _gate.WaitAsync(ct);
        try
        {
            var entries = _entries ??= await LoadAsync(ct);
            if (await FindAsync(slice.PageCount, edges, tail, entries, _ => true, 0, ct) is { } covered)
            {
                await CountHitUnlockedAsync(covered.EntryId, ct);
                return;
            }

            if (entries.Count(e => e.Tail == tail) >= MaxEntriesPerEdge)
            {
                return;
            }

            using var stream = new MemoryStream();
            using (var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true))
            {
                edge.Write(writer);
            }

            var row = new CreditBankEntry
            {
                Tail = tail, SeriesId = slice.Series.Id, SourceChapterId = slice.Chapter.Id, Strip = stream.ToArray(),
                SignatureFirst = signature.First, Signature = signature.Rows, CreatedAtUtc = DateTime.UtcNow,
            };
            using var scope = scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<MakiDbContext>();
            db.CreditBank.Add(row);
            await db.SaveChangesAsync(ct);
            Keep(row.Id, edge);
            _entries = [.. entries, new Entry(row.Id, tail, row.SeriesId, row.SourceChapterId, signature)];
            logger.LogDebug("Credit bank: kept the {Edge} of chapter {ChapterId} as entry {Id}",
                tail ? "tail" : "head", slice.Chapter.Id, row.Id);
        }
        catch (DbUpdateException e)
        {
            // The chapter or series went while the search ran.
            logger.LogDebug(e, "Could not add chapter {ChapterId} to the credit bank", slice.Chapter.Id);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Counts a chapter an entry caught.</summary>
    public Task CountHitAsync(int entryId, CancellationToken ct) => CountHitUnlockedAsync(entryId, ct);

    private async Task CountHitUnlockedAsync(int entryId, CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MakiDbContext>();
        await db.CreditBank.IgnoreQueryFilters().Where(b => b.Id == entryId)
            .ExecuteUpdateAsync(u => u.SetProperty(b => b.Hits, b => b.Hits + 1), ct);
    }

    /// <summary>An entry's strip, decoded; null when the entry is gone (its series was deleted) or unreadable.</summary>
    private async Task<CreditDetector.Edge?> StripAsync(int id, CancellationToken ct)
    {
        if (_strips.TryGetValue(id, out var cached))
        {
            return cached;
        }

        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MakiDbContext>();
        var bytes = await db.CreditBank.AsNoTracking().IgnoreQueryFilters()
            .Where(b => b.Id == id).Select(b => b.Strip).FirstOrDefaultAsync(ct);
        if (bytes is null)
        {
            return null;
        }

        try
        {
            using var reader = new BinaryReader(new MemoryStream(bytes));
            var edge = CreditDetector.Edge.Read(reader);
            Keep(id, edge);
            return edge;
        }
        catch (Exception e) when (e is InvalidDataException or EndOfStreamException)
        {
            logger.LogDebug(e, "Credit bank entry {Id} could not be read; left out", id);
            return null;
        }
    }

    private void Keep(int id, CreditDetector.Edge edge)
    {
        if (_strips.Count >= MaxStrips)
        {
            _strips.Clear();
        }

        _strips[id] = edge;
    }

    private async Task<IReadOnlyList<Entry>> LoadAsync(CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MakiDbContext>();
        var rows = await db.CreditBank.AsNoTracking().IgnoreQueryFilters()
            .OrderBy(b => b.Id)
            .Select(b => new { b.Id, b.Tail, b.SeriesId, b.SourceChapterId, b.SignatureFirst, b.Signature })
            .ToListAsync(ct);
        return
        [
            .. rows.Where(r => r.Signature.Length == CreditDetector.SignatureRows * CreditDetector.Width)
                .Select(r => new Entry(r.Id, r.Tail, r.SeriesId, r.SourceChapterId, new BankSignature(r.SignatureFirst, r.Signature))),
        ];
    }
}
