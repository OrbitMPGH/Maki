using Microsoft.AspNetCore.SignalR;

namespace Maki.Api.Hubs;

/// <summary>Pushes queue progress and import events to the UI.</summary>
public class EventsHub : Hub;

public class EventBroadcaster(IHubContext<EventsHub> hubContext)
{
    public Task QueueUpdated(object queueItem) =>
        hubContext.Clients.All.SendAsync("queueUpdated", queueItem);

    public Task ChapterImported(int seriesId, int chapterId) =>
        hubContext.Clients.All.SendAsync("chapterImported", new { seriesId, chapterId });

    /// <summary>Per-folder progress while a library import runs. Stage is display text;
    /// current/total are set for per-file stages; done/success/error mark completion.</summary>
    public Task ImportProgress(
        string folderName, string stage, int? current = null, int? total = null,
        bool done = false, bool success = false, string? error = null) =>
        hubContext.Clients.All.SendAsync("importProgress",
            new { folderName, stage, current, total, done, success, error });
}
