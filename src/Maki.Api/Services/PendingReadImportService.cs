using System.Text.Json;
using Maki.Core.Entities;
using Maki.Data;
using Microsoft.EntityFrameworkCore;

namespace Maki.Api.Services;

/// <summary>
/// Per-chapter read marks brought in from a Mihon backup. They are stored first
/// (<see cref="PendingReadImport"/>) and written to <see cref="ChapterProgress"/> once the series
/// has chapter rows, which for a fresh add is after the deferred source match:
/// <see cref="ChapterSyncService"/> calls <see cref="ApplyAsync"/> whenever a sync finds new chapters.
/// <para>
/// The rows take the Kavita import's shape (completed, bulk marked, external, page count 0) and raise
/// no StatsEvent, XP or inbox row. Mihon does record when chapters were read, but imported reading
/// stays out of Rewind on purpose, like the Kavita import. One-shots are never marked: there is no
/// number to match them on. A fallback tracker number (<c>trackerLastRead</c>) only raises the
/// silent mark and may follow the tracker's numbering rather than the source's.
/// </para>
/// </summary>
public class PendingReadImportService(IServiceScopeFactory scopes, ILogger<PendingReadImportService> logger)
{
    /// <summary>Stores the numbers for this reader, merged into any row an earlier import left.</summary>
    public async Task AddAsync(int userId, int seriesId, IReadOnlyCollection<decimal> numbers, CancellationToken ct)
    {
        if (numbers.Count == 0)
        {
            return;
        }

        for (var attempt = 0; ; attempt++)
        {
            using var scope = scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<MakiDbContext>();
            var row = await db.PendingReadImports.IgnoreQueryFilters()
                .FirstOrDefaultAsync(p => p.SeriesId == seriesId && p.UserId == userId, ct);
            if (row is null)
            {
                db.PendingReadImports.Add(new PendingReadImport
                {
                    SeriesId = seriesId,
                    UserId = userId,
                    NumbersJson = Serialize(numbers),
                    CreatedAt = DateTime.UtcNow,
                });
            }
            else
            {
                row.NumbersJson = Serialize(Parse(row.NumbersJson).Concat(numbers));
            }

            try
            {
                await db.SaveChangesAsync(ct);
                return;
            }
            catch (DbUpdateException e) when (attempt == 0 && DbErrors.IsUniqueViolation(e))
            {
                logger.LogDebug("Pending read import insert lost a race on series {SeriesId}, merging", seriesId);
            }
        }
    }

    /// <summary>
    /// Marks every pending number that now has a chapter, for every reader with a pending row, and
    /// removes those rows. Does nothing while the series has no chapters at all.
    /// </summary>
    public async Task ApplyAsync(int seriesId, CancellationToken ct)
    {
        List<PendingReadImport> pending;
        bool hasChapters;
        using (var scope = scopes.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MakiDbContext>();
            pending = await db.PendingReadImports.IgnoreQueryFilters().AsNoTracking()
                .Where(p => p.SeriesId == seriesId)
                .ToListAsync(ct);
            hasChapters = pending.Count > 0 &&
                          await db.Chapters.IgnoreQueryFilters().AnyAsync(c => c.SeriesId == seriesId, ct);
        }

        if (!hasChapters)
        {
            return;
        }

        foreach (var row in pending)
        {
            try
            {
                try
                {
                    await ApplyForUserAsync(row, ct);
                }
                catch (DbUpdateException e) when (DbErrors.IsUniqueViolation(e))
                {
                    // A reader or Kavita wrote one of these chapters' rows in between; the re-run
                    // reads it and leaves it alone.
                    await ApplyForUserAsync(row, ct);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Could not apply imported read marks on series {SeriesId} for user {UserId}",
                    seriesId, row.UserId);
            }
        }
    }

    private async Task ApplyForUserAsync(PendingReadImport pending, CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MakiDbContext>();
        var numbers = Parse(pending.NumbersJson).ToHashSet();

        // In memory: Chapter.Number is REAL and EF's SQLite provider cannot compare decimals.
        var targets = (await db.Chapters.IgnoreQueryFilters().AsNoTracking()
                .Where(c => c.SeriesId == pending.SeriesId && c.Number != null)
                .Select(c => new { c.Id, c.Number })
                .ToListAsync(ct))
            .Where(c => numbers.Contains(c.Number!.Value))
            .Select(c => c.Id)
            .ToList();

        var existing = await db.ChapterProgress.IgnoreQueryFilters()
            .Where(p => p.UserId == pending.UserId && p.SeriesId == pending.SeriesId && targets.Contains(p.ChapterId))
            .ToDictionaryAsync(p => p.ChapterId, ct);

        var now = DateTime.UtcNow;
        foreach (var chapterId in targets)
        {
            if (existing.TryGetValue(chapterId, out var row))
            {
                if (row.Completed || row.UnreadAt is not null)
                {
                    continue;
                }

                row.Completed = true;
                row.UpdatedAt = now;
                continue;
            }

            db.ChapterProgress.Add(new ChapterProgress
            {
                UserId = pending.UserId,
                SeriesId = pending.SeriesId,
                ChapterId = chapterId,
                PageIndex = 0,
                PageCount = 0,
                Completed = true,
                BulkMarked = true,
                External = true,
                StartedAt = now,
                UpdatedAt = now,
            });
        }

        await db.SaveChangesAsync(ct);

        // Matched on the numbers too: an import that merged more into the row since it was read
        // leaves it in place, and its own apply picks the rest up.
        await db.PendingReadImports.IgnoreQueryFilters()
            .Where(p => p.Id == pending.Id && p.NumbersJson == pending.NumbersJson)
            .ExecuteDeleteAsync(ct);
    }

    internal static IEnumerable<decimal> Parse(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<decimal[]>(json) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static string Serialize(IEnumerable<decimal> numbers) =>
        JsonSerializer.Serialize(numbers.Distinct().Order().ToArray());
}
