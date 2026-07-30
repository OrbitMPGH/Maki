using System.Security.Claims;
using Maki.Core.Security;
using Maki.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace Maki.Api.Hubs;

/// <summary>
/// Pushes queue progress and import events to the UI.
/// <para>
/// Authenticated by the session cookie, which the WebSocket handshake carries because it is
/// same-origin — the connection used to append the instance API key as a query parameter instead,
/// putting a credential into every proxy access log on the way past.
/// </para>
/// <para>
/// Each connection joins a group for its user and, for admins, the admin group. That is what lets
/// <see cref="EventBroadcaster"/> address an audience instead of shouting at
/// <c>Clients.All</c>, which in a multi-user instance would tell every reader what every other
/// reader is downloading.
/// </para>
/// </summary>
[Authorize]
public class EventsHub(MakiDbContext db) : Hub
{
    public const string AdminGroup = "admins";

    public static string UserGroup(int userId) => $"user-{userId}";

    public override async Task OnConnectedAsync()
    {
        if (int.TryParse(Context.User?.FindFirstValue(ClaimTypes.NameIdentifier), out var userId))
        {
            await Groups.AddToGroupAsync(Context.ConnectionId, UserGroup(userId));

            // Read from the database rather than from a claim on the principal: a claim is baked at
            // sign-in, so an account promoted to admin an hour ago would still be in the wrong group.
            // Group membership is fixed for the life of the connection either way, but this at least
            // makes it correct as of the moment the client connected.
            var isAdmin = await db.Users
                .Where(u => u.Id == userId)
                .Select(u => (u.Permissions & MakiPermission.Admin) != 0)
                .FirstOrDefaultAsync();

            if (isAdmin)
            {
                await Groups.AddToGroupAsync(Context.ConnectionId, AdminGroup);
            }
        }

        await base.OnConnectedAsync();
    }
}

/// <summary>
/// Fans events out to the audience that should see them.
/// <para>
/// Instance-wide machinery — the download queue, imports, update availability — goes to admins only.
/// Nothing here is a security boundary for library <em>data</em> (an event carries an id and a
/// status, not content), but a household instance where every member sees every download notification
/// is a privacy problem regardless.
/// </para>
/// </summary>
public class EventBroadcaster(IHubContext<EventsHub> hubContext)
{
    public Task QueueUpdated(object queueItem) =>
        hubContext.Clients.Group(EventsHub.AdminGroup).SendAsync("queueUpdated", queueItem);

    public Task ChapterImported(int seriesId, int chapterId) =>
        hubContext.Clients.All.SendAsync("chapterImported", new { seriesId, chapterId });

    /// <summary>Per-folder progress while a library import runs. Stage is display text;
    /// current/total are set for per-file stages; done/success/error mark completion.</summary>
    public Task ImportProgress(
        string folderName, string stage, int? current = null, int? total = null,
        bool done = false, bool success = false, string? error = null) =>
        hubContext.Clients.Group(EventsHub.AdminGroup).SendAsync("importProgress",
            new { folderName, stage, current, total, done, success, error });

    public Task UpdateAvailable(string latestVersion, string? releaseUrl) =>
        hubContext.Clients.Group(EventsHub.AdminGroup).SendAsync("updateAvailable", new { latestVersion, releaseUrl });
}
