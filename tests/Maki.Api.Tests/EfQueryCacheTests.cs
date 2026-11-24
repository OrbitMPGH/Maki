using System.Linq.Expressions;
using Maki.Api.Jobs;
using Maki.Core.Entities;
using Maki.Core.Sources;
using Maki.Data;
using Maki.Metadata.MangaBaka;
using Maki.Metadata.Catalogue;
using Maki.Metadata.CoRead;
using Maki.Metadata.Ease;
using Maki.Metadata.Embedding;
using Maki.Metadata.ReaderCohorts;
using Maki.Metadata.RecoGraph;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Internal;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Maki.Api.Tests;

public sealed class EfQueryCacheTests : IDisposable
{
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(10);

    private readonly FakeClock _clock = new();
    private readonly SqliteConnection _connection = new("DataSource=:memory:");

    public EfQueryCacheTests() => _connection.Open();

    public void Dispose() => _connection.Dispose();

    [Fact]
    public void An_unused_query_expires_while_a_query_in_use_stays_compiled()
    {
        using var cache = new EfQueryCache(Window, _clock);
        var options = Options(cache);
        using (var db = new MakiDbContext(options))
        {
            db.Database.EnsureCreated();
        }

        Count(options, 1);
        var hot = NonModelKeys(cache);
        Assert.NotEmpty(hot);

        Count(options, 2);
        var cold = NonModelKeys(cache).Except(hot).ToList();
        Assert.NotEmpty(cold);

        _clock.Advance(TimeSpan.FromMinutes(6));
        Count(options, 1);
        _clock.Advance(TimeSpan.FromMinutes(6));
        cache.Sweep();

        var left = NonModelKeys(cache);
        Assert.All(hot, key => Assert.Contains(key, left));
        Assert.All(cold, key => Assert.DoesNotContain(key, left));
        Assert.NotEmpty(ModelKeys(cache));
        WaitFor(() => cache.PendingEvictions >= cold.Count);
        Assert.Equal(cold.Count, cache.PendingEvictions);
    }

    [Fact]
    public void The_model_survives_expiry_and_size_pressure()
    {
        // The model takes 250 of the 500, leaving room for 25 query and command entries.
        using var cache = new EfQueryCache(Window, _clock, sizeLimit: 500);
        var options = Options(cache);
        IModel model;
        using (var db = new MakiDbContext(options))
        {
            db.Database.EnsureCreated();
            model = db.Model;
        }

        var modelKeys = ModelKeys(cache);
        Assert.NotEmpty(modelKeys);

        for (var i = 0; i < 60; i++)
        {
            Count(options, i);
        }

        WaitFor(() => cache.PendingEvictions > 0);

        _clock.Advance(TimeSpan.FromDays(3));
        cache.Sweep();

        Assert.Equal(modelKeys.ToHashSet(), ModelKeys(cache).ToHashSet());
        Assert.Empty(NonModelKeys(cache));
        using var after = new MakiDbContext(options);
        Assert.Same(model, after.Model);
    }

    [Fact]
    public void A_zero_window_keeps_every_query()
    {
        using var cache = new EfQueryCache(window: null, _clock);
        cache.Set("query", new object(), new MemoryCacheEntryOptions { Size = 10 });

        _clock.Advance(TimeSpan.FromDays(30));
        cache.Sweep();

        Assert.True(cache.TryGetValue("query", out _));
    }

    [Fact]
    public void An_expiry_the_caller_set_is_kept()
    {
        using var cache = new EfQueryCache(Window, _clock);
        using (var entry = cache.CreateEntry("query"))
        {
            entry.Value = new object();
            entry.Size = 10;
            entry.SlidingExpiration = TimeSpan.FromHours(2);
        }

        _clock.Advance(TimeSpan.FromHours(1));
        cache.Sweep();

        Assert.True(cache.TryGetValue("query", out _));
    }

    [Fact]
    public async Task The_idle_job_collects_only_once_enough_queries_expired()
    {
        using var cache = new EfQueryCache(Window, _clock);
        var collects = 0;
        var job = Job(cache, () => collects++);

        await job.Execute(null!);
        Assert.Equal(0, collects);

        Expire(cache, "a", 10);
        await job.Execute(null!);
        Assert.Equal(0, collects);
        Assert.Equal(10, cache.PendingEvictions);

        Expire(cache, "b", EfQueryCache.MinEvictionsToCollect);
        await job.Execute(null!);
        Assert.Equal(1, collects);
        Assert.Equal(0, cache.PendingEvictions);

        await job.Execute(null!);
        Assert.Equal(1, collects);
    }

    [Fact]
    public void The_window_defaults_to_twenty_minutes_and_zero_keeps_everything()
    {
        Assert.Equal(20, EfQueryCache.ResolveMinutes(null));
        Assert.Equal(20, EfQueryCache.ResolveMinutes("nope"));
        Assert.Equal(0, EfQueryCache.ResolveMinutes("0"));

        var expectedMinutes = EfQueryCache.ResolveMinutes(Environment.GetEnvironmentVariable(EfQueryCache.WindowMinutesVariable));
        Assert.Equal(
            expectedMinutes > 0 ? TimeSpan.FromMinutes(expectedMinutes) : (TimeSpan?)null,
            EfQueryCache.Shared.SlidingWindow);
    }

    [Fact]
    public void Disposing_a_cache_entry_twice_counts_at_most_one_eviction()
    {
        using var cache = new EfQueryCache(Window, _clock);
        var entry = cache.CreateEntry("dup");
        entry.Value = new object();
        entry.Size = 10;
        entry.Dispose();
        entry.Dispose();

        _clock.Advance(Window + TimeSpan.FromMinutes(1));
        cache.Sweep();
        WaitFor(() => cache.PendingEvictions >= 1);
        Thread.Sleep(50);

        Assert.Equal(1, cache.PendingEvictions);
    }

    [Fact]
    public void Contexts_without_their_own_cache_use_the_shared_one()
    {
        var options = new DbContextOptionsBuilder<MakiDbContext>().UseSqlite(_connection).Options;
        using var db = new MakiDbContext(options);

        var core = db.GetService<IDbContextOptions>().FindExtension<CoreOptionsExtension>();

        Assert.Same(EfQueryCache.Shared, core?.MemoryCache);
    }

    [Fact]
    public void Contexts_with_their_own_cache_do_not_share_the_process_cache()
    {
        using var isolated = new EfQueryCache(Window, _clock);
        var options = new DbContextOptionsBuilder<MakiDbContext>()
            .UseSqlite(_connection)
            .UseMemoryCache(isolated)
            .Options;
        using var db = new MakiDbContext(options);

        var core = db.GetService<IDbContextOptions>().FindExtension<CoreOptionsExtension>();

        Assert.Same(isolated, core?.MemoryCache);
    }

    [Fact]
    public void The_design_time_factory_builds_its_model_through_the_shared_cache()
    {
        using var db = new DesignTimeDbContextFactory().CreateDbContext([]);

        Assert.NotEmpty(db.GetService<IDesignTimeModel>().Model.GetEntityTypes());
        Assert.Same(EfQueryCache.Shared, db.GetService<IDbContextOptions>().FindExtension<CoreOptionsExtension>()?.MemoryCache);
    }

    private void Expire(EfQueryCache cache, string prefix, int count)
    {
        var before = cache.PendingEvictions;
        for (var i = 0; i < count; i++)
        {
            cache.Set($"{prefix}{i}", new object(), new MemoryCacheEntryOptions { Size = 10 });
        }

        _clock.Advance(Window + TimeSpan.FromMinutes(1));
        cache.Sweep();
        // Eviction callbacks run on the thread pool, so the job's own sweep may not see them yet.
        WaitFor(() => cache.PendingEvictions >= before + count);
    }

    private static ArtifactIdleUnloadJob Job(EfQueryCache cache, Action collect)
    {
        var staging = Path.GetTempPath();
        var dump = new MangaBakaDumpOptions("", staging);
        return new ArtifactIdleUnloadJob(
            new CatalogueIndexCache(dump, NullLogger<CatalogueIndexCache>.Instance),
            new VectorIndexCache(new EmbeddingOptions("", "", "", EmbeddingModelProfile.Base), dump,
                NullLogger<VectorIndexCache>.Instance),
            new CoReadCache(new CoReadOptions("", staging), NullLogger<CoReadCache>.Instance),
            new EaseCache(new EaseOptions(""), NullLogger<EaseCache>.Instance),
            new RecoGraphCache(new RecoGraphOptions("", staging), NullLogger<RecoGraphCache>.Instance),
            new ReaderCohortCache(new ReaderCohortOptions("", staging), NullLogger<ReaderCohortCache>.Instance),
            new SourceChapterListCache(TimeProvider.System, NullLogger<SourceChapterListCache>.Instance),
            NullLogger<ArtifactIdleUnloadJob>.Instance,
            queryCache: cache,
            collect: collect);
    }

    private DbContextOptions<MakiDbContext> Options(EfQueryCache cache) =>
        new DbContextOptionsBuilder<MakiDbContext>()
            .UseSqlite(_connection)
            .UseMemoryCache(cache)
            .Options;

    /// <summary>A query whose shape differs per <paramref name="threshold"/>: a literal constant is part of EF Core's cache key.</summary>
    private static void Count(DbContextOptions<MakiDbContext> options, int threshold)
    {
        var series = Expression.Parameter(typeof(Series));
        var filter = Expression.Lambda<Func<Series, bool>>(
            Expression.GreaterThan(Expression.Property(series, nameof(Series.Id)), Expression.Constant(threshold)),
            series);
        using var db = new MakiDbContext(options);
        _ = db.Series.Where(filter).Count();
    }

    private static List<object> ModelKeys(EfQueryCache cache) =>
        cache.Keys.Where(k => k is ModelCacheKey).ToList();

    private static List<object> NonModelKeys(EfQueryCache cache) =>
        cache.Keys.Where(k => k is not ModelCacheKey).ToList();

    private static void WaitFor(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "timed out");
            Thread.Sleep(10);
        }
    }

    private sealed class FakeClock : ISystemClock
    {
        public DateTimeOffset UtcNow { get; private set; } = new(2026, 11, 23, 12, 0, 0, TimeSpan.Zero);

        public void Advance(TimeSpan by) => UtcNow += by;
    }
}
