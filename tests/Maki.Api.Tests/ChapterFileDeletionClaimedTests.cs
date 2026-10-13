using Maki.Api.Services;
using Maki.Core.Entities;

namespace Maki.Api.Tests;

/// <summary>
/// <see cref="ChapterFileDeletion.ClaimedAsync"/> reads only the rows whose name
/// could match, and must still find a row that names the file another way.
/// </summary>
public class ChapterFileDeletionClaimedTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly string _root = Path.Combine(Path.GetTempPath(), "maki-claimed-" + Guid.NewGuid().ToString("N"));

    public void Dispose() => _db.Dispose();

    private int AddFile(Maki.Data.MakiDbContext db, int rootId, string title, string relativePath)
    {
        var series = new Series { Title = title, SortTitle = title.ToLowerInvariant(), FolderName = title, RootFolderId = rootId };
        db.Series.Add(series);
        db.SaveChanges();
        var file = new ChapterFile { SeriesId = series.Id, RelativePath = relativePath, DateAdded = DateTime.UtcNow };
        db.ChapterFiles.Add(file);
        db.SaveChanges();
        return file.Id;
    }

    [Fact]
    public async Task A_nested_root_row_spelling_the_name_in_another_case_still_claims_the_file()
    {
        int leaving;
        using (var db = _db.NewContext())
        {
            var outer = new RootFolder { Path = _root };
            var inner = new RootFolder { Path = Path.Combine(_root, "shonen") };
            db.RootFolders.AddRange(outer, inner);
            db.SaveChanges();
            leaving = AddFile(db, outer.Id, "Mine", Path.Combine("shonen", "One", "Ch_1 [100%] Ü.cbz"));
            AddFile(db, inner.Id, "Theirs", "One/ch_1 [100%] ü.cbz");
            AddFile(db, inner.Id, "Decoy", "One/Chx1 [100%] ü.cbz");
        }

        var wanted = Path.Combine(_root, "shonen", "One", "Ch_1 [100%] Ü.cbz");
        using (var db = _db.NewContext())
        {
            var claimed = await TestDeletion.For(db).ClaimedAsync([wanted], new HashSet<int> { leaving }, default);
            Assert.Contains(wanted, claimed);
        }

        using (var db = _db.NewContext())
        {
            var other = Path.Combine(_root, "shonen", "One", "Ch_2.cbz");
            Assert.Empty(await TestDeletion.For(db).ClaimedAsync([other], new HashSet<int>(), default));
        }
    }

    [Theory]
    [InlineData(ChapterFileDeletion.NamePrefilterLimit)]
    [InlineData(ChapterFileDeletion.NamePrefilterLimit + 1)]
    [InlineData(100)]
    public async Task Any_number_of_names_finds_exactly_the_claimed_files(int count)
    {
        var paths = Enumerable.Range(1, count).Select(i => Path.Combine(_root, "One", $"Ch_{i}.cbz")).ToList();
        using (var db = _db.NewContext())
        {
            var root = new RootFolder { Path = _root };
            db.RootFolders.Add(root);
            db.SaveChanges();
            AddFile(db, root.Id, "Theirs", "One/ch_1.cbz");
            AddFile(db, root.Id, "Decoy", "One/Chx2.cbz");
        }

        using (var db = _db.NewContext())
        {
            var claimed = await TestDeletion.For(db).ClaimedAsync(paths, new HashSet<int>(), default);
            Assert.Equal(paths[0], Assert.Single(claimed), ignoreCase: true);
        }
    }
}
