using Maki.Core.Entities;
using Maki.Core.Images;
using Maki.Data;
using Microsoft.EntityFrameworkCore;

namespace Maki.Api.Services;

/// <summary>
/// Credit regions the search already accepted (<see cref="CreditBankEntry"/>), so a chapter whose
/// notice nothing near it repeats is still caught: a lone scanlated chapter among volumes, a group's
/// card pasted into one chapter of many. Kept in memory once read; the search adds to it.
/// <para>
/// An entry only proposes candidates (<see cref="CreditDetector.BankCandidates"/>, after the cheap
/// <see cref="CreditDetector.Alike"/>); the text judge decides as for any other. Unchecked, bank
/// matches were mostly blank margins: on the 4,942-chapter scan (2026-11-13, git-ignored
/// <c>research/credit-pages/2026-11-13-lone-credits</c>) 34 of 406 passed the judge, and all 34 were credits.
/// Entries come only from regions repetition found and the text judge accepted, never from a bank
/// match, so a mistake cannot feed itself.
/// </para>
/// </summary>
public sealed class CreditBank(IServiceScopeFactory scopes, ILogger<CreditBank> logger)
{
    /// <summary>Entries kept per edge at most; the scan's library needed 82 head and 143 tail.</summary>
    public const int MaxEntriesPerEdge = 2000;

    public sealed record Entry(int Id, bool Tail, int SeriesId, int? SourceChapterId, CreditDetector.Edge Edge, BankSignature Signature);

    /// <summary>A bank match: the candidates it proposed at that edge, and the entry that proposed them.</summary>
    public sealed record Match(IReadOnlyList<CreditCandidate> Candidates, int EntryId);

    private readonly SemaphoreSlim _gate = new(1, 1);
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
    /// The first entry at <paramref name="tail"/>'s edge, newer than <paramref name="afterId"/> and
    /// <paramref name="eligible"/>, that proposes candidates on <paramref name="chapter"/>; null when none does.
    /// </summary>
    public static Match? Find(int pageCount, CreditDetector.Edges chapter, bool tail, IReadOnlyList<Entry> entries,
        Func<Entry, bool> eligible, int afterId = 0)
    {
        if (CreditDetector.SignatureOf(tail ? chapter.Tail : chapter.Head) is not { } mine)
        {
            return null;
        }

        foreach (var entry in entries)
        {
            if (entry.Tail != tail || entry.Id <= afterId || !CreditDetector.Alike(mine, entry.Signature) || !eligible(entry))
            {
                continue;
            }

            var candidates = CreditDetector.BankCandidates(pageCount, chapter, entry.Edge, tail);
            if (candidates.Count > 0)
            {
                return new Match(candidates, entry.Id);
            }
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
            if (Find(slice.PageCount, edges, tail, entries, _ => true) is { } covered)
            {
                await CountHitLockedAsync(covered.EntryId, ct);
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
            _entries = [.. entries, new Entry(row.Id, tail, row.SeriesId, row.SourceChapterId, edge, signature)];
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
    public async Task CountHitAsync(int entryId, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            await CountHitLockedAsync(entryId, ct);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task CountHitLockedAsync(int entryId, CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MakiDbContext>();
        await db.CreditBank.IgnoreQueryFilters().Where(b => b.Id == entryId).ExecuteUpdateAsync(u => u.SetProperty(b => b.Hits, b => b.Hits + 1), ct);
    }

    private async Task<IReadOnlyList<Entry>> LoadAsync(CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MakiDbContext>();
        var rows = await db.CreditBank.AsNoTracking().IgnoreQueryFilters().OrderBy(b => b.Id).ToListAsync(ct);
        var entries = new List<Entry>(rows.Count);
        foreach (var row in rows)
        {
            try
            {
                using var reader = new BinaryReader(new MemoryStream(row.Strip));
                entries.Add(new Entry(row.Id, row.Tail, row.SeriesId, row.SourceChapterId, CreditDetector.Edge.Read(reader),
                    new BankSignature(row.SignatureFirst, row.Signature)));
            }
            catch (Exception e) when (e is InvalidDataException or EndOfStreamException)
            {
                logger.LogDebug(e, "Credit bank entry {Id} could not be read; left out", row.Id);
            }
        }

        return entries;
    }
}
