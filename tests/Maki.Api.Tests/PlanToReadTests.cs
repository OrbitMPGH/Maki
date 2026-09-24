using Maki.Api.Services;
using Maki.Core.Entities;
using Maki.Core.Security;
using Maki.Data;
using Maki.Metadata.MangaBaka;
using Maki.Metadata.Tests;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Maki.Api.Tests;

public class PlanToReadTests : IDisposable
{
    private readonly TestDb _fixture = new();
    private readonly DumpDbBuilder _dump = new();

    public PlanToReadTests()
    {
        _dump.AddSeries(1234, "Dandadan", coverUrl: "https://covers.example/dandadan.jpg")
            .AddSeries(2345, "Spicy Title", contentRating: "erotica")
            .AddSeries(3456, "Unrated", contentRating: null);
    }

    public void Dispose()
    {
        _dump.Dispose();
        _fixture.Dispose();
    }

    private sealed class Viewer(int id, string ceiling) : ICurrentUser
    {
        public bool IsAuthenticated => true;
        public int UserId => id;
        public string UserName => "viewer";
        public MakiPermission Permissions => MakiPermission.None;
        public bool AllRootFolders => true;
        public IReadOnlySet<int> RootFolderIds => new HashSet<int>();
        public string MaxContentRating => ceiling;
    }

    private PlanToReadService Service(MakiDbContext db, int userId = 1, string ceiling = "erotica")
    {
        var viewer = new Viewer(userId, ceiling);
        return new PlanToReadService(db,
            new MangaBakaLocalStore(new MangaBakaDumpOptions(_dump.Path, Path.GetTempPath()), new FakeAppSettings(),
                NullLogger<MangaBakaLocalStore>.Instance),
            viewer, new HiddenContentService(new UserSettingsService(db, viewer), null!));
    }

    [Fact]
    public async Task Title_and_cover_come_from_the_catalogue_not_the_client()
    {
        using var db = _fixture.NewContext(1);

        var entry = await Service(db).AddAsync(1234, new PlanToReadCommand("taste", Guid.NewGuid()));

        Assert.Equal("Dandadan", entry.Title);
        Assert.Equal("https://covers.example/dandadan.jpg", entry.CoverUrl);
        Assert.Equal("taste", entry.Origin);
        Assert.Null(entry.InLibrarySeriesId);
        Assert.Null(entry.RequestStatus);
    }

    [Fact]
    public async Task A_replayed_mutation_is_stable_and_a_second_save_adds_nothing()
    {
        using var db = _fixture.NewContext(1);
        var service = Service(db);
        var command = new PlanToReadCommand("trending", Guid.NewGuid());

        var first = await service.AddAsync(1234, command);
        var replay = await service.AddAsync(1234, command);
        var again = await service.AddAsync(1234, new PlanToReadCommand("manual", Guid.NewGuid()));

        Assert.Equal(first, replay);
        Assert.Equal("trending", again.Origin);
        Assert.Single(await db.PlanToReadEntries.ToListAsync());
        await Assert.ThrowsAsync<FeedbackConflictException>(() =>
            service.AddAsync(2345, command));
    }

    [Fact]
    public async Task A_title_above_the_ceiling_cannot_be_saved()
    {
        using var db = _fixture.NewContext(1);

        await Assert.ThrowsAsync<FeedbackValidationException>(() =>
            Service(db, ceiling: "safe").AddAsync(2345, new PlanToReadCommand("taste", Guid.NewGuid())));
        Assert.Empty(await db.PlanToReadEntries.ToListAsync());
    }

    [Fact]
    public async Task Unknown_titles_and_bad_origins_are_rejected()
    {
        using var db = _fixture.NewContext(1);
        var service = Service(db);

        await Assert.ThrowsAsync<FeedbackNotFoundException>(() =>
            service.AddAsync(9999, new PlanToReadCommand("taste", Guid.NewGuid())));
        await Assert.ThrowsAsync<FeedbackValidationException>(() =>
            service.AddAsync(1234, new PlanToReadCommand("swiped", Guid.NewGuid())));
    }

    [Fact]
    public async Task The_list_drops_what_a_lowered_ceiling_no_longer_allows()
    {
        using (var db = _fixture.NewContext(1))
        {
            var service = Service(db);
            await service.AddAsync(1234, new PlanToReadCommand("taste", Guid.NewGuid()));
            await service.AddAsync(2345, new PlanToReadCommand("taste", Guid.NewGuid()));
            Assert.Equal(2, (await service.ListAsync()).Count);
        }

        using var lowered = _fixture.NewContext(1);
        var entry = Assert.Single(await Service(lowered, ceiling: "safe").ListAsync());
        Assert.Equal(1234, entry.ProviderId);
    }

    [Fact]
    public async Task An_unrated_title_stays_listed_below_the_top_ceiling()
    {
        using var db = _fixture.NewContext(1);
        var service = Service(db, ceiling: "safe");

        await service.AddAsync(3456, new PlanToReadCommand("taste", Guid.NewGuid()));

        Assert.Equal(3456, Assert.Single(await service.ListAsync()).ProviderId);
    }

    [Fact]
    public async Task The_ceiling_still_applies_with_no_dump()
    {
        using (var db = _fixture.NewContext(1))
        {
            await Service(db).AddAsync(2345, new PlanToReadCommand("taste", Guid.NewGuid()));
        }

        File.Delete(_dump.Path);
        using var lowered = _fixture.NewContext(1);
        Assert.Empty(await Service(lowered, ceiling: "safe").ListAsync());
        Assert.Single(await Service(lowered).ListAsync());
    }

    [Fact]
    public async Task The_list_reports_library_and_pending_request_status()
    {
        var seriesId = _fixture.SeedSeries(configure: s => s.MangaBakaId = 1234);
        using var db = _fixture.NewContext(1);
        db.SeriesRequests.Add(new SeriesRequest
        {
            UserId = 1, Kind = SeriesRequestKind.NewSeries, Status = SeriesRequestStatus.Pending,
            MetadataProviderId = "2345", Title = "Spicy Title", Created = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
        var service = Service(db);
        await service.AddAsync(1234, new PlanToReadCommand("taste", Guid.NewGuid()));
        await service.AddAsync(2345, new PlanToReadCommand("taste", Guid.NewGuid()));

        var list = (await service.ListAsync()).ToDictionary(x => x.ProviderId);

        Assert.Equal(seriesId, list[1234].InLibrarySeriesId);
        Assert.Null(list[1234].RequestStatus);
        Assert.Equal("pending", list[2345].RequestStatus);
    }

    [Fact]
    public async Task Another_users_pending_request_is_not_reported()
    {
        var other = _fixture.SeedUser("other");
        using (var seed = _fixture.NewContext())
        {
            seed.SeriesRequests.Add(new SeriesRequest
            {
                UserId = other, Kind = SeriesRequestKind.NewSeries, Status = SeriesRequestStatus.Pending,
                MetadataProviderId = "1234", Title = "Dandadan", Created = DateTime.UtcNow,
            });
            await seed.SaveChangesAsync();
        }

        using var db = _fixture.NewContext();
        var entry = await Service(db).AddAsync(1234, new PlanToReadCommand("taste", Guid.NewGuid()));

        Assert.Null(entry.RequestStatus);
    }

    [Fact]
    public async Task Delete_removes_the_entry_and_is_idempotent()
    {
        using var db = _fixture.NewContext(1);
        var service = Service(db);
        await service.AddAsync(1234, new PlanToReadCommand("taste", Guid.NewGuid()));

        await service.RemoveAsync(1234);
        await service.RemoveAsync(1234);

        Assert.Empty(await service.ListAsync());
        Assert.Empty(await db.RecommendationFeedbackEvents.ToListAsync());
    }
}
