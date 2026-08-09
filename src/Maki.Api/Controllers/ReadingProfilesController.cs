using Maki.Api.Services;
using Maki.Core.Entities;
using Maki.Core.Reading;
using Maki.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Maki.Api.Controllers;

public record ReadingProfileDto(int Id, string Name, ReaderPrefsSpec Prefs, List<string> SeriesTypes);

/// <param name="SeriesTypes">
/// Which <see cref="Maki.Core.Entities.SeriesTypes"/> this profile is picked for automatically.
/// Empty is legitimate and means "only when I pin it to a series".
/// </param>
public record ReadingProfileRequest(string? Name, ReaderPrefsSpec? Prefs, List<string>? SeriesTypes);

/// <summary>
/// A user's named reader presets. Private per account — the query filter narrows every read, and
/// nothing here is admin-gated: a profile is a display preference, and one that could be withheld
/// would just push people back to re-tuning the reader per series.
/// </summary>
[ApiController]
[Route("api/v1/readingprofiles")]
public class ReadingProfilesController(MakiDbContext db, ReadingProfileService profiles) : ControllerBase
{
    private const int MaxNameLength = 60;

    [HttpGet]
    public async Task<IActionResult> List(CancellationToken ct) =>
        Ok((await profiles.ListAsync(ct)).Select(ToDto));

    /// <summary>The vocabulary the client's type picker offers, so it can't drift from the server's.</summary>
    [HttpGet("types")]
    public IActionResult Types() => Ok(SeriesTypes.All);

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] ReadingProfileRequest request, CancellationToken ct)
    {
        if (Validate(request) is { } error)
        {
            return BadRequest(new { error });
        }

        var name = request.Name!.Trim();
        if (await db.ReadingProfiles.AnyAsync(p => p.Name == name, ct))
        {
            return Conflict(new { error = $"You already have a profile called \"{name}\"" });
        }

        var types = NormalizeTypes(request.SeriesTypes);
        if (await profiles.ConflictingClaimAsync(types, null, ct) is { } clash)
        {
            return Conflict(new { error = $"\"{clash.ProfileName}\" already covers {clash.Type}" });
        }

        var now = DateTime.UtcNow;
        var profile = new ReadingProfile
        {
            Name = name,
            PrefsJson = ReaderPrefsSpec.Serialize(request.Prefs ?? new ReaderPrefsSpec()),
            SeriesTypes = string.Join(',', types),
            CreatedAt = now,
            UpdatedAt = now,
        };

        db.ReadingProfiles.Add(profile);
        await db.SaveChangesAsync(ct);
        return Ok(ToDto(profile));
    }

    [HttpPut("{id:int}")]
    public async Task<IActionResult> Update(int id, [FromBody] ReadingProfileRequest request, CancellationToken ct)
    {
        var profile = await db.ReadingProfiles.FirstOrDefaultAsync(p => p.Id == id, ct);
        if (profile is null)
        {
            return NotFound();
        }

        if (Validate(request) is { } error)
        {
            return BadRequest(new { error });
        }

        var name = request.Name!.Trim();
        if (await db.ReadingProfiles.AnyAsync(p => p.Id != id && p.Name == name, ct))
        {
            return Conflict(new { error = $"You already have a profile called \"{name}\"" });
        }

        var types = NormalizeTypes(request.SeriesTypes);
        if (await profiles.ConflictingClaimAsync(types, id, ct) is { } clash)
        {
            return Conflict(new { error = $"\"{clash.ProfileName}\" already covers {clash.Type}" });
        }

        profile.Name = name;
        profile.PrefsJson = ReaderPrefsSpec.Serialize(request.Prefs ?? new ReaderPrefsSpec());
        profile.SeriesTypes = string.Join(',', types);
        profile.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        return Ok(ToDto(profile));
    }

    /// <summary>
    /// Deletes a profile. Series pinned to it fall back to whatever their type resolves to — the
    /// FK is <c>SetNull</c>, so this cannot take a rating or a per-series override with it.
    /// </summary>
    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Delete(int id, CancellationToken ct)
    {
        var profile = await db.ReadingProfiles.FirstOrDefaultAsync(p => p.Id == id, ct);
        if (profile is null)
        {
            return NotFound();
        }

        db.ReadingProfiles.Remove(profile);
        await db.SaveChangesAsync(ct);
        return NoContent();
    }

    private static string? Validate(ReadingProfileRequest request)
    {
        var name = request.Name?.Trim();
        if (string.IsNullOrEmpty(name))
        {
            return "Name is required";
        }

        if (name.Length > MaxNameLength)
        {
            return $"Name must be {MaxNameLength} characters or fewer";
        }

        var unknown = (request.SeriesTypes ?? [])
            .FirstOrDefault(t => SeriesTypes.Normalize(t) is null);
        return unknown is null
            ? null
            : $"\"{unknown}\" is not a series type. Known types: {string.Join(", ", SeriesTypes.All)}";
    }

    private static List<string> NormalizeTypes(List<string>? types) =>
        [.. (types ?? []).Select(SeriesTypes.Normalize).OfType<string>().Distinct(StringComparer.Ordinal)];

    private static ReadingProfileDto ToDto(ReadingProfile p) =>
        new(p.Id, p.Name, ReaderPrefsSpec.Parse(p.PrefsJson), [.. p.Types()]);
}
