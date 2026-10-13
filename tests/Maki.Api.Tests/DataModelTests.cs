using Maki.Core.Entities;
using Maki.Core.Security;
using Maki.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

namespace Maki.Api.Tests;

public class DataModelTests
{
    [Fact]
    public void Every_user_owned_entity_and_series_child_has_a_query_filter()
    {
        using var db = new TestDb().NewContext();
        var model = db.Model;

        var unfiltered = model.GetEntityTypes()
            .Where(t => !t.IsOwned() && t.ClrType != typeof(Series))
            .Where(t => typeof(IUserOwned).IsAssignableFrom(t.ClrType) || RequiresSeries(t))
            .Where(t => t.GetDeclaredQueryFilters().Count == 0)
            .Select(t => t.ClrType.Name)
            .Order()
            .ToList();

        Assert.True(unfiltered.Count == 0, "No query filter: " + string.Join(", ", unfiltered));
    }

    private static bool RequiresSeries(IEntityType type) =>
        type.GetForeignKeys().Any(fk => fk.IsRequired && fk.PrincipalEntityType.ClrType == typeof(Series));

    [Fact]
    public void Tag_counts_exclude_series_in_folders_the_user_was_not_granted()
    {
        using var fixture = new TestDb();
        var visible = fixture.SeedSeries("Visible");
        var hidden = fixture.SeedSeries("Hidden");

        using (var db = fixture.NewContext())
        {
            var tag = new Tag { Label = "Completed" };
            db.Tags.Add(tag);
            db.SaveChanges();
            db.SeriesTags.AddRange(
                new SeriesTag { SeriesId = visible, TagId = tag.Id },
                new SeriesTag { SeriesId = hidden, TagId = tag.Id });
            db.SaveChanges();
        }

        var userId = fixture.SeedUser("limited", MakiPermission.None, allRootFolders: false);
        using (var db = fixture.NewContext())
        {
            var folder = db.Series.IgnoreQueryFilters().Where(s => s.Id == visible).Select(s => s.RootFolderId).Single();
            db.UserRootFolders.Add(new Maki.Data.Identity.UserRootFolder { UserId = userId, RootFolderId = folder });
            db.SaveChanges();
        }

        using var scoped = fixture.NewContext(userId, allRootFolders: false);
        Assert.Equal(1, scoped.SeriesTags.Count());
        Assert.Equal(visible, scoped.SeriesTags.Single().SeriesId);

        using var all = fixture.NewContext();
        Assert.Equal(2, all.SeriesTags.Count());
    }

    [Fact]
    public void Chapter_source_links_follow_the_chapter_visibility()
    {
        using var fixture = new TestDb();
        var visible = fixture.SeedSeries("Visible");
        var hidden = fixture.SeedSeries("Hidden");

        using (var db = fixture.NewContext())
        {
            foreach (var seriesId in new[] { visible, hidden })
            {
                var mapping = new SourceMapping { SeriesId = seriesId, SourceName = "src", SourceSeriesId = "x" + seriesId };
                var chapter = new Chapter { SeriesId = seriesId, Number = 1, Language = "en" };
                db.SourceMappings.Add(mapping);
                db.Chapters.Add(chapter);
                db.SaveChanges();
                db.ChapterSourceLinks.Add(new ChapterSourceLink { ChapterId = chapter.Id, SourceMappingId = mapping.Id });
                db.SaveChanges();
            }
        }

        var userId = fixture.SeedUser("limited", MakiPermission.None, allRootFolders: false);
        using (var db = fixture.NewContext())
        {
            var folder = db.Series.IgnoreQueryFilters().Where(s => s.Id == visible).Select(s => s.RootFolderId).Single();
            db.UserRootFolders.Add(new Maki.Data.Identity.UserRootFolder { UserId = userId, RootFolderId = folder });
            db.SaveChanges();
        }

        using var scoped = fixture.NewContext(userId, allRootFolders: false);
        var links = scoped.ChapterSourceLinks.Include(l => l.Chapter).ToList();
        Assert.Single(links);
        Assert.Equal(visible, links[0].Chapter!.SeriesId);
    }

    [Theory]
    [InlineData("not json", 0)]
    [InlineData("{\"a\":1}", 0)]
    [InlineData("[null]", 1)]
    public void A_malformed_genre_value_does_not_take_the_series_read_down(string raw, int expectedCount)
    {
        using var fixture = new TestDb();
        var id = fixture.SeedSeries("Broken");

        using (var db = fixture.NewContext())
        {
            db.Database.ExecuteSqlRaw("UPDATE Series SET Genres = {0}, Tags = {0} WHERE Id = {1}", raw, id);
        }

        using var read = fixture.NewContext();
        var series = read.Series.Single(s => s.Id == id);
        Assert.Equal(expectedCount, series.Genres.Count);
        read.ChangeTracker.DetectChanges();
    }
}
