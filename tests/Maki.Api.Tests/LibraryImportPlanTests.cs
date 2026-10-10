using System.IO.Compression;
using System.Text;
using Maki.Api.Hubs;
using Maki.Api.Services;
using Maki.Core.Entities;
using Maki.Core.Kavita;
using Maki.Core.Metadata;
using Maki.Core.Sources;
using Maki.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using SharpCompress.Common;
using SharpCompress.Writers;

namespace Maki.Api.Tests;

/// <summary>
/// The import preview has to say what the import then does: the folder it lands in, the files it
/// registers, the CBZs it builds, what it leaves out, and the chapters each file links to once the
/// link stage runs. Each test plans a fixture folder, imports it, and compares.
/// </summary>
public class LibraryImportPlanTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly FakeAppSettings _settings = new();
    private readonly SourceMatchQueue _queue = new();
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "maki-import-plan-" + Guid.NewGuid().ToString("N")[..8]);

    public LibraryImportPlanTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        _db.Dispose();
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private string At(params string[] parts) => Path.Combine([_root, .. parts]);

    private void WriteZip(string path, params string[] pages)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var page in pages.Length == 0 ? ["001.png"] : pages)
        {
            using var writer = new StreamWriter(archive.CreateEntry(page).Open());
            writer.Write("page");
        }
    }

    /// <summary>A tar stands in for RAR and 7z, which SharpCompress cannot write.</summary>
    private static void WriteTar(string path, params string[] pages)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var stream = File.Create(path);
        using var writer = WriterFactory.OpenWriter(stream, ArchiveType.Tar, new WriterOptions(CompressionType.None));
        foreach (var page in pages)
        {
            writer.Write(page, new MemoryStream(Encoding.UTF8.GetBytes(page)), DateTime.UtcNow);
        }
    }

    private CbzLinkService LinkService(MakiDbContext db)
    {
        var registry = new SourceRegistry([]);
        return new CbzLinkService(
            db,
            registry,
            new KavitaScanService(
                new KavitaClient(new StubHttpClientFactory("{}")),
                _settings,
                _db.ScopeFactory(),
                NullLogger<KavitaScanService>.Instance),
            new StatsEventService(db),
            new ReaderArchiveCache(NullLogger<ReaderArchiveCache>.Instance),
            new SourceAvailability(_settings, registry),
            TestQuality.Create(registry),
            _settings,
            NullLogger<CbzLinkService>.Instance);
    }

    private LibraryImportService ImportService(MakiDbContext db) => new(
        db, [new FixedProvider()], null!, LinkService(db), _queue,
        new EventBroadcaster(new NoopHubContext(), _db.ScopeFactory()),
        _settings, new NamingService(_settings), new StatsEventService(db),
        new SeriesIdentityService(db, NullLogger<SeriesIdentityService>.Instance), new TestLocalizer(),
        new TestCurrentUser(1), NullLogger<LibraryImportService>.Instance);

    private SourceMatchWorkerHostedService Worker()
    {
        var services = new ServiceCollection();
        services.AddScoped(_ => _db.NewContext());
        services.AddSingleton<EventBroadcaster>(
            sp => new EventBroadcaster(new NoopHubContext(), sp.GetRequiredService<IServiceScopeFactory>()));
        services.AddScoped(sp => LinkService(sp.GetRequiredService<MakiDbContext>()));
        var scopes = services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
        return new SourceMatchWorkerHostedService(_queue, scopes, NullLogger<SourceMatchWorkerHostedService>.Instance);
    }

    private int SeedRoot()
    {
        using var db = _db.NewContext();
        var root = new RootFolder { Path = _root };
        db.RootFolders.Add(root);
        db.SaveChanges();
        return root.Id;
    }

    /// <summary>One comic of every shape the import handles, plus one it cannot read and one duplicate.</summary>
    private void WriteFixture(string folder)
    {
        WriteZip(At(folder, "Chainsaw Man 001.cbz"));
        WriteTar(At(folder, "Chainsaw Man 001.cbt"), "001.png");
        WriteZip(At(folder, "Chainsaw Man 002.zip"));
        Directory.CreateDirectory(At(folder, "Chainsaw Man 003"));
        File.WriteAllText(At(folder, "Chainsaw Man 003", "001.png"), "page");
        File.WriteAllText(At(folder, "Chainsaw Man 003", "002.png"), "page");
        WriteTar(At(folder, "Chainsaw Man 004.cbz"), "001.png", "002.png");
        File.WriteAllText(At(folder, "broken.cbz"), "not an archive");
    }

    private static List<string> Snapshot(string dir) =>
        Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(dir, f))
            .Order(StringComparer.Ordinal)
            .ToList();

    [Fact]
    public async Task The_plan_writes_nothing_and_predicts_what_the_import_does()
    {
        var rootId = SeedRoot();
        WriteFixture("chainsaw raws");
        var before = Snapshot(_root);

        LibraryImportPlan plan;
        await using (var db = _db.NewContext())
        {
            plan = await ImportService(db).PlanAsync(rootId, new ImportRequestItem("chainsaw raws", "42"));
        }

        Assert.Null(plan.Error);
        Assert.Equal(before, Snapshot(_root));
        await using (var db = _db.NewContext())
        {
            Assert.Empty(db.Series);
            Assert.Empty(db.ChapterFiles);
        }

        Assert.Equal(ImportFolderAction.Rename, plan.FolderAction);
        Assert.True(plan.LinkDeferred);
        var files = plan.Files!.ToDictionary(f => f.Name);
        Assert.Equal(ImportFileAction.Register, files["Chainsaw Man 001.cbz"].Action);
        Assert.Equal(ImportFileAction.Build, files["Chainsaw Man 002.cbz"].Action);
        Assert.Equal("zip", files["Chainsaw Man 002.cbz"].Kind);
        Assert.Equal(ImportFileAction.Build, files["Chainsaw Man 003.cbz"].Action);
        Assert.Equal("looseImages", files["Chainsaw Man 003.cbz"].Kind);
        Assert.Equal(ImportFileAction.RebuildInPlace, files["Chainsaw Man 004.cbz"].Action);
        Assert.Equal("Chainsaw Man 004.cbt", files["Chainsaw Man 004.cbz"].Aside);
        Assert.Equal(["1", "2", "3", "4"], plan.Files!.Select(f => f.Number));
        Assert.Contains(new ImportSkippedFile("broken.cbz", ImportSkipReason.Unreadable), plan.Skipped!);
        Assert.Contains(new ImportSkippedFile("Chainsaw Man 001.cbt", ImportSkipReason.Duplicate), plan.Skipped!);

        ImportResult result;
        await using (var db = _db.NewContext())
        {
            result = await ImportService(db).ImportAsync(rootId, new ImportRequestItem("chainsaw raws", "42"));
        }

        Assert.True(result.Success, result.Error);
        Assert.Equal(plan.TargetFolderName, result.NewFolderName);
        Assert.Equal(plan.Files!.Count, result.FilesAdded);
        Assert.Equal(
            plan.Skipped!.Where(s => s.Reason == ImportSkipReason.Unreadable),
            result.Skipped ?? []);

        var target = At(plan.TargetFolderName!);
        await using (var db = _db.NewContext())
        {
            var registered = await db.ChapterFiles.Select(f => f.RelativePath).ToListAsync();
            Assert.Equal(
                plan.Files!.Select(f => Path.Combine(plan.TargetFolderName!, f.Name)).Order(StringComparer.Ordinal),
                registered.Order(StringComparer.Ordinal));
        }

        foreach (var file in plan.Files!)
        {
            Assert.True(File.Exists(Path.Combine(target, file.Name)), file.Name);
            if (file.Aside is not null)
            {
                Assert.True(File.Exists(Path.Combine(target, file.Aside)), file.Aside);
            }
        }

        // The link stage, once the match has synced chapters, links each file to the chapter the
        // plan read off its name.
        await using (var db = _db.NewContext())
        {
            var series = await db.Series.SingleAsync();
            db.Chapters.AddRange(Enumerable.Range(1, 4).Select(n =>
                new Chapter { SeriesId = series.Id, Number = n, Language = "en" }));
            series.SourceMatchPending = false;
            await db.SaveChangesAsync();
        }

        var seriesId = result.SeriesId!.Value;
        await Worker().LinkAsync(seriesId, default);

        await using (var db = _db.NewContext())
        {
            var linked = await db.Chapters
                .Where(c => c.SeriesId == seriesId && c.ChapterFileId != null)
                .Join(db.ChapterFiles, c => c.ChapterFileId, f => f.Id, (c, f) => new { c.Number, f.RelativePath })
                .ToListAsync();
            foreach (var file in plan.Files!)
            {
                var path = Path.Combine(plan.TargetFolderName!, file.Name);
                Assert.Equal(decimal.Parse(file.Number!), Assert.Single(linked, l => l.RelativePath == path).Number);
            }
        }
    }

    [Fact]
    public async Task For_a_series_that_already_has_chapters_the_plan_names_the_chapters_each_file_links_to()
    {
        var rootId = SeedRoot();
        int seriesId;
        await using (var db = _db.NewContext())
        {
            var series = new Series
            {
                Title = "Chainsaw Man", SortTitle = "Chainsaw Man", FolderName = "chainsaw raws",
                RootFolderId = rootId, MangaBakaId = 42,
            };
            db.Series.Add(series);
            db.SaveChanges();
            db.Chapters.AddRange(Enumerable.Range(1, 3).Select(n =>
                new Chapter { SeriesId = series.Id, Number = n, Language = "en" }));
            db.SaveChanges();
            seriesId = series.Id;
        }

        _settings.Set(Maki.Core.Configuration.SettingKeys.LibraryFolderNamingMode,
            Maki.Core.Naming.FolderNamingMode.KeepOriginal);
        WriteZip(At("chainsaw raws", "Chainsaw Man 001.cbz"));
        WriteZip(At("chainsaw raws", "Chainsaw Man 002.cbz"));
        WriteZip(At("chainsaw raws", "Chainsaw Man 009.cbz"));

        LibraryImportPlan plan;
        await using (var db = _db.NewContext())
        {
            plan = await ImportService(db).PlanAsync(rootId, new ImportRequestItem("chainsaw raws", "42"));
        }

        Assert.Null(plan.Error);
        Assert.False(plan.LinkDeferred);
        Assert.Equal(seriesId, plan.ExistingSeriesId);
        Assert.Equal(ImportFolderAction.Keep, plan.FolderAction);
        Assert.Equal([["1"], ["2"], []], plan.Files!.Select(f => f.Chapters!.ToArray()));
        Assert.Equal(ImportSkipReason.NoMatchingChapter, plan.Files![2].Unlinked);

        ImportResult result;
        await using (var db = _db.NewContext())
        {
            result = await ImportService(db).ImportAsync(rootId, new ImportRequestItem("chainsaw raws", "42"),
                updateComicInfo: false);
        }

        Assert.True(result.Success, result.Error);
        Assert.False(result.LinkPending);
        Assert.Equal(new ImportSkippedFile("Chainsaw Man 009.cbz", ImportSkipReason.NoMatchingChapter),
            Assert.Single(result.Skipped!));
        await using var check = _db.NewContext();
        var linked = await check.Chapters
            .Where(c => c.SeriesId == seriesId && c.ChapterFileId != null)
            .Select(c => c.Number)
            .ToListAsync();
        Assert.Equal([1m, 2m], linked.Order());
    }

    [Fact]
    public async Task A_merge_preview_keeps_the_series_folder_CBZ_over_a_scanned_copy_to_repack()
    {
        var rootId = SeedRoot();
        await using (var db = _db.NewContext())
        {
            db.Series.Add(new Series
            {
                Title = "Chainsaw Man", SortTitle = "Chainsaw Man", FolderName = "Chainsaw Man",
                RootFolderId = rootId, MangaBakaId = 42,
            });
            await db.SaveChangesAsync();
        }

        // The series folder already holds a CBZ of chapter 1; the scanned folder brings a tar of it.
        WriteZip(At("Chainsaw Man", "Chainsaw Man 001.cbz"));
        WriteTar(At("cm raws", "Chainsaw Man 001.cbt"), "001.png");
        WriteZip(At("cm raws", "Chainsaw Man 002.cbz"));

        LibraryImportPlan plan;
        await using (var db = _db.NewContext())
        {
            plan = await ImportService(db).PlanAsync(rootId, new ImportRequestItem("cm raws", "42"));
        }

        Assert.Equal(ImportFolderAction.Merge, plan.FolderAction);
        var first = Assert.Single(plan.Files!, f => f.Name == "Chainsaw Man 001.cbz");
        Assert.Equal(ImportFileAction.Register, first.Action);
        Assert.Contains(new ImportSkippedFile("Chainsaw Man 001.cbt", ImportSkipReason.Duplicate), plan.Skipped!);

        ImportResult result;
        await using (var db = _db.NewContext())
        {
            result = await ImportService(db).ImportAsync(rootId, new ImportRequestItem("cm raws", "42"));
        }

        Assert.True(result.Success, result.Error);
        await using var check = _db.NewContext();
        Assert.Equal(
            plan.Files!.Select(f => Path.Combine("Chainsaw Man", f.Name)).Order(StringComparer.Ordinal),
            (await check.ChapterFiles.Select(f => f.RelativePath).ToListAsync()).Order(StringComparer.Ordinal));
        // Nothing was built from the tar: the CBZ already in the folder is the one registered.
        Assert.Equal(1, Directory.GetFiles(At("Chainsaw Man"), "Chainsaw Man 001.*").Count(f => f.EndsWith(".cbz")));
    }

    [Fact]
    public async Task The_plan_reports_the_refusal_the_import_would_give()
    {
        var rootId = SeedRoot();
        WriteZip(At("chainsaw raws", "Chainsaw Man 001.cbz"));

        await using var db = _db.NewContext();
        var gone = await ImportService(db).PlanAsync(rootId, new ImportRequestItem("missing", "42"));
        var escape = await ImportService(db).PlanAsync(rootId, new ImportRequestItem("../outside", "42"));

        Assert.Equal("error.libraryImport.folderGone", gone.Error);
        Assert.Equal("error.libraryImport.invalidFolderName", escape.Error);
    }

    private sealed class FixedProvider : IMetadataProvider
    {
        public string Name => "fake";

        public Task<IReadOnlyList<MetadataSearchResult>> SearchAsync(
            string query, string maxContentRating, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<MetadataSearchResult>>([]);

        public Task<SeriesMetadata?> GetAsync(string providerId, CancellationToken ct = default) =>
            Task.FromResult<SeriesMetadata?>(new SeriesMetadata
            {
                ProviderId = providerId, Title = "Chainsaw Man", MangaBakaId = 42,
            });
    }
}
