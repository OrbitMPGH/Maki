using Maki.Api.Configuration;
using Maki.Api.Jobs;
using Maki.Core.Entities;
using Maki.Core.Inbox;
using Maki.Core.Notifications;
using Maki.Core.Security;
using Maki.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Quartz;

namespace Maki.Api.Tests;

/// <summary>
/// The inbox grows a row per automatic download forever unless something prunes it.
/// <see cref="HousekeepingJob"/> applies two rules and needs both: an age rule alone never touches
/// somebody who has not opened the bell, and a cap alone keeps a year of acknowledged noise.
/// </summary>
public class InboxRetentionTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly string _configDir = Directory.CreateTempSubdirectory("maki-inbox-retention").FullName;
    private readonly string? _priorEnv;
    private readonly AppPaths _paths;

    public InboxRetentionTests()
    {
        // AppPaths reads MAKI_CONFIG_DIR at construction, so point it somewhere disposable before
        // building one — same dance BackupServiceTests does.
        _priorEnv = Environment.GetEnvironmentVariable("MAKI_CONFIG_DIR");
        Environment.SetEnvironmentVariable("MAKI_CONFIG_DIR", _configDir);
        _paths = new AppPaths();
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("MAKI_CONFIG_DIR", _priorEnv);
        _db.Dispose();
        try
        {
            Directory.Delete(_configDir, recursive: true);
        }
        catch (IOException)
        {
            // A leftover temp directory is not worth failing a test over.
        }
    }

    [Fact]
    public async Task Read_notifications_past_thirty_days_are_deleted_and_unread_ones_are_not()
    {
        var user = _db.SeedUser("reader", MakiPermission.None);

        var oldRead = Seed(user, "old read", DateTime.UtcNow.AddDays(-40), read: true);
        var oldUnread = Seed(user, "old unread", DateTime.UtcNow.AddDays(-40));
        var freshRead = Seed(user, "fresh read", DateTime.UtcNow.AddDays(-2), read: true);

        await RunHousekeeping();

        using var db = _db.NewContext();
        var ids = db.UserNotifications.IgnoreQueryFilters().Select(n => n.Id).ToList();

        Assert.DoesNotContain(oldRead, ids);
        Assert.Contains(oldUnread, ids);
        Assert.Contains(freshRead, ids);
    }

    [Fact]
    public async Task The_per_user_cap_trims_the_oldest_including_unread_ones()
    {
        var user = _db.SeedUser("hoarder", MakiPermission.None);

        // 205 fresh, unread rows: past the cap, but nothing an age rule would ever touch.
        var start = DateTime.UtcNow.AddDays(-5);
        for (var i = 0; i < 205; i++)
        {
            Seed(user, $"row {i}", start.AddMinutes(i));
        }

        await RunHousekeeping();

        using var db = _db.NewContext();
        var kept = db.UserNotifications.IgnoreQueryFilters()
            .OrderBy(n => n.CreatedAt)
            .Select(n => n.Title)
            .ToList();

        Assert.Equal(200, kept.Count);
        Assert.Equal("row 5", kept[0]);
        Assert.Equal("row 204", kept[^1]);
    }

    [Fact]
    public async Task The_cap_is_per_user_not_per_table()
    {
        var one = _db.SeedUser("one", MakiPermission.None);
        var two = _db.SeedUser("two", MakiPermission.None);

        var start = DateTime.UtcNow.AddDays(-5);
        for (var i = 0; i < 150; i++)
        {
            Seed(one, $"one {i}", start.AddMinutes(i));
            Seed(two, $"two {i}", start.AddMinutes(i));
        }

        await RunHousekeeping();

        using var db = _db.NewContext();
        Assert.Equal(150, db.UserNotifications.IgnoreQueryFilters().Count(n => n.UserId == one));
        Assert.Equal(150, db.UserNotifications.IgnoreQueryFilters().Count(n => n.UserId == two));
    }

    private async Task RunHousekeeping()
    {
        using var db = _db.NewContext();
        var job = new HousekeepingJob(db, _paths, NullLogger<HousekeepingJob>.Instance);
        await job.Execute(new NoopJobContext());
    }

    private int Seed(int userId, string title, DateTime createdAt, bool read = false)
    {
        using var db = _db.NewContext();
        var row = new UserNotification
        {
            UserId = userId,
            Type = InboxEventType.ChapterDownloaded,
            Level = NotificationLevel.Info,
            Title = title,
            Body = title,
            CreatedAt = createdAt,
            ReadAt = read ? createdAt : null,
        };
        db.UserNotifications.Add(row);
        db.SaveChanges();
        return row.Id;
    }

    /// <summary>Quartz hands the job a context it only reads a cancellation token off.</summary>
    private sealed class NoopJobContext : IJobExecutionContext
    {
        public CancellationToken CancellationToken => CancellationToken.None;

        public IScheduler Scheduler => throw new NotSupportedException();
        public ITrigger Trigger => throw new NotSupportedException();
        public ICalendar? Calendar => null;
        public bool Recovering => false;
        public TriggerKey RecoveringTriggerKey => throw new NotSupportedException();
        public int RefireCount => 0;
        public JobDataMap MergedJobDataMap => [];
        public IJobDetail JobDetail => throw new NotSupportedException();
        public IJob JobInstance => throw new NotSupportedException();
        public DateTimeOffset FireTimeUtc => DateTimeOffset.UtcNow;
        public DateTimeOffset? ScheduledFireTimeUtc => null;
        public DateTimeOffset? PreviousFireTimeUtc => null;
        public DateTimeOffset? NextFireTimeUtc => null;
        public string FireInstanceId => "test";
        public object? Result { get; set; }
        public TimeSpan JobRunTime => TimeSpan.Zero;
        public void Put(object key, object objectValue) { }
        public object? Get(object key) => null;
    }
}
