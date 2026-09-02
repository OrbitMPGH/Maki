using Maki.Api.Controllers;
using Maki.Core.Entities;
using Maki.Data.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Maki.Api.Tests;

/// <summary>
/// The two writes behind the per-series notification picker and the Library's bulk bar. Both live in
/// the caller's own <see cref="UserSeriesState"/> row, so what these pin is ownership: whose rows get
/// written, which ids are allowed to be written at all, and that a second pass updates rather than
/// piling up rows against the (UserId, SeriesId) unique index.
/// </summary>
public sealed class SeriesNotificationsControllerTests : IDisposable
{
    private readonly TestDb _db = new();

    public void Dispose() => _db.Dispose();

    /// <summary>
    /// Both actions touch nothing but the DbContext, so the other eighteen dependencies are left
    /// null on purpose: an action that grows a use for one will fail here loudly rather than
    /// quietly running against a stub that does nothing.
    /// </summary>
    private static SeriesController Controller(Maki.Data.MakiDbContext db) =>
        new(db, null!, null!, null!, null!, null!, null!, null!, null!, null!, null!, null!, null!,
            null!, null!, null!, null!, null!, null!);

    [Fact]
    public async Task Setting_a_mode_creates_the_row_and_stamps_it_with_the_caller()
    {
        var alice = _db.SeedUser("alice");
        var seriesId = _db.SeedSeries();

        using (var db = _db.NewContext(alice))
        {
            var result = await Controller(db).SetNotificationMode(
                seriesId, new SeriesController.SetSeriesNotificationsRequest("Muted"), default);

            Assert.IsType<OkObjectResult>(result);
        }

        using var check = _db.NewContext();
        var state = Assert.Single(check.UserSeriesStates.IgnoreQueryFilters().ToList());
        Assert.Equal(alice, state.UserId);
        Assert.Equal(SeriesNotificationMode.Muted, state.NotificationMode);
    }

    [Fact]
    public async Task Setting_a_mode_keeps_the_rating_already_in_the_row()
    {
        // One row holds several unrelated per-user facts. Replacing it wholesale, or upserting
        // without loading first, would silently clear the user's score.
        var alice = _db.SeedUser("alice");
        var seriesId = _db.SeedSeries();
        SeedState(alice, seriesId, rating: 9);

        using (var db = _db.NewContext(alice))
        {
            await Controller(db).SetNotificationMode(
                seriesId, new SeriesController.SetSeriesNotificationsRequest("Reading"), default);
        }

        using var check = _db.NewContext();
        var state = Assert.Single(check.UserSeriesStates.IgnoreQueryFilters().ToList());
        Assert.Equal(9, state.Rating);
        Assert.Equal(SeriesNotificationMode.Reading, state.NotificationMode);
    }

    [Fact]
    public async Task An_unknown_mode_is_rejected_rather_than_stored()
    {
        var alice = _db.SeedUser("alice");
        var seriesId = _db.SeedSeries();

        using var db = _db.NewContext(alice);
        var result = await Controller(db).SetNotificationMode(
            seriesId, new SeriesController.SetSeriesNotificationsRequest("Loud"), default);

        Assert.IsType<BadRequestObjectResult>(result);
        Assert.Empty(db.UserSeriesStates.IgnoreQueryFilters().ToList());
    }

    [Fact]
    public async Task A_series_that_does_not_exist_is_a_404_not_an_orphan_row()
    {
        var alice = _db.SeedUser("alice");

        using var db = _db.NewContext(alice);
        var result = await Controller(db).SetNotificationMode(
            9999, new SeriesController.SetSeriesNotificationsRequest("Muted"), default);

        Assert.IsType<NotFoundResult>(result);
        Assert.Empty(db.UserSeriesStates.IgnoreQueryFilters().ToList());
    }

    [Fact]
    public async Task A_bulk_write_lands_only_on_the_caller_and_leaves_the_other_reader_alone()
    {
        var alice = _db.SeedUser("alice");
        var bob = _db.SeedUser("bob");
        var one = _db.SeedSeries("One");
        var two = _db.SeedSeries("Two");
        SeedState(bob, one, mode: SeriesNotificationMode.All);

        int updated;
        using (var db = _db.NewContext(alice))
        {
            updated = Updated(await Controller(db).SetNotificationModeBulk(
                new SeriesController.BulkSeriesNotificationsRequest([one, two], "Muted"), default));
        }

        Assert.Equal(2, updated);

        using var check = _db.NewContext();
        var rows = check.UserSeriesStates.IgnoreQueryFilters().ToList();
        Assert.Equal(3, rows.Count);
        Assert.All(rows.Where(r => r.UserId == alice), r =>
            Assert.Equal(SeriesNotificationMode.Muted, r.NotificationMode));
        Assert.Equal(SeriesNotificationMode.All, rows.Single(r => r.UserId == bob).NotificationMode);
    }

    [Fact]
    public async Task A_second_bulk_write_updates_the_same_rows_rather_than_duplicating_them()
    {
        var alice = _db.SeedUser("alice");
        var seriesId = _db.SeedSeries();

        foreach (var mode in new[] { "Muted", "Reading" })
        {
            using var db = _db.NewContext(alice);
            await Controller(db).SetNotificationModeBulk(
                new SeriesController.BulkSeriesNotificationsRequest([seriesId], mode), default);
        }

        using var check = _db.NewContext();
        var state = Assert.Single(check.UserSeriesStates.IgnoreQueryFilters().ToList());
        Assert.Equal(SeriesNotificationMode.Reading, state.NotificationMode);
    }

    [Fact]
    public async Task Ids_the_caller_cannot_see_are_dropped_rather_than_written()
    {
        // The root-folder query filter is what decides visibility, so a guessed id in the body has
        // to fall out here rather than quietly creating a row against a hidden series.
        var alice = _db.SeedUser("alice");
        var visible = _db.SeedSeries("Visible");

        int updated;
        using (var db = _db.NewContext(alice))
        {
            updated = Updated(await Controller(db).SetNotificationModeBulk(
                new SeriesController.BulkSeriesNotificationsRequest([visible, 9999], "Muted"),
                default));
        }

        Assert.Equal(1, updated);

        using var check = _db.NewContext();
        var state = Assert.Single(check.UserSeriesStates.IgnoreQueryFilters().ToList());
        Assert.Equal(visible, state.SeriesId);
    }

    [Fact]
    public async Task An_empty_selection_is_a_no_op()
    {
        var alice = _db.SeedUser("alice");

        using var db = _db.NewContext(alice);
        var updated = Updated(await Controller(db).SetNotificationModeBulk(
            new SeriesController.BulkSeriesNotificationsRequest([], "Muted"), default));

        Assert.Equal(0, updated);
        Assert.Empty(db.UserSeriesStates.IgnoreQueryFilters().ToList());
    }

    private static int Updated(IActionResult result)
    {
        var value = Assert.IsType<OkObjectResult>(result).Value!;
        return (int)value.GetType().GetProperty("updated")!.GetValue(value)!;
    }

    private void SeedState(
        int userId,
        int seriesId,
        int? rating = null,
        SeriesNotificationMode mode = SeriesNotificationMode.Default)
    {
        using var db = _db.NewContext();
        db.UserSeriesStates.Add(new UserSeriesState
        {
            UserId = userId,
            SeriesId = seriesId,
            Rating = rating,
            NotificationMode = mode,
            UpdatedAt = new DateTime(2026, 3, 1, 12, 0, 0, DateTimeKind.Utc),
        });
        db.SaveChanges();
    }
}
