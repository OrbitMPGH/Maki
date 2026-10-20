using System.Globalization;
using Maki.Core.Entities;
using Maki.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace Maki.Api.Services;

/// <summary>A position as KOReader's progress sync exchanges it.</summary>
public record KoreaderProgress(string Progress, double Percentage, string Device, string DeviceId, long Timestamp);

/// <summary>
/// Turns KOReader sync pushes into Maki reading progress and Maki progress back into KOReader
/// positions. There is no store of its own: a push becomes <see cref="ReaderService.SaveProgressAsync"/>
/// calls for the chapters the file backs, and a pull reads <see cref="ChapterProgress"/>.
/// <para>
/// KOReader reports a page of the whole archive. A volume archive backs several chapters, so that
/// page completes the chapters before it and positions the one it falls in. A chapter that shares
/// the archive but has no range of its own (no chapter markers to go by) only completes on the last
/// page, since any other page cannot say how far into it the reader is.
/// </para>
/// </summary>
public class KoreaderSyncService(MakiDbContext db, ReaderService reader, IMemoryCache cache)
{
    public const string MakiDevice = "Maki";
    public const string MakiDeviceId = "maki";

    private static readonly TimeSpan EchoFor = TimeSpan.FromDays(7);

    /// <summary>
    /// The last push, handed back on a pull for as long as nothing in Maki moved since. Without it a
    /// device would get its own position back under Maki's name, and KOReader would offer to jump
    /// wherever rounding put it.
    /// </summary>
    private sealed record Echo(KoreaderProgress Body, int? ChapterId, DateTime? UpdatedAt);

    private sealed record Document(int FileId, List<ReaderService.ChapterSlice> Slices, int PageCount)
    {
        public bool SharedWhole(ReaderService.ChapterSlice slice) =>
            Slices.Count > 1 && slice.StartPage == 0 && slice.PageCount == PageCount;
    }

    /// <returns>The push's timestamp, or null when the document is not a file this user can read.</returns>
    public async Task<long?> PushAsync(int userId, string digest, string? progress, double? percentage,
        string? device, string? deviceId, CancellationToken ct)
    {
        if (await ResolveAsync(digest, ct) is not { } doc)
        {
            return null;
        }

        var page = ArchivePageFor(progress, percentage, doc.PageCount);
        if (page < 0)
        {
            return null;
        }

        var rows = await RowsAsync(doc, ct);
        foreach (var slice in doc.Slices)
        {
            var completed = rows.FirstOrDefault(r => r.ChapterId == slice.Chapter.Id) is { Completed: true };
            if (WriteFor(slice.StartPage, slice.PageCount, doc.SharedWhole(slice), page, doc.PageCount, completed) is { } write)
            {
                await reader.SaveProgressAsync(slice, write.PageIndex, write.Completed, ReaderService.TimeReport.None, ct);
            }
        }

        var latest = Latest(await RowsAsync(doc, ct));
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var body = new KoreaderProgress(progress ?? string.Empty, percentage ?? 0, device ?? string.Empty,
            deviceId ?? string.Empty, now);
        cache.Set(EchoKey(userId, doc.FileId), new Echo(body, latest?.ChapterId, latest?.UpdatedAt), EchoFor);
        return now;
    }

    /// <returns>Where Maki has this user in the file, or null when there is nothing to report.</returns>
    public async Task<KoreaderProgress?> PullAsync(int userId, string digest, CancellationToken ct)
    {
        if (await ResolveAsync(digest, ct) is not { } doc)
        {
            return null;
        }

        var latest = Latest(await RowsAsync(doc, ct));
        if (cache.TryGetValue(EchoKey(userId, doc.FileId), out Echo? echo) &&
            echo!.ChapterId == latest?.ChapterId && echo.UpdatedAt == latest?.UpdatedAt)
        {
            return echo.Body;
        }

        if (latest is null || doc.Slices.FirstOrDefault(s => s.Chapter.Id == latest.ChapterId) is not { } slice)
        {
            return null;
        }

        var page = PageFor(slice.StartPage, slice.PageCount, doc.PageCount, latest.PageIndex, latest.Completed);
        return new KoreaderProgress(
            (page + 1).ToString(CultureInfo.InvariantCulture),
            Math.Round((page + 1) / (double)doc.PageCount, 4),
            MakiDevice,
            MakiDeviceId,
            new DateTimeOffset(DateTime.SpecifyKind(latest.UpdatedAt, DateTimeKind.Utc)).ToUnixTimeSeconds());
    }

    private static (Type, int, int) EchoKey(int userId, int fileId) => (typeof(Echo), userId, fileId);

    /// <summary>
    /// The one file the digest names among those this user can see. Two files can share a name, and
    /// identical bytes can sit in two series; neither is a guess worth writing progress on.
    /// </summary>
    private async Task<Document?> ResolveAsync(string digest, CancellationToken ct)
    {
        var normalized = digest.Trim().ToLowerInvariant();
        var fileIds = await db.KoreaderDocuments.AsNoTracking()
            .Where(d => d.Digest == normalized)
            .Select(d => d.ChapterFileId)
            .Distinct()
            .Take(2)
            .ToListAsync(ct);
        if (fileIds.Count != 1)
        {
            return null;
        }

        var chapterIds = await db.Chapters.AsNoTracking()
            .Where(c => c.ChapterFileId == fileIds[0])
            .Select(c => c.Id)
            .ToListAsync(ct);
        var slices = await reader.SlicesAsync(chapterIds, ct);
        if (slices.Count == 0)
        {
            return null;
        }

        var ordered = slices.Values.OrderBy(s => s.StartPage).ThenBy(s => s.Chapter.Id).ToList();
        return new Document(fileIds[0], ordered, ordered[0].Pages.Count);
    }

    private async Task<List<ChapterProgress>> RowsAsync(Document doc, CancellationToken ct)
    {
        var ids = doc.Slices.Select(s => s.Chapter.Id).ToList();
        return await db.ChapterProgress.AsNoTracking().Where(p => ids.Contains(p.ChapterId)).ToListAsync(ct);
    }

    /// <summary>The most recent position. A chapter marked unread is not one.</summary>
    private static ChapterProgress? Latest(List<ChapterProgress> rows) =>
        rows.Where(r => r.Completed || r.UnreadAt == null)
            .OrderByDescending(r => r.UpdatedAt)
            .ThenByDescending(r => r.ChapterId)
            .FirstOrDefault();

    /// <summary>
    /// The zero-based archive page a push names, or -1. KOReader counts pages itself; when its count
    /// clearly disagrees with the archive's, the percentage carries over better than the page number.
    /// </summary>
    internal static int ArchivePageFor(string? progress, double? percentage, int total)
    {
        if (total <= 0)
        {
            return -1;
        }

        var pct = percentage is { } p && double.IsFinite(p) && p > 0 ? Math.Min(p, 1) : (double?)null;
        if (int.TryParse(progress, NumberStyles.Integer, CultureInfo.InvariantCulture, out var page) && page >= 1 &&
            (pct is null || Math.Abs(page / pct.Value - total) <= Math.Max(1, total * 0.02)))
        {
            return Math.Min(page, total) - 1;
        }

        return pct is { } known ? Math.Clamp((int)Math.Round(known * total), 1, total) - 1 : -1;
    }

    internal readonly record struct Write(int PageIndex, bool? Completed);

    /// <summary>What a push at archive page <paramref name="page"/> writes for one chapter's slice.</summary>
    internal static Write? WriteFor(int start, int count, bool sharedWhole, int page, int total, bool completed)
    {
        if (sharedWhole)
        {
            return page >= total - 1 && !completed ? new Write(count - 1, true) : null;
        }

        if (page >= start + count)
        {
            return completed ? null : new Write(count - 1, true);
        }

        return page < start ? null : new Write(page - start, null);
    }

    /// <summary>The zero-based archive page a stored position maps to. A finished chapter points past itself.</summary>
    internal static int PageFor(int start, int count, int total, int pageIndex, bool completed) =>
        completed
            ? Math.Min(start + count, total - 1)
            : Math.Clamp(start + Math.Clamp(pageIndex, 0, Math.Max(0, count - 1)), 0, total - 1);
}
