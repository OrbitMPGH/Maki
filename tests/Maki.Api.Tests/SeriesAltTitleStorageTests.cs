using Maki.Core.Entities;
using Microsoft.EntityFrameworkCore;

namespace Maki.Api.Tests;

/// <summary>
/// How <c>Series.AltTitles</c> survives the round trip through SQLite, including from the bare
/// <c>["a","b"]</c> shape the column held before alt titles carried a language.
/// </summary>
public sealed class SeriesAltTitleStorageTests : IDisposable
{
    private readonly TestDb _db = new();

    public void Dispose() => _db.Dispose();

    private int SeedSeries()
    {
        using var db = _db.NewContext();
        var rootFolder = new RootFolder { Path = "/tmp/maki-alttitles" };
        db.RootFolders.Add(rootFolder);
        db.SaveChanges();

        var series = new Series
        {
            Title = "Berserk",
            SortTitle = "berserk",
            FolderName = "Berserk",
            RootFolderId = rootFolder.Id
        };
        db.Series.Add(series);
        db.SaveChanges();
        return series.Id;
    }

    private void WriteRawAltTitles(int seriesId, string json)
    {
        using var db = _db.NewContext();
        db.Database.ExecuteSql($"UPDATE Series SET AltTitles = {json} WHERE Id = {seriesId}");
    }

    [Fact]
    public void Language_tags_round_trip()
    {
        var id = SeedSeries();

        using (var db = _db.NewContext())
        {
            var series = db.Series.Single(s => s.Id == id);
            series.AltTitles = [new LocalizedTitle("ベルセルク", "ja"), new LocalizedTitle("Berserk Deluxe", null)];
            db.SaveChanges();
        }

        using var check = _db.NewContext();
        Assert.Equal(
            [new LocalizedTitle("ベルセルク", "ja"), new LocalizedTitle("Berserk Deluxe", null)],
            check.Series.Single(s => s.Id == id).AltTitles);
    }

    [Fact]
    public void The_old_bare_string_shape_still_reads()
    {
        // What every row held before the language tag existed. The AddSeriesAltTitleLanguages
        // migration rewrites these, so in a live database there are none left — but a database
        // restored from an older backup still has them, and throwing here is a crash on read
        // rather than a missing field.
        var id = SeedSeries();
        WriteRawAltTitles(id, """["Berserk Deluxe","ベルセルク"]""");

        using var db = _db.NewContext();
        Assert.Equal(
            [new LocalizedTitle("Berserk Deluxe", null), new LocalizedTitle("ベルセルク", null)],
            db.Series.Single(s => s.Id == id).AltTitles);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json at all")]
    [InlineData("{}")]
    public void An_unreadable_value_reads_as_no_alt_titles(string stored)
    {
        var id = SeedSeries();
        WriteRawAltTitles(id, stored);

        using var db = _db.NewContext();
        Assert.Empty(db.Series.Single(s => s.Id == id).AltTitles);
    }
}
