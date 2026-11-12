using System.IO.Compression;
using System.Text.Json;
using Maki.Api.Configuration;
using Maki.Api.Controllers;
using Maki.Api.Services;
using Maki.Core.Entities;
using Maki.Data;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;

namespace Maki.Api.Tests;

public sealed class ReaderRecapEndpointTests : IDisposable
{
    private const int W = 64;
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private readonly TestDb _db = new();
    private readonly string _configDir = Path.Combine(Path.GetTempPath(), "maki-recap-" + Guid.NewGuid().ToString("N"));
    private readonly string _root;
    private readonly int _seriesId;
    private readonly ReaderArchiveCache _archives = new(NullLogger<ReaderArchiveCache>.Instance);

    public ReaderRecapEndpointTests()
    {
        _root = Path.Combine(_configDir, "library");
        using var db = _db.NewContext();
        var folder = new RootFolder { Path = _root };
        db.RootFolders.Add(folder);
        db.SaveChanges();
        var series = new Series { Title = "S", SortTitle = "s", FolderName = "S", RootFolderId = folder.Id };
        db.Series.Add(series);
        db.SaveChanges();
        _seriesId = series.Id;
    }

    public void Dispose()
    {
        _db.Dispose();
        try
        {
            Directory.Delete(_configDir, true);
        }
        catch (IOException)
        {
        }
    }

    /// <summary>Smooth value noise in panels with white gutters: art that never repeats by chance.</summary>
    private static byte[] Art(int seed, int rows)
    {
        var random = new Random(seed);
        var strip = new byte[rows * W];
        Array.Fill(strip, (byte)255);
        for (var y = 0; y < rows;)
        {
            var panel = Math.Min(rows - y, random.Next(90, 220));
            var grid = new double[(panel / 24 + 2) * 9];
            for (var g = 0; g < grid.Length; g++)
            {
                grid[g] = random.Next(20, 236);
            }

            for (var r = 0; r < panel; r++)
            {
                var fy = r / 24.0 - r / 24;
                for (var x = 0; x < W; x++)
                {
                    var (cx, fx) = (x / 8, x / 8.0 - x / 8);
                    double At(int row) => grid[row * 9 + cx] * (1 - fx) + grid[row * 9 + Math.Min(cx + 1, 8)] * fx;
                    strip[(y + r) * W + x] = (byte)(At(r / 24) * (1 - fy) + At(r / 24 + 1) * fy);
                }
            }

            y += panel + random.Next(20, 60);
        }

        return strip;
    }

    private int AddChapter(int number, byte[] strip, int pageHeight)
    {
        var name = $"c{number}.cbz";
        var path = Path.Combine(_root, "S", name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            var rows = strip.Length / W;
            for (int y = 0, page = 1; y < rows; y += pageHeight, page++)
            {
                var height = Math.Min(pageHeight, rows - y);
                using var image = NetVips.Image.NewFromMemory(strip.AsSpan(y * W, height * W).ToArray(), W, height, 1,
                    NetVips.Enums.BandFormat.Uchar);
                using var entry = zip.CreateEntry($"{page:000}.png").Open();
                entry.Write(image.PngsaveBuffer());
            }
        }

        using var db = _db.NewContext();
        var file = new ChapterFile
        {
            SeriesId = _seriesId, RelativePath = $"S/{name}", Size = new FileInfo(path).Length,
            SourceName = "test", DateAdded = DateTime.UtcNow
        };
        db.ChapterFiles.Add(file);
        db.SaveChanges();
        var chapter = new Chapter { SeriesId = _seriesId, Number = number, ChapterFileId = file.Id };
        db.Chapters.Add(chapter);
        db.SaveChanges();
        return chapter.Id;
    }

    private async Task<JsonElement> RecapAsync(int chapterId)
    {
        using var db = _db.NewContext(1);
        var controller = new ReaderController(
            new TestLocalizer(), db,
            new ReaderService(db, _archives, null!, null!, new ReadingSessionService(db), NullLogger<ReaderService>.Instance),
            null!, null!, null!, null!, null!, new AppPaths(_configDir),
            new TestCurrentUser(1), null!, new FakeAppSettings())
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };
        var result = await controller.Recap(chapterId, new ReaderRecapCache(_archives, NullLogger<ReaderRecapCache>.Instance), default);
        return JsonSerializer.SerializeToElement(Assert.IsType<OkObjectResult>(result).Value, Web);
    }

    [Fact]
    public async Task Points_past_the_previous_chapters_ending_when_a_chapter_repeats_it()
    {
        var previous = Art(1, 3000);
        AddChapter(1, previous, 126);
        var second = AddChapter(2, [.. previous.AsSpan((3000 - 520) * W), .. Art(2, 2000)], 131);

        var recap = await RecapAsync(second);

        var page = recap.GetProperty("page").GetInt32();
        var landing = page * 131 + recap.GetProperty("offset").GetDouble() * 131;
        Assert.InRange(landing, 520 - 60, 520 + 4);
    }

    [Fact]
    public async Task The_first_chapter_and_a_chapter_without_a_recap_get_nothing()
    {
        var first = AddChapter(1, Art(3, 3000), 126);
        var second = AddChapter(2, Art(4, 2000), 131);

        foreach (var chapter in new[] { first, second })
        {
            var recap = await RecapAsync(chapter);
            Assert.Equal(JsonValueKind.Null, recap.GetProperty("page").ValueKind);
            Assert.Equal(JsonValueKind.Null, recap.GetProperty("offset").ValueKind);
        }
    }

    private async Task<IActionResult> ToggleBookmarkAsync(int chapterId, int page)
    {
        using var db = _db.NewContext(1);
        var controller = new ReaderController(
            new TestLocalizer(), db,
            new ReaderService(db, _archives, null!, null!, new ReadingSessionService(db), NullLogger<ReaderService>.Instance),
            null!, null!, null!, null!, null!, new AppPaths(_configDir),
            new TestCurrentUser(1), null!, new FakeAppSettings())
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };
        return await controller.ToggleBookmark(chapterId, page, default);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(5)]
    [InlineData(100)]
    public async Task A_bookmark_outside_the_chapter_is_refused_and_not_stored(int page)
    {
        var chapter = AddChapter(1, Art(5, 500), 100);

        var result = await ToggleBookmarkAsync(chapter, page);

        Assert.IsNotType<OkObjectResult>(result);
        using var db = _db.NewContext(1);
        Assert.Empty(db.ReaderBookmarks.Where(b => b.ChapterId == chapter));
    }

    [Fact]
    public async Task A_bookmark_on_a_real_page_toggles_on_and_off()
    {
        var chapter = AddChapter(1, Art(6, 500), 100);

        var on = JsonSerializer.SerializeToElement(Assert.IsType<OkObjectResult>(await ToggleBookmarkAsync(chapter, 4)).Value, Web);
        Assert.True(on.GetProperty("bookmarked").GetBoolean());
        var off = JsonSerializer.SerializeToElement(Assert.IsType<OkObjectResult>(await ToggleBookmarkAsync(chapter, 4)).Value, Web);
        Assert.False(off.GetProperty("bookmarked").GetBoolean());
    }
}
