using Maki.Api.Services;
using Maki.Core.Entities;
using Maki.Core.Sources;
using Microsoft.Extensions.Logging.Abstractions;

namespace Maki.Api.Tests;

public sealed class ChapterPageHashServiceTests : IDisposable
{
    private readonly TestDb _db = new();

    public void Dispose() => _db.Dispose();

    private static SourceMapping Mapping(string source) => new()
    {
        SourceName = source, SourceSeriesId = source + "-series", Url = $"https://{source}.test", Priority = 1, Enabled = true,
    };

    /// <summary>Floor 3 as 3.1 and 3.2 on "parts" and whole on "whole", counted so the counts nominate a merge.</summary>
    private int SeedNominatedFloor()
    {
        var seriesId = _db.SeedSeries(mappings: [Mapping("parts"), Mapping("whole")]);
        using var db = _db.NewContext();
        var mappings = db.SourceMappings.Where(m => m.SeriesId == seriesId).ToDictionary(m => m.SourceName);
        void Link(string source, decimal number, int pages)
        {
            var chapter = db.Chapters.Local.FirstOrDefault(c => c.Number == number)
                          ?? db.Chapters.Add(new Chapter { SeriesId = seriesId, Number = number, Language = "en" }).Entity;
            db.ChapterSourceLinks.Add(new ChapterSourceLink
            {
                Chapter = chapter, SourceMappingId = mappings[source].Id,
                SourceChapterId = FormattableString.Invariant($"{source}-{number}"), NumberRaw = number.ToString(System.Globalization.CultureInfo.InvariantCulture),
                PageCount = pages,
            });
        }

        Link("parts", 3.1m, 13);
        Link("parts", 3.2m, 26);
        Link("parts", 4, 50);
        Link("whole", 3, 39);
        Link("whole", 4, 50);
        db.SaveChanges();
        return seriesId;
    }

    private static List<ulong> Hashes(int from, int count) => [.. Enumerable.Range(from, count).Select(seed =>
    {
        var random = new Random(seed);
        ulong hash;
        do
        {
            hash = (ulong)random.NextInt64();
        }
        while (System.Numerics.BitOperations.PopCount(hash) is < 24 or > 40);

        return hash;
    })];

    private sealed class FakeHasher(Func<SourceChapter, IReadOnlyList<ulong>?> hash) : IChapterPageHasher
    {
        public List<string> Asked { get; } = [];

        public Task<PageEvidence?> HashAsync(ISource source, SourceChapter chapter, CancellationToken ct)
        {
            Asked.Add(chapter.SourceChapterId);
            return Task.FromResult(hash(chapter) is { } pages ? new PageEvidence(pages, pages) : null);
        }
    }

    private ChapterPageHashService Service(IChapterPageHasher hasher, PageCountFailures? failures = null)
    {
        var db = _db.NewContext();
        return new ChapterPageHashService(
            db,
            new ChapterFoldPreviewService(db, Sources.AllEnabled),
            new SourceRegistry([new FakeSource { Name = "parts" }, new FakeSource { Name = "whole" }]),
            new DownloadQueueService(null!, TimeProvider.System, null!, NullLogger<DownloadQueueService>.Instance),
            hasher,
            failures ?? new PageCountFailures(),
            TimeProvider.System,
            NullLogger<ChapterPageHashService>.Instance);
    }

    private async Task<ChapterFoldFloorDto> FloorThree(int seriesId)
    {
        using var db = _db.NewContext();
        var preview = await new ChapterFoldPreviewService(db, Sources.AllEnabled).ForSeriesAsync(seriesId, CancellationToken.None);
        return preview!.Floors.Single(f => f.Floor == 3);
    }

    [Fact]
    public async Task Hashes_what_a_nominated_merge_waits_on_and_the_pages_then_fold_it()
    {
        var seriesId = SeedNominatedFloor();
        Assert.Equal("needsPageCheck", (await FloorThree(seriesId)).Reason);
        var whole = Hashes(0, 39);
        var hasher = new FakeHasher(c => c.SourceChapterId switch
        {
            "whole-3" => whole,
            "parts-3.1" => whole[..13],
            "parts-3.2" => whole[13..],
            _ => null,
        });

        var run = await Service(hasher).RunAsync(10, CancellationToken.None);

        Assert.Equal(3, run.Hashed);
        Assert.Equal(["parts-3.1", "parts-3.2", "whole-3"], hasher.Asked.Order());
        var three = await FloorThree(seriesId);
        Assert.Equal("Merged", three.State);
        Assert.Equal("pagesMatch", three.Reason);
        Assert.True(three.Fold);
        Assert.Empty(three.MissingPages);
    }

    [Fact]
    public async Task Parts_whose_pages_are_elsewhere_stay_rows_of_their_own()
    {
        var seriesId = SeedNominatedFloor();
        var hasher = new FakeHasher(c => c.SourceChapterId switch
        {
            "whole-3" => Hashes(0, 39),
            "parts-3.1" => Hashes(500, 13),
            "parts-3.2" => Hashes(13, 26),
            _ => null,
        });

        await Service(hasher).RunAsync(10, CancellationToken.None);

        var three = await FloorThree(seriesId);
        Assert.Equal("Keep", three.State);
        Assert.Equal("pagesDiffer", three.Reason);
        Assert.False(three.Fold);
        Assert.Equal([3.1m, 3.2m], three.Held["parts"]);
    }

    [Fact]
    public async Task A_failed_hash_is_not_retried_in_the_next_run()
    {
        SeedNominatedFloor();
        var failures = new PageCountFailures();
        var hasher = new FakeHasher(_ => null);

        await Service(hasher, failures).RunAsync(10, CancellationToken.None);
        var second = await Service(hasher, failures).RunAsync(10, CancellationToken.None);

        Assert.Equal(3, hasher.Asked.Count);
        Assert.Equal(0, second.Hashed);
        Assert.Equal(3, second.Skipped);
    }

    [Fact]
    public async Task The_budget_caps_the_chapters_of_a_run()
    {
        SeedNominatedFloor();
        var hasher = new FakeHasher(_ => Hashes(0, 10));

        var run = await Service(hasher).RunAsync(2, CancellationToken.None);

        Assert.Equal(2, run.Hashed);
        Assert.Equal(3, run.Waiting);
    }

    [Fact]
    public async Task Only_chapters_on_a_floor_with_parts_are_hashed_at_download()
    {
        var seriesId = SeedNominatedFloor();
        using var db = _db.NewContext();

        Assert.True(await PageHashStore.WantedForAsync(db, seriesId, 3, CancellationToken.None));
        Assert.True(await PageHashStore.WantedForAsync(db, seriesId, 3.2m, CancellationToken.None));
        Assert.False(await PageHashStore.WantedForAsync(db, seriesId, 4, CancellationToken.None));
    }

    [Fact]
    public async Task Hashes_are_stored_on_that_upload_only()
    {
        var seriesId = SeedNominatedFloor();
        using (var db = _db.NewContext())
        {
            await PageHashStore.StoreAsync(db, db.SourceMappings.Single(m => m.SeriesId == seriesId && m.SourceName == "whole").Id,
                "whole-3", new PageEvidence(Hashes(0, 39), Hashes(0, 60)), CancellationToken.None);
        }

        using var check = _db.NewContext();
        var stored = check.ChapterSourceLinks.Single(l => l.SourceChapterId == "whole-3");
        Assert.Equal(39 * 16, stored.PageHashes?.Length);
        Assert.Equal(Hashes(0, 60), ChapterPageMatch.ParseStrip(stored.StripHashes));
        Assert.All(check.ChapterSourceLinks.Where(l => l.SourceChapterId != "whole-3"), l => Assert.Null(l.PageHashes));
    }
}
