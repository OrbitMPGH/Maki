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

        public Task<string?> ReadBottomAsync(byte[] image, double share, CancellationToken ct) => Task.FromResult<string?>(null);

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

    private int AddChapter(int number, string? group = null)
    {
        using var db = _db.NewContext();
        var file = new ChapterFile
        {
            SeriesId = _seriesId, RelativePath = $"S/c{number}.cbz", SourceName = "test", DateAdded = DateTime.UtcNow, Group = group,
        };
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
    public async Task The_top_and_the_bottom_of_one_page_are_different_reads()
    {
        var store = Store();
        CreditNoticeStore.Query Partial(byte[] rows, bool bottom) =>
            new(rows, rows.Length / W, false, 0.3, 5, 2000, true, false, 1, bottom);

        var top = await store.JudgeAsync(Partial(Noise(7, 30), bottom: false), Reads(Story), default);
        var bottom = await store.JudgeAsync(Partial(Noise(99, 30), bottom: true), Reads(Credit), default);
        var again = await store.JudgeAsync(Partial(Noise(98, 30), bottom: true), Reads(Story), default);

        Assert.False(top.Accepted);
        Assert.True(bottom is { Accepted: true, Read: true, Text: Credit });
        Assert.NotEqual(top.NoticeId, bottom.NoticeId);
        Assert.True(again is { Accepted: true, Read: false, Text: Credit });
        Assert.Equal(bottom.NoticeId, again.NoticeId);
        Assert.Equal(2, _reads);
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

    private async Task<int> AcceptedNoticeAsync(CreditNoticeStore store, byte[] rows, string text = Credit)
    {
        var verdict = await store.JudgeAsync(Query(rows, crc: 900), Reads(text), default);
        return verdict.NoticeId!.Value;
    }

    private CreditNotice Notice(int id)
    {
        using var db = _db.NewContext();
        return db.CreditNotices.AsNoTracking().Single(n => n.Id == id);
    }

    private async Task UpdateNoticeAsync(int id, Action<CreditNotice> change)
    {
        using var db = _db.NewContext();
        var notice = await db.CreditNotices.SingleAsync(n => n.Id == id);
        change(notice);
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task Keep_rejects_an_accepted_notice_without_a_read()
    {
        var store = Store();
        var page = Noise(7);
        var id = await AcceptedNoticeAsync(store, page);

        Assert.True(await store.SetOverrideAsync(id, CreditNoticeOverride.Keep, default));
        var verdict = await store.JudgeAsync(Query(page, crc: 2), Reads(Credit), default);
        var twin = await store.JudgeAsync(Query(Noise(99), crc: 900), Reads(Credit), default);

        Assert.True(verdict is { Accepted: false, Read: false, ByUser: true, Text: Credit });
        Assert.Equal(id, verdict.NoticeId);
        Assert.True(twin is { Accepted: false, ByUser: true });
        Assert.Equal(1, _reads);
    }

    [Fact]
    public async Task Hide_accepts_a_trusted_rejection_and_a_bank_proposal_on_the_notices_text()
    {
        var store = Store();
        var page = Noise(7);
        await store.JudgeAsync(Query(page, crc: 1), Reads(Story), default);
        var rejected = await store.JudgeAsync(Query(page, crc: 2), Reads(Story), default);
        Assert.False(rejected.Accepted);

        await store.SetOverrideAsync(rejected.NoticeId!.Value, CreditNoticeOverride.Hide, default);
        var proposal = await store.JudgeAsync(Query(page, crc: 3, fromBank: true), Reads(null), default);

        // Text is what ReaderCreditsCache asks of a bank proposal before it hides one.
        Assert.True(proposal is { Accepted: true, Read: false, ByUser: true, Text: Story });
        Assert.Equal(2, _reads);
    }

    [Fact]
    public async Task An_override_sends_every_linked_chapter_back_and_moves_the_generation()
    {
        var store = Store();
        var (hidden, kept) = (AddChapter(1), AddChapter(2));
        var id = await AcceptedNoticeAsync(store, Noise(7));
        await store.SaveLinksAsync(hidden, _seriesId, [new JudgedCandidate(false, 0, 1, true, Credit, true, NoticeId: id)], default);
        await store.SaveLinksAsync(kept, _seriesId, [new JudgedCandidate(true, 0, 1, true, Credit, false, NoticeId: id)], default);
        var generation = store.Generation;

        await store.SetOverrideAsync(id, CreditNoticeOverride.Keep, default);

        Assert.Equal(0, DetectorVersionOf(hidden));
        Assert.Equal(0, DetectorVersionOf(kept));
        Assert.NotEqual(generation, store.Generation);
        Assert.Equal(CreditNoticeOverride.Keep, Notice(id).Override);
        Assert.False(await store.SetOverrideAsync(id + 100, CreditNoticeOverride.Hide, default));
    }

    [Fact]
    public async Task An_override_survives_a_restart()
    {
        var page = Noise(7);
        var id = await AcceptedNoticeAsync(Store(), page);
        await Store().SetOverrideAsync(id, CreditNoticeOverride.Keep, default);

        var verdict = await Store().JudgeAsync(Query(page, crc: 2), Reads(Credit), default);

        Assert.True(verdict is { Accepted: false, ByUser: true });
    }

    [Fact]
    public async Task A_new_notice_gets_a_suggested_name_and_invite_from_its_text()
    {
        var id = await AcceptedNoticeAsync(Store(), Noise(7), "Join our Discord | SETSU SCANS | discord.gg/abcDEF123");

        var notice = Notice(id);
        Assert.Equal("SETSU SCANS", notice.SuggestedName);
        Assert.Equal("abcDEF123", notice.Invite);
        Assert.Equal(CreditGroupName.Version, notice.NameVersion);
        Assert.Null(notice.GroupName);
    }

    [Fact]
    public async Task Notices_from_before_the_name_reader_are_named_on_load()
    {
        var id = await SeedAsync(PageTextReader.Version, CreditText.Version, accepted: true, "Thanks for reading | SETSU SCANS", true);

        await Store().JudgeAsync(Query(Noise(50), crc: 77), Reads(Story), default);

        Assert.Equal("SETSU SCANS", Notice(id).SuggestedName);
    }

    private async Task<int> LinkedNoticeAsync(CreditNoticeStore store, params string?[] groups)
    {
        var id = await AcceptedNoticeAsync(store, Noise(7));
        var number = 1;
        foreach (var group in groups)
        {
            var chapter = AddChapter(number++, group);
            await store.SaveLinksAsync(chapter, _seriesId, [new JudgedCandidate(false, 0, 1, true, Credit, true, NoticeId: id)], default);
        }

        return id;
    }

    [Fact]
    public async Task A_notice_takes_the_group_most_of_its_chapters_carry()
    {
        var id = await LinkedNoticeAsync(Store(), "Asura Scans", null, "asura scans", "Flame Scans", "Asura Scans");

        var notice = Notice(id);
        Assert.Equal("Asura Scans", notice.GroupName);
        Assert.Equal(CreditGroupSource.Propagated, notice.GroupSource);
        Assert.Equal(CreditNoticeKind.Group, notice.Kind);
    }

    [Fact]
    public async Task A_tie_leaves_the_notice_as_it_is()
    {
        var store = Store();
        var tied = await AcceptedNoticeAsync(store, Noise(7));
        using (var db = _db.NewContext())
        {
            foreach (var chapter in new[] { AddChapter(1, "Asura Scans"), AddChapter(2, "Flame Scans") })
            {
                db.ChapterCreditNotices.Add(new ChapterCreditNotice
                {
                    ChapterId = chapter, SeriesId = _seriesId, Share = 1000, NoticeId = tied, Accepted = true,
                });
            }

            await db.SaveChangesAsync();
        }

        await store.PropagateAsync([tied], default);
        Assert.Null(Notice(tied).GroupName);

        await UpdateNoticeAsync(tied, n => (n.GroupName, n.GroupSource) = ("Flame Scans", CreditGroupSource.Propagated));
        await store.PropagateAsync([tied], default);
        Assert.Equal("Flame Scans", Notice(tied).GroupName);
    }

    [Fact]
    public async Task A_confirmed_group_and_a_site_ad_are_never_propagated_onto()
    {
        var store = Store();
        var confirmed = await AcceptedNoticeAsync(store, Noise(7));
        await UpdateNoticeAsync(confirmed, n => (n.GroupName, n.GroupSource) = ("Mine", CreditGroupSource.Confirmed));
        var site = (await store.JudgeAsync(Query(Noise(8), crc: 901), Reads(Credit), default)).NoticeId!.Value;
        await UpdateNoticeAsync(site, n => n.Kind = CreditNoticeKind.Site);

        await store.SaveLinksAsync(AddChapter(1, "Asura Scans"), _seriesId,
        [
            new JudgedCandidate(false, 0, 1, true, Credit, true, NoticeId: confirmed),
            new JudgedCandidate(true, 0, 1, true, Credit, true, NoticeId: site),
        ], default);

        Assert.True(Notice(confirmed) is { GroupName: "Mine", GroupSource: CreditGroupSource.Confirmed });
        Assert.True(Notice(site) is { GroupName: null, Kind: CreditNoticeKind.Site });
    }

    [Fact]
    public async Task A_notice_readers_keep_takes_no_group()
    {
        var store = Store();
        var id = (await store.JudgeAsync(Query(Noise(7), crc: 1), Reads(Story), default)).NoticeId!.Value;

        await store.SaveLinksAsync(AddChapter(1, "Asura Scans"), _seriesId,
            [new JudgedCandidate(false, 0, 1, true, Story, false, NoticeId: id)], default);

        Assert.Null(Notice(id).GroupName);
    }
}
