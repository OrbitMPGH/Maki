using Maki.Api.Configuration;
using Maki.Api.Services;
using Maki.Core.Metadata;
using Microsoft.Extensions.Logging.Abstractions;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace Maki.Api.Tests;

/// <summary>
/// The rebuild deletes files and re-downloads artwork over the whole library, so what it decides to
/// touch is the whole test surface: a non-forced pass must leave good posters exactly as they are
/// (otherwise "rebuild missing" quietly costs a download per series), and it has to recognise a
/// truncated poster as broken, which <c>File.Exists</c> alone cannot.
/// </summary>
public class ImageCacheRebuildTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly string _configDir;
    private readonly string? _priorEnv;
    private readonly AppPaths _paths;

    public ImageCacheRebuildTests()
    {
        _configDir = Path.Combine(Path.GetTempPath(), "maki-imagecache-tests", Guid.NewGuid().ToString("N"));
        _priorEnv = Environment.GetEnvironmentVariable("MAKI_CONFIG_DIR");
        Environment.SetEnvironmentVariable("MAKI_CONFIG_DIR", _configDir);
        _paths = new AppPaths();
    }

    [Fact]
    public async Task RebuildsOnlyThePostersThatAreMissingOrUnreadable()
    {
        var good = SeedSeries("Good");
        var missing = SeedSeries("Missing");
        var truncated = SeedSeries("Truncated");
        WriteCover(good, Jpeg(8));
        WriteCover(truncated, []);

        var status = new ImageCacheRebuildStatus();
        Assert.True(await Service(status).RunAsync(force: false, CancellationToken.None));

        var snapshot = status.Snapshot();
        Assert.Equal(2, snapshot.Total);
        Assert.Equal(2, snapshot.Downloaded);
        Assert.Equal(0, snapshot.Failed);
        // The downloaded poster is 4px wide, so a re-download of the good one would be visible here.
        Assert.Equal(8, Image.Identify(CoverPath(good)).Width);
        Assert.Equal(4, Image.Identify(CoverPath(missing)).Width);
        Assert.Equal(4, Image.Identify(CoverPath(truncated)).Width);
    }

    [Fact]
    public async Task ForceRebuildsEveryPoster()
    {
        var good = SeedSeries("Good");
        SeedSeries("Missing");
        WriteCover(good, Jpeg(8));

        var status = new ImageCacheRebuildStatus();
        await Service(status).RunAsync(force: true, CancellationToken.None);

        Assert.Equal(2, status.Snapshot().Total);
        Assert.Equal(2, status.Snapshot().Downloaded);
        Assert.Equal(4, Image.Identify(CoverPath(good)).Width);
    }

    [Fact]
    public async Task ClearsTheReaderAndSourcePreviewCaches()
    {
        var thumb = Path.Combine(_paths.ReaderCacheDir, "12", "3400-0.jpg");
        var sample = Path.Combine(_paths.SourcePreviewDir, "7", "mangadex", "000.jpg");
        Directory.CreateDirectory(Path.GetDirectoryName(thumb)!);
        Directory.CreateDirectory(Path.GetDirectoryName(sample)!);
        File.WriteAllBytes(thumb, Jpeg(4));
        File.WriteAllBytes(sample, Jpeg(4));

        var status = new ImageCacheRebuildStatus();
        await Service(status).RunAsync(force: false, CancellationToken.None);

        Assert.False(File.Exists(thumb));
        Assert.False(File.Exists(sample));
        Assert.Empty(Directory.GetDirectories(_paths.ReaderCacheDir));
        Assert.Equal(2, status.Snapshot().ThumbnailsCleared);
    }

    /// <summary>
    /// Poster folders are named by series id and SQLite reuses rowids, so one left behind by a
    /// deleted series eventually serves its art under whichever series lands on that id next.
    /// </summary>
    [Fact]
    public async Task DropsPosterFoldersForSeriesThatNoLongerExist()
    {
        var live = SeedSeries("Live");
        WriteCover(live, Jpeg(8));
        var orphan = Path.Combine(_paths.MediaCoverDir, "9999");
        Directory.CreateDirectory(orphan);
        File.WriteAllBytes(Path.Combine(orphan, "cover.jpg"), Jpeg(8));

        await Service(new ImageCacheRebuildStatus()).RunAsync(force: false, CancellationToken.None);

        Assert.False(Directory.Exists(orphan));
        Assert.True(File.Exists(CoverPath(live)));
    }

    [Fact]
    public async Task CountsASeriesWithNoProviderIdAsSkippedRatherThanFailed()
    {
        _db.SeedSeries("Manual", configure: s => s.MangaBakaId = null);

        var status = new ImageCacheRebuildStatus();
        await Service(status).RunAsync(force: false, CancellationToken.None);

        var snapshot = status.Snapshot();
        Assert.Equal(1, snapshot.Skipped);
        Assert.Equal(0, snapshot.Failed);
        Assert.Equal(0, snapshot.Downloaded);
    }

    [Fact]
    public async Task RefusesASecondRunWhileOneIsInFlight()
    {
        var status = new ImageCacheRebuildStatus();
        Assert.True(status.TryBegin(force: false));

        Assert.False(await Service(status).RunAsync(force: false, CancellationToken.None));
    }

    private int SeedSeries(string title) =>
        _db.SeedSeries(title, configure: s => s.MangaBakaId = 42);

    private string CoverPath(int seriesId) =>
        Path.Combine(_paths.MediaCoverDir, seriesId.ToString(), "cover.jpg");

    private void WriteCover(int seriesId, byte[] bytes)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(CoverPath(seriesId))!);
        File.WriteAllBytes(CoverPath(seriesId), bytes);
    }

    private ImageCacheRebuildService Service(ImageCacheRebuildStatus status)
    {
        var covers = new CoverService(
            new JpegClientFactory(Jpeg(4)), _paths, new FakeAppSettings(), NullLogger<CoverService>.Instance);
        var refresh = new SeriesMetadataRefreshService([new StubProvider()], covers);
        return new ImageCacheRebuildService(
            _db.NewContext(), _paths, covers, refresh, status,
            NullLogger<ImageCacheRebuildService>.Instance);
    }

    private static byte[] Jpeg(int size)
    {
        using var image = new Image<Rgba32>(size, size);
        using var buffer = new MemoryStream();
        image.SaveAsJpeg(buffer);
        return buffer.ToArray();
    }

    /// <summary>Answers every cover download with the same image, so a re-download is detectable.</summary>
    private sealed class JpegClientFactory(byte[] bytes) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(new Handler(bytes));

        private sealed class Handler(byte[] bytes) : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(
                HttpRequestMessage request, CancellationToken ct) =>
                Task.FromResult(new HttpResponseMessage { Content = new ByteArrayContent(bytes) });
        }
    }

    private sealed class StubProvider : IMetadataProvider
    {
        public string Name => "stub";

        public Task<IReadOnlyList<MetadataSearchResult>> SearchAsync(
            string query, string maxContentRating, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<MetadataSearchResult>>([]);

        public Task<SeriesMetadata?> GetAsync(string providerId, CancellationToken ct = default) =>
            Task.FromResult<SeriesMetadata?>(new SeriesMetadata
            {
                ProviderId = providerId,
                Title = "Stub",
                CoverUrl = "https://example.test/cover.jpg"
            });
    }

    public void Dispose()
    {
        _db.Dispose();
        Environment.SetEnvironmentVariable("MAKI_CONFIG_DIR", _priorEnv);
        try { Directory.Delete(_configDir, recursive: true); } catch (IOException) { }
    }
}
