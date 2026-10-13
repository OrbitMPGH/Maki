using Maki.Api.Auth;
using Maki.Api.Controllers;
using Maki.Api.Jobs;
using Maki.Api.Services;
using Maki.Core.Entities;
using Maki.Core.Kavita;
using Maki.Core.Metadata;
using Maki.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Maki.Api.Tests;

public class MetadataRefreshRaceTests : IDisposable
{
    private readonly TestDb _db = new();

    public void Dispose() => _db.Dispose();

    private static SeriesMetadata ProviderValues() => new()
    {
        ProviderId = "42",
        Title = "Provider Title",
        Description = "Provider synopsis",
        Status = SeriesStatus.Completed,
        TotalChapters = 120,
    };

    [Fact]
    public async Task A_hand_edit_saved_while_the_job_waits_on_the_provider_survives_the_refresh()
    {
        var id = _db.SeedSeries("Old Title", configure: s =>
        {
            s.MangaBakaId = 42;
            s.Overview = "Old synopsis";
        });
        var provider = new EditingProvider(ProviderValues(), async () =>
        {
            await using var db = _db.NewContext();
            var series = await db.Series.SingleAsync(s => s.Id == id);
            series.Title = "My Title";
            series.LockedFields |= SeriesMetadataField.Title;
            await db.SaveChangesAsync();
        });
        var job = new MetadataRefreshJob(Scopes(provider), NullLogger<MetadataRefreshJob>.Instance);

        Assert.True(await job.RefreshSeriesAsync(id, CancellationToken.None));

        await using var read = _db.NewContext();
        var stored = await read.Series.AsNoTracking().SingleAsync(s => s.Id == id);
        Assert.Equal("My Title", stored.Title);
        Assert.Equal(SeriesMetadataField.Title, stored.LockedFields);
        Assert.Equal("Provider synopsis", stored.Overview);
        Assert.Equal(SeriesStatus.Completed, stored.Status);
    }

    [Fact]
    public async Task A_series_deleted_while_the_job_waits_is_skipped()
    {
        var id = _db.SeedSeries("Gone", configure: s => s.MangaBakaId = 42);
        var provider = new EditingProvider(ProviderValues(), async () =>
        {
            await using var db = _db.NewContext();
            await db.Series.Where(s => s.Id == id).ExecuteDeleteAsync();
        });
        var job = new MetadataRefreshJob(Scopes(provider), NullLogger<MetadataRefreshJob>.Instance);

        Assert.False(await job.RefreshSeriesAsync(id, CancellationToken.None));
    }

    [Fact]
    public async Task Concurrent_edits_of_different_fields_keep_both_locks()
    {
        var id = _db.SeedSeries("Berserk", configure: s => s.MangaBakaId = 42);
        await using var first = _db.NewContext();
        await using var second = _db.NewContext();
        var a = Controller(first);
        var b = Controller(second);

        // Both contexts read the series before either saves, which is the race.
        var seriesA = await first.Series.SingleAsync(s => s.Id == id);
        var seriesB = await second.Series.SingleAsync(s => s.Id == id);
        Assert.Equal(SeriesMetadataField.None, seriesA.LockedFields | seriesB.LockedFields);

        Assert.IsType<OkObjectResult>(await a.Edit(
            id, new SeriesMetadataController.EditMetadataRequest(["totalChapters"], TotalChapters: 5), CancellationToken.None));
        Assert.IsType<OkObjectResult>(await b.Edit(
            id, new SeriesMetadataController.EditMetadataRequest(["status"], Status: "hiatus"), CancellationToken.None));

        await using var read = _db.NewContext();
        var stored = await read.Series.AsNoTracking().SingleAsync(s => s.Id == id);
        Assert.Equal(SeriesMetadataField.TotalChapters | SeriesMetadataField.Status, stored.LockedFields);
        Assert.Equal(5, stored.TotalChapters);
        Assert.Equal(SeriesStatus.Hiatus, stored.Status);
    }

    [Fact]
    public async Task Resetting_one_field_does_not_drop_a_lock_another_request_added()
    {
        var id = _db.SeedSeries("Berserk", configure: s =>
        {
            s.MangaBakaId = 42;
            s.LockedFields = SeriesMetadataField.Title;
        });
        await using var resetting = _db.NewContext();
        var series = await resetting.Series.Include(s => s.RootFolder).SingleAsync(s => s.Id == id);
        Assert.Equal(SeriesMetadataField.Title, series.LockedFields);

        await using (var other = _db.NewContext())
        {
            Assert.IsType<OkObjectResult>(await Controller(other).Edit(
                id, new SeriesMetadataController.EditMetadataRequest(["status"], Status: "hiatus"), CancellationToken.None));
        }

        // The reset's context still believes only Title is locked.
        Assert.IsType<OkObjectResult>(await Controller(resetting).Reset(
            id, new SeriesMetadataController.ResetMetadataRequest(["title"]), CancellationToken.None));

        await using var read = _db.NewContext();
        var stored = await read.Series.AsNoTracking().SingleAsync(s => s.Id == id);
        Assert.Equal(SeriesMetadataField.Status, stored.LockedFields);
    }

    private SeriesMetadataController Controller(MakiDbContext db) =>
        new(new TestLocalizer(), db, new SeriesMetadataRefreshService([new EditingProvider(ProviderValues(), null)], null!),
            new SeriesMetadataChangeLog(db, new RecordingInbox(), new RecordingNotifications(), new TestUserLocaleResolver(),
                new TestLocalizer(), TimeProvider.System, NullLogger<SeriesMetadataChangeLog>.Instance),
            new SeriesIdentityService(db, NullLogger<SeriesIdentityService>.Instance),
            new TestCurrentUser(1),
            new KavitaScanService(new KavitaClient(new StubHttpClientFactory("{}")), new FakeAppSettings(),
                _db.ScopeFactory(), NullLogger<KavitaScanService>.Instance));

    private IServiceScopeFactory Scopes(IMetadataProvider provider)
    {
        var services = new ServiceCollection();
        services.AddScoped(_ => _db.NewContext());
        services.AddScoped(_ => new SeriesMetadataRefreshService([provider], null!));
        return services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
    }

    private sealed class EditingProvider(SeriesMetadata metadata, Func<Task>? whileWaiting) : IMetadataProvider
    {
        public string Name => "stub";

        public Task<IReadOnlyList<MetadataSearchResult>> SearchAsync(
            string query, string maxContentRating, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<MetadataSearchResult>>([]);

        public async Task<SeriesMetadata?> GetAsync(string providerId, CancellationToken ct = default)
        {
            if (whileWaiting is not null)
            {
                await whileWaiting();
            }

            return metadata;
        }
    }
}
