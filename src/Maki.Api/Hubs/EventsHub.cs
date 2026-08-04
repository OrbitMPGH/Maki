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
/// Instance-wide machinery — the download queue, library imports, update availability — goes to
/// admins only; a chapter import goes to the readers who hold that series' root folder.
/// Nothing here is a security boundary for library <em>data</em> (an event carries an id and a
/// status, not content), but a household instance where every member sees every download notification
/// is a privacy problem regardless.
/// </para>
/// </summary>
public class EventBroadcaster(IHubContext<EventsHub> hubContext, IServiceScopeFactory scopeFactory)
{
    public Task QueueUpdated(object queueItem) =>
        hubContext.Clients.Group(EventsHub.AdminGroup).SendAsync("queueUpdated", queueItem);

    /// <summary>
    /// A chapter finished importing. Unlike the rest of this class the audience is not "admins" —
    /// a reader with a grant on the folder genuinely wants to know their series just gained a
    /// chapter — so it is resolved from the grants each time.
    /// <para>
    /// The caller passes the root folder rather than letting this look it up, because the lookup
    /// would go through the <c>Series</c> query filter and this runs on a download worker whose
    /// scope is unrestricted only by convention. Taking the id as a parameter removes the question.
    /// </para>
    /// </summary>
    public async Task ChapterImported(int seriesId, int chapterId, int rootFolderId)
    {
        var groups = await AudienceForAsync(rootFolderId);
        if (groups.Count == 0)
        {
            return;
        }

        await hubContext.Clients.Groups(groups).SendAsync("chapterImported", new { seriesId, chapterId });
    }

    /// <summary>
    /// The user groups allowed to see anything in <paramref name="rootFolderId"/>. Opens its own
    /// scope because this class is a singleton and the callers are background workers with no
    /// request scope of their own.
    /// </summary>
    private async Task<IReadOnlyList<string>> AudienceForAsync(int rootFolderId)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MakiDbContext>();

        var ids = await db.Users
            .Where(u => !u.Disabled && !u.PendingSetup &&
                        (u.AllRootFolders ||
                         db.UserRootFolders.Any(g => g.UserId == u.Id && g.RootFolderId == rootFolderId)))
            .Select(u => u.Id)
            .ToListAsync();

        return ids.Select(EventsHub.UserGroup).ToList();
    }

    /// <summary>
    /// Background auto source matching finished for a series. Same audience as
    /// <see cref="ChapterImported"/> and for the same reason: whoever can see the series is who is
    /// staring at its Sources card waiting for the spinner to go away.
    /// </summary>
    public async Task SourceMatchFinished(int seriesId, int rootFolderId, int mappedCount)
    {
        var groups = await AudienceForAsync(rootFolderId);
        if (groups.Count == 0)
        {
            return;
        }

        await hubContext.Clients.Groups(groups).SendAsync("sourceMatchFinished", new { seriesId, mappedCount });
    }

    /// <summary>Per-folder progress while a library import runs. Stage is display text;
    /// current/total are set for per-file stages; done/success/error mark completion.</summary>
    public Task ImportProgress(
        string folderName, string stage, int? current = null, int? total = null,
        bool done = false, bool success = false, string? error = null) =>
        hubContext.Clients.Group(EventsHub.AdminGroup).SendAsync("importProgress",
            new { folderName, stage, current, total, done, success, error });

    /// <summary>
    /// Somebody asked for a series or a chapter range. Admins only — they are the audience that can
    /// action it, and it names another user's reading interest.
    /// </summary>
    public virtual Task SeriesRequested(int requestId, string title, string requestedBy) =>
        hubContext.Clients.Group(EventsHub.AdminGroup).SendAsync("seriesRequested",
            new { requestId, title, requestedBy });

    public Task UpdateAvailable(string latestVersion, string? releaseUrl) =>
        hubContext.Clients.Group(EventsHub.AdminGroup).SendAsync("updateAvailable", new { latestVersion, releaseUrl });
}
