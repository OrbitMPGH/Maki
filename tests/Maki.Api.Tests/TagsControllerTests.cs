using Maki.Api.Controllers;
using Maki.Api.Dtos;
using Maki.Core.Entities;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Maki.Api.Tests;

/// <summary>
/// User tag CRUD and bulk assignment. Runs against real SQLite, which is the point for the list
/// queries: reading the many-to-many through the skip navigation compiles to a SQL APPLY that
/// SQLite rejects at runtime, and only a real provider catches that.
/// </summary>
public class TagsControllerTests : IDisposable
{
    private readonly TestDb _db = new();

    public void Dispose() => _db.Dispose();

    private TagsController Controller() => new(_db.NewContext());

    private static T Body<T>(IActionResult result) => (T)((OkObjectResult)result).Value!;

    private async Task<TagDto> CreateTag(string label)
    {
        var result = await Controller().Create(new CreateTagRequest(label), CancellationToken.None);
        return Body<TagDto>(result);
    }

    [Fact]
    public async Task Create_rejects_a_blank_label()
    {
        var result = await Controller().Create(new CreateTagRequest("  "), CancellationToken.None);
        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task Create_returns_the_existing_tag_for_a_duplicate_label()
    {
        var first = await CreateTag("Action");
        var second = await CreateTag("action");

        Assert.Equal(first.Id, second.Id);
        Assert.Equal("Action", second.Label);
        using var db = _db.NewContext();
        Assert.Equal(1, await db.Tags.CountAsync());
    }

    [Fact]
    public async Task Create_assigns_the_same_colour_to_the_same_label_every_time()
    {
        var a = await CreateTag("Favourites");
        using (var db = _db.NewContext())
        {
            db.Tags.RemoveRange(db.Tags);
            await db.SaveChangesAsync();
        }

        var b = await CreateTag("Favourites");
        Assert.Equal(a.Color, b.Color);
    }

    [Fact]
    public async Task List_reports_how_many_series_carry_each_tag()
    {
        var tag = await CreateTag("Action");
        var other = await CreateTag("Hiatus");
        var seriesId = _db.SeedSeries("Berserk");
        _db.SeedSeries("Vagabond");

        using (var db = _db.NewContext())
        {
            db.SeriesTags.Add(new SeriesTag { SeriesId = seriesId, TagId = tag.Id });
            await db.SaveChangesAsync();
        }

        var tags = Body<IEnumerable<TagDto>>(await Controller().List(CancellationToken.None)).ToList();

        Assert.Equal(1, tags.Single(t => t.Id == tag.Id).SeriesCount);
        Assert.Equal(0, tags.Single(t => t.Id == other.Id).SeriesCount);
    }

    [Fact]
    public async Task Update_rejects_a_label_another_tag_already_uses()
    {
        await CreateTag("Action");
        var second = await CreateTag("Drama");

        var result = await Controller().Update(second.Id, new UpdateTagRequest(Label: "action"), CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task Update_renames_and_recolours()
    {
        var tag = await CreateTag("Action");

        var updated = Body<TagDto>(
            await Controller().Update(tag.Id, new UpdateTagRequest("Shounen", "teal"), CancellationToken.None));

        Assert.Equal("Shounen", updated.Label);
        Assert.Equal("teal", updated.Color);
    }

    [Fact]
    public async Task Bulk_adds_and_removes_in_one_pass()
    {
        var keep = await CreateTag("Keep");
        var drop = await CreateTag("Drop");
        var first = _db.SeedSeries("Berserk");
        var second = _db.SeedSeries("Vagabond");

        using (var db = _db.NewContext())
        {
            db.SeriesTags.Add(new SeriesTag { SeriesId = first, TagId = drop.Id });
            await db.SaveChangesAsync();
        }

        await Controller().Bulk(
            new BulkTagRequest([first, second], Add: [keep.Id], Remove: [drop.Id]), CancellationToken.None);

        using var check = _db.NewContext();
        var links = await check.SeriesTags.ToListAsync();
        Assert.Equal(2, links.Count);
        Assert.All(links, l => Assert.Equal(keep.Id, l.TagId));
    }

    [Fact]
    public async Task Bulk_is_idempotent_for_a_tag_the_series_already_has()
    {
        var tag = await CreateTag("Action");
        var seriesId = _db.SeedSeries("Berserk");

        await Controller().Bulk(new BulkTagRequest([seriesId], Add: [tag.Id]), CancellationToken.None);
        await Controller().Bulk(new BulkTagRequest([seriesId], Add: [tag.Id]), CancellationToken.None);

        using var db = _db.NewContext();
        Assert.Equal(1, await db.SeriesTags.CountAsync());
    }

    [Fact]
    public async Task Bulk_rejects_unknown_tag_ids()
    {
        var seriesId = _db.SeedSeries("Berserk");

        var result = await Controller().Bulk(new BulkTagRequest([seriesId], Add: [404]), CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task Delete_unlinks_the_tag_from_every_series()
    {
        var tag = await CreateTag("Action");
        var seriesId = _db.SeedSeries("Berserk");
        await Controller().Bulk(new BulkTagRequest([seriesId], Add: [tag.Id]), CancellationToken.None);

        await Controller().Delete(tag.Id, CancellationToken.None);

        using var db = _db.NewContext();
        Assert.Empty(await db.Tags.ToListAsync());
        Assert.Empty(await db.SeriesTags.ToListAsync());
        Assert.NotNull(await db.Series.FindAsync(seriesId));
    }
}
