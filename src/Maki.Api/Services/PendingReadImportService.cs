using System.Text.Json;
using Maki.Core.Entities;
using Maki.Data;
using Microsoft.EntityFrameworkCore;

namespace Maki.Api.Services;

/// <summary>
/// Read marks from a Mihon backup, held in <see cref="PendingReadImport"/> until the series has chapter
/// rows. The row keeps the numbers that matched no chapter yet, so a later sync can place them.
/// Imported reading stays out of Rewind on purpose, like the Kavita import.
/// </summary>
public class PendingReadImportService(IServiceScopeFactory scopes, ILogger<PendingReadImportService> logger)
{
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

    /// <summary>Marks every pending number that now has a chapter, for every reader. Does nothing while the series has none.</summary>
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

        // In memory: EF's SQLite provider cannot compare decimals.
        var matched = (await db.Chapters.IgnoreQueryFilters().AsNoTracking()
                .Where(c => c.SeriesId == pending.SeriesId && c.Number != null)
                .Select(c => new { c.Id, c.Number })
                .ToListAsync(ct))
            .Where(c => numbers.Contains(c.Number!.Value))
            .ToList();
        var targets = matched.Select(c => c.Id).ToList();
        var left = numbers.Except(matched.Select(c => c.Number!.Value)).ToList();

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

        // Guarded on the numbers: an import that merged more into the row since it was read keeps it.
        var current = db.PendingReadImports.IgnoreQueryFilters()
            .Where(p => p.Id == pending.Id && p.NumbersJson == pending.NumbersJson);
        if (left.Count == 0)
        {
            await current.ExecuteDeleteAsync(ct);
        }
        else if (left.Count < numbers.Count)
        {
            var rewritten = Serialize(left);
            await current.ExecuteUpdateAsync(set => set.SetProperty(p => p.NumbersJson, rewritten), ct);
        }
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
