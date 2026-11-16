using Maki.Api.Controllers;
using Maki.Api.Dtos;
using Maki.Api.Services;
using Maki.Core.Entities;
using Maki.Core.Metadata;
using Maki.Core.Security;
using Maki.Core.Tags;
using Maki.Data;
using Maki.Data.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Maki.Api.Tests;

public sealed class RootFolderRoutingTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly List<string> _paths = [];

    public void Dispose()
    {
        _db.Dispose();
        foreach (var path in _paths.Where(Directory.Exists))
        {
            Directory.Delete(path, recursive: true);
        }
    }

    /// <summary>Provider id "manhwa-12" is MangaBaka id 12 of type manhwa; a third part is its content rating.</summary>
    private sealed class Provider : IMetadataProvider
    {
        public string Name => "stub";

        public Task<IReadOnlyList<MetadataSearchResult>> SearchAsync(
            string query, string maxContentRating, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<MetadataSearchResult>>([]);

        public Task<SeriesMetadata?> GetAsync(string providerId, CancellationToken ct = default)
        {
            var parts = providerId.Split('-');
            return Task.FromResult<SeriesMetadata?>(new SeriesMetadata
            {
                ProviderId = providerId, Title = $"Series {parts[1]}", MangaBakaId = int.Parse(parts[1]),
                Type = parts[0], Genres = parts[0] == "manga" ? ["Ecchi"] : ["Action"],
                ContentRating = parts.Length > 2 ? parts[2] : "safe",
            });
        }
    }

    private int Folder()
    {
        using var db = _db.NewContext();
        var path = Path.Combine(Path.GetTempPath(), "maki-routing-" + Guid.NewGuid().ToString("N"));
        _paths.Add(path);
        var folder = new RootFolder { Path = path };
        db.RootFolders.Add(folder);
        db.SaveChanges();
        return folder.Id;
    }

    private int Rule(int folderId, int position, bool enabled = true, params AutoTagCondition[][] groups)
    {
        using var db = _db.NewContext();
        var rule = new RootFolderRule
        {
            RootFolderId = folderId, Position = position, Enabled = enabled,
            ConditionsJson = ConditionGroups.Serialize(groups.Select(g => (IReadOnlyList<AutoTagCondition>)g).ToList()),
        };
        db.RootFolderRules.Add(rule);
        db.SaveChanges();
        return rule.Id;
    }

    private static SeriesCreationService Creation(MakiDbContext db) => new(
        db, [new Provider()],
        coverService: null!, sourceMatchService: null!, chapterSyncService: null!,
        sourceMatchQueue: new SourceMatchQueue(),
        stats: null!, identity: null!, appSettings: new FakeAppSettings(),
        naming: new NamingService(new FakeAppSettings()),
        notifications: new RecordingNotifications(), locales: new TestUserLocaleResolver(),
        catalog: new TestLocalizer(), localizer: new TestLocalizer(), logger: NullLogger<SeriesCreationService>.Instance,
        routing: new RootFolderRoutingService(db));

    private async Task<int?> AddedIntoAsync(
        string providerId, int? chosen = null, IReadOnlyCollection<int>? usable = null, string? addedFrom = null)
    {
        await using var db = _db.NewContext();
        var result = await Creation(db).CreateAsync(
            providerId, chosen, monitored: false, null, default, deferSourceMatching: true,
            addedFrom: addedFrom, usableRootFolderIds: usable);
        return result.Series?.RootFolderId;
    }

    [Fact]
    public async Task The_first_matching_rule_in_order_picks_the_folder()
    {
        var fallback = Folder();
        var korean = Folder();
        var spicy = Folder();
        Rule(spicy, position: 0, groups: [[new(AutoTagFields.ContentRating, "pornographic")]]);
        Rule(korean, position: 1, groups: [[new(AutoTagFields.Type, "manhwa")], [new(AutoTagFields.Type, "manhua")]]);

        Assert.Equal(korean, await AddedIntoAsync("manhwa-1"));
        Assert.Equal(korean, await AddedIntoAsync("manhua-2"));
        Assert.Equal(spicy, await AddedIntoAsync("manhwa-3-pornographic"));
        Assert.Equal(fallback, await AddedIntoAsync("manga-4"));
    }

    [Fact]
    public async Task A_chosen_folder_always_wins_over_the_rules()
    {
        var chosen = Folder();
        var routed = Folder();
        Rule(routed, position: 0, groups: [[new(AutoTagFields.Type, "manhwa")]]);

        Assert.Equal(chosen, await AddedIntoAsync("manhwa-1", chosen));
    }

    [Fact]
    public async Task Disabled_rules_and_folders_the_adder_cannot_use_are_passed_over()
    {
        var first = Folder();
        var hidden = Folder();
        var off = Folder();
        var open = Folder();
        Rule(hidden, position: 0, groups: [[new(AutoTagFields.Type, "manhwa")]]);
        Rule(off, position: 1, enabled: false, groups: [[new(AutoTagFields.Type, "manhwa")]]);
        Rule(open, position: 2, groups: [[new(AutoTagFields.Genre, "action")]]);

        Assert.Equal(open, await AddedIntoAsync("manhwa-1", usable: [first, off, open]));
        Assert.Equal(first, await AddedIntoAsync("manga-2", usable: [first, off, open]));
    }

    [Fact]
    public async Task How_the_series_came_in_is_a_condition()
    {
        var fallback = Folder();
        var discover = Folder();
        Rule(discover, position: 0, groups: [[new(AutoTagFields.AddedVia, AutoTagAddedVia.Discover)]]);

        Assert.Equal(discover, await AddedIntoAsync("manga-1", addedFrom: "recommendation"));
        Assert.Equal(fallback, await AddedIntoAsync("manga-2", addedFrom: "library"));
    }

    [Fact]
    public async Task No_usable_folder_fails_as_not_found()
    {
        Folder();

        await using var db = _db.NewContext();
        var result = await Creation(db).CreateAsync(
            "manga-1", null, monitored: false, null, default, deferSourceMatching: true, usableRootFolderIds: []);

        Assert.Equal(SeriesCreationError.RootFolderNotFound, result.Error);
    }

    [Fact]
    public async Task Deleting_a_root_folder_deletes_the_rules_pointing_at_it()
    {
        var kept = Folder();
        var removed = Folder();
        Rule(kept, position: 0, groups: [[new(AutoTagFields.Type, "manga")]]);
        Rule(removed, position: 1, groups: [[new(AutoTagFields.Type, "manhwa")]]);

        await using (var db = _db.NewContext())
        {
            db.RootFolders.Remove(await db.RootFolders.SingleAsync(f => f.Id == removed));
            await db.SaveChangesAsync();
        }

        await using var verify = _db.NewContext();
        Assert.Equal([kept], await verify.RootFolderRules.Select(r => r.RootFolderId).ToListAsync());
    }

    private static RootFolderRulesController Controller(MakiDbContext db, ICurrentUser user) =>
        new(new TestLocalizer(), db, new RootFolderRoutingService(db), user, [new Provider()]);

    private static RootFolderRouteDto Route(IActionResult result) => (RootFolderRouteDto)((OkObjectResult)result).Value!;

    [Fact]
    public async Task The_route_preview_names_the_rule_and_only_folders_the_caller_may_use()
    {
        Folder();
        var routed = Folder();
        var ruleId = Rule(routed, position: 0, groups: [[new(AutoTagFields.Type, "manhwa")]]);
        await using var db = _db.NewContext();
        var restricted = new TestCurrentUser(
            2, permissions: MakiPermission.AddSeries, allRootFolders: false, rootFolderIds: new HashSet<int>());

        var admin = Route(await Controller(db, new TestCurrentUser(1)).Route("manhwa-1", "library", null, default));
        var scoped = Route(await Controller(db, restricted).Route("manhwa-1", "library", null, default));
        var unmatched = Route(await Controller(db, new TestCurrentUser(1)).Route("manga-2", "library", null, default));

        Assert.Equal(new RootFolderRouteDto(routed, ruleId), admin);
        Assert.Equal(new RootFolderRouteDto(null, null), scoped);
        Assert.Equal(new RootFolderRouteDto(null, null), unmatched);
    }

    [Fact]
    public async Task Saving_refuses_a_root_folder_condition_and_a_missing_folder()
    {
        var folder = Folder();
        await using var db = _db.NewContext();
        var controller = Controller(db, new TestCurrentUser(1));

        var circular = await controller.Create(new SaveRootFolderRuleRequest(folder, true,
            [[new AutoTagConditionDto(AutoTagFields.RootFolder, folder.ToString())]]), default);
        var missing = await controller.Create(new SaveRootFolderRuleRequest(folder + 100, true,
            [[new AutoTagConditionDto(AutoTagFields.Type, "manga")]]), default);
        var saved = await controller.Create(new SaveRootFolderRuleRequest(folder, true,
        [
            [new AutoTagConditionDto(AutoTagFields.Type, "manga")],
            [new AutoTagConditionDto(AutoTagFields.Genre, "Ecchi")],
        ]), default);

        Assert.IsType<BadRequestObjectResult>(circular);
        Assert.IsType<BadRequestObjectResult>(missing);
        var dto = (RootFolderRuleDto)((OkObjectResult)saved).Value!;
        Assert.Equal(2, dto.Groups.Count);
    }

    [Fact]
    public async Task Reordering_sets_the_order_rules_are_tried_in()
    {
        var folder = Folder();
        var a = Rule(folder, position: 0, groups: [[new(AutoTagFields.Type, "manga")]]);
        var b = Rule(folder, position: 1, groups: [[new(AutoTagFields.Type, "manhwa")]]);
        var c = Rule(folder, position: 2, groups: [[new(AutoTagFields.Type, "manhua")]]);
        await using var db = _db.NewContext();

        await Controller(db, new TestCurrentUser(1)).Reorder(new ReorderRootFolderRulesRequest([c, a]), default);

        await using var verify = _db.NewContext();
        Assert.Equal([c, a, b], await verify.RootFolderRules.OrderBy(r => r.Position).Select(r => r.Id).ToListAsync());
    }

    [Fact]
    public async Task The_test_endpoint_reads_a_library_series_provenance()
    {
        var folder = Folder();
        var ruleId = Rule(folder, position: 0, groups: [[new(AutoTagFields.Type, "manhwa"), new(AutoTagFields.AddedVia, AutoTagAddedVia.Discover)]]);
        var user = _db.SeedUser("adder");
        var seriesId = _db.SeedSeries("Solo", configure: s => s.Type = "manhwa");
        await using (var db = _db.NewContext())
        {
            db.UserSeriesStates.Add(new UserSeriesState
            {
                UserId = user, SeriesId = seriesId, AddedToLibraryAtUtc = DateTime.UtcNow, AddedFrom = "recommendation",
                UpdatedAt = DateTime.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        await using var read = _db.NewContext();
        var route = Route(await Controller(read, new TestCurrentUser(1)).Test(seriesId, default));

        Assert.Equal(new RootFolderRouteDto(folder, ruleId), route);
    }
}
