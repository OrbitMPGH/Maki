using Maki.Core.Entities;
using Maki.Data;
using Microsoft.EntityFrameworkCore;

namespace Maki.Api.Services;

/// <summary>User tag writes shared by <c>TagsController</c> and the Mihon import.</summary>
public class TagService(MakiDbContext db)
{
    private static readonly string[] Palette =
        ["blue", "grape", "teal", "orange", "violet", "cyan", "pink", "lime", "indigo", "red"];

    /// <summary>Finds the tag case-insensitively or creates it; a lost race re-reads the winner's row.</summary>
    public async Task<(Tag Tag, bool Created)> FindOrCreateAsync(string label, string? color, CancellationToken ct)
    {
        var existing = await db.Tags.FirstOrDefaultAsync(t => t.Label == label, ct);
        if (existing is not null)
        {
            return (existing, false);
        }

        var tag = new Tag { Label = label, Color = color ?? Palette[ColorSlot(label)] };
        db.Tags.Add(tag);
        try
        {
            await db.SaveChangesAsync(ct);
            return (tag, true);
        }
        catch (DbUpdateException e) when (DbErrors.IsUniqueViolation(e))
        {
            db.Entry(tag).State = EntityState.Detached;
            return (await db.Tags.FirstAsync(t => t.Label == label, ct), false);
        }
    }

    /// <summary>Links the tags without touching ones already on the series. Returns how many were added.</summary>
    public async Task<int> AddToSeriesAsync(int seriesId, IEnumerable<int> tagIds, CancellationToken ct)
    {
        var wanted = tagIds.Distinct().ToList();
        for (var attempt = 0; ; attempt++)
        {
            var present = await db.SeriesTags
                .Where(x => x.SeriesId == seriesId && wanted.Contains(x.TagId))
                .Select(x => x.TagId)
                .ToListAsync(ct);
            var added = wanted.Except(present).Select(id => new SeriesTag { SeriesId = seriesId, TagId = id }).ToList();
            if (added.Count == 0)
            {
                return 0;
            }

            db.SeriesTags.AddRange(added);
            try
            {
                await db.SaveChangesAsync(ct);
                return added.Count;
            }
            catch (DbUpdateException e) when (attempt == 0 && DbErrors.IsUniqueViolation(e))
            {
                foreach (var row in added)
                {
                    db.Entry(row).State = EntityState.Detached;
                }
            }
        }
    }

    // Not string.GetHashCode: it is salted per process, so the colour would change on restart.
    private static int ColorSlot(string label)
    {
        var sum = 0;
        foreach (var c in label.ToLowerInvariant())
        {
            sum = (sum * 31 + c) % Palette.Length;
        }

        return sum;
    }
}
