using Maki.Api.Services;
using Maki.Core.Entities;
using Maki.Core.Images;
using Microsoft.EntityFrameworkCore;

namespace Maki.Api.Tests;

public sealed class PageSplitRemapTests : IDisposable
{
    private readonly UpgradeWorld _world = new UpgradeWorld().Seed();

    public void Dispose() => _world.Dispose();

    private (int ChapterId, int FileId, int UserId) Chapter()
    {
        var (chapterId, fileId) = _world.Chapter(1, f => f.ImageFormat = "jpg");
        return (chapterId, fileId!.Value, _world.Db.SeedUser("alice"));
    }

    private int[] Bookmark(int userId, int chapterId, params int[] pages)
    {
        using var db = _world.Db.NewContext();
        var rows = pages.Select(p => new ReaderBookmark
        {
            UserId = userId, SeriesId = _world.SeriesId, ChapterId = chapterId, PageIndex = p
        }).ToList();
        foreach (var row in rows)
        {
            db.Add(row);
            db.SaveChanges();
        }

        return rows.Select(r => r.Id).ToArray();
    }

    private int[] Pages(int chapterId)
    {
        using var db = _world.Db.NewContext();
        return db.ReaderBookmarks.IgnoreQueryFilters().Where(b => b.ChapterId == chapterId)
            .OrderBy(b => b.PageIndex).Select(b => b.PageIndex).ToArray();
    }

    private static string[] Names(int count) => Enumerable.Range(1, count).Select(i => $"{i:000}.jpg").ToArray();

    [Fact]
    public async Task A_split_shifts_bookmarks_onto_pages_another_bookmark_still_holds()
    {
        var (chapterId, fileId, alice) = Chapter();
        // Saved in this order, the bookmark on page 1 moves to 3 while the one on 3 is still there.
        Bookmark(alice, chapterId, 1, 3);
        var map = new PageSplitMap([new KeyValuePair<int, int>(0, 3)]);

        await using (var db = _world.Db.NewContext())
        {
            await PageSplitRemap.ApplyAsync(db, fileId, Names(4), map, undo: false, CancellationToken.None);
        }

        Assert.Equal([3, 5], Pages(chapterId));
    }

    [Fact]
    public async Task An_undo_keeps_one_bookmark_per_user_where_pieces_collapse_onto_one_page()
    {
        var (chapterId, fileId, alice) = Chapter();
        var bob = _world.Db.SeedUser("bob");
        var kept = Bookmark(alice, chapterId, 1, 2, 3, 4)[0];
        Bookmark(bob, chapterId, 2);
        var map = new PageSplitMap([new KeyValuePair<int, int>(1, 3)]);

        await using (var db = _world.Db.NewContext())
        {
            await PageSplitRemap.ApplyAsync(db, fileId, Names(5), map, undo: true, CancellationToken.None);
        }

        await using var check = _world.Db.NewContext();
        var rows = check.ReaderBookmarks.IgnoreQueryFilters().Where(b => b.ChapterId == chapterId).ToList();
        Assert.Equal([1, 2], rows.Where(b => b.UserId == alice).Select(b => b.PageIndex).Order());
        Assert.Equal(kept, rows.Single(b => b.UserId == alice && b.PageIndex == 1).Id);
        Assert.Equal(1, rows.Single(b => b.UserId == bob).PageIndex);
    }
}
