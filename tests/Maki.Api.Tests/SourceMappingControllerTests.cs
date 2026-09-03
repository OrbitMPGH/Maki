using Maki.Api.Controllers;
using Maki.Api.Services;
using Maki.Core.Entities;
using Maki.Core.Sources;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Maki.Api.Tests;

/// <summary>
/// Covers the re-runnable auto-match endpoint: what it flags, what it hands to the background
/// worker, and the guards that stop a second click queueing the same series twice.
/// </summary>
public class SourceMappingControllerTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly SourceMatchQueue _queue = new();

    public void Dispose() => _db.Dispose();

    private SourceMappingController BuildController(
        SourceAvailability? availability = null, params ISource[] sources) =>
        new(_db.NewContext(),
            new SourceRegistry(sources.Length > 0 ? sources : [new FakeSource { Name = "fake" }]),
            new FakeAppSettings(), availability ?? Sources.AllEnabled, _queue,
            // Every compare path exercised here is rejected before the preview service is reached.
            null!, null!, null!, new TestCurrentUser(1));

    /// <summary>Everything the worker was handed, in order.</summary>
    private List<int> Queued()
    {
        var ids = new List<int>();
        while (_queue.Reader.TryRead(out var id))
        {
            ids.Add(id);
        }
        return ids;
    }

    private bool PendingOf(int seriesId)
    {
        using var db = _db.NewContext();
        return db.Series.Single(s => s.Id == seriesId).SourceMatchPending;
    }

    private static int QueuedCount(IActionResult result) =>
        (int)Assert.IsType<OkObjectResult>(result).Value!.GetType()
            .GetProperty("queued")!.GetValue(Assert.IsType<OkObjectResult>(result).Value)!;

    [Fact]
    public async Task Flags_the_series_and_hands_it_to_the_worker()
    {
        var first = _db.SeedSeries("Hajime no Ippo");
        var second = _db.SeedSeries("Berserk");

        var result = await BuildController().AutoMatch(new([first, second]), default);

        Assert.Equal(2, QueuedCount(result));
        Assert.Equal([first, second], Queued());
        Assert.True(PendingOf(first));
        Assert.True(PendingOf(second));
    }

    [Fact]
    public async Task Series_already_matching_is_not_queued_again()
    {
        // A second click, or the same series caught by both a bulk run and a single one: the flag
        // is the record of what's owed, and the worker would drop the duplicate anyway.
        var seriesId = _db.SeedSeries("Hajime no Ippo", configure: s => s.SourceMatchPending = true);

        var result = await BuildController().AutoMatch(new([seriesId]), default);

        Assert.Equal(0, QueuedCount(result));
        Assert.Empty(Queued());
    }

    [Fact]
    public async Task Duplicate_ids_in_one_request_queue_once()
    {
        var seriesId = _db.SeedSeries("Hajime no Ippo");

        var result = await BuildController().AutoMatch(new([seriesId, seriesId]), default);

        Assert.Equal(1, QueuedCount(result));
        Assert.Equal([seriesId], Queued());
    }

    [Fact]
    public async Task Unknown_ids_are_ignored_rather_than_failing_the_batch()
    {
        var seriesId = _db.SeedSeries("Hajime no Ippo");

        var result = await BuildController().AutoMatch(new([seriesId, 9999]), default);

        Assert.Equal(1, QueuedCount(result));
        Assert.Equal([seriesId], Queued());
    }

    [Fact]
    public async Task Empty_request_is_rejected()
    {
        var result = await BuildController().AutoMatch(new([]), default);

        Assert.IsType<BadRequestObjectResult>(result);
        Assert.Empty(Queued());
    }

    [Fact]
    public async Task Existing_mappings_are_left_alone()
    {
        // The endpoint only ever flags the row; nothing already linked is touched, which is what
        // makes re-running safe on a series that's already partly matched.
        var seriesId = _db.SeedSeries("Hajime no Ippo", mappings: new SourceMapping
        {
            SourceName = "fake", SourceSeriesId = "existing", Url = "https://fake.test/s"
        });

        await BuildController().AutoMatch(new([seriesId]), default);

        using var db = _db.NewContext();
        Assert.Equal("existing", db.SourceMappings.Single(m => m.SeriesId == seriesId).SourceSeriesId);
    }

    [Fact]
    public async Task Changing_language_filter_invalidates_the_chapter_snapshot()
    {
        var seriesId = _db.SeedSeries(mappings: Mapping("fake", 1));
        SourceMapping mapping;
        using (var db = _db.NewContext())
        {
            mapping = db.SourceMappings.Single(m => m.SeriesId == seriesId);
            mapping.ChapterSnapshotAt = DateTime.UtcNow;
            db.SaveChanges();
        }

        mapping.LanguageFilter = "ja";
        await BuildController().Update(mapping.Id, mapping, default);

        using var check = _db.NewContext();
        Assert.Null(check.SourceMappings.Single(m => m.Id == mapping.Id).ChapterSnapshotAt);
    }

    [Fact]
    public async Task Deleting_files_during_cleanup_requires_delete_series_permission()
    {
        var controller = new SourceMappingController(
            _db.NewContext(),
            new SourceRegistry([new FakeSource { Name = "fake" }]),
            new FakeAppSettings(),
            Sources.AllEnabled,
            _queue,
            null!,
            null!,
            null!,
            new TestCurrentUser(1, permissions: Maki.Core.Security.MakiPermission.ManageSources));

        var result = await controller.RemoveWithCleanup(
            123, new SourceMappingController.RemoveMappingRequest(DeleteFiles: true), default);

        Assert.IsType<ForbidResult>(result);
    }

    [Fact]
    public async Task Source_manager_can_refresh_cleanup_snapshots()
    {
        var seriesId = _db.SeedSeries(mappings: Mapping("fake", 1));
        var fake = new FakeSource { Name = "fake" };
        var source = new FakeSource
        {
            Name = "fake", OnListChapters = _ => [fake.Chapter(1, title: "Chapter one")]
        };
        var registry = new SourceRegistry([source]);
        var availability = Sources.AllEnabled;
        var settings = new FakeAppSettings();
        using var db = _db.NewContext();
        var sync = new ChapterSyncService(
            db,
            registry,
            new DownloadQueueService(null!, TimeProvider.System, null!,
                NullLogger<DownloadQueueService>.Instance),
            availability,
            new SourceChapterListCache(TimeProvider.System, NullLogger<SourceChapterListCache>.Instance),
            settings,
            NullLogger<ChapterSyncService>.Instance);
        var controller = new SourceMappingController(
            db,
            registry,
            settings,
            availability,
            _queue,
            null!,
            sync,
            null!,
            new TestCurrentUser(1, permissions: Maki.Core.Security.MakiPermission.ManageSources));

        var result = await controller.RefreshSnapshots(
            new SourceMappingController.RefreshSnapshotsRequest(seriesId, ExcludeMappingId: -1), default);

        Assert.IsType<OkObjectResult>(result);
        Assert.NotNull(db.SourceMappings.Single(m => m.SeriesId == seriesId).ChapterSnapshotAt);
        Assert.Single(db.ChapterSourceLinks);
    }

    [Fact]
    public async Task Cleanup_snapshot_refresh_only_calls_missing_remaining_sources()
    {
        var seriesId = _db.SeedSeries(
            mappings:
            [
                Mapping("target", 1),
                Mapping("ready", 2),
                Mapping("missing", 3)
            ]);
        using var db = _db.NewContext();
        var mappings = db.SourceMappings.Where(m => m.SeriesId == seriesId).ToList();
        var target = mappings.Single(m => m.SourceName == "target");
        mappings.Single(m => m.SourceName == "ready").ChapterSnapshotAt = DateTime.UtcNow;
        await db.SaveChangesAsync();

        var targetSource = new FakeSource { Name = "target" };
        var readySource = new FakeSource { Name = "ready" };
        var missingSource = new FakeSource
        {
            Name = "missing",
            OnListChapters = _ => [new FakeSource { Name = "missing" }.Chapter(1)]
        };
        var registry = new SourceRegistry([targetSource, readySource, missingSource]);
        var availability = Sources.AllEnabled;
        var settings = new FakeAppSettings();
        var sync = new ChapterSyncService(
            db,
            registry,
            new DownloadQueueService(null!, TimeProvider.System, null!,
                NullLogger<DownloadQueueService>.Instance),
            availability,
            new SourceChapterListCache(TimeProvider.System, NullLogger<SourceChapterListCache>.Instance),
            settings,
            NullLogger<ChapterSyncService>.Instance);
        var controller = new SourceMappingController(
            db,
            registry,
            settings,
            availability,
            _queue,
            null!,
            sync,
            null!,
            new TestCurrentUser(1, permissions: Maki.Core.Security.MakiPermission.ManageSources));

        var result = await controller.RefreshSnapshots(
            new SourceMappingController.RefreshSnapshotsRequest(seriesId, target.Id), default);

        Assert.IsType<OkObjectResult>(result);
        Assert.Equal(0, targetSource.ListCalls);
        Assert.Equal(0, readySource.ListCalls);
        Assert.Equal(1, missingSource.ListCalls);
        Assert.Null(db.SourceMappings.Single(m => m.Id == target.Id).ChapterSnapshotAt);
        Assert.NotNull(db.SourceMappings.Single(m => m.SourceName == "missing").ChapterSnapshotAt);
    }

    private static SourceMapping Mapping(string name, int priority) => new()
    {
        SourceName = name,
        SourceSeriesId = "s",
        Url = $"https://{name}.test/s",
        Priority = priority
    };

    private Dictionary<string, int> PrioritiesOf(int seriesId)
    {
        using var db = _db.NewContext();
        return db.SourceMappings.Where(m => m.SeriesId == seriesId)
            .ToDictionary(m => m.SourceName, m => m.Priority);
    }

    [Fact]
    public async Task Reorder_renumbers_the_series_sources_from_one()
    {
        var seriesId = _db.SeedSeries("Hajime no Ippo",
            mappings: [Mapping("a", 1), Mapping("b", 2), Mapping("c", 3)]);
        var ids = PrioritiesOf(seriesId).Count == 3 ? IdsOf(seriesId) : [];

        // Dragged into c, a, b.
        var result = await BuildController().Reorder(
            new(seriesId, [ids["c"], ids["a"], ids["b"]]), default);

        Assert.IsType<OkObjectResult>(result);
        Assert.Equal(new Dictionary<string, int> { ["c"] = 1, ["a"] = 2, ["b"] = 3 }, PrioritiesOf(seriesId));
    }

    [Fact]
    public async Task Reorder_refuses_a_mapping_from_another_series()
    {
        // The ids come from the client, so "this list is exactly this series' mappings" has to be
        // checked here — otherwise one series' drag renumbers another's sources.
        var mine = _db.SeedSeries("Hajime no Ippo", mappings: [Mapping("a", 1), Mapping("b", 2)]);
        var theirs = _db.SeedSeries("Berserk", mappings: [Mapping("a", 1)]);

        var result = await BuildController().Reorder(
            new(mine, [IdsOf(mine)["a"], IdsOf(theirs)["a"]]), default);

        Assert.IsType<BadRequestObjectResult>(result);
        Assert.Equal(new Dictionary<string, int> { ["a"] = 1, ["b"] = 2 }, PrioritiesOf(mine));
    }

    [Fact]
    public async Task Reorder_of_an_empty_list_is_rejected()
    {
        var seriesId = _db.SeedSeries("Hajime no Ippo", mappings: Mapping("a", 1));

        Assert.IsType<BadRequestObjectResult>(
            await BuildController().Reorder(new(seriesId, []), default));
    }

    [Fact]
    public async Task Compare_needs_two_live_sources()
    {
        var seriesId = _db.SeedSeries("Hajime no Ippo", mappings: Mapping("fake", 1));

        Assert.IsType<BadRequestObjectResult>(
            await BuildController().StartCompare(new(seriesId), default));
    }

    [Fact]
    public async Task Compare_ignores_a_globally_switched_off_source()
    {
        // Both switches, same as every other liveness check: two mappings but one of the sources is
        // off instance-wide, so there is nothing to compare against.
        var seriesId = _db.SeedSeries("Hajime no Ippo", mappings: [Mapping("fake", 1), Mapping("other", 2)]);

        var controller = BuildController(
            Sources.Disabled("other"),
            new FakeSource { Name = "fake" }, new FakeSource { Name = "other" });

        Assert.IsType<BadRequestObjectResult>(await controller.StartCompare(new(seriesId), default));
    }

    [Fact]
    public async Task Compare_on_an_unknown_series_is_a_404()
    {
        Assert.IsType<NotFoundResult>(await BuildController().StartCompare(new(9999), default));
    }

    private Dictionary<string, int> IdsOf(int seriesId)
    {
        using var db = _db.NewContext();
        return db.SourceMappings.Where(m => m.SeriesId == seriesId)
            .ToDictionary(m => m.SourceName, m => m.Id);
    }
}
