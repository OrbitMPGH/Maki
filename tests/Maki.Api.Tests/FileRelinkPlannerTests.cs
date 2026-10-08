using System.IO.Compression;
using Maki.Api.Services;
using Maki.Core.Entities;
using Maki.Core.Kavita;
using Maki.Core.Reading;
using Maki.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Maki.Api.Tests;

public class FileRelinkPlannerTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly FakeAppSettings _settings = new();
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "maki-relink-" + Guid.NewGuid().ToString("N")[..8]);

    public void Dispose()
    {
        _db.Dispose();
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    /// <summary>
    /// A volume whose pages name chapters 1-3 does not contain 4-6, whatever the provider says
    /// volume 1 holds, so those chapters stay on their own files and those files are not superseded.
    /// </summary>
    [Fact]
    public async Task A_partial_volume_takes_only_the_chapters_its_pages_name()
    {
        int seriesId;
        using (var db = _db.NewContext())
        {
            var rootFolder = new RootFolder { Path = _root };
            db.RootFolders.Add(rootFolder);
            db.SaveChanges();
            var series = new Series
            {
                Title = "Berserk", SortTitle = "Berserk", FolderName = "Berserk", RootFolderId = rootFolder.Id
            };
            db.Series.Add(series);
            db.SaveChanges();
            seriesId = series.Id;

            foreach (var number in Enumerable.Range(1, 6))
            {
                var relative = Path.Combine("Berserk", $"Berserk Vol.1 Ch.{number}.cbz");
                WriteCbz(Path.Combine(_root, relative), [$"Berserk - c00{number} - p001.png"]);
                var file = new ChapterFile
                {
                    SeriesId = series.Id, RelativePath = relative, SourceName = "mangadex", DateAdded = DateTime.UtcNow
                };
                db.ChapterFiles.Add(file);
                db.SaveChanges();
                db.Chapters.Add(new Chapter
                {
                    SeriesId = series.Id, Number = number, Volume = 1, Language = "en", ChapterFileId = file.Id
                });
                db.SaveChanges();
            }
        }

        WriteCbz(
            Path.Combine(_root, "Berserk", "Berserk v01 (Digital) (1r0n).cbz"),
            [.. Enumerable.Range(1, 3).Select(c => $"Berserk - c{c:000} - p001 [Oak].png")]);

        using var context = _db.NewContext();
        var loaded = await context.Series.Include(s => s.RootFolder).SingleAsync(s => s.Id == seriesId);
        var scans = new KavitaScanService(
            new KavitaClient(new StubHttpClientFactory("{}")), _settings, _db.ScopeFactory(),
            NullLogger<KavitaScanService>.Instance);
        var planner = new FileRelinkPlanner(
            context, new ReaderArchiveCache(NullLogger<ReaderArchiveCache>.Instance), scans,
            new ChapterFileDeletion(context, new ReaderArchiveCache(NullLogger<ReaderArchiveCache>.Instance),
                TimeProvider.System, NullLogger<ChapterFileDeletion>.Instance),
            NullLogger<FileRelinkPlanner>.Instance);

        var plan = await planner.PlanAsync(loaded, RelinkOptions.None);

        Assert.Equal(3, plan.SupersededCount);
        var volume = Assert.Single(plan.Files, f => f.IsVolume);
        Assert.Equal(["1", "2", "3"], volume.Chapters);
        var chapterFour = Assert.Single(plan.Files, f => f.FileName == "Berserk Vol.1 Ch.4.cbz");
        Assert.False(chapterFour.Superseded);
        Assert.Empty(chapterFour.Loses);
    }

    [Fact]
    public async Task A_volume_known_only_by_estimate_is_never_superseded()
    {
        var seriesId = SeedCompletedSeries();
        foreach (var number in Enumerable.Range(41, 10))
        {
            WriteSeriesFile($"Series Ch.{number}.cbz");
        }

        var plan = await PlanAsync(seriesId);

        var volumeFive = Assert.Single(plan.Files, f => f.FileName == "Series v05.cbz");
        Assert.Empty(volumeFive.Chapters);
        Assert.False(volumeFive.Superseded);
        Assert.Equal(0, plan.SupersededCount);
    }

    [Fact]
    public async Task A_volume_with_a_partial_range_and_estimated_chapters_is_not_superseded_by_its_twin()
    {
        var seriesId = SeedCompletedSeries(providerVolumeFive: [41, 42, 43]);
        WriteSeriesFile("Series v05 (Digital).cbz");

        var plan = await PlanAsync(seriesId);

        Assert.Equal(10, Assert.Single(plan.Files, f => f.FileName == "Series v05 (Digital).cbz").Chapters.Count);
        var plain = Assert.Single(plan.Files, f => f.FileName == "Series v05.cbz");
        Assert.Empty(plain.Chapters);
        Assert.False(plain.Superseded);
    }

    [Fact]
    public async Task A_named_single_beats_an_existing_guess_on_a_volume_without_evidence()
    {
        var seriesId = SeedSeries(s => s.Status = SeriesStatus.Ongoing);
        using (var db = _db.NewContext())
        {
            var volume = AddFile(db, seriesId, "Series v05.cbz");
            db.Chapters.AddRange(Enumerable.Range(41, 10).Select(n => new Chapter
            {
                SeriesId = seriesId, Number = n, Language = "en", ChapterFileId = volume.Id
            }));
            db.SaveChanges();
        }

        WriteSeriesFile("Series v05.cbz");
        WriteSeriesFile("Series Ch.45.cbz");

        var plan = await PlanAsync(seriesId);

        var single = Assert.Single(plan.Files, f => f.FileName == "Series Ch.45.cbz");
        Assert.Equal(["45"], single.Chapters);
        Assert.False(single.Superseded);
        var volumeFive = Assert.Single(plan.Files, f => f.FileName == "Series v05.cbz");
        Assert.Equal(["45"], volumeFive.Loses.Select(r => r.Label));
        Assert.Equal(9, volumeFive.Chapters.Count);
        Assert.False(volumeFive.Superseded);
    }

    /// <summary>A provider range for 41-43 says nothing about the guessed 44-50 on the same volume.</summary>
    [Fact]
    public async Task A_partial_range_does_not_vouch_for_the_volume_s_other_guessed_links()
    {
        var seriesId = SeedSeries(s => s.Status = SeriesStatus.Ongoing);
        using (var db = _db.NewContext())
        {
            var volume = AddFile(db, seriesId, "Series v05.cbz");
            db.Chapters.AddRange(Enumerable.Range(41, 10).Select(n => new Chapter
            {
                SeriesId = seriesId, Number = n, Volume = n <= 43 ? 5 : null, Language = "en", ChapterFileId = volume.Id
            }));
            db.SaveChanges();
        }

        WriteSeriesFile("Series v05.cbz");
        WriteSeriesFile("Series Ch.45.cbz");

        var plan = await PlanAsync(seriesId);

        var single = Assert.Single(plan.Files, f => f.FileName == "Series Ch.45.cbz");
        Assert.Equal(["45"], single.Chapters);
        Assert.False(single.Superseded);
    }

    /// <summary>Pages marking 1-4 are evidence the volume lacks 5, whatever it is linked to today.</summary>
    [Fact]
    public async Task Page_markers_do_not_vouch_for_a_guessed_link_they_leave_out()
    {
        var seriesId = SeedSeries(s =>
        {
            s.Status = SeriesStatus.Completed;
            s.TotalVolumes = 1;
            s.TotalChapters = 10;
        });
        using (var db = _db.NewContext())
        {
            var volume = AddFile(db, seriesId, "Series v01.cbz");
            db.Chapters.AddRange(Enumerable.Range(1, 10).Select(n => new Chapter
            {
                SeriesId = seriesId, Number = n, Language = "en", ChapterFileId = volume.Id
            }));
            db.SaveChanges();
        }

        WriteSeriesFile("Series v01.cbz", [.. Enumerable.Range(1, 4).Select(c => $"Series - c{c:000} - p001.png")]);
        WriteSeriesFile("Series Ch.5.cbz");

        var plan = await PlanAsync(seriesId);

        var single = Assert.Single(plan.Files, f => f.FileName == "Series Ch.5.cbz");
        Assert.Equal(["5"], single.Chapters);
        Assert.False(single.Superseded);
    }

    [Fact]
    public async Task An_english_file_does_not_take_the_spanish_row()
    {
        var seriesId = SeedSeries();
        int spanishId;
        using (var db = _db.NewContext())
        {
            var file = AddFile(db, seriesId, "Series Ch.45.cbz");
            var spanish = new Chapter { SeriesId = seriesId, Number = 45, Language = "es" };
            db.Chapters.AddRange(
                new Chapter { SeriesId = seriesId, Number = 45, Language = "en", ChapterFileId = file.Id }, spanish);
            db.SaveChanges();
            spanishId = spanish.Id;
        }

        WriteSeriesFile("Series Ch.45.cbz");

        var plan = await PlanAsync(seriesId);

        Assert.Equal(0, plan.Moved);
        Assert.Empty(Assert.Single(plan.Files).Gains);
        Assert.Equal("missing", Assert.Single(plan.Chapters, c => c.Id == spanishId).State);
    }

    /// <summary>A library already mislinked is repaired: the Spanish row comes off the English file.</summary>
    [Fact]
    public async Task A_spanish_row_already_on_the_english_file_is_unlinked()
    {
        var seriesId = SeedSeries();
        int englishId, spanishId;
        using (var db = _db.NewContext())
        {
            var file = AddFile(db, seriesId, "Series Ch.45.cbz");
            var english = new Chapter { SeriesId = seriesId, Number = 45, Language = "en", ChapterFileId = file.Id };
            var spanish = new Chapter { SeriesId = seriesId, Number = 45, Language = "es", ChapterFileId = file.Id };
            db.Chapters.AddRange(english, spanish);
            db.SaveChanges();
            englishId = english.Id;
            spanishId = spanish.Id;
        }

        WriteSeriesFile("Series Ch.45.cbz");

        var plan = await PlanAsync(seriesId);

        Assert.Equal("missing", Assert.Single(plan.Chapters, c => c.Id == spanishId).State);
        Assert.Equal([spanishId], Assert.Single(plan.Files).Loses.Select(r => r.Id));

        await ApplyAsync(seriesId);

        using var check = _db.NewContext();
        Assert.Null(check.Chapters.Single(c => c.Id == spanishId).ChapterFileId);
        Assert.NotNull(check.Chapters.Single(c => c.Id == englishId).ChapterFileId);
    }

    [Fact]
    public async Task An_untagged_file_takes_the_default_language_row()
    {
        var (seriesId, englishId, spanishId) = SeedTwoLanguageChapter();
        WriteSeriesFile("Series Ch.45.cbz");

        var plan = await PlanAsync(seriesId);

        Assert.Equal([englishId], Assert.Single(plan.Files).Gains.Select(r => r.Id));
        Assert.Equal("missing", Assert.Single(plan.Chapters, c => c.Id == spanishId).State);
    }

    [Fact]
    public async Task A_language_tag_picks_the_row_for_an_unlinked_file()
    {
        var (seriesId, englishId, spanishId) = SeedTwoLanguageChapter();
        WriteSeriesFile("Series Ch.45 [es].cbz");

        var plan = await PlanAsync(seriesId);

        Assert.Equal([spanishId], Assert.Single(plan.Files).Gains.Select(r => r.Id));
        Assert.Equal("missing", Assert.Single(plan.Chapters, c => c.Id == englishId).State);
    }

    [Fact]
    public async Task A_tag_is_ignored_when_the_series_has_one_language()
    {
        var seriesId = SeedSeries();
        using (var db = _db.NewContext())
        {
            db.Chapters.Add(new Chapter { SeriesId = seriesId, Number = 45, Language = "en" });
            db.SaveChanges();
        }

        WriteSeriesFile("Series Ch.45 [fr].cbz");

        var plan = await PlanAsync(seriesId);

        Assert.Single(Assert.Single(plan.Files).Gains);
    }

    [Fact]
    public async Task A_manual_link_keeps_its_row_over_the_untagged_convention()
    {
        var seriesId = SeedSeries();
        using (var db = _db.NewContext())
        {
            var file = AddFile(db, seriesId, "Series Ch.45 MANUAL.cbz");
            file.SourceName = "Manual";
            db.Chapters.Add(new Chapter { SeriesId = seriesId, Number = 45, Language = "en" });
            db.Chapters.Add(new Chapter { SeriesId = seriesId, Number = 45, Language = "es", ChapterFileId = file.Id });
            db.SaveChanges();
        }

        WriteSeriesFile("Series Ch.45 MANUAL.cbz");

        var plan = await PlanAsync(seriesId);

        var single = Assert.Single(plan.Files);
        Assert.Empty(single.Gains);
        Assert.Empty(single.Loses);
    }

    /// <summary>Real evidence on both sides: a provider range beats named singles, and wins a tie with its twin.</summary>
    [Fact]
    public async Task Files_whose_chapters_a_provider_volume_holds_are_superseded()
    {
        var seriesId = SeedSeries();
        using (var db = _db.NewContext())
        {
            foreach (var number in Enumerable.Range(1, 3))
            {
                var file = AddFile(db, seriesId, $"Series Ch.{number}.cbz");
                db.Chapters.Add(new Chapter
                {
                    SeriesId = seriesId, Number = number, Volume = 1, Language = "en", ChapterFileId = file.Id
                });
                db.SaveChanges();
                WriteSeriesFile($"Series Ch.{number}.cbz");
            }
        }

        WriteSeriesFile("Series v01.cbz");
        WriteSeriesFile("Series v01 (Digital).cbz");

        var plan = await PlanAsync(seriesId);

        Assert.Equal(4, plan.SupersededCount);
        Assert.All(plan.Files.Where(f => !f.IsVolume), f => Assert.True(f.Superseded));
        Assert.True(Assert.Single(plan.Files, f => f.FileName == "Series v01.cbz").Superseded);
        Assert.Equal(["1", "2", "3"], Assert.Single(plan.Files, f => f.FileName == "Series v01 (Digital).cbz").Chapters);
    }

    /// <summary>Completed, 12 volumes of 120 chapters, a file per volume and no page markers.</summary>
    private int SeedCompletedSeries(int[]? providerVolumeFive = null)
    {
        var seriesId = SeedSeries(s =>
        {
            s.Status = SeriesStatus.Completed;
            s.TotalVolumes = 12;
            s.TotalChapters = 120;
        });
        using (var db = _db.NewContext())
        {
            db.Chapters.AddRange(Enumerable.Range(1, 120).Select(n => new Chapter
            {
                SeriesId = seriesId,
                Number = n,
                Volume = providerVolumeFive?.Contains(n) == true ? 5 : null,
                Language = "en"
            }));
            db.SaveChanges();
        }

        foreach (var volume in Enumerable.Range(1, 12))
        {
            WriteSeriesFile($"Series v{volume:00}.cbz");
        }

        return seriesId;
    }

    private (int SeriesId, int EnglishId, int SpanishId) SeedTwoLanguageChapter()
    {
        var seriesId = SeedSeries();
        using var db = _db.NewContext();
        var english = new Chapter { SeriesId = seriesId, Number = 45, Language = "en" };
        var spanish = new Chapter { SeriesId = seriesId, Number = 45, Language = "es" };
        db.Chapters.AddRange(english, spanish);
        db.SaveChanges();
        return (seriesId, english.Id, spanish.Id);
    }

    private int SeedSeries(Action<Series>? configure = null)
    {
        using var db = _db.NewContext();
        var rootFolder = new RootFolder { Path = _root };
        db.RootFolders.Add(rootFolder);
        db.SaveChanges();
        var series = new Series
        {
            Title = "Series", SortTitle = "Series", FolderName = "Series", RootFolderId = rootFolder.Id
        };
        configure?.Invoke(series);
        db.Series.Add(series);
        db.SaveChanges();
        return series.Id;
    }

    private static ChapterFile AddFile(MakiDbContext db, int seriesId, string fileName)
    {
        var file = new ChapterFile
        {
            SeriesId = seriesId,
            RelativePath = Path.Combine("Series", fileName),
            SourceName = "mangadex",
            DateAdded = DateTime.UtcNow
        };
        db.ChapterFiles.Add(file);
        db.SaveChanges();
        return file;
    }

    private void WriteSeriesFile(string fileName, IReadOnlyList<string>? pageNames = null) =>
        WriteCbz(Path.Combine(_root, "Series", fileName), pageNames ?? ["001.png"]);

    private async Task<RelinkPlan> PlanAsync(int seriesId)
    {
        using var context = _db.NewContext();
        var loaded = await context.Series.Include(s => s.RootFolder).SingleAsync(s => s.Id == seriesId);
        return await Planner(context).PlanAsync(loaded, RelinkOptions.None);
    }

    private async Task<RelinkResult> ApplyAsync(
        int seriesId, bool deleteSuperseded = false, IReadOnlyCollection<string>? confirmedSuperseded = null)
    {
        using var context = _db.NewContext();
        var loaded = await context.Series.Include(s => s.RootFolder).SingleAsync(s => s.Id == seriesId);
        return await Planner(context).ApplyAsync(loaded, RelinkOptions.None, deleteSuperseded, confirmedSuperseded ?? []);
    }

    private (int SeriesId, int ChapterId, string SinglePath) SeedSupersededSingle()
    {
        var seriesId = SeedSeries();
        int chapterId;
        string singlePath;
        using (var db = _db.NewContext())
        {
            var file = AddFile(db, seriesId, "Series Vol.1 Ch.1.cbz");
            singlePath = file.RelativePath;
            var chapter = new Chapter { SeriesId = seriesId, Number = 1, Volume = 1, Language = "en", ChapterFileId = file.Id };
            db.Chapters.Add(chapter);
            db.SaveChanges();
            chapterId = chapter.Id;
        }

        WriteSeriesFile("Series Vol.1 Ch.1.cbz", ["Series - c001 - p001.png"]);
        WriteSeriesFile("Series v01 (Digital).cbz", ["Series - c001 - p001 [Grp].png"]);
        return (seriesId, chapterId, singlePath);
    }

    [Fact]
    public async Task Apply_deletes_a_superseded_file_the_caller_confirmed()
    {
        var (seriesId, chapterId, singlePath) = SeedSupersededSingle();
        var plan = await PlanAsync(seriesId);
        Assert.Equal([singlePath], plan.Files.Where(f => f.Superseded).Select(f => f.RelativePath));

        var result = await ApplyAsync(seriesId, deleteSuperseded: true, confirmedSuperseded: [singlePath]);

        Assert.Equal(1, result.Deleted);
        Assert.False(File.Exists(Path.Combine(_root, singlePath)));
        using var check = _db.NewContext();
        var chapter = check.Chapters.Single(c => c.Id == chapterId);
        Assert.EndsWith("Series v01 (Digital).cbz", check.ChapterFiles.Single(f => f.Id == chapter.ChapterFileId).RelativePath);
        Assert.DoesNotContain(check.ChapterFiles, f => f.RelativePath == singlePath);
    }

    /// <summary>
    /// The plan is recomputed on apply, so a file that became superseded after the preview was
    /// never shown to the user as one and must survive a delete they confirmed for other files.
    /// </summary>
    [Fact]
    public async Task Apply_keeps_a_superseded_file_the_caller_never_saw()
    {
        var (seriesId, chapterId, singlePath) = SeedSupersededSingle();

        var result = await ApplyAsync(seriesId, deleteSuperseded: true, confirmedSuperseded: []);

        Assert.Equal(0, result.Deleted);
        Assert.Equal(1, result.Moved);
        Assert.True(File.Exists(Path.Combine(_root, singlePath)));
        using var check = _db.NewContext();
        var chapter = check.Chapters.Single(c => c.Id == chapterId);
        Assert.EndsWith("Series v01 (Digital).cbz", check.ChapterFiles.Single(f => f.Id == chapter.ChapterFileId).RelativePath);
    }

    [Fact]
    public async Task Apply_keeps_a_superseded_file_another_series_records()
    {
        var (seriesId, _, singlePath) = SeedSupersededSingle();
        var otherId = SeedSeries(s =>
        {
            s.Title = "Other";
            s.SortTitle = "Other";
            s.FolderName = "Other";
        });
        using (var db = _db.NewContext())
        {
            db.ChapterFiles.Add(new ChapterFile
            {
                SeriesId = otherId, RelativePath = singlePath, SourceName = "manual", DateAdded = DateTime.UtcNow
            });
            db.SaveChanges();
        }

        var result = await ApplyAsync(seriesId, deleteSuperseded: true, confirmedSuperseded: [singlePath]);

        Assert.Equal(0, result.Deleted);
        Assert.Equal(0, result.Failed);
        Assert.True(File.Exists(Path.Combine(_root, singlePath)));
        using var check = _db.NewContext();
        Assert.DoesNotContain(check.ChapterFiles, f => f.SeriesId == seriesId && f.RelativePath == singlePath);
        Assert.Single(check.ChapterFiles, f => f.SeriesId == otherId);
    }

    [Fact]
    public async Task Apply_without_delete_leaves_a_confirmed_superseded_file_on_disk()
    {
        var (seriesId, _, singlePath) = SeedSupersededSingle();

        var result = await ApplyAsync(seriesId, deleteSuperseded: false, confirmedSuperseded: [singlePath]);

        Assert.Equal(0, result.Deleted);
        Assert.Equal(1, result.Superseded);
        Assert.True(File.Exists(Path.Combine(_root, singlePath)));
    }

    private FileRelinkPlanner Planner(MakiDbContext context)
    {
        var scans = new KavitaScanService(
            new KavitaClient(new StubHttpClientFactory("{}")), _settings, _db.ScopeFactory(),
            NullLogger<KavitaScanService>.Instance);
        return new FileRelinkPlanner(
            context, new ReaderArchiveCache(NullLogger<ReaderArchiveCache>.Instance), scans,
            new ChapterFileDeletion(context, new ReaderArchiveCache(NullLogger<ReaderArchiveCache>.Instance),
                TimeProvider.System, NullLogger<ChapterFileDeletion>.Instance),
            NullLogger<FileRelinkPlanner>.Instance);
    }

    private static void WriteCbz(string path, IReadOnlyList<string> pageNames)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var name in pageNames)
        {
            using var writer = new StreamWriter(archive.CreateEntry(name).Open());
            writer.Write("page");
        }
    }
}
