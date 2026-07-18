using Maki.Api.Services;
using Maki.Core.Entities;
using Maki.Core.Notifications;
using Microsoft.Extensions.Logging.Abstractions;

namespace Maki.Api.Tests;

public class NotificationServiceTests : IDisposable
{
    private readonly TestDb _db = new();

    public void Dispose() => _db.Dispose();

    private sealed class RecordingProvider(NotificationType type, bool throws = false) : INotificationProvider
    {
        public List<string> Sent { get; } = [];
        public NotificationType Type => type;

        public Task SendAsync(Notification connection, NotificationMessage message, CancellationToken ct = default)
        {
            if (throws)
            {
                throw new InvalidOperationException("boom");
            }

            Sent.Add(connection.Name);
            return Task.CompletedTask;
        }
    }

    private NotificationService Service(params INotificationProvider[] providers) =>
        new(_db.ScopeFactory(), providers, NullLogger<NotificationService>.Instance);

    private void Seed(params Notification[] notifications)
    {
        using var db = _db.NewContext();
        db.Notifications.AddRange(notifications);
        db.SaveChanges();
    }

    [Fact]
    public async Task Dispatch_only_hits_enabled_connections_with_the_matching_toggle()
    {
        Seed(
            new Notification { Name = "wants", Type = NotificationType.Discord, Enabled = true, OnChapterDownloaded = true },
            new Notification { Name = "toggle-off", Type = NotificationType.Discord, Enabled = true, OnChapterDownloaded = false },
            new Notification { Name = "disabled", Type = NotificationType.Discord, Enabled = false, OnChapterDownloaded = true });

        var provider = new RecordingProvider(NotificationType.Discord);
        await Service(provider).DispatchAsync(
            NotificationEventType.ChapterDownloaded,
            new NotificationMessage(NotificationEventType.ChapterDownloaded, "t", "b"));

        Assert.Equal(["wants"], provider.Sent);
    }

    [Fact]
    public async Task A_throwing_provider_is_swallowed_and_does_not_block_others()
    {
        Seed(
            new Notification { Name = "bad", Type = NotificationType.Discord, Enabled = true, OnDownloadFailed = true },
            new Notification { Name = "good", Type = NotificationType.Webhook, Enabled = true, OnDownloadFailed = true });

        var bad = new RecordingProvider(NotificationType.Discord, throws: true);
        var good = new RecordingProvider(NotificationType.Webhook);

        // Must not throw.
        await Service(bad, good).DispatchAsync(
            NotificationEventType.DownloadFailed,
            new NotificationMessage(NotificationEventType.DownloadFailed, "t", "b"));

        Assert.Equal(["good"], good.Sent);
    }
}
