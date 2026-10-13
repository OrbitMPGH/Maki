using Maki.Api.Services;
using Maki.Core.Configuration;
using Maki.Core.Entities;
using Maki.Core.Inbox;
using Maki.Core.Progress;
using Maki.Data;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Maki.Api.Tests;

public sealed class AchievementEvaluationQueueTests : IDisposable
{
    private const int UserId = 1;
    private static readonly DateTime Now = new(2026, 6, 15, 12, 0, 0, DateTimeKind.Utc);

    private readonly TestDb _db = new();
    private readonly RecordingInbox _inbox = new();
    private readonly MemoryCache _cache = new(new MemoryCacheOptions());
    private readonly ServiceProvider _services;

    public AchievementEvaluationQueueTests()
    {
        _db.SeedUser();

        var services = new ServiceCollection();
        services.AddScoped(_ => _db.NewContext());
        services.AddSingleton<IMemoryCache>(_cache);
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<IUserSettingsStore>(new TestSettingsStore(_db));
        services.AddSingleton<InboxService>(_inbox);
        services.AddScoped<UserMetricsService>();
        services.AddScoped<AchievementService>();
        services.AddLogging();
        _services = services.BuildServiceProvider();
    }

    public void Dispose()
    {
        _services.Dispose();
        _cache.Dispose();
        _db.Dispose();
    }

    private sealed class TestSettingsStore(TestDb db) : IUserSettingsStore
    {
        public Task<string?> GetAsync(int userId, string key, CancellationToken ct = default)
        {
            using var context = db.NewContext();
            return Task.FromResult(context.UserSettings
                .Where(s => s.UserId == userId && s.Key == key)
                .Select(s => s.Value)
                .FirstOrDefault());
        }

        public Task SetAsync(int userId, string key, string? value, CancellationToken ct = default)
        {
            db.SetUserConfig(userId, (key, value ?? string.Empty));
            return Task.CompletedTask;
        }
    }

    private AchievementEvaluationQueue Queue() => new(
        _services.GetRequiredService<IServiceScopeFactory>(), NullLogger<AchievementEvaluationQueue>.Instance);

    private void SeedChaptersRead(int chapters)
    {
        using var db = _db.NewContext();
        db.StatsEvents.Add(new StatsEvent
        {
            Type = StatsEventType.ChaptersRead, UserId = UserId, SeriesTitle = "Seeded", Timestamp = Now, Value = chapters,
        });
        db.SaveChanges();
    }

    [Fact]
    public async Task A_queued_user_is_evaluated_and_the_unlock_is_raised_to_their_inbox()
    {
        SeedChaptersRead(3000);
        var queue = Queue();

        queue.Enqueue(UserId);
        await queue.FlushAsync(default);

        Assert.Contains(_inbox.Raised, r => r.Type == InboxEventType.AchievementUnlocked &&
            r.Audience.Equals(InboxAudience.User(UserId)));
        Assert.NotEmpty(_db.NewContext().UserAchievements.Where(a => a.UserId == UserId).ToList());
    }

    [Fact]
    public void Nothing_is_evaluated_until_the_worker_runs()
    {
        SeedChaptersRead(3000);

        Queue().Enqueue(UserId);

        Assert.Empty(_inbox.Raised);
        Assert.Empty(_db.NewContext().UserAchievements.ToList());
    }

    [Fact]
    public async Task Chapters_finished_before_the_worker_runs_are_evaluated_once()
    {
        SeedChaptersRead(3000);
        var queue = Queue();

        queue.Enqueue(UserId);
        queue.Enqueue(UserId);
        queue.Enqueue(UserId);
        Assert.Equal(1, queue.Pending);

        await queue.FlushAsync(default);

        Assert.Equal(0, queue.Pending);
        var keys = _db.NewContext().UserAchievements.Select(a => a.Key).Distinct().Count();
        Assert.Equal(keys, _inbox.Raised.Count(r => r.Type == InboxEventType.AchievementUnlocked));
    }

    [Fact]
    public async Task A_cached_snapshot_does_not_hide_the_chapter_that_was_just_finished()
    {
        _db.SetUserConfig(UserId, (SettingKeys.UserTimeZone, "UTC"));
        var queue = Queue();
        await using (var scope = _services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<UserMetricsService>().GetAsync(UserId);
        }

        SeedChaptersRead(3000);
        queue.Enqueue(UserId);
        await queue.FlushAsync(default);

        Assert.Contains(_inbox.Raised, r => r.Type == InboxEventType.AchievementUnlocked);
    }

    private sealed class ReenqueueingQueue(IServiceScopeFactory scopes)
        : AchievementEvaluationQueue(scopes, NullLogger<AchievementEvaluationQueue>.Instance)
    {
        public int Passes { get; private set; }

        protected override Task EvaluateUserAsync(int userId, CancellationToken ct)
        {
            if (++Passes == 1)
            {
                Enqueue(userId);
            }

            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task A_completion_that_lands_during_an_evaluation_queues_a_second_pass()
    {
        var queue = new ReenqueueingQueue(_services.GetRequiredService<IServiceScopeFactory>());

        queue.Enqueue(UserId);
        await queue.FlushAsync(default);

        Assert.Equal(2, queue.Passes);
        Assert.Equal(0, queue.Pending);
    }
}
