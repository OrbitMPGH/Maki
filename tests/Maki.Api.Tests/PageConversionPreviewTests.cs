using System.IO.Compression;
using Maki.Api.Controllers;
using Maki.Api.Services;
using Maki.Core.Entities;
using Maki.Core.Images;
using Maki.Core.Quality;
using Maki.Core.Tests;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;

namespace Maki.Api.Tests;

public sealed class PageConversionPreviewTests : IDisposable
{
    private readonly UpgradeWorld _world = new UpgradeWorld().Seed(series: s => s.Type = SeriesTypes.Manga);

    private static readonly PageConversionSettings High = new(PageConversionFormat.Webp, PageConversionStop.High, false);

    public void Dispose() => _world.Dispose();

    private PageConversionPreviewService Service() =>
        new(_world.Db.NewContext(), _world.Paths, NullLogger<PageConversionPreviewService>.Instance);

    private static byte[] Grey(int seed)
    {
        var image = new Raster(500, 750, bands: 3);
        var random = new Random(seed);
        for (var y = 0; y < 750; y++)
        for (var x = 0; x < 500; x++)
        {
            var tone = (byte)Math.Clamp((x + y) * 200 / 1250 + random.Next(24), 0, 255);
            image[x, y] = new Rgba(tone, tone, tone);
        }

        return image.EncodeJpeg(95);
    }

    private static byte[] Colour(int seed)
    {
        var image = new Raster(500, 750, bands: 3);
        var random = new Random(seed);
        for (var y = 0; y < 750; y++)
        for (var x = 0; x < 500; x++)
        {
            image[x, y] = new Rgba((byte)(x * 255 / 500), (byte)random.Next(256), (byte)(y * 255 / 750));
        }

        return image.EncodeJpeg(95);
    }

    private static byte[] Noise(int width, int height, int seed, int quality)
    {
        var image = new Raster(width, height, bands: 3);
        var random = new Random(seed);
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            var tone = (byte)random.Next(256);
            image[x, y] = new Rgba(tone, tone, tone);
        }

        return image.EncodeJpeg(quality);
    }

    private static byte[] Truncated(byte[] jpeg) => jpeg[..(jpeg.Length * 6 / 10)];

    private int AddSeries(string title, string? type)
    {
        using var db = _world.Db.NewContext();
        var series = new Series
        {
            Title = title, SortTitle = title.ToLowerInvariant(), FolderName = title, Type = type,
            RootFolderId = db.RootFolders.Single().Id, UpgradeProfileId = _world.ProfileId,
            Added = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)
        };
        db.Series.Add(series);
        db.SaveChanges();
        return series.Id;
    }

    /// <summary>A linked chapter file on disk, with its first page tiny so it is never the suggestion.</summary>
    private (int FileId, int ChapterId, string Path) AddFile(int seriesId, string folder, decimal number, QualityTier tier,
        Func<int, byte[]> page, int pages = 6, string extension = "cbz")
    {
        var relative = $"{folder}/{folder} {number:000}.{extension}";
        var path = Path.Combine(_world.Library, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            for (var i = 0; i < pages; i++)
            {
                using var stream = zip.CreateEntry($"{i + 1:000}.jpg", CompressionLevel.NoCompression).Open();
                stream.Write(i == 2 ? new Raster(20, 20, bands: 3).EncodeJpeg(80) : page(i));
            }
        }

        using var db = _world.Db.NewContext();
        var file = new ChapterFile
        {
            SeriesId = seriesId, RelativePath = relative, SourceName = UpgradeWorld.Agg, Tier = tier, PageCount = pages,
            ImageFormat = "jpg", Size = new FileInfo(path).Length, DateAdded = DateTime.UtcNow, MeasuredAtUtc = DateTime.UtcNow
        };
        db.ChapterFiles.Add(file);
        db.SaveChanges();
        var chapter = new Chapter { SeriesId = seriesId, Number = number, NumberRaw = number.ToString(), ChapterFileId = file.Id };
        db.Chapters.Add(chapter);
        db.SaveChanges();
        return (file.Id, chapter.Id, path);
    }

    private static byte[] Entry(string path, int page)
    {
        using var zip = ZipFile.OpenRead(path);
        var name = Maki.Core.Reading.CbzReader.PageNames(zip)[page];
        using var stream = zip.GetEntry(name)!.Open();
        using var copy = new MemoryStream();
        stream.CopyTo(copy);
        return copy.ToArray();
    }

    [Fact]
    public async Task A_page_is_encoded_once_and_then_served_from_the_cache()
    {
        var (fileId, _, path) = AddFile(_world.SeriesId, "Series", 1, QualityTier.Scanlator, Grey);

        var first = await Service().PreviewAsync(fileId, 1, High, CancellationToken.None);

        Assert.NotNull(first);
        Assert.False(first.Kept);
        Assert.Equal("image/webp", first.ContentType);
        Assert.Equal(Entry(path, 1).Length, first.OriginalBytes);
        Assert.True(first.EncodedBytes < first.OriginalBytes);
        Assert.Equal(first.EncodedBytes, new FileInfo(first.Path).Length);
        Assert.StartsWith(_world.Paths.ConversionPreviewDir, first.Path);
        Assert.Equal(first.EncodedBytes, PageConversion.Convert(Entry(path, 1), ".jpg", High).Bytes.Length);

        await File.WriteAllBytesAsync(first.Path, [1, 2, 3]);
        var second = await Service().PreviewAsync(fileId, 1, High, CancellationToken.None);

        Assert.Equal(first.Path, second!.Path);
        Assert.Equal(3, second.EncodedBytes);
        Assert.Equal(first.Key, second.Key);

        var avif = await Service().PreviewAsync(fileId, 1, High with { Format = PageConversionFormat.Avif }, CancellationToken.None);
        Assert.Equal("image/avif", avif!.ContentType);
        Assert.NotEqual(first.Key, avif.Key);
    }

    [Fact]
    public async Task A_rewritten_archive_gets_a_fresh_preview()
    {
        var (fileId, _, path) = AddFile(_world.SeriesId, "Series", 1, QualityTier.Scanlator, Grey);
        var first = await Service().PreviewAsync(fileId, 1, High, CancellationToken.None);

        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(-5));
        var second = await Service().PreviewAsync(fileId, 1, High, CancellationToken.None);

        Assert.NotEqual(first!.Key, second!.Key);
    }

    [Fact]
    public async Task The_original_is_the_stored_page_as_it_is()
    {
        var (fileId, _, path) = AddFile(_world.SeriesId, "Series", 1, QualityTier.Scanlator, Grey);

        var original = await Service().PreviewAsync(fileId, 3, null, CancellationToken.None);

        Assert.Equal("image/jpeg", original!.ContentType);
        Assert.Equal(Entry(path, 3), await File.ReadAllBytesAsync(original.Path));
        Assert.Equal(original.OriginalBytes, original.EncodedBytes);
        Assert.False(original.Kept);
    }

    [Fact]
    public async Task A_page_the_pass_would_keep_is_served_as_stored_and_marked_kept()
    {
        var (fileId, _, path) = AddFile(_world.SeriesId, "Series", 1, QualityTier.Scanlator, Grey);
        var lossless = new PageConversionSettings(PageConversionFormat.Webp, PageConversionStop.Lossless, false);

        var kept = await Service().PreviewAsync(fileId, 1, lossless, CancellationToken.None);
        var tiny = await Service().PreviewAsync(fileId, 2, High, CancellationToken.None);

        Assert.True(kept!.Kept);
        Assert.Equal("image/jpeg", kept.ContentType);
        Assert.Equal(Entry(path, 1), await File.ReadAllBytesAsync(kept.Path));
        Assert.Equal(kept.OriginalBytes, kept.EncodedBytes);
        Assert.True(tiny!.Kept);
    }

    [Fact]
    public async Task Missing_files_pages_and_pdfs_have_no_preview()
    {
        var (fileId, _, path) = AddFile(_world.SeriesId, "Series", 1, QualityTier.Scanlator, Grey);
        var pdf = AddFile(_world.SeriesId, "Series", 2, QualityTier.Scanlator, Grey, extension: "pdf");

        Assert.Null(await Service().PreviewAsync(fileId, 6, High, CancellationToken.None));
        Assert.Null(await Service().PreviewAsync(fileId, -1, High, CancellationToken.None));
        Assert.Null(await Service().PreviewAsync(999, 0, High, CancellationToken.None));
        Assert.Null(await Service().PreviewAsync(pdf.FileId, 0, High, CancellationToken.None));
        Assert.Null(await Service().SampleAsync(pdf.FileId, CancellationToken.None));
        Assert.Null(await Service().SampleAsync(999, CancellationToken.None));

        File.Delete(path);
        Assert.Null(await Service().PreviewAsync(fileId, 1, High, CancellationToken.None));
    }

    private PageConversionController Controller()
    {
        var db = _world.Db.NewContext();
        return new PageConversionController(db, new SettingsService(_world.Db.ScopeFactory()),
            new PageConversionWorker(_world.Db.ScopeFactory(), new SettingsService(_world.Db.ScopeFactory()),
                NullLogger<PageConversionWorker>.Instance),
            new PageConversionPreviewService(db, _world.Paths, NullLogger<PageConversionPreviewService>.Instance),
            new TestLocalizer())
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };
    }

    [Fact]
    public async Task The_endpoint_sends_the_sizes_in_headers_and_refuses_bad_parameters()
    {
        var (fileId, _, _) = AddFile(_world.SeriesId, "Series", 1, QualityTier.Scanlator, Grey);
        var controller = Controller();

        var result = Assert.IsType<PhysicalFileResult>(
            await controller.Preview(fileId, 1, "webp", "high", false, false, CancellationToken.None));

        Assert.Equal("image/webp", result.ContentType);
        var headers = controller.Response.Headers;
        Assert.Equal(new FileInfo(result.FileName).Length.ToString(), headers["X-Maki-Encoded-Bytes"].ToString());
        Assert.True(long.Parse(headers["X-Maki-Original-Bytes"].ToString()) > long.Parse(headers["X-Maki-Encoded-Bytes"].ToString()));
        Assert.Equal("0", headers["X-Maki-Kept"].ToString());
        Assert.NotNull(result.EntityTag);

        Assert.IsType<BadRequestObjectResult>(await Controller().Preview(fileId, 1, "jxl", "high", false, false, CancellationToken.None));
        Assert.IsType<BadRequestObjectResult>(await Controller().Preview(fileId, 1, "webp", "best", false, false, CancellationToken.None));
        Assert.IsType<NotFoundResult>(await Controller().Preview(fileId, 9, "webp", "high", false, false, CancellationToken.None));
        Assert.IsType<PhysicalFileResult>(await Controller().Preview(fileId, 1, null, "lossless", false, false, CancellationToken.None));
        Assert.IsType<PhysicalFileResult>(await Controller().Preview(fileId, 1, null, null, false, true, CancellationToken.None));
    }

    [Fact]
    public async Task The_endpoint_names_why_a_page_is_kept_and_a_cache_hit_repeats_it()
    {
        var (fileId, _, _) = AddFile(_world.SeriesId, "Series", 1, QualityTier.Scanlator, Grey);

        for (var round = 0; round < 2; round++)
        {
            var controller = Controller();
            Assert.IsType<PhysicalFileResult>(
                await controller.Preview(fileId, 2, "webp", "high", false, false, CancellationToken.None));
            Assert.Equal("1", controller.Response.Headers["X-Maki-Kept"].ToString());
            Assert.Equal("toosmall", controller.Response.Headers["X-Maki-Kept-Reason"].ToString());
        }

        var lossless = Controller();
        await lossless.Preview(fileId, 1, null, "lossless", false, false, CancellationToken.None);
        Assert.Equal("lossless", lossless.Response.Headers["X-Maki-Kept-Reason"].ToString());

        var converted = Controller();
        await converted.Preview(fileId, 1, "webp", "high", false, false, CancellationToken.None);
        Assert.Equal("0", converted.Response.Headers["X-Maki-Kept"].ToString());
        Assert.False(converted.Response.Headers.ContainsKey("X-Maki-Kept-Reason"));
    }

    [Fact]
    public async Task A_page_that_does_not_decode_is_kept_as_unreadable()
    {
        var (fileId, _, path) = AddFile(_world.SeriesId, "Series", 1, QualityTier.Scanlator, i => Truncated(Grey(i)));

        var first = await Service().PreviewAsync(fileId, 1, High, CancellationToken.None);
        var again = await Service().PreviewAsync(fileId, 1, High, CancellationToken.None);

        Assert.True(first!.Kept);
        Assert.Equal("unreadable", first.KeptReason);
        Assert.Equal("unreadable", again!.KeptReason);
        Assert.Equal(Entry(path, 1), await File.ReadAllBytesAsync(first.Path));
    }

    [Fact]
    public async Task A_cache_hit_renews_the_files_the_sweep_goes_by()
    {
        var (fileId, _, _) = AddFile(_world.SeriesId, "Series", 1, QualityTier.Scanlator, Grey);
        await Service().PreviewAsync(fileId, 1, High, CancellationToken.None);
        var old = DateTime.UtcNow.AddDays(-3);
        foreach (var file in Directory.EnumerateFiles(_world.Paths.ConversionPreviewDir))
        {
            File.SetLastWriteTimeUtc(file, old);
        }

        var hit = await Service().PreviewAsync(fileId, 1, High, CancellationToken.None);

        var files = Directory.EnumerateFiles(_world.Paths.ConversionPreviewDir).ToList();
        Assert.Equal(2, files.Count);
        Assert.All(files, f => Assert.True(File.GetLastWriteTimeUtc(f) > DateTime.UtcNow.AddHours(-1)));
        Assert.True(File.Exists(hit!.Path));
    }

    [Fact]
    public async Task Samples_pick_a_greyscale_manga_a_colour_manhwa_and_the_heaviest_tier()
    {
        var manga = AddFile(_world.SeriesId, "Series", 1, QualityTier.Scanlator, Grey);
        var manhwaId = AddSeries("Strip", SeriesTypes.Manhwa);
        var colour = AddFile(manhwaId, "Strip", 1, QualityTier.Aggregator, Colour);
        var volumesId = AddSeries("Volumes", null);
        var volumes = new[]
        {
            AddFile(volumesId, "Volumes", 1, QualityTier.Volume, Grey, pages: 9),
            AddFile(volumesId, "Volumes", 2, QualityTier.Volume, Grey, pages: 10),
            AddFile(volumesId, "Volumes", 3, QualityTier.Volume, Grey, pages: 11)
        };
        var converted = AddFile(manhwaId, "Strip", 2, QualityTier.Volume, Colour, pages: 30);
        using (var db = _world.Db.NewContext())
        {
            db.ChapterFiles.Single(f => f.Id == converted.FileId).OriginalImageFormat = "jpg";
            db.SaveChanges();
        }

        var samples = await Service().SamplesAsync(CancellationToken.None);

        Assert.Equal(["greyscale", "colour", "heaviest"], samples.Select(s => s.Reason));
        var grey = samples[0];
        Assert.Equal((manga.FileId, manga.ChapterId, _world.SeriesId, "Series", "Ch.1"),
            (grey.FileId, grey.ChapterId, grey.SeriesId, grey.SeriesTitle, grey.ChapterLabel));
        Assert.Equal((3, 6, "scanlator"), (grey.Page, grey.PageCount, grey.Tier));
        Assert.Equal(colour.FileId, samples[1].FileId);
        Assert.Equal(volumes[1].FileId, samples[2].FileId);
        Assert.Equal("volume", samples[2].Tier);
        Assert.InRange(samples[2].Page, 1, 6);
    }

    [Fact]
    public async Task A_library_with_no_colour_returns_fewer_samples()
    {
        AddFile(_world.SeriesId, "Series", 1, QualityTier.Scanlator, Grey);
        AddFile(AddSeries("Other", SeriesTypes.Manga), "Other", 1, QualityTier.Aggregator, Grey);

        var samples = await Service().SamplesAsync(CancellationToken.None);

        Assert.Equal(["greyscale", "heaviest"], samples.Select(s => s.Reason));
        Assert.NotEqual(samples[0].SeriesId, samples[1].SeriesId);
    }

    [Fact]
    public async Task A_page_the_High_stop_keeps_is_not_a_sample()
    {
        var compact = Noise(500, 750, 1, quality: 5);
        Assert.Equal(PageSkip.NotSmaller, PageConversion.Convert(compact, ".jpg", High).Skipped);
        var keeper = AddFile(_world.SeriesId, "Series", 1, QualityTier.Scanlator, _ => compact);
        var other = AddFile(AddSeries("Other", SeriesTypes.Manga), "Other", 1, QualityTier.Aggregator, Grey);

        var samples = await Service().SamplesAsync(CancellationToken.None);

        Assert.DoesNotContain(samples, s => s.FileId == keeper.FileId);
        Assert.Contains(samples, s => s.FileId == other.FileId);
    }

    [Fact]
    public async Task A_thin_slice_is_not_a_sample()
    {
        var other = AddFile(_world.SeriesId, "Series", 1, QualityTier.Scanlator, Grey);
        var thin = AddFile(AddSeries("Strip", SeriesTypes.Manhwa), "Strip", 1, QualityTier.Aggregator, i => Noise(940, 66, i, 90));
        Assert.True(Entry(thin.Path, 3).Length > PageConversion.MinPageBytes);

        var samples = await Service().SamplesAsync(CancellationToken.None);

        Assert.DoesNotContain(samples, s => s.FileId == thin.FileId);
        Assert.DoesNotContain(samples, s => s.Reason == "colour");
        Assert.Contains(samples, s => s.FileId == other.FileId);
    }

    [Fact]
    public async Task The_page_a_sample_names_is_already_in_the_preview_cache()
    {
        var manga = AddFile(_world.SeriesId, "Series", 1, QualityTier.Scanlator, Grey);

        var sample = Assert.Single(await Service().SamplesAsync(CancellationToken.None));
        var encoded = Directory.EnumerateFiles(_world.Paths.ConversionPreviewDir, "*.webp").Single();
        await File.WriteAllBytesAsync(encoded, [1, 2, 3]);
        var preview = await Service().PreviewAsync(manga.FileId, sample.Page, High, CancellationToken.None);

        Assert.False(preview!.Kept);
        Assert.Equal(3, preview.EncodedBytes);
        Assert.Equal(2, Directory.EnumerateFiles(_world.Paths.ConversionPreviewDir).Count());
    }

    [Fact]
    public async Task A_picked_file_gets_its_middle_page_as_a_custom_sample()
    {
        var (fileId, _, _) = AddFile(_world.SeriesId, "Series", 1, QualityTier.Scanlator, Grey, pages: 9);

        var sample = await Service().SampleAsync(fileId, CancellationToken.None);

        Assert.Equal(("custom", 3, 9), (sample!.Reason, sample.Page, sample.PageCount));
    }
}
