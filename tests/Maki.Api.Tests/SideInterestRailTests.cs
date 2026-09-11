using Maki.Api.Services;
using Maki.Core.Entities;
using Maki.Data.Identity;
using Maki.Metadata.Embedding;
using static Maki.Api.Services.SideInterestRailService;

namespace Maki.Api.Tests;

public class SideInterestRailTests
{
    private static Seed Work(long id, string genre, params string[] tags) => new(id, $"Work {id}", [genre], tags, tags);

    [Fact]
    public void Minority_interests_get_rows_in_an_eighty_percent_romcom_library()
    {
        var library = Enumerable.Range(1, 80).Select(i => Work(i, "Romance", "School Life"))
            .Concat(Enumerable.Range(81, 10).Select(i => Work(i, "Mystery", "Detectives")))
            .Concat(Enumerable.Range(91, 10).Select(i => Work(i, "Adventure", "Space"))).ToList();
        var rows = SelectInterests(library);
        Assert.Equal(2, rows.Count);
        Assert.Contains(rows, r => r.Name == "Detectives");
        Assert.Contains(rows, r => r.Name == "Space");
        Assert.All(rows.SelectMany(r => r.Seeds), s => Assert.True(s.Id > 80));
    }

    [Fact]
    public void A_rare_tag_on_majority_titles_does_not_outrank_a_separate_interest()
    {
        var library = Enumerable.Range(1, 16).Select(i => Work(i, "Romance", "School", i < 5 ? "Clubs" : "School"))
            .Concat(Enumerable.Range(17, 4).Select(i => Work(i, "Mystery", "Detectives"))).ToList();
        Assert.Equal("Detectives", SelectInterests(library)[0].Name);
    }

    [Fact]
    public void Duplicate_works_and_tags_cannot_turn_a_one_off_into_an_interest()
    {
        var library = Enumerable.Range(1, 8).Select(i => Work(i, "Romance"))
            .Concat([Work(9, "Mystery", "Space", "space"), Work(9, "Mystery", "Space")]).ToList();
        Assert.Empty(SelectInterests(library));
    }

    [Fact]
    public void Identical_supporting_titles_do_not_produce_multiple_rows()
    {
        var library = Enumerable.Range(1, 8).Select(i => Work(i, "Romance"))
            .Concat([Work(9, "Mystery", "Detectives", "Crime"), Work(10, "Mystery", "Detectives", "Crime")]).ToList();
        Assert.Single(SelectInterests(library));
        Assert.Equal(SelectInterests(library)[0].Name, SelectInterests(library.AsEnumerable().Reverse().ToList())[0].Name);
    }

    [Fact]
    public async Task Fully_incognito_and_disliked_titles_do_not_supply_themes()
    {
        using var test = new TestDb();
        test.SeedUser();
        test.SeedSeries("Visible", configure: s => { s.MangaBakaId = 1; s.Tags = ["Space"]; });
        test.SeedSeries("Private", configure: s => { s.MangaBakaId = 2; s.Incognito = IncognitoMode.Full; });
        test.SeedSeries("Above ceiling", configure: s => { s.MangaBakaId = 4; s.ContentRating = "pornographic"; });
        var disliked = test.SeedSeries("Disliked", configure: s => s.MangaBakaId = 3);
        using (var db = test.NewContext())
        {
            db.UserSeriesStates.Add(new UserSeriesState { UserId = 1, SeriesId = disliked, Rating = 2 });
            await db.SaveChangesAsync();
        }
        var service = new SideInterestRailService(test.ScopeFactory(), null!, null!, null!);
        var seeds = await service.ReadSeedsAsync(new TestCurrentUser(1), default);
        Assert.Equal(1, Assert.Single(seeds).Id);
    }

    [Fact]
    public void Only_non_spoiler_core_story_tags_can_name_a_row()
    {
        var vocabulary = new Dictionary<int, TagInfo>
        {
            [1] = new("Detectives", 10, false, "Occupations"),
            [2] = new("Calm Male Lead", 10, false, "Character Traits"),
            [3] = new("Space", 10, false, "Settings"),
            [4] = new("Secret identity", 10, true, "Themes"),
            [5] = new("Unclassified", 10, false),
        };
        var tags = TagMath.Pack([(1, TagMath.Core), (2, TagMath.Core), (3, TagMath.Defining),
            (4, TagMath.Core), (5, TagMath.Core), (6, TagMath.Core)]);
        Assert.Equal(["Detectives"], CoreStoryTags(tags, vocabulary));
        Assert.Empty(CoreStoryTags(null, vocabulary));
    }

    [Fact]
    public void A_seed_must_mark_the_shared_theme_as_core()
    {
        var library = Enumerable.Range(1, 8).Select(i => Work(i, "Romance", "School"))
            .Concat([Work(9, "Mystery", "Detectives"), Work(10, "Mystery", "Detectives"),
                Work(11, "Mystery", "Detectives") with { CoreTags = [] }]).ToList();
        var interest = Assert.Single(SelectInterests(library));
        Assert.Equal(new long[] { 9, 10 }, interest.Seeds.Select(s => s.Id));
        library[9] = library[9] with { CoreTags = [] };
        Assert.Empty(SelectInterests(library));
    }

    [Fact]
    public void Common_tags_do_not_become_minority_themes_through_few_core_assignments()
    {
        var library = Enumerable.Range(1, 10).Select(i => Work(i, "Romance", "School")
            with { CoreTags = i <= 2 ? ["School"] : [] }).ToList();
        Assert.Empty(SelectInterests(library));
    }
}
