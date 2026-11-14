using Maki.Api.Services;
using Maki.Api.Services.Ocr;
using Maki.Core.Entities;
using Maki.Core.Images;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Maki.Api.Tests;

public sealed class CreditNoticeStoreTests : IDisposable
{
    private const int W = CreditDetector.Width;
    private const string Credit = "Join our Discord | Support us on Patreon";
    private const string Story = "Chapter 12 | The End?";

    private readonly TestDb _db = new();
    private readonly int _seriesId;
    private int _reads;

    public CreditNoticeStoreTests()
    {
        using var db = _db.NewContext();
        var folder = new RootFolder { Path = "/library" };
        db.RootFolders.Add(folder);
        db.SaveChanges();
        var series = new Series { Title = "S", SortTitle = "s", FolderName = "S", RootFolderId = folder.Id };
        db.Series.Add(series);
        db.SaveChanges();
        _seriesId = series.Id;
    }

    public void Dispose() => _db.Dispose();

    private CreditNoticeStore Store() =>
        new(_db.ScopeFactory(), new CreditStore(_db.ScopeFactory(), new NoReader(), NullLogger<CreditStore>.Instance),
            NullLogger<CreditNoticeStore>.Instance);

    private sealed class NoReader : IPageTextReader
    {
        public bool Available => true;

        public Task<string?> ReadAsync(byte[] image, double share, CancellationToken ct) => Task.FromResult<string?>(null);

        public Task PrepareAsync() => Task.CompletedTask;
    }

    private static byte[] Noise(int seed, int rows = 90)
    {
        var pixels = new byte[rows * W];
        new Random(seed).NextBytes(pixels);
        return pixels;
    }

    private static CreditNoticeStore.Query Query(
        byte[] rows, uint crc, int chapterId = 1, bool colour = true, bool fromBank = false) =>
        new(rows, rows.Length / W, true, 1, crc, 1000 + crc, colour, fromBank, chapterId);

    /// <summary>A read that counts itself and answers <paramref name="text"/>.</summary>
    private Func<CancellationToken, Task<string?>> Reads(string? text) => _ =>
    {
        _reads++;
        return Task.FromResult(text);
    };

    private int AddChapter(int number)
    {
        using var db = _db.NewContext();
        var file = new ChapterFile { SeriesId = _seriesId, RelativePath = $"S/c{number}.cbz", SourceName = "test", DateAdded = DateTime.UtcNow };
        db.ChapterFiles.Add(file);
        db.SaveChanges();
        var chapter = new Chapter { SeriesId = _seriesId, Number = number, ChapterFileId = file.Id };
        db.Chapters.Add(chapter);
        db.SaveChanges();
        db.ChapterCredits.Add(new ChapterCredits
        {
            ChapterId = chapter.Id, SeriesId = _seriesId, ChapterFileId = file.Id, EdgeKey = "k" + number,
            DetectorVersion = CreditDetector.Version, TextVersion = CreditText.Version, OcrVersion = PageTextReader.Version,
        });
        db.SaveChanges();
        return chapter.Id;
    }

    private int DetectorVersionOf(int chapterId)
    {
        using var db = _db.NewContext();
        return db.ChapterCredits.Where(c => c.ChapterId == chapterId).Select(c => c.DetectorVersion).First();
    }

    private int Count<T>() where T : class
    {
        using var db = _db.NewContext();
        return db.Set<T>().IgnoreQueryFilters().Count();
    }

    [Fact]
    public async Task A_second_chapter_carrying_an_accepted_notice_is_accepted_without_a_read()
    {
        var store = Store();
        var credit = Noise(7);

        var first = await store.JudgeAsync(Query(credit, crc: 1), Reads(Credit), default);
        var second = await store.JudgeAsync(Query(credit, crc: 2, chapterId: 2), Reads(Credit), default);

        Assert.True(first is { Accepted: true, Read: true, Text: Credit });
        Assert.True(second is { Accepted: true, Read: false, Text: Credit });
        Assert.Equal(first.NoticeId, second.NoticeId);
        Assert.Equal(1, _reads);
    }

    [Fact]
    public async Task A_different_page_is_its_own_notice()
    {
        var store = Store();

        var first = await store.JudgeAsync(Query(Noise(7), crc: 1), Reads(Credit), default);
        var second = await store.JudgeAsync(Query(Noise(8), crc: 2), Reads(Story), default);

        Assert.NotEqual(first.NoticeId, second.NoticeId);
        Assert.False(second.Accepted);
        Assert.Equal(2, _reads);
    }

    [Fact]
    public async Task A_rejected_notice_is_read_on_its_second_member_and_then_trusted()
    {
        var store = Store();
        var page = Noise(7);

        Assert.True((await store.JudgeAsync(Query(page, crc: 1), Reads(Story), default)) is { Accepted: false, Read: true });
        Assert.True((await store.JudgeAsync(Query(page, crc: 2), Reads(Story), default)) is { Accepted: false, Read: true });
        var third = await store.JudgeAsync(Query(page, crc: 3), Reads(Credit), default);

        Assert.True(third is { Accepted: false, Read: false, Text: Story });
        Assert.Equal(2, _reads);
    }

    [Fact]
    public async Task One_accepting_read_after_a_rejection_accepts_the_notice_and_requeues_the_earlier_chapter()
    {
        var store = Store();
        var page = Noise(7);
        var (earlier, later) = (AddChapter(1), AddChapter(2));
        var generation = store.Generation;

        var rejected = await store.JudgeAsync(Query(page, crc: 1, chapterId: earlier), Reads(Story), default);
        await store.SaveLinksAsync(earlier, _seriesId,
            [new JudgedCandidate(false, 0, 1, true, Story, false, NoticeId: rejected.NoticeId)], default);
        var accepted = await store.JudgeAsync(Query(page, crc: 2, chapterId: later), Reads(Credit), default);

        Assert.True(accepted is { Accepted: true, Read: true, Text: Credit });
        Assert.Equal(rejected.NoticeId, accepted.NoticeId);
        Assert.Equal(0, DetectorVersionOf(earlier));
        Assert.Equal(CreditDetector.Version, DetectorVersionOf(later));
        Assert.NotEqual(generation, store.Generation);
    }

    [Fact]
    public async Task The_same_bytes_are_never_read_twice_and_take_the_candidates_own_colour()
    {
        var store = Store();
        const string address = "visit mangaplace.com";

        // A bare web address reads as a scanlator's only on a page the colour rule takes for one.
        var grey = await store.JudgeAsync(Query(Noise(7), crc: 1, colour: false), Reads(address), default);
        var coloured = await store.JudgeAsync(Query(Noise(99), crc: 1, colour: true), Reads(Story), default);

        Assert.False(grey.Accepted);
        Assert.True(coloured is { Accepted: true, Read: false, Text: address });
        Assert.Equal(grey.NoticeId, coloured.NoticeId);
        Assert.Equal(1, _reads);

        using var db = _db.NewContext();
        Assert.Equal(1, db.CreditNotices.Single().Reads);
    }

    [Fact]
    public async Task A_bank_proposal_is_not_accepted_without_read_text()
    {
        var store = Store();

        var verdict = await store.JudgeAsync(Query(Noise(7), crc: 1, fromBank: true), Reads(null), default);

        Assert.True(verdict is { Accepted: false, Text: null, NoticeId: null });
        Assert.Equal(0, Count<CreditNotice>());
    }

    [Fact]
    public async Task A_read_that_returns_nothing_stores_nothing()
    {
        var store = Store();
        var page = Noise(7);

        Assert.Null((await store.JudgeAsync(Query(page, crc: 1), Reads(null), default)).Text);
        Assert.Equal(0, Count<CreditNotice>());
        Assert.Equal(0, Count<CreditNoticeRead>());

        // Nothing was learned, so the next member is read.
        await store.JudgeAsync(Query(page, crc: 1), Reads(Credit), default);
        Assert.Equal(2, _reads);
    }

    private async Task<int> SeedAsync(int ocrVersion, int textVersion, bool accepted, string readText, bool readColour)
    {
        var rows = Noise(7);
        using var db = _db.NewContext();
        var notice = new CreditNotice
        {
            Whole = true, Share = 1, Height = 90, Rows = rows,
            Fingerprint = CreditNotices.ToBytes(CreditNotices.Fingerprint(new CreditRegion(rows, 90))),
            Accepted = accepted, Reads = 1, Text = readText, OcrVersion = ocrVersion, TextVersion = textVersion,
            CreatedAtUtc = DateTime.UtcNow, UpdatedAtUtc = DateTime.UtcNow,
        };
        db.CreditNotices.Add(notice);
        db.SaveChanges();
        db.CreditNoticeReads.Add(new CreditNoticeRead
        {
            Crc = 1, Length = 1001, Share = 1000, Text = readText, Colour = readColour, NoticeId = notice.Id, CreatedAtUtc = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
        return notice.Id;
    }

    [Fact]
    public async Task Notices_read_by_another_OCR_version_are_cleared()
    {
        var old = await SeedAsync(PageTextReader.Version + 1, CreditText.Version, accepted: true, Credit, true);

        var verdict = await Store().JudgeAsync(Query(Noise(7), crc: 1), Reads(Story), default);

        Assert.True(verdict is { Accepted: false, Read: true });
        Assert.NotEqual(old, verdict.NoticeId);
        using var db = _db.NewContext();
        Assert.False(db.CreditNotices.Any(n => n.Id == old));
        Assert.Equal(1, db.CreditNoticeReads.Count());
    }

    [Fact]
    public async Task A_new_text_rule_judges_each_notice_again_from_its_stored_reads()
    {
        var id = await SeedAsync(PageTextReader.Version, CreditText.Version - 1, accepted: false, Credit, false);

        var verdict = await Store().JudgeAsync(Query(Noise(7), crc: 2), Reads(Story), default);

        Assert.True(verdict is { Accepted: true, Read: false, Text: Credit });
        Assert.Equal(id, verdict.NoticeId);
        Assert.Equal(0, _reads);
        using var db = _db.NewContext();
        Assert.Equal(CreditText.Version, db.CreditNotices.Single().TextVersion);
    }

    [Fact]
    public async Task Notices_no_chapter_links_to_are_pruned_once_old()
    {
        var store = Store();
        var chapter = AddChapter(1);
        var linked = await store.JudgeAsync(Query(Noise(7), crc: 1, chapterId: chapter), Reads(Credit), default);
        var loose = await store.JudgeAsync(Query(Noise(8), crc: 2, chapterId: chapter), Reads(Credit), default);
        await store.SaveLinksAsync(chapter, _seriesId,
            [new JudgedCandidate(false, 0, 1, true, Credit, true, NoticeId: linked.NoticeId)], default);

        Assert.Equal(0, await store.PruneAsync(default));
        using (var db = _db.NewContext())
        {
            await db.CreditNotices.ExecuteUpdateAsync(u => u.SetProperty(n => n.UpdatedAtUtc, DateTime.UtcNow.AddDays(-2)));
        }

        Assert.Equal(1, await store.PruneAsync(default));
        using var after = _db.NewContext();
        Assert.Equal([linked.NoticeId!.Value], after.CreditNotices.Select(n => n.Id).ToList());
        Assert.NotEqual(linked.NoticeId, loose.NoticeId);
    }
}
