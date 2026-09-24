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
        public List<NotificationMessage> Messages { get; } = [];
        public NotificationType Type => type;
        public NotificationProviderDescriptor Descriptor => new(type, []);

        public Task SendAsync(Notification connection, NotificationMessage message, CancellationToken ct = default)
        {
            if (throws)
            {
                throw new InvalidOperationException("boom");
            }

            Sent.Add(connection.Name);
            Messages.Add(message);
            return Task.CompletedTask;
        }
    }

    private NotificationService Service(params INotificationProvider[] providers) =>
        new(_db.ScopeFactory(), providers, NullLogger<NotificationService>.Instance,
            new TestUserLocaleResolver(), new TestLocalizer());

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

    [Fact]
    public async Task Dispatch_fills_series_and_chapter_labels_in_the_instance_language()
    {
        Seed(new Notification { Name = "wants", Type = NotificationType.Discord, Enabled = true, OnChapterDownloaded = true });
        var provider = new RecordingProvider(NotificationType.Discord);

        await Service(provider).DispatchAsync(
            NotificationEventType.ChapterDownloaded,
            new NotificationMessage(NotificationEventType.ChapterDownloaded, "t", "b"));

        var message = Assert.Single(provider.Messages);
        Assert.Equal("en:notify.label.series", message.SeriesLabel);
        Assert.Equal("en:notify.label.chapter", message.ChapterLabel);
    }

    [Fact]
    public async Task SendTo_fills_the_labels_too()
    {
        var provider = new RecordingProvider(NotificationType.Discord);

        await Service(provider).SendToAsync(
            new Notification { Name = "x", Type = NotificationType.Discord },
            new NotificationMessage(NotificationEventType.Test, "t", "b"));

        Assert.Equal("en:notify.label.chapter", Assert.Single(provider.Messages).ChapterLabel);
    }

    [Fact]
    public async Task Without_localization_the_labels_stay_english()
    {
        var provider = new RecordingProvider(NotificationType.Discord);

        await new NotificationService(_db.ScopeFactory(), [provider], NullLogger<NotificationService>.Instance)
            .SendToAsync(new Notification { Name = "x", Type = NotificationType.Discord },
                new NotificationMessage(NotificationEventType.Test, "t", "b"));

        Assert.Equal("Series", Assert.Single(provider.Messages).SeriesLabel);
    }

    private int SeedTag(string label)
    {
        using var db = _db.NewContext();
        var tag = new Tag { Label = label };
        db.Tags.Add(tag);
        db.SaveChanges();
        return tag.Id;
    }

    private void TagSeries(int seriesId, int tagId)
    {
        using var db = _db.NewContext();
        db.SeriesTags.Add(new SeriesTag { SeriesId = seriesId, TagId = tagId });
        db.SaveChanges();
    }

    private async Task<List<string>> DispatchAsync(RecordingProvider provider, int? seriesId)
    {
        await Service(provider).DispatchAsync(NotificationEventType.ChapterDownloaded,
            new NotificationMessage(NotificationEventType.ChapterDownloaded, "t", "b", SeriesId: seriesId));
        var sent = provider.Sent.Order().ToList();
        provider.Sent.Clear();
        return sent;
    }

    [Fact]
    public async Task Tag_scoped_connections_only_get_series_carrying_one_of_their_tags()
    {
        var webtoon = SeedTag("webtoon");
        var other = SeedTag("other");
        var tagged = _db.SeedSeries();
        var untagged = _db.SeedSeries();
        TagSeries(tagged, webtoon);

        var scoped = new Notification { Name = "scoped", Type = NotificationType.Discord, Enabled = true, OnChapterDownloaded = true };
        scoped.Tags.Add(new NotificationTag { TagId = webtoon });
        scoped.Tags.Add(new NotificationTag { TagId = other });
        Seed(scoped, new Notification { Name = "all", Type = NotificationType.Discord, Enabled = true, OnChapterDownloaded = true });

        var provider = new RecordingProvider(NotificationType.Discord);
        Assert.Equal(["all", "scoped"], await DispatchAsync(provider, tagged));
        Assert.Equal(["all"], await DispatchAsync(provider, untagged));
        Assert.Equal(["all", "scoped"], await DispatchAsync(provider, null));
    }

    [Fact]
    public async Task Carried_tag_ids_stand_in_for_a_series_that_is_gone()
    {
        var webtoon = SeedTag("webtoon");
        var scoped = new Notification { Name = "scoped", Type = NotificationType.Discord, Enabled = true, OnSeriesRemoved = true };
        scoped.Tags.Add(new NotificationTag { TagId = webtoon });
        Seed(scoped);
        var provider = new RecordingProvider(NotificationType.Discord);

        await Service(provider).DispatchAsync(NotificationEventType.SeriesRemoved,
            new NotificationMessage(NotificationEventType.SeriesRemoved, "t", "b", SeriesTagIds: [webtoon]));
        await Service(provider).DispatchAsync(NotificationEventType.SeriesRemoved,
            new NotificationMessage(NotificationEventType.SeriesRemoved, "t", "b", SeriesTagIds: []));

        Assert.Equal(["scoped"], provider.Sent);
    }

    [Fact]
    public async Task SendTo_ignores_tag_scope()
    {
        var provider = new RecordingProvider(NotificationType.Discord);
        var connection = new Notification { Name = "x", Type = NotificationType.Discord };
        connection.Tags.Add(new NotificationTag { TagId = 999 });

        await Service(provider).SendToAsync(connection,
            new NotificationMessage(NotificationEventType.Test, "t", "b", SeriesId: 1));

        Assert.Equal(["x"], provider.Sent);
    }

    private static readonly NotificationEventType[] NewEventTypes =
    [
        NotificationEventType.SeriesAdded,
        NotificationEventType.SeriesRemoved,
        NotificationEventType.RequestSubmitted,
        NotificationEventType.RequestResolved,
        NotificationEventType.ManualMatchNeeded,
    ];

    public static TheoryData<NotificationEventType> NewEvents => new(NewEventTypes);

    [Theory]
    [MemberData(nameof(NewEvents))]
    public async Task Each_new_event_follows_only_its_own_toggle(NotificationEventType type)
    {
        Notification Connection(string name, NotificationEventType on) => new()
        {
            Name = name, Type = NotificationType.Discord, Enabled = true,
            OnSeriesAdded = on == NotificationEventType.SeriesAdded,
            OnSeriesRemoved = on == NotificationEventType.SeriesRemoved,
            OnRequestSubmitted = on == NotificationEventType.RequestSubmitted,
            OnRequestResolved = on == NotificationEventType.RequestResolved,
            OnManualMatchNeeded = on == NotificationEventType.ManualMatchNeeded,
        };
        Seed([.. NewEventTypes.Select(e => Connection(e.ToString(), e))]);
        var provider = new RecordingProvider(NotificationType.Discord);

        await Service(provider).DispatchAsync(type, new NotificationMessage(type, "t", "b"));

        Assert.Equal([type.ToString()], provider.Sent);
    }
}
