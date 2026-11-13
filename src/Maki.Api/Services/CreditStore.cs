using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Maki.Api.Services.Ocr;
using Maki.Core.Entities;
using Maki.Core.Images;
using Maki.Data;
using Microsoft.EntityFrameworkCore;

namespace Maki.Api.Services;

/// <summary>One credit candidate as it was judged, edge inward, with the text read off it (null when it was not read).</summary>
public sealed record JudgedCandidate(bool Tail, int Page, double Share, bool Colour, string? Text, bool Accepted);

/// <summary>
/// The credit page answers found ahead of time (<see cref="ChapterCredits"/>), read by the reader
/// before it searches and written by every search, so the reader and <see cref="CreditScanWorker"/>
/// never search one chapter twice. A row is only served while it belongs to the slice's
/// <see cref="ReaderService.ChapterSlice.EdgeKey"/> and was found under the current versions.
/// </summary>
public sealed class CreditStore(IServiceScopeFactory scopes, IPageTextReader text, ILogger<CreditStore> logger)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>The nearby chapters a search compared against, as one key: their edge keys hashed in order.</summary>
    public static string ReferencesKey(IEnumerable<ReaderService.ChapterSlice> references)
    {
        var keys = string.Join(',', references.Select(r => r.EdgeKey ?? "-"));
        return keys.Length == 0
            ? string.Empty
            : Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(keys)).AsSpan(0, 10));
    }

    /// <summary>
    /// Whether <paramref name="row"/> answers for a slice with <paramref name="edgeKey"/> under the current
    /// versions. An answer the colour rule gave alone stops counting once the OCR models can be read.
    /// </summary>
    public bool Current(ChapterCredits row, string? edgeKey) =>
        edgeKey is not null && row.EdgeKey == edgeKey && !row.Failed &&
        row.DetectorVersion == CreditDetector.Version && row.TextVersion == CreditText.Version &&
        row.OcrVersion == PageTextReader.Version && (row.Judge == CreditJudge.Ocr || !text.Available);

    /// <summary>Whether answers the colour rule gave alone are due again: the OCR models can be read.</summary>
    public bool OcrAvailable => text.Available;

    public static CreditPages Answer(ChapterCredits row) => new(row.HeadPages, row.HeadOffset, row.TailPages);

    /// <summary>The stored answer for <paramref name="slice"/>, or null when there is none it can trust.</summary>
    public async Task<CreditPages?> GetAsync(ReaderService.ChapterSlice slice, CancellationToken ct)
    {
        if (slice.EdgeKey is null)
        {
            return null;
        }

        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MakiDbContext>();
        var row = await db.ChapterCredits.AsNoTracking().IgnoreQueryFilters()
            .FirstOrDefaultAsync(c => c.ChapterId == slice.Chapter.Id, ct);
        return row is not null && Current(row, slice.EdgeKey) ? Answer(row) : null;
    }

    /// <summary>The stored row for a chapter, current or not.</summary>
    public async Task<ChapterCredits?> RowAsync(int chapterId, CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MakiDbContext>();
        return await db.ChapterCredits.AsNoTracking().IgnoreQueryFilters().FirstOrDefaultAsync(c => c.ChapterId == chapterId, ct);
    }

    /// <summary>The candidates a row stored, or empty when it stored none or they cannot be read.</summary>
    public static IReadOnlyList<JudgedCandidate> Candidates(ChapterCredits row)
    {
        if (string.IsNullOrEmpty(row.CandidatesJson))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<List<JudgedCandidate>>(row.CandidatesJson, Json) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <summary>Stores what a search found for <paramref name="slice"/>.</summary>
    /// <param name="probeHit">For a volume's chapter, what the probe of its file read; kept so the next pass need not probe again.</param>
    public Task SaveAsync(ReaderService.ChapterSlice slice, string referencesKey, CreditPages found, CreditJudge judge,
        IReadOnlyList<JudgedCandidate> judged, CancellationToken ct, bool? probeHit = null) =>
        UpsertAsync(slice, row =>
        {
            row.ProbeHit = probeHit;
            row.ReferencesKey = referencesKey;
            row.HeadPages = found.HeadPages;
            row.HeadOffset = found.HeadOffset;
            row.TailPages = found.TailPages;
            row.Judge = judge;
            row.CandidatesJson = judged.Count == 0 ? null : JsonSerializer.Serialize(judged, Json);
            row.Skipped = false;
            row.Failed = false;
        }, ct);

    /// <summary>
    /// Stores a volume chapter left unsearched, with what the probe of its file's edges read. A probe made
    /// without the OCR models is stored as the colour rule's, so the scan probes again once they load.
    /// </summary>
    public Task SaveSkippedAsync(ReaderService.ChapterSlice slice, bool probeHit, CancellationToken ct) =>
        UpsertAsync(slice, row =>
        {
            row.ReferencesKey = string.Empty;
            row.HeadPages = 0;
            row.HeadOffset = 0;
            row.TailPages = 0;
            row.Judge = text.Available ? CreditJudge.Ocr : CreditJudge.Colour;
            row.CandidatesJson = null;
            row.Skipped = true;
            row.ProbeHit = probeHit;
            row.Failed = false;
        }, ct);

    /// <summary>Stores that the search threw, so the scan does not start on this chapter again until something changes.</summary>
    public Task SaveFailedAsync(ReaderService.ChapterSlice slice, CancellationToken ct) =>
        UpsertAsync(slice, row =>
        {
            row.HeadPages = 0;
            row.HeadOffset = 0;
            row.TailPages = 0;
            row.Failed = true;
        }, ct);

    /// <summary>Records what the probe of a volume file read on a row the search already wrote.</summary>
    public async Task SetProbeAsync(int chapterId, bool probeHit, CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MakiDbContext>();
        await db.ChapterCredits.IgnoreQueryFilters()
            .Where(c => c.ChapterId == chapterId)
            .ExecuteUpdateAsync(u => u.SetProperty(c => c.ProbeHit, probeHit), ct);
    }

    /// <summary>
    /// Whether <paramref name="row"/> settles a chapter the search cannot key (a PDF, a short chapter):
    /// written for this file and size under the current versions.
    /// </summary>
    public static bool Settled(ChapterCredits row, ReaderService.ChapterSlice slice) =>
        !row.Failed && row.ChapterFileId == slice.ChapterFileId && row.FileSize == slice.ArchiveSize &&
        row.DetectorVersion == CreditDetector.Version && row.TextVersion == CreditText.Version &&
        row.OcrVersion == PageTextReader.Version;

    /// <summary>Writes the file a row was stamped with, after the scan found its edge key unchanged (a ComicInfo rewrite).</summary>
    public async Task RestampAsync(int chapterId, int chapterFileId, long fileSize, CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MakiDbContext>();
        await db.ChapterCredits.IgnoreQueryFilters()
            .Where(c => c.ChapterId == chapterId)
            .ExecuteUpdateAsync(u => u.SetProperty(c => c.ChapterFileId, chapterFileId).SetProperty(c => c.FileSize, fileSize), ct);
    }

    /// <summary>
    /// Stores that a chapter's file could not be read at all, so the scan moves on and comes back to it
    /// a day later. Never served: it has no edge key.
    /// </summary>
    public async Task SaveUnreadableAsync(int chapterId, int seriesId, int chapterFileId, long fileSize, CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MakiDbContext>();
        var row = await db.ChapterCredits.IgnoreQueryFilters().FirstOrDefaultAsync(c => c.ChapterId == chapterId, ct);
        if (row is null)
        {
            row = new ChapterCredits { ChapterId = chapterId };
            db.ChapterCredits.Add(row);
        }

        row.SeriesId = seriesId;
        row.ChapterFileId = chapterFileId;
        row.FileSize = fileSize;
        row.EdgeKey = string.Empty;
        row.DetectorVersion = CreditDetector.Version;
        row.TextVersion = CreditText.Version;
        row.OcrVersion = PageTextReader.Version;
        row.HeadPages = 0;
        row.HeadOffset = 0;
        row.TailPages = 0;
        row.Failed = true;
        row.ScannedAtUtc = DateTime.UtcNow;
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException e)
        {
            logger.LogDebug(e, "Could not mark chapter {ChapterId} unreadable for the credit scan", chapterId);
        }
    }

    private async Task UpsertAsync(ReaderService.ChapterSlice slice, Action<ChapterCredits> fill, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            using var scope = scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<MakiDbContext>();
            var row = await db.ChapterCredits.IgnoreQueryFilters().FirstOrDefaultAsync(c => c.ChapterId == slice.Chapter.Id, ct);
            if (row is null)
            {
                row = new ChapterCredits { ChapterId = slice.Chapter.Id };
                db.ChapterCredits.Add(row);
            }

            row.SeriesId = slice.Series.Id;
            row.ChapterFileId = slice.ChapterFileId;
            row.FileSize = slice.ArchiveSize;
            // A PDF has no edge key: its row only tells the scan it was seen, and is never served.
            row.EdgeKey = slice.EdgeKey ?? string.Empty;
            row.DetectorVersion = CreditDetector.Version;
            row.TextVersion = CreditText.Version;
            row.OcrVersion = PageTextReader.Version;
            row.ScannedAtUtc = DateTime.UtcNow;
            fill(row);
            try
            {
                await db.SaveChangesAsync(ct);
                return;
            }
            catch (DbUpdateException e) when (attempt == 0)
            {
                // Another search inserted the row meanwhile (the reader and the scan on one chapter): write over it.
                logger.LogDebug(e, "Credit answer for chapter {ChapterId} raced another write", slice.Chapter.Id);
            }
            catch (DbUpdateException e)
            {
                // The chapter or series went while the search ran.
                logger.LogDebug(e, "Could not store the credit answer for chapter {ChapterId}", slice.Chapter.Id);
                return;
            }
        }
    }
}
