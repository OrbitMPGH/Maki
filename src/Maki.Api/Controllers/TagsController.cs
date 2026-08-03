using Microsoft.AspNetCore.Authorization;
using Maki.Api.Auth;
using Maki.Api.Dtos;
using Maki.Core.Entities;
using Maki.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Maki.Api.Controllers;

[ApiController]
[Route("api/v1/tags")]
public class TagsController(MakiDbContext db) : ControllerBase
{
    private static readonly string[] Palette =
        ["blue", "grape", "teal", "orange", "violet", "cyan", "pink", "lime", "indigo", "red"];

    [HttpGet]
    public async Task<IActionResult> List(CancellationToken ct)
    {
        // Read the join table directly — the skip navigation would need SQL APPLY (unsupported).
        var counts = await db.SeriesTags
            .GroupBy(x => x.TagId)
            .Select(g => new { TagId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.TagId, x => x.Count, ct);

        var tags = await db.Tags.OrderBy(t => t.Label).ToListAsync(ct);
        return Ok(tags.Select(t => new TagDto(t.Id, t.Label, t.Color, counts.GetValueOrDefault(t.Id))));
    }

    /// <summary>
    /// Creates a tag, or returns the existing one when the label is already taken (compared
    /// case-insensitively). Idempotent on purpose: the series tag input creates as you type, and
    /// a 409 there would just mean the UI has to re-look-up the tag it already asked for.
    /// </summary>
    [Authorize(Policy = Policies.ManageTags)]
    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateTagRequest request, CancellationToken ct)
    {
        var label = request.Label?.Trim();
        if (string.IsNullOrEmpty(label))
        {
            return BadRequest(new { error = "Label is required" });
        }

        var existing = await db.Tags.FirstOrDefaultAsync(t => t.Label == label, ct);
        if (existing is not null)
        {
            return Ok(new TagDto(existing.Id, existing.Label, existing.Color, await SeriesCount(existing.Id, ct)));
        }

        var tag = new Tag
        {
            Label = label,
            Color = request.Color ?? Palette[ColorSlot(label)],
        };
        db.Tags.Add(tag);
        await db.SaveChangesAsync(ct);
        return Ok(new TagDto(tag.Id, tag.Label, tag.Color, 0));
    }

    [Authorize(Policy = Policies.ManageTags)]
    [HttpPut("{id:int}")]
    public async Task<IActionResult> Update(int id, [FromBody] UpdateTagRequest request, CancellationToken ct)
    {
        var tag = await db.Tags.FindAsync([id], ct);
        if (tag is null)
        {
            return NotFound();
        }

        if (request.Label?.Trim() is { Length: > 0 } label && !string.Equals(label, tag.Label, StringComparison.OrdinalIgnoreCase))
        {
            if (await db.Tags.AnyAsync(t => t.Label == label && t.Id != id, ct))
            {
                return BadRequest(new { error = $"A tag called \"{label}\" already exists" });
            }

            tag.Label = label;
        }

        if (request.Color is { Length: > 0 } color)
        {
            tag.Color = color;
        }

        await db.SaveChangesAsync(ct);
        return Ok(new TagDto(tag.Id, tag.Label, tag.Color, await SeriesCount(tag.Id, ct)));
    }

    /// <summary>Deletes the tag and unlinks it from every series (the join rows cascade).</summary>
    [Authorize(Policy = Policies.ManageTags)]
    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Delete(int id, CancellationToken ct)
    {
        var tag = await db.Tags.Include(t => t.Series).FirstOrDefaultAsync(t => t.Id == id, ct);
        if (tag is null)
        {
            return NotFound();
        }

        tag.Series.Clear();
        db.Tags.Remove(tag);
        await db.SaveChangesAsync(ct);
        return NoContent();
    }

    /// <summary>Applies tag adds and removes across a set of series in one call.</summary>
    [Authorize(Policy = Policies.ManageTags)]
    [HttpPost("bulk")]
    public async Task<IActionResult> Bulk([FromBody] BulkTagRequest request, CancellationToken ct)
    {
        var add = request.Add ?? [];
        var remove = request.Remove ?? [];
        if (request.SeriesIds.Count == 0 || (add.Count == 0 && remove.Count == 0))
        {
            return Ok(new { updated = 0 });
        }

        var wanted = add.Concat(remove).Distinct().ToList();
        var tags = await db.Tags.Where(t => wanted.Contains(t.Id)).ToDictionaryAsync(t => t.Id, ct);
        if (tags.Count != wanted.Count)
        {
            return BadRequest(new { error = "One or more tag ids do not exist" });
        }

        var series = await db.Series
            .Include(s => s.UserTags)
            .Where(s => request.SeriesIds.Contains(s.Id))
            .ToListAsync(ct);

        foreach (var s in series)
        {
            foreach (var tagId in add)
            {
                if (s.UserTags.All(t => t.Id != tagId))
                {
                    s.UserTags.Add(tags[tagId]);
                }
            }

            s.UserTags.RemoveAll(t => remove.Contains(t.Id));
        }

        await db.SaveChangesAsync(ct);
        return Ok(new { updated = series.Count });
    }

    /// <summary>
    /// Picks a default colour from the label. Rolled by hand rather than via
    /// <c>string.GetHashCode</c>, which is salted per process — the same label would otherwise
    /// come out a different colour on every restart.
    /// </summary>
    private static int ColorSlot(string label)
    {
        var sum = 0;
        foreach (var c in label.ToLowerInvariant())
        {
            sum = (sum * 31 + c) % Palette.Length;
        }

        return sum;
    }

    private Task<int> SeriesCount(int tagId, CancellationToken ct) =>
        db.SeriesTags.CountAsync(x => x.TagId == tagId, ct);
}
