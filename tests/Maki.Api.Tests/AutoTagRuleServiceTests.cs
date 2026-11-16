using Maki.Api.Services;
using Maki.Core.Entities;
using Maki.Core.Security;
using Maki.Core.Tags;
using Maki.Data;
using Maki.Data.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Maki.Api.Tests;

public sealed class AutoTagRuleServiceTests : IDisposable
{
    private readonly TestDb _db = new();

    public void Dispose() => _db.Dispose();

    private static AutoTagRuleService Service(MakiDbContext db) =>
        new(db, new TagService(db), NullLogger<AutoTagRuleService>.Instance);

    private int Tag(string label)
    {
        using var db = _db.NewContext();
        var tag = new Tag { Label = label };
        db.Tags.Add(tag);
        db.SaveChanges();
        return tag.Id;
    }

    private AutoTagRule Rule(int tagId, bool enabled = true, params AutoTagCondition[] conditions)
    {
        using var db = _db.NewContext();
        var rule = new AutoTagRule { TagId = tagId, Enabled = enabled, ConditionsJson = AutoTagRules.Serialize(conditions) };
        db.AutoTagRules.Add(rule);
        db.SaveChanges();
        return rule;
    }

    private int Series(string title, string type = "manga", params string[] genres) =>
        _db.SeedSeries(title, configure: s =>
        {
            s.Type = type;
            s.Genres = [.. genres];
        });

    private List<int> TagsOf(int seriesId)
    {
        using var db = _db.NewContext();
        return db.SeriesTags.Where(t => t.SeriesId == seriesId).Select(t => t.TagId).ToList();
    }

    [Fact]
    public async Task A_new_series_gets_the_tag_of_every_enabled_rule_it_matches()
    {
        var korean = Tag("Korean");
        var fantasy = Tag("Fantasy picks");
        var off = Tag("Unused");
        var horror = Tag("Horror");
        Rule(korean, conditions: [new(AutoTagFields.Type, "manhwa")]);
        Rule(fantasy, conditions: [new(AutoTagFields.Genre, "fantasy"), new(AutoTagFields.AddedVia, AutoTagAddedVia.ImportList)]);
        Rule(off, enabled: false, conditions: [new(AutoTagFields.Type, "manhwa")]);
        Rule(horror, conditions: [new(AutoTagFields.Genre, "horror")]);
        var seriesId = Series("Solo", "manhwa", "Fantasy");

        await using (var db = _db.NewContext())
        {
            var series = await db.Series.SingleAsync(s => s.Id == seriesId);
            await Service(db).ApplyOnAddAsync(series, AutoTagAddedVia.ImportList, addedBy: null, default);
        }

        Assert.Equal([korean, fantasy], TagsOf(seriesId).Order());
    }

    [Fact]
    public async Task Applying_to_existing_series_tags_only_matches_without_the_tag_and_reads_provenance()
    {
        var tag = Tag("Mine");
        var user = _db.SeedUser("reader");
        var mine = Series("Mine");
        var other = Series("Other");
        var already = Series("Already");
        using (var db = _db.NewContext())
        {
            db.UserSeriesStates.Add(new UserSeriesState
            {
                UserId = user, SeriesId = mine, AddedToLibraryAtUtc = DateTime.UtcNow, AddedFrom = "recommendation",
                UpdatedAt = DateTime.UtcNow,
            });
            db.UserSeriesStates.Add(new UserSeriesState
            {
                UserId = user, SeriesId = already, AddedToLibraryAtUtc = DateTime.UtcNow, AddedFrom = "recommendation",
                UpdatedAt = DateTime.UtcNow,
            });
            db.SeriesTags.Add(new SeriesTag { SeriesId = already, TagId = tag });
            db.SaveChanges();
        }

        var rule = Rule(tag, conditions:
            [new(AutoTagFields.AddedBy, user.ToString()), new(AutoTagFields.AddedVia, AutoTagAddedVia.Discover)]);

        await using (var db = _db.NewContext())
        {
            Assert.Equal([mine], await Service(db).MatchingExistingAsync(rule, default));
            Assert.Equal(1, await Service(db).ApplyToExistingAsync(rule, default));
        }

        Assert.Equal([tag], TagsOf(mine));
        Assert.Empty(TagsOf(other));
    }

    [Fact]
    public async Task A_restricted_caller_only_tags_the_series_they_can_see()
    {
        var tag = Tag("Manga");
        var user = _db.SeedUser("restricted", MakiPermission.ManageTags, allRootFolders: false);
        Series("Hidden");
        var rule = Rule(tag, conditions: [new(AutoTagFields.Type, "manga")]);

        await using var db = _db.NewContext(user, allRootFolders: false);

        Assert.Equal(0, await Service(db).ApplyToExistingAsync(rule, default));
    }
}
