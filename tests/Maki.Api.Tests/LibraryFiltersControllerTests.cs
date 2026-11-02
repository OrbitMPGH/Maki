using System.Text.Json;
using Maki.Api.Controllers;
using Maki.Api.Dtos;
using Maki.Core.Entities;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;

namespace Maki.Api.Tests;

/// <summary>Saved Library filter presets — round-tripping the spec and surviving a bad one.</summary>
public class LibraryFiltersControllerTests : IDisposable
{
    /// <summary>Presets are private to a user now, so the controller under test needs one.</summary>
    private const int TestUser = 1;

    private readonly TestDb _db = new();

    public void Dispose() => _db.Dispose();

    private LibraryFiltersController Controller() =>
        new(new TestLocalizer(), _db.NewContext(TestUser), NullLogger<LibraryFiltersController>.Instance);

    private static T Body<T>(IActionResult result) => (T)((OkObjectResult)result).Value!;

    [Fact]
    public async Task Create_rejects_a_blank_name()
    {
        var result = await Controller().Create(
            new SaveFilterRequest("  ", new LibraryFilterSpec()), CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task Create_round_trips_every_field_of_the_spec()
    {
        var spec = new LibraryFilterSpec(
            Query: "berserk", Status: "Ongoing", TagIds: [1, 2], TagMatch: "all",
            Monitored: "monitored", Completeness: "behind", Sort: "read",
            Genres: ["Action", "Drama"], GenreMatch: "all",
            MetadataTags: ["Revenge"], MetadataTagMatch: "all",
            ReadMin: 10, ReadMax: 90,
            ChapterMin: 1, ChapterMax: 20, ChapterMode: "total",
            ContentRatings: ["safe"],
            Sources: ["mangadex", "asura"], SourceMatch: "all", SourceState: "hasDisabled",
            FileSources: ["mangapill"], FileSourceMatch: "all", QualityProfile: "7",
            Types: [SeriesTypes.Manhwa, SeriesTypes.Manhua],
            ExcludeTagIds: [4, 5], ExcludeGenres: ["Horror"], UntaggedOnly: true,
            SortDir: "asc",
            RatingState: "rated", RatingMin: 7, RatingMax: 9, CommunityRatingMin: 7.5, CommunityRatingMax: 9.5);

        var created = Body<SavedFilterDto>(
            await Controller().Create(new SaveFilterRequest("Ongoing & behind", spec), CancellationToken.None));
        var listed = Body<IEnumerable<SavedFilterDto>>(await Controller().List(CancellationToken.None)).Single();

        Assert.Equal("Ongoing & behind", created.Name);
        // Compared as JSON: the record's generated equality compares its lists by reference.
        Assert.Equal(JsonSerializer.Serialize(spec), JsonSerializer.Serialize(listed.Spec));
    }

    [Theory]
    // camelCase is what current builds store; PascalCase is what the first release of saved
    // filters wrote, and it has to keep applying rather than silently reading as "no filter".
    [InlineData("""{"query":"","status":"Ongoing","tagIds":[3],"tagMatch":"any","monitored":"all","completeness":"behind","sort":"added"}""")]
    [InlineData("""{"Query":"","Status":"Ongoing","TagIds":[3],"TagMatch":"any","Monitored":"all","Completeness":"behind","Sort":"added"}""")]
    public async Task A_spec_saved_before_the_newer_fields_existed_reads_back_with_defaults(string stored)
    {
        using (var db = _db.NewContext())
        {
            db.SavedFilters.Add(new SavedFilter { UserId = 1, Name = "Old preset", Spec = stored, Created = DateTime.UtcNow });
            await db.SaveChangesAsync();
        }

        var listed = Body<IEnumerable<SavedFilterDto>>(await Controller().List(CancellationToken.None)).Single();

        Assert.Equal("Ongoing", listed.Spec.Status);
        Assert.Equal([3], listed.Spec.TagIds);
        Assert.Null(listed.Spec.Genres);
        Assert.Equal("any", listed.Spec.GenreMatch);
        Assert.Equal(0, listed.Spec.ReadMin);
        Assert.Equal("all", listed.Spec.RatingState);
        Assert.Equal(10, listed.Spec.CommunityRatingMax);
        Assert.Equal(100, listed.Spec.ReadMax);
        // Absent source filters have to read as "unfiltered", not as an empty list — the grid
        // treats an empty selection as "don't filter" but a present-and-empty one is the same
        // shape, so the defaults are what keep an old preset from matching nothing.
        Assert.Null(listed.Spec.Sources);
        Assert.Null(listed.Spec.FileSources);
        Assert.Equal("all", listed.Spec.SourceState);
        Assert.Equal("any", listed.Spec.SourceMatch);
        Assert.Equal("all", listed.Spec.QualityProfile);
        // Same reasoning for the chapter window: null ends mean "unbounded", and a 0 would
        // silently turn an old preset into a filter with a real lower bound.
        Assert.Null(listed.Spec.ChapterMin);
        Assert.Null(listed.Spec.ChapterMax);
        Assert.Equal("downloaded", listed.Spec.ChapterMode);
        Assert.Equal("", listed.Spec.SortDir);
        Assert.Null(listed.Spec.ExcludeTagIds);
        Assert.Null(listed.Spec.ExcludeGenres);
        Assert.False(listed.Spec.UntaggedOnly);
    }

    [Fact]
    public async Task Update_replaces_the_spec()
    {
        var created = Body<SavedFilterDto>(await Controller().Create(
            new SaveFilterRequest("Preset", new LibraryFilterSpec(Status: "Ongoing")), CancellationToken.None));

        var updated = Body<SavedFilterDto>(await Controller().Update(
            created.Id, new SaveFilterRequest("Preset", new LibraryFilterSpec(Status: "Completed")),
            CancellationToken.None));

        Assert.Equal("Completed", updated.Spec.Status);
    }

    [Fact]
    public async Task An_unreadable_spec_serves_an_empty_one_rather_than_failing_the_list()
    {
        using (var db = _db.NewContext())
        {
            db.SavedFilters.Add(new SavedFilter { UserId = 1, Name = "Broken", Spec = "not json", Created = DateTime.UtcNow });
            await db.SaveChangesAsync();
        }

        var listed = Body<IEnumerable<SavedFilterDto>>(await Controller().List(CancellationToken.None)).Single();

        Assert.Equal("Broken", listed.Name);
        Assert.Equal("all", listed.Spec.Status);
    }

    [Fact]
    public async Task Delete_removes_the_preset()
    {
        var created = Body<SavedFilterDto>(await Controller().Create(
            new SaveFilterRequest("Preset", new LibraryFilterSpec()), CancellationToken.None));

        await Controller().Delete(created.Id, CancellationToken.None);

        Assert.Empty(Body<IEnumerable<SavedFilterDto>>(await Controller().List(CancellationToken.None)));
    }

    [Fact]
    public async Task Delete_404s_for_an_unknown_id()
    {
        Assert.IsType<NotFoundResult>(await Controller().Delete(404, CancellationToken.None));
    }
}
