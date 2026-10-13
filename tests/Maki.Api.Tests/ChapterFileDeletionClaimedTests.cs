using Maki.Core.Entities;

namespace Maki.Api.Tests;

/// <summary>
/// <see cref="Maki.Api.Services.ChapterFileDeletion.ClaimedAsync"/> reads only the rows whose name
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
}
