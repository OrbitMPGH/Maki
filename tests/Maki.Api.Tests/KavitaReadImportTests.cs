using Maki.Api.Services;
using Maki.Core.Entities;
using Maki.Core.Kavita;
using Microsoft.Extensions.Logging.Abstractions;

namespace Maki.Api.Tests;

/// <summary>
/// The one-off import of read status out of Kavita: which Kavita chapters count as read, and
/// which local chapters get marked. The silent-merge half (that none of this reaches Rewind) is
/// covered in <see cref="RewindStatsTests"/>.
/// </summary>
public sealed class KavitaReadImportTests : IDisposable
{
    private readonly TestDb _db = new();

    public void Dispose() => _db.Dispose();

    private KavitaReadImportService Service()
    {
        var scopeFactory = _db.ScopeFactory();
        // Only the scope factory is exercised by MarkAsync; the Kavita client is never touched.
        return new KavitaReadImportService(scopeFactory, new SettingsService(scopeFactory), null!,
            NullLogger<KavitaReadImportService>.Instance);
    }

    private static KavitaProgress.KavitaChapterDto Chapter(
        double number, int pages, int pagesRead, bool special = false) =>
        new(number, number, pages, pagesRead, special);

    private static List<KavitaProgress.KavitaVolumeDto> Volume(params KavitaProgress.KavitaChapterDto[] chapters) =>
        [new(1, 1, chapters.Sum(c => c.Pages), chapters.Sum(c => c.PagesRead), chapters.ToList())];

    // ---- which Kavita chapters count as read ----

    [Fact]
    public void OnlyFullyReadChaptersCount()
    {
        var numbers = KavitaReadImportService.ReadChapterNumbers(Volume(
            Chapter(1, 20, 20),
            Chapter(2, 20, 19),
            Chapter(3, 20, 0)));

        Assert.Equal([1m], numbers);
    }

    [Fact]
    public void SpecialsAndSentinelNumbersAreSkipped()
    {
        // Kavita tags uncounted entries with huge sentinel numbers; matching one against a real
        // local chapter number would mark the wrong thing read.
        var numbers = KavitaReadImportService.ReadChapterNumbers(Volume(
            Chapter(5, 10, 10),
            Chapter(6, 10, 10, special: true),
            Chapter(100_000, 10, 10)));

        Assert.Equal([5m], numbers);
    }

    [Fact]
    public void ZeroPageChaptersAreNotRead()
    {
        // pagesRead >= pages is trivially true at 0/0 — that's an empty entry, not a read one.
        Assert.Empty(KavitaReadImportService.ReadChapterNumbers(Volume(Chapter(1, 0, 0))));
    }

    // ---- which local chapters get marked ----

    [Fact]
    public async Task MarksOnlyDownloadedChaptersWithMatchingNumbers()
    {
        var seriesId = Seed(
            (1m, true),
            (2m, true),
            (3m, false), // known but not downloaded — nothing to read
            (4m, true)); // not in Kavita's read set

        var marked = await Service().MarkAsync(seriesId, [1m, 2m, 3m], CancellationToken.None);

        Assert.Equal(2, marked);
        using var db = _db.NewContext();
        var rows = db.ChapterProgress.ToList();
        Assert.Equal(2, rows.Count);
        Assert.All(rows, r => Assert.True(r.Completed));
    }

    [Fact]
    public async Task ImportIsIdempotent()
    {
        var seriesId = Seed((1m, true), (2m, true));

        Assert.Equal(2, await Service().MarkAsync(seriesId, [1m, 2m], CancellationToken.None));
        Assert.Equal(0, await Service().MarkAsync(seriesId, [1m, 2m], CancellationToken.None));

        using var db = _db.NewContext();
        Assert.Equal(2, db.ChapterProgress.Count());
    }

    [Fact]
    public async Task ImportCompletesAnInProgressChapterButKeepsItsPosition()
    {
        var seriesId = Seed((1m, true));
        using (var db = _db.NewContext())
        {
            db.ChapterProgress.Add(new ChapterProgress
            {
                SeriesId = seriesId,
                ChapterId = db.Chapters.Single().Id,
                PageIndex = 4,
                PageCount = 20,
                Completed = false,
                StartedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            });
            db.SaveChanges();
        }

        await Service().MarkAsync(seriesId, [1m], CancellationToken.None);

        using var after = _db.NewContext();
        var row = after.ChapterProgress.Single();
        Assert.True(row.Completed);
        Assert.Equal(4, row.PageIndex);
        Assert.Equal(20, row.PageCount);
    }

    private int Seed(params (decimal Number, bool Downloaded)[] chapters)
    {
        var seriesId = _db.SeedSeries("Imported Series");
        using var db = _db.NewContext();
        var file = new ChapterFile
        {
            SeriesId = seriesId,
            RelativePath = "x.cbz",
            SourceName = "Test",
            DateAdded = DateTime.UtcNow,
        };
        db.ChapterFiles.Add(file);
        db.SaveChanges();

        db.Chapters.AddRange(chapters.Select(c => new Chapter
        {
            SeriesId = seriesId,
            Number = c.Number,
            Language = "en",
            ChapterFileId = c.Downloaded ? file.Id : null,
        }));
        db.SaveChanges();
        return seriesId;
    }
}
