using Maki.Api.Dtos;
using Maki.Core.Entities;
using Maki.Core.Metadata;
using Microsoft.EntityFrameworkCore;

namespace Maki.Api.Tests;

/// <summary>
/// Provider tags gained a weight bucket and a taxonomy path without a migration, which only works
/// because the column reads both shapes it has ever held. That claim is the risky part of the
/// change, so it is pinned here against a real SQLite round-trip rather than asserted in a comment.
/// </summary>
public class MetadataTagStorageTests
{
    [Fact]
    public async Task A_row_written_before_facets_existed_still_loads()
    {
        using var testDb = new TestDb();
        await using (var db = testDb.NewContext())
        {
            db.Series.Add(new Series { Title = "Old Entry", FolderName = "old", RootFolderId = Root(db) });
            await db.SaveChangesAsync();
            // The pre-facets shape, written straight past the converter.
            await db.Database.ExecuteSqlRawAsync(
                """UPDATE Series SET Tags = '["Pirates","Amnesia"]'""");
        }

        await using var read = testDb.NewContext();
        var series = await read.Series.SingleAsync();

        Assert.Equal(["Pirates", "Amnesia"], series.Tags.Select(t => t.Name));
        // Unknown, not "incidental": nothing recorded a bucket for these.
        Assert.All(series.Tags, t => Assert.Null(t.Weight));
        Assert.All(series.Tags, t => Assert.Null(t.Path));
    }

    [Fact]
    public async Task Facets_survive_a_round_trip()
    {
        using var testDb = new TestDb();
        await using (var db = testDb.NewContext())
        {
            db.Series.Add(new Series
            {
                Title = "New Entry",
                FolderName = "new",
                RootFolderId = Root(db),
                Tags =
                [
                    new MetadataTag("Shounen", "defining", "Audience Demographics > Male Oriented > Shounen"),
                    new MetadataTag("Swordplay"),
                ],
            });
            await db.SaveChangesAsync();
        }

        await using var read = testDb.NewContext();
        var tags = (await read.Series.SingleAsync()).Tags;

        Assert.Equal("defining", tags[0].Weight);
        Assert.Equal("Audience Demographics > Male Oriented", tags[0].Category);
        Assert.Null(tags[1].Weight);
    }

    [Fact]
    public async Task A_malformed_column_costs_the_tags_and_not_the_series()
    {
        using var testDb = new TestDb();
        await using (var db = testDb.NewContext())
        {
            db.Series.Add(new Series { Title = "Broken", FolderName = "broken", RootFolderId = Root(db) });
            await db.SaveChangesAsync();
            await db.Database.ExecuteSqlRawAsync("""UPDATE Series SET Tags = 'not json'""");
        }

        await using var read = testDb.NewContext();
        var series = await read.Series.SingleAsync();

        Assert.Empty(series.Tags);
        Assert.Equal("Broken", series.Title);
    }

    [Fact]
    public void Groups_run_most_important_first_and_skip_empty_buckets()
    {
        List<MetadataTag> tags =
        [
            new("Swordplay", "incidental", "Activities > Physical Activities > Swordplay"),
            new("Pirates", "core", "Settings > Pirates"),
            new("Shounen", "defining", null),
        ];

        var groups = MetadataTagGroupDto.From(tags);

        Assert.Equal(["Core", "Defining", "Incidental"], groups.Select(g => g.Label));
        Assert.Equal("Settings", groups[0].Tags[0].Path);
        // No "recurrent" section, because nothing is recurrent.
        Assert.DoesNotContain(groups, g => g.Label == "Recurrent");
    }

    [Fact]
    public void Tags_with_no_weight_group_under_Other_and_sort_last()
    {
        List<MetadataTag> tags = [new("Orphan"), new("Pirates", "core")];

        var groups = MetadataTagGroupDto.From(tags);

        Assert.Equal(["Core", "Other"], groups.Select(g => g.Label));
        Assert.Equal("unknown", groups[1].Weight);
    }

    /// <summary>A root folder to hang a series off; Series.RootFolderId is a real foreign key.</summary>
    private static int Root(Maki.Data.MakiDbContext db)
    {
        var root = new RootFolder { Path = "/manga" };
        db.RootFolders.Add(root);
        db.SaveChanges();
        return root.Id;
    }
}
