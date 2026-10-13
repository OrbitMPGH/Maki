using Maki.Api.Configuration;
using Maki.Api.Controllers;
using Maki.Api.Services;
using Maki.Core.Configuration;
using Maki.Core.Entities;
using Maki.Core.Security;
using Maki.Data;
using Maki.Data.Identity;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;

namespace Maki.Api.Tests;

/// <summary>
/// The OPDS cover endpoint, which is the one place in the catalogue that builds a filesystem path
/// from a caller-supplied id instead of querying. Resolving the token narrows the data scope to its
/// owner, but a scope only does anything if something is queried through it, so serving the file
/// straight off disk would hand every cover on the instance to a token whose owner holds one root
/// folder — and turn 404-vs-200 into an oracle for which series ids exist.
/// </summary>
[Collection(ConfigDirCollection.Name)]
public sealed class OpdsControllerTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly string _configDir;
    private readonly string? _priorEnv;
    private readonly AppPaths _paths;

    public OpdsControllerTests()
    {
        _configDir = Path.Combine(Path.GetTempPath(), "maki-opds-tests", Guid.NewGuid().ToString("N"));
        _priorEnv = Environment.GetEnvironmentVariable("MAKI_CONFIG_DIR");
        Environment.SetEnvironmentVariable("MAKI_CONFIG_DIR", _configDir);
        _paths = new AppPaths();
    }

    [Fact]
    public async Task ServesACoverForASeriesInAGrantedRootFolder()
    {
        var (granted, _, token) = Seed();

        var result = await Controller().Cover(token, granted, default);

        Assert.IsType<PhysicalFileResult>(result);
    }

    [Fact]
    public async Task RefusesACoverForASeriesInAFolderTheTokensOwnerWasNotGranted()
    {
        var (_, ungranted, token) = Seed();

        var result = await Controller().Cover(token, ungranted, default);

        // The file is on disk and the token is valid; only the Series query filter separates these
        // two cases. 404 rather than 403, matching every other answer this controller gives.
        Assert.IsType<NotFoundResult>(result);
        Assert.True(File.Exists(Path.Combine(_paths.MediaCoverDir, ungranted.ToString(), "cover.jpg")));
    }

    [Fact]
    public async Task ServesEveryCoverToATokenWhoseOwnerHoldsAllRootFolders()
    {
        var (granted, ungranted, _) = Seed();
        var admin = _db.SeedUser("admin", MakiPermission.Admin, allRootFolders: true);
        var token = _db.SeedApiKey(admin, UserApiKeyScope.Opds);
        EnableCatalogue(admin);

        Assert.IsType<PhysicalFileResult>(await Controller().Cover(token, granted, default));
        Assert.IsType<PhysicalFileResult>(await Controller().Cover(token, ungranted, default));
    }

    [Fact]
    public async Task APdfPageIsRenderedOnceAndServedFromTheDiskCacheAfterwards()
    {
        var reader = _db.SeedUser("pdfreader", MakiPermission.UseOpds, allRootFolders: true);
        EnableCatalogue(reader);
        var token = _db.SeedApiKey(reader, UserApiKeyScope.Opds);
        var root = Path.Combine(Path.GetTempPath(), "maki-opds-pdf-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(root);
        try
        {
            var pdf = Path.Combine(root, "Volume.pdf");
            PdfFixture.Write(pdf, count: 3);
            int chapterId, fileId;
            using (var db = _db.NewContext())
            {
                var folder = new RootFolder { Path = root };
                db.RootFolders.Add(folder);
                db.SaveChanges();
                var series = new Series { Title = "Pdf", SortTitle = "pdf", FolderName = "", RootFolderId = folder.Id };
                db.Series.Add(series);
                db.SaveChanges();
                var file = new ChapterFile
                {
                    SeriesId = series.Id, RelativePath = "Volume.pdf", Size = new FileInfo(pdf).Length,
                    SourceName = "test", DateAdded = DateTime.UtcNow
                };
                db.ChapterFiles.Add(file);
                db.SaveChanges();
                var chapter = new Chapter { SeriesId = series.Id, Number = 1, ChapterFileId = file.Id };
                db.Chapters.Add(chapter);
                db.SaveChanges();
                (chapterId, fileId) = (chapter.Id, file.Id);
            }

            Assert.IsAssignableFrom<FileResult>(await Controller(withReader: true).Page(token, chapterId, 0, default));
            var dir = Path.Combine(_paths.ReaderCacheDir, fileId.ToString());
            var cached = Assert.Single(Directory.GetFiles(dir, "*.full.jpg"));
            var written = File.GetLastWriteTimeUtc(cached);

            Assert.IsAssignableFrom<FileResult>(await Controller(withReader: true).Page(token, chapterId, 0, default));

            Assert.Equal(written, File.GetLastWriteTimeUtc(cached));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>
    /// Two series in two root folders with a cover file each, plus an OPDS token belonging to a
    /// reader granted only the first. <c>SeedSeries</c> creates a root folder per call, so the two
    /// are genuinely separate.
    /// </summary>
    private (int Granted, int Ungranted, string Token) Seed()
    {
        var granted = _db.SeedSeries("Granted");
        var ungranted = _db.SeedSeries("Ungranted");
        var reader = _db.SeedUser("reader", MakiPermission.UseOpds, allRootFolders: false);

        using (var db = _db.NewContext())
        {
            var grantedFolder = db.Series.Single(s => s.Id == granted).RootFolderId;
            db.UserRootFolders.Add(new UserRootFolder { UserId = reader, RootFolderId = grantedFolder });
            db.SaveChanges();
        }

        EnableCatalogue(reader);
        WriteCover(granted);
        WriteCover(ungranted);

        return (granted, ungranted, _db.SeedApiKey(reader, UserApiKeyScope.Opds));
    }

    private void EnableCatalogue(int userId) =>
        _db.SetUserConfig(userId, (SettingKeys.OpdsEnabled, "true"));

    private void WriteCover(int seriesId)
    {
        var dir = Path.Combine(_paths.MediaCoverDir, seriesId.ToString());
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, "cover.jpg"), [0xFF, 0xD8, 0xFF]);
    }

    /// <summary>
    /// A controller over a context scoped to nobody, which is what an OPDS request actually gets:
    /// no cookie and no API-key header, so <c>CurrentUserMiddleware</c> narrows it before routing.
    /// The catalogue and reader services are unreachable from the cover action.
    /// </summary>
    private OpdsController Controller(bool withReader = false)
    {
        var nobody = new DataScope();
        nobody.SetNobody();
        var db = new MakiDbContext(_db.Options, nobody);

        return new OpdsController(
            catalog: null!,
            access: new OpdsAccessService(db, TimeProvider.System),
            reader: withReader
                ? new ReaderService(db, new ReaderArchiveCache(NullLogger<ReaderArchiveCache>.Instance),
                    null!, null!, new ReadingSessionService(db), NullLogger<ReaderService>.Instance)
                : null!,
            progressWriter: withReader
                ? new OpdsProgressWriter(_db.ScopeFactory(), NullLogger<OpdsProgressWriter>.Instance)
                : null!,
            db: db,
            paths: _paths,
            localizer: new TestLocalizer(),
            logger: NullLogger<OpdsController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };
    }

    public void Dispose()
    {
        _db.Dispose();
        Environment.SetEnvironmentVariable("MAKI_CONFIG_DIR", _priorEnv);
        try { Directory.Delete(_configDir, recursive: true); } catch (IOException) { }
    }
}
