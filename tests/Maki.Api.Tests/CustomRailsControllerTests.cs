using Maki.Api.Controllers;
using Maki.Api.Dtos;
using Maki.Api.Services;
using Maki.Core.Configuration;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;

namespace Maki.Api.Tests;

/// <summary>
/// Custom rails: CRUD, per-user isolation, the placement rules that keep a rail's source sane on
/// the page it draws on, and that a rail never crosses into the Library/Discover preset controllers
/// (and vice versa) despite sharing <c>SavedFilters</c>. <see cref="CustomRailService.ItemsAsync"/>
/// is not exercised: no action under test calls it, so the service is built with every dependency
/// but the db context and current user left null.
/// </summary>
public sealed class CustomRailsControllerTests : IDisposable
{
    private readonly TestDb _db = new();

    public void Dispose() => _db.Dispose();

    private CustomRailsController Controller(int userId)
    {
        var db = _db.NewContext(userId);
        var service = new CustomRailService(db, new TestCurrentUser(userId), null!, null!, null!, null!, null!, null!);
        return new CustomRailsController(new TestLocalizer(), db, service);
    }

    private static T Body<T>(IActionResult result) => (T)((OkObjectResult)result).Value!;

    private static SaveCustomRailRequest Request(
        string name = "My rail", string placement = CustomRailPlacements.Home, CustomRailSpec? spec = null) =>
        new(name, placement, spec ?? new CustomRailSpec(Source: CustomRailSources.Catalogue));

    [Fact]
    public async Task Create_rejects_a_blank_name()
    {
        var alice = _db.SeedUser("alice");

        var result = await Controller(alice).Create(Request(name: "   "), CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task Create_rejects_an_unknown_placement()
    {
        var alice = _db.SeedUser("alice");

        var result = await Controller(alice).Create(Request(placement: "nowhere"), CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task Create_round_trips_the_name_placement_and_spec()
    {
        var alice = _db.SeedUser("alice");
        var spec = new CustomRailSpec(Source: CustomRailSources.Catalogue, ExcludeOwned: false);

        var created = Body<CustomRailDto>(await Controller(alice).Create(
            Request("Ongoing action", CustomRailPlacements.Discover, spec), CancellationToken.None));

        Assert.Equal("Ongoing action", created.Name);
        Assert.Equal(CustomRailPlacements.Discover, created.Placement);
        Assert.Equal(CustomRailSources.Catalogue, created.Spec.Source);
        Assert.False(created.Spec.ExcludeOwned);
    }

    [Fact]
    public async Task Create_rejects_a_library_source_on_a_discover_placement()
    {
        var alice = _db.SeedUser("alice");
        var spec = new CustomRailSpec(Source: CustomRailSources.Library);

        var result = await Controller(alice).Create(
            Request(placement: CustomRailPlacements.Discover, spec: spec), CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task Create_allows_a_library_source_on_a_home_placement()
    {
        var alice = _db.SeedUser("alice");
        var spec = new CustomRailSpec(Source: CustomRailSources.Library);

        var created = Body<CustomRailDto>(await Controller(alice).Create(
            Request(placement: CustomRailPlacements.Home, spec: spec), CancellationToken.None));

        Assert.Equal(CustomRailSources.Library, created.Spec.Source);
    }

    [Fact]
    public async Task Update_rejects_moving_a_library_rail_to_discover()
    {
        var alice = _db.SeedUser("alice");
        var created = Body<CustomRailDto>(await Controller(alice).Create(
            Request(placement: CustomRailPlacements.Home, spec: new CustomRailSpec(Source: CustomRailSources.Library)),
            CancellationToken.None));

        var result = await Controller(alice).Update(
            created.Id, new SaveCustomRailRequest(null, CustomRailPlacements.Discover, null), CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task Update_moves_a_rail_between_placements_and_resets_its_sort_order()
    {
        var alice = _db.SeedUser("alice");
        var first = Body<CustomRailDto>(await Controller(alice).Create(
            Request(placement: CustomRailPlacements.Discover), CancellationToken.None));
        await Controller(alice).Create(Request(placement: CustomRailPlacements.Discover), CancellationToken.None);

        var moved = Body<CustomRailDto>(await Controller(alice).Update(
            first.Id, new SaveCustomRailRequest(null, CustomRailPlacements.Home, null), CancellationToken.None));

        Assert.Equal(CustomRailPlacements.Home, moved.Placement);
        Assert.Equal(0, moved.SortOrder); // the only Home rail now
    }

    [Fact]
    public async Task Delete_removes_the_rail()
    {
        var alice = _db.SeedUser("alice");
        var created = Body<CustomRailDto>(await Controller(alice).Create(Request(), CancellationToken.None));

        await Controller(alice).Delete(created.Id, CancellationToken.None);

        Assert.Empty(Body<IEnumerable<CustomRailDto>>(await Controller(alice).List(null, CancellationToken.None)));
    }

    [Fact]
    public async Task Delete_404s_for_an_unknown_id()
    {
        var alice = _db.SeedUser("alice");

        Assert.IsType<NotFoundResult>(await Controller(alice).Delete(404, CancellationToken.None));
    }

    [Fact]
    public async Task Create_is_capped_at_thirty_rails()
    {
        var alice = _db.SeedUser("alice");
        var controller = Controller(alice);
        for (var i = 0; i < 30; i++)
        {
            Assert.IsType<OkObjectResult>(await controller.Create(Request($"Rail {i}"), CancellationToken.None));
        }

        var result = await controller.Create(Request("One too many"), CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task Reorder_applies_the_requested_order_and_appends_unnamed_ids_after_it()
    {
        var alice = _db.SeedUser("alice");
        var controller = Controller(alice);
        var a = Body<CustomRailDto>(await controller.Create(Request("A", CustomRailPlacements.Discover), CancellationToken.None));
        var b = Body<CustomRailDto>(await controller.Create(Request("B", CustomRailPlacements.Discover), CancellationToken.None));
        var c = Body<CustomRailDto>(await controller.Create(Request("C", CustomRailPlacements.Discover), CancellationToken.None));

        var reordered = Body<IEnumerable<CustomRailDto>>(await controller.Reorder(
            new ReorderCustomRailsRequest(CustomRailPlacements.Discover, [c.Id, a.Id]), CancellationToken.None)).ToList();

        Assert.Equal([c.Id, a.Id, b.Id], reordered.Select(r => r.Id));
        Assert.Equal([0, 1, 2], reordered.Select(r => r.SortOrder));
    }

    [Fact]
    public async Task Reorder_ignores_ids_that_are_not_the_callers_rails()
    {
        var alice = _db.SeedUser("alice");
        var bob = _db.SeedUser("bob");
        var mine = Body<CustomRailDto>(await Controller(alice).Create(
            Request(placement: CustomRailPlacements.Discover), CancellationToken.None));
        var theirs = Body<CustomRailDto>(await Controller(bob).Create(
            Request(placement: CustomRailPlacements.Discover), CancellationToken.None));

        var reordered = Body<IEnumerable<CustomRailDto>>(await Controller(alice).Reorder(
            new ReorderCustomRailsRequest(CustomRailPlacements.Discover, [theirs.Id, mine.Id]), CancellationToken.None));

        Assert.Equal([mine.Id], reordered.Select(r => r.Id));
    }

    [Fact]
    public async Task List_filters_by_placement()
    {
        var alice = _db.SeedUser("alice");
        var controller = Controller(alice);
        await controller.Create(Request("Home rail", CustomRailPlacements.Home), CancellationToken.None);
        await controller.Create(Request("Discover rail", CustomRailPlacements.Discover), CancellationToken.None);

        var home = Body<IEnumerable<CustomRailDto>>(
            await controller.List(CustomRailPlacements.Home, CancellationToken.None));

        Assert.Equal("Home rail", Assert.Single(home).Name);
    }

    [Fact]
    public async Task A_users_rails_are_invisible_to_and_unreachable_by_another_user()
    {
        var alice = _db.SeedUser("alice");
        var bob = _db.SeedUser("bob");
        var created = Body<CustomRailDto>(await Controller(alice).Create(Request(), CancellationToken.None));

        Assert.Empty(Body<IEnumerable<CustomRailDto>>(await Controller(bob).List(null, CancellationToken.None)));
        Assert.IsType<NotFoundResult>(await Controller(bob).Update(
            created.Id, new SaveCustomRailRequest("Hijacked", null, null), CancellationToken.None));
        Assert.IsType<NotFoundResult>(await Controller(bob).Delete(created.Id, CancellationToken.None));

        // The rail is untouched from alice's side.
        Assert.Equal("My rail", Assert.Single(
            Body<IEnumerable<CustomRailDto>>(await Controller(alice).List(null, CancellationToken.None))).Name);
    }

    [Fact]
    public async Task Custom_rails_do_not_appear_in_the_library_or_discover_preset_lists_and_vice_versa()
    {
        var alice = _db.SeedUser("alice");
        await Controller(alice).Create(Request("A home rail", CustomRailPlacements.Home), CancellationToken.None);
        await Controller(alice).Create(Request("A discover rail", CustomRailPlacements.Discover), CancellationToken.None);

        using var db = _db.NewContext(alice);
        var libraryController = new LibraryFiltersController(
            new TestLocalizer(), db, NullLogger<LibraryFiltersController>.Instance);
        var discoverController = new DiscoverFiltersController(new TestLocalizer(), db);

        await libraryController.Create(
            new SaveFilterRequest("A library preset", new LibraryFilterSpec()), CancellationToken.None);
        await discoverController.Create(
            new SaveDiscoverFilterRequest("A discover preset", null), CancellationToken.None);

        var libraryPresets = Body<IEnumerable<SavedFilterDto>>(await libraryController.List(CancellationToken.None));
        var discoverPresets = Body<IEnumerable<DiscoverFilterDto>>(await discoverController.List(CancellationToken.None));
        var rails = Body<IEnumerable<CustomRailDto>>(await Controller(alice).List(null, CancellationToken.None));

        Assert.Equal("A library preset", Assert.Single(libraryPresets).Name);
        Assert.Equal("A discover preset", Assert.Single(discoverPresets).Name);
        Assert.Equal(2, rails.Count());
    }
}
