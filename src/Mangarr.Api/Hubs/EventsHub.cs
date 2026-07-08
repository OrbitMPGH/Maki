using Microsoft.AspNetCore.SignalR;

namespace Mangarr.Api.Hubs;

/// <summary>Pushes queue progress and import events to the UI.</summary>
public class EventsHub : Hub;

public class EventBroadcaster(IHubContext<EventsHub> hubContext)
{
    public Task QueueUpdated(object queueItem) =>
        hubContext.Clients.All.SendAsync("queueUpdated", queueItem);

    public Task ChapterImported(int seriesId, int chapterId) =>
        hubContext.Clients.All.SendAsync("chapterImported", new { seriesId, chapterId });
}
