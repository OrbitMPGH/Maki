using Maki.Api.Dtos;
using Maki.Api.Services;
using Maki.Core.Configuration;
using Maki.Core.Entities;
using Maki.Core.Inbox;
using Maki.Core.Security;
using Maki.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Maki.Api.Controllers;

/// <param name="CoverUrl">
/// The series' poster, when the notification names one that still exists and the caller can see it.
/// Resolved at read time rather than stored on the row: a poster is replaced in place by a metadata
/// refresh, and a URL frozen at write time would serve a stale cache-buster forever.
/// </param>
public record InboxItemDto(
    int Id,
    string Type,
    string Level,
    string Title,
    string Body,
    int? SeriesId,
    int? ChapterId,
    string? Url,
    string? CoverUrl,
    DateTime CreatedAt,
    bool Read);

/// <param name="NextCursor">
/// Pass back as <c>before</c> to fetch the following page. Null when the feed is exhausted.
/// </param>
public record InboxPageDto(IReadOnlyList<InboxItemDto> Items, int Unread, int? NextCursor);

/// <summary>
/// The signed-in user's notification inbox.
/// <para>
/// No <c>[Authorize]</c> attribute and no explicit <c>UserId ==</c> anywhere: the fail-closed
/// <c>FallbackPolicy</c> already requires sign-in, and the global query filter on
/// <see cref="UserNotification"/> narrows every read to the caller. Same posture as
/// <see cref="ReadingProfilesController"/>. Nothing here is admin-gated — an inbox nobody but its
/// owner can read needs no second gate, and there is deliberately no way to read somebody else's
/// (unlike stats, where an admin genuinely needs to; a notification carries no library fact an
/// admin cannot already see).
/// </para>
/// <para>
/// Distinct from <c>NotificationsController</c>, which manages instance-wide Discord/webhook
/// connections and is admin-only.
/// </para>
/// </summary>
[ApiController]
[Route("api/v1/inbox")]
public class InboxController(
    MakiDbContext db,
    IUserSettings userSettings,
    ICurrentUser currentUser,
    TimeProvider time) : ControllerBase
{
    /// <summary>One page of the feed. Deliberately modest: the bell shows far fewer.</summary>
    private const int MaxTake = 100;

    private const int DefaultTake = 25;

    /// <summary>
    /// Newest first, paged by id rather than by offset. Ids are monotonic here (rows are only ever
    /// appended) so an id cursor cannot skip or repeat a row when something arrives mid-scroll, which
    /// an OFFSET would.
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> List(
        [FromQuery] int? before = null,
        [FromQuery] bool unreadOnly = false,
        [FromQuery] string? type = null,
        [FromQuery] int take = DefaultTake,
        CancellationToken ct = default)
    {
        take = Math.Clamp(take, 1, MaxTake);

        var query = db.UserNotifications.AsQueryable();

        if (before is { } cursor)
        {
            query = query.Where(n => n.Id < cursor);
        }

        if (unreadOnly)
        {
            query = query.Where(n => n.ReadAt == null);
        }

        if (ParseType(type) is { } wanted)
        {
            query = query.Where(n => n.Type == wanted);
        }

        // One extra row, only to learn whether another page exists — cheaper than a second count.
        var rows = await query
            .OrderByDescending(n => n.Id)
            .Take(take + 1)
            .ToListAsync(ct);

        var hasMore = rows.Count > take;
        var page = hasMore ? rows.Take(take).ToList() : rows;
        var covers = await CoversForAsync(page, ct);

        return Ok(new InboxPageDto(
            page.Select(n => ToDto(n, covers)).ToList(),
            await UnreadAsync(ct),
            hasMore ? page[^1].Id : null));
    }

    /// <summary>
    /// Poster URLs for the series named by a page of notifications, in one query.
    /// <para>
    /// Runs with the <c>Series</c> query filter <b>on</b>, which is the access check: a notification
    /// can outlive the grant that produced it (or name a series since moved to a folder the caller
    /// lost), and that must degrade to "no cover" rather than leaking the poster. A series that was
    /// deleted outright simply has no row, which lands in the same place.
    /// </para>
    /// </summary>
    private async Task<Dictionary<int, string>> CoversForAsync(
        List<UserNotification> page, CancellationToken ct)
    {
        var ids = page.Where(n => n.SeriesId is not null).Select(n => n.SeriesId!.Value).Distinct().ToList();
        if (ids.Count == 0)
        {
            return [];
        }

        var rows = await db.Series
            .Where(s => ids.Contains(s.Id))
            .Select(s => new { s.Id, s.CoverPath, s.LastMetadataRefresh })
            .ToListAsync(ct);

        return rows
            .Select(s => (s.Id, Url: SeriesDto.CoverUrlFor(s.Id, s.CoverPath, s.LastMetadataRefresh)))
            .Where(x => x.Url is not null)
            .ToDictionary(x => x.Id, x => x.Url!);
    }

    /// <summary>Just the badge. Its own endpoint because the header polls it without the feed.</summary>
    [HttpGet("unread-count")]
    public async Task<IActionResult> UnreadCount(CancellationToken ct) =>
        Ok(new { count = await UnreadAsync(ct) });

    /// <summary>Idempotent: marking an already-read row changes nothing and still returns 204.</summary>
    [HttpPost("{id:int}/read")]
    public async Task<IActionResult> MarkRead(int id, CancellationToken ct)
    {
        var updated = await db.UserNotifications
            .Where(n => n.Id == id && n.ReadAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(n => n.ReadAt, time.GetUtcNow().UtcDateTime), ct);

        // Zero rows means either already read or not the caller's. Both answer the same way: there
        // is nothing unread with that id for you. Distinguishing them would confirm the existence of
        // another user's notification by its id.
        return updated == 0 && !await db.UserNotifications.AnyAsync(n => n.Id == id, ct)
            ? NotFound()
            : NoContent();
    }

    [HttpPost("read-all")]
    public async Task<IActionResult> MarkAllRead(CancellationToken ct)
    {
        var now = time.GetUtcNow().UtcDateTime;
        var updated = await db.UserNotifications
            .Where(n => n.ReadAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(n => n.ReadAt, now), ct);

        return Ok(new { marked = updated });
    }

    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Dismiss(int id, CancellationToken ct)
    {
        var deleted = await db.UserNotifications.Where(n => n.Id == id).ExecuteDeleteAsync(ct);
        return deleted == 0 ? NotFound() : NoContent();
    }

    /// <summary>Empties the caller's inbox. Read and unread alike — "clear all" means all.</summary>
    [HttpDelete]
    public async Task<IActionResult> Clear(CancellationToken ct)
    {
        var deleted = await db.UserNotifications.ExecuteDeleteAsync(ct);
        return Ok(new { deleted });
    }

    /// <summary>
    /// Always merged before it is returned, so the client renders every event type this build knows
    /// rather than only the ones the user has an opinion about.
    /// </summary>
    [HttpGet("prefs")]
    public async Task<IActionResult> GetPrefs(CancellationToken ct) =>
        Ok(InboxPrefsSpec.Parse(await userSettings.GetAsync(SettingKeys.NotificationsInbox, ct)));

    [HttpPut("prefs")]
    public async Task<IActionResult> SavePrefs([FromBody] InboxPrefsSpec spec, CancellationToken ct)
    {
        var merged = spec.Merge();

        // Silently drop admin-only types from a non-admin's spec rather than rejecting the whole
        // save: the client hides those switches, so a stored value for one can only come from an
        // account that was demoted after setting it, and refusing the save would strand them.
        if (!currentUser.Has(MakiPermission.Admin))
        {
            merged = merged with
            {
                Types = merged.Types!
                    .Where(kv => !InboxEventTypes.All
                        .Any(t => InboxEventTypes.IsAdminOnly(t) && InboxEventTypes.Key(t) == kv.Key))
                    .ToDictionary(kv => kv.Key, kv => kv.Value),
            };
        }

        await userSettings.SetAsync(SettingKeys.NotificationsInbox, InboxPrefsSpec.Serialize(merged), ct);
        return Ok(InboxPrefsSpec.Parse(await userSettings.GetAsync(SettingKeys.NotificationsInbox, ct)));
    }

    private Task<int> UnreadAsync(CancellationToken ct) =>
        db.UserNotifications.CountAsync(n => n.ReadAt == null, ct);

    /// <summary>
    /// Matches the camelCase key the DTOs and the preference spec use, so a client filters by the
    /// same string it was given. An unknown name yields null, which the caller reads as "no filter"
    /// rather than "match nothing" — a stale bookmark should show the feed, not an empty page.
    /// </summary>
    private static InboxEventType? ParseType(string? key) =>
        string.IsNullOrWhiteSpace(key)
            ? null
            : InboxEventTypes.All
                .Cast<InboxEventType?>()
                .FirstOrDefault(t => string.Equals(
                    InboxEventTypes.Key(t!.Value), key, StringComparison.OrdinalIgnoreCase));

    private static InboxItemDto ToDto(UserNotification n, Dictionary<int, string> covers) => new(
        n.Id,
        InboxEventTypes.Key(n.Type),
        n.Level.ToString().ToLowerInvariant(),
        n.Title,
        n.Body,
        n.SeriesId,
        n.ChapterId,
        n.Url,
        n.SeriesId is { } sid ? covers.GetValueOrDefault(sid) : null,
        n.CreatedAt,
        n.ReadAt is not null);
}
