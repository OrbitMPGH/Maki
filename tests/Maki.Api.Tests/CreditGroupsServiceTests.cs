using System.IO.Compression;
using Maki.Api.Auth;
using Maki.Api.Controllers;
using Maki.Api.Dtos;
using Maki.Api.Localization;
using Maki.Api.Services;
using Maki.Api.Services.Ocr;
using Maki.Core.Configuration;
using Maki.Core.Entities;
using Maki.Core.Kavita;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Maki.Api.Tests;

/// <summary>The groups view, a user's decisions on a notice, and applying a notice's group to its chapters.</summary>
public sealed class CreditGroupsServiceTests : IDisposable
{
    private const string GroupFormat = "{Series Title} {Chapter VolChap}< ({Chapter Group})>";

    private readonly TestDb _db = new TestDb().WithDistinctSeriesIds();
    private readonly FakeAppSettings _settings = new();
    private readonly string _root = Path.Combine(Path.GetTempPath(), "maki-groups-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly ReaderArchiveCache _archives = new(NullLogger<ReaderArchiveCache>.Instance);
    private readonly int _seriesId;
    private CreditNoticeStore? _store;

    public CreditGroupsServiceTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "Solo"));
        using var db = _db.NewContext();
        var folder = new RootFolder { Path = _root };
        db.RootFolders.Add(folder);
        db.SaveChanges();
        var series = new Series { Title = "Solo", SortTitle = "solo", FolderName = "Solo", RootFolderId = folder.Id };
        db.Series.Add(series);
        db.SaveChanges();
        _seriesId = series.Id;
    }

    public void Dispose()
    {
        _db.Dispose();
        try
        {
            Directory.Delete(_root, true);
        }
        catch (IOException)
        {
        }
    }

    private CreditNoticeStore Store() => _store ??= new CreditNoticeStore(
        _db.ScopeFactory(), new CreditStore(_db.ScopeFactory(), new PageTextReader(NullLogger<PageTextReader>.Instance, _root),
            NullLogger<CreditStore>.Instance),
        NullLogger<CreditNoticeStore>.Instance);

    private CreditGroupsService Service(Maki.Data.MakiDbContext db)
    {
        var scans = new KavitaScanService(
            new KavitaClient(new StubHttpClientFactory("{}")), _settings, _db.ScopeFactory(), NullLogger<KavitaScanService>.Instance);
        var naming = new NamingService(_settings);
        var renames = new SeriesRenameService(db, naming, scans, new TestLocalizer(), NullLogger<SeriesRenameService>.Instance);
        return new CreditGroupsService(db, Store(), naming, renames, _settings, NullLogger<CreditGroupsService>.Instance);
    }

    private CreditGroupsController Controller(Maki.Data.MakiDbContext db) =>
        new(Service(db), new TestLocalizer()) { ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() } };

    /// <summary>A chapter backed by a file on disk named the way the default format names it.</summary>
    private int AddChapter(int number, string? group = null, byte[]? content = null)
    {
        var relative = $"Solo/Solo Ch.{number}.cbz";
        File.WriteAllBytes(Path.Combine(_root, relative), content ?? [1, 2, 3]);
        using var db = _db.NewContext();
        var file = new ChapterFile
        {
            SeriesId = _seriesId, RelativePath = relative, Size = new FileInfo(Path.Combine(_root, relative)).Length, SourceName = "test",
            DateAdded = DateTime.UtcNow, Group = group, NamedByMaki = true,
        };
        db.ChapterFiles.Add(file);
        db.SaveChanges();
        var chapter = new Chapter { SeriesId = _seriesId, Number = number, ChapterFileId = file.Id };
        db.Chapters.Add(chapter);
        db.SaveChanges();
        return chapter.Id;
    }

    private int AddNotice(
        bool accepted = true, CreditNoticeKind kind = CreditNoticeKind.Unknown, string? groupName = null,
        CreditGroupSource source = CreditGroupSource.None, string? suggested = null, bool whole = true, double share = 1,
        string text = "Thanks for reading")
    {
        using var db = _db.NewContext();
        var notice = new CreditNotice
        {
            Whole = whole, Share = share, Height = 1, Rows = new byte[64], Fingerprint = [], Accepted = accepted, Reads = 1, Text = text,
            OcrVersion = PageTextReader.Version, TextVersion = Maki.Core.Images.CreditText.Version, Kind = kind, GroupName = groupName,
            GroupSource = source, SuggestedName = suggested, NameVersion = Maki.Core.Images.CreditGroupName.Version,
            CreatedAtUtc = DateTime.UtcNow, UpdatedAtUtc = DateTime.UtcNow,
        };
        db.CreditNotices.Add(notice);
        db.SaveChanges();
        return notice.Id;
    }

    private void Link(int noticeId, int chapterId, bool tail = false, int page = 0, int share = 1000)
    {
        using var db = _db.NewContext();
        db.ChapterCreditNotices.Add(new ChapterCreditNotice
        {
            ChapterId = chapterId, SeriesId = _seriesId, Tail = tail, Page = page, Share = share, NoticeId = noticeId, Accepted = true,
        });
        db.SaveChanges();
    }

    private (string Path, string? Group) FileOf(int chapterId)
    {
        using var db = _db.NewContext();
        var file = db.Chapters.Where(c => c.Id == chapterId).Select(c => c.ChapterFile!).Single();
        return (file.RelativePath, file.Group);
    }

    [Fact]
    public async Task Apply_fills_only_chapters_without_a_group_and_renames_them()
    {
        _settings.Set(SettingKeys.LibraryChapterFormat, GroupFormat);
        var (empty, tagged, other) = (AddChapter(1), AddChapter(2, "Flame Scans"), AddChapter(3));
        var notice = AddNotice(kind: CreditNoticeKind.Group, groupName: "Asura Scans", source: CreditGroupSource.Confirmed);
        Link(notice, empty);
        Link(notice, tagged, tail: true);

        await using var db = _db.NewContext();
        var result = await Service(db).ApplyAsync([notice], default);

        Assert.Equal(new ApplyCreditGroupsResult(1, 1, 0, []), result with { Warnings = [] });
        Assert.Empty(result.Warnings);
        Assert.Equal((Path.Combine("Solo", "Solo Ch.1 (Asura Scans).cbz"), "Asura Scans"), FileOf(empty));
        Assert.True(File.Exists(Path.Combine(_root, "Solo", "Solo Ch.1 (Asura Scans).cbz")));
        Assert.Equal(("Solo/Solo Ch.2.cbz", "Flame Scans"), FileOf(tagged));
        Assert.Equal(("Solo/Solo Ch.3.cbz", null), FileOf(other));
    }

    [Fact]
    public async Task Apply_again_changes_nothing()
    {
        _settings.Set(SettingKeys.LibraryChapterFormat, GroupFormat);
        var chapter = AddChapter(1);
        var notice = AddNotice(kind: CreditNoticeKind.Group, groupName: "Asura Scans", source: CreditGroupSource.Propagated);
        Link(notice, chapter);

        await using (var db = _db.NewContext())
        {
            Assert.Equal(1, (await Service(db).ApplyAsync([notice], default)).ChaptersUpdated);
        }

        await using var again = _db.NewContext();
        Assert.Equal(new ApplyCreditGroupsResult(0, 0, 0, []), await Service(again).ApplyAsync([notice], default) with { Warnings = [] });
        Assert.Equal((Path.Combine("Solo", "Solo Ch.1 (Asura Scans).cbz"), "Asura Scans"), FileOf(chapter));
    }

    [Fact]
    public async Task A_format_without_the_group_sets_the_group_and_renames_nothing()
    {
        var chapter = AddChapter(1);
        var notice = AddNotice(kind: CreditNoticeKind.Group, groupName: "Asura Scans", source: CreditGroupSource.Confirmed);
        Link(notice, chapter);

        await using var db = _db.NewContext();
        var result = await Service(db).ApplyAsync([notice], default);

        Assert.Equal((1, 0), (result.ChaptersUpdated, result.FilesRenamed));
        Assert.Equal(("Solo/Solo Ch.1.cbz", "Asura Scans"), FileOf(chapter));
    }

    private void AddInFlightDownload(int chapterId)
    {
        using var db = _db.NewContext();
        db.DownloadQueue.Add(new DownloadQueueItem { SeriesId = _seriesId, ChapterId = chapterId, Status = QueueStatus.Downloading });
        db.SaveChanges();
    }

    [Fact]
    public async Task A_refused_rename_leaves_the_series_ungrouped_and_a_later_apply_does_both()
    {
        _settings.Set(SettingKeys.LibraryChapterFormat, GroupFormat);
        var chapter = AddChapter(1);
        var notice = AddNotice(kind: CreditNoticeKind.Group, groupName: "Asura Scans", source: CreditGroupSource.Confirmed);
        Link(notice, chapter);
        AddInFlightDownload(chapter);

        await using (var db = _db.NewContext())
        {
            var refused = await Service(db).ApplyAsync([notice], default);
            Assert.Equal((0, 0), (refused.ChaptersUpdated, refused.FilesRenamed));
            Assert.Contains("error.seriesRename.activeDownload", Assert.Single(refused.Warnings));
        }

        Assert.Equal(("Solo/Solo Ch.1.cbz", (string?)null), FileOf(chapter));
        await using (var db = _db.NewContext())
        {
            await db.DownloadQueue.ExecuteDeleteAsync();
        }

        await using var again = _db.NewContext();
        var applied = await Service(again).ApplyAsync([notice], default);

        Assert.Equal((1, 1), (applied.ChaptersUpdated, applied.FilesRenamed));
        Assert.Equal((Path.Combine("Solo", "Solo Ch.1 (Asura Scans).cbz"), "Asura Scans"), FileOf(chapter));
    }

    [Fact]
    public async Task A_file_the_rename_skips_keeps_no_group()
    {
        _settings.Set(SettingKeys.LibraryChapterFormat, GroupFormat);
        var chapter = AddChapter(1);
        File.WriteAllBytes(Path.Combine(_root, "Solo", "Solo Ch.1 (Asura Scans).cbz"), [9]);
        var notice = AddNotice(kind: CreditNoticeKind.Group, groupName: "Asura Scans", source: CreditGroupSource.Confirmed);
        Link(notice, chapter);

        await using var db = _db.NewContext();
        var result = await Service(db).ApplyAsync([notice], default);

        Assert.Equal((0, 0), (result.ChaptersUpdated, result.FilesRenamed));
        Assert.NotEmpty(result.Warnings);
        Assert.Null(FileOf(chapter).Group);
    }

    [Fact]
    public async Task A_file_two_notices_name_differently_is_a_conflict_from_either_bucket()
    {
        var shared = AddChapter(1);
        var asura = AddNotice(kind: CreditNoticeKind.Group, groupName: "Asura Scans", source: CreditGroupSource.Confirmed);
        var flame = AddNotice(kind: CreditNoticeKind.Group, groupName: "Flame Scans", source: CreditGroupSource.Propagated);
        Link(asura, shared);
        Link(flame, shared, tail: true);

        await using var db = _db.NewContext();
        var list = (await Controller(db).List(all: false, default)).Value!;
        var result = await Service(db).ApplyAsync([asura], default);

        Assert.All(list.Groups, g => Assert.Equal(0, g.ChaptersWithoutGroup));
        Assert.Equal((0, 1), (result.ChaptersUpdated, result.Conflicts));
        Assert.Null(FileOf(shared).Group);
    }

    [Fact]
    public async Task A_site_ad_an_unconfirmed_suggestion_or_a_conflict_applies_nothing()
    {
        var (first, second) = (AddChapter(1), AddChapter(2));
        var site = AddNotice(kind: CreditNoticeKind.Site, groupName: "Bato", source: CreditGroupSource.Confirmed);
        var suggested = AddNotice(suggested: "Asura Scans");
        Link(site, first);
        Link(suggested, first);
        var asura = AddNotice(kind: CreditNoticeKind.Group, groupName: "Asura Scans", source: CreditGroupSource.Confirmed);
        var flame = AddNotice(kind: CreditNoticeKind.Group, groupName: "Flame Scans", source: CreditGroupSource.Confirmed);
        Link(asura, second);
        Link(flame, second, tail: true);

        await using var db = _db.NewContext();
        var none = await Service(db).ApplyAsync([site, suggested], default);
        var conflict = await Service(db).ApplyAsync([asura, flame], default);

        Assert.Equal(new ApplyCreditGroupsResult(0, 0, 0, []), none with { Warnings = [] });
        Assert.Equal((0, 1), (conflict.ChaptersUpdated, conflict.Conflicts));
        Assert.Null(FileOf(first).Group);
        Assert.Null(FileOf(second).Group);
    }

    [Fact]
    public async Task The_list_buckets_notices_by_name_with_site_and_unnamed_apart()
    {
        var (one, two, three, four) = (AddChapter(1), AddChapter(2, "Other"), AddChapter(3), AddChapter(4));
        var confirmed = AddNotice(kind: CreditNoticeKind.Group, groupName: "Asura Scans", source: CreditGroupSource.Confirmed);
        var suggested = AddNotice(suggested: "ASURA SCANS");
        var site = AddNotice(kind: CreditNoticeKind.Site);
        var unnamed = AddNotice();
        var kept = AddNotice(accepted: false, suggested: "Title Page");
        Link(confirmed, one);
        Link(confirmed, two);
        Link(suggested, three);
        Link(site, four, tail: true);
        Link(unnamed, four);
        Link(kept, four);

        await using var db = _db.NewContext();
        var result = Assert.IsType<CreditGroupListDto>((await Controller(db).List(all: false, default)).Value);
        var all = (await Controller(db).List(all: true, default)).Value!;

        Assert.Equal(["group:asurascans", "site", "unnamed"], result.Groups.Select(g => g.Key).Order());
        var asura = result.Groups.Single(g => g.Key == "group:asurascans");
        // Three chapters carry the bucket, but apply changes only chapter one: two has a group and three's notice is a suggestion.
        Assert.Equal(new CreditGroupDto("group:asurascans", "Asura Scans", "confirmed", "group", [confirmed, suggested], 2, 1, 3, 1),
            asura with { NoticeIds = asura.NoticeIds.ToList() }, new GroupComparer());
        Assert.Equal(("site", "none", 1), result.Groups.Where(g => g.Key == "site").Select(g => (g.Kind, g.NameSource, g.Chapters)).Single());
        Assert.Contains(all.Groups, g => g.Key == "group:titlepage" && g.NameSource == "suggested");
    }

    private sealed class GroupComparer : IEqualityComparer<CreditGroupDto>
    {
        public bool Equals(CreditGroupDto? x, CreditGroupDto? y) =>
            x is not null && y is not null && x with { NoticeIds = [] } == y with { NoticeIds = [] } && x.NoticeIds.SequenceEqual(y.NoticeIds);

        public int GetHashCode(CreditGroupDto obj) => obj.Key.GetHashCode();
    }

    [Fact]
    public async Task The_detail_lists_notices_and_the_chapters_that_carry_them()
    {
        var (one, two) = (AddChapter(1), AddChapter(2, "Other"));
        var notice = AddNotice(groupName: "Asura Scans", source: CreditGroupSource.Propagated, kind: CreditNoticeKind.Group,
            whole: false, share: 0.25, text: new string('x', 300));
        Link(notice, one, tail: true, share: 250);
        Link(notice, two, tail: true, share: 250);

        await using var db = _db.NewContext();
        var detail = (await Controller(db).Notices(new CreditNoticeIdsRequest([notice]), default)).Value!;

        var dto = Assert.Single(detail.Notices);
        Assert.Equal(new CreditNoticeDto(notice, true, true, "none", "group", "Asura Scans", "propagated", null, null,
            new string('x', 200), false, 0.25, true, 2, 1), dto);
        var series = Assert.Single(detail.Series);
        Assert.Equal(("Solo", _seriesId), (series.Title, series.SeriesId));
        Assert.Equal(new (int, string, string?)[] { (one, "Ch.1", null), (two, "Ch.2", "Other") },
            series.Chapters.Select(c => (c.ChapterId, c.Label, c.Group)).ToArray());
        Assert.All(series.Chapters, c => Assert.Equal([notice], c.NoticeIds));
    }

    [Fact]
    public async Task Updating_a_notice_confirms_its_name_and_sets_the_override()
    {
        var chapter = AddChapter(1);
        var notice = AddNotice(suggested: "ASURA SCANS");
        Link(notice, chapter);

        await using var db = _db.NewContext();
        var ok = Assert.IsType<OkObjectResult>(await Controller(db).Update(notice,
            new UpdateCreditNoticeRequest(GroupName: " Asura Scans ", Override: CreditNoticeOverride.Keep), default));

        var dto = Assert.IsType<CreditNoticeDto>(ok.Value);
        Assert.True(dto is { GroupName: "Asura Scans", GroupSource: "confirmed", Kind: "group", Override: "keep", Hidden: false });
        await using var check = _db.NewContext();
        Assert.Equal(CreditNoticeOverride.Keep, check.CreditNotices.Single(n => n.Id == notice).Override);

        var cleared = Assert.IsType<OkObjectResult>(await Controller(db).Update(notice, new UpdateCreditNoticeRequest(GroupName: ""), default));
        Assert.True(cleared.Value is CreditNoticeDto { GroupName: null, GroupSource: "none" });
    }

    [Fact]
    public async Task A_site_ad_cannot_take_a_name_and_an_unknown_notice_is_not_found()
    {
        var notice = AddNotice();
        Link(notice, AddChapter(1));

        await using var db = _db.NewContext();
        var refused = Assert.IsType<BadRequestObjectResult>(await Controller(db).Update(notice,
            new UpdateCreditNoticeRequest(Kind: CreditNoticeKind.Site, GroupName: "Bato"), default));
        var missing = await Controller(db).Update(notice + 50, new UpdateCreditNoticeRequest(Kind: CreditNoticeKind.Site), default);
        var tooLong = await Controller(db).Update(notice, new UpdateCreditNoticeRequest(GroupName: new string('a', 101)), default);

        Assert.Contains(CreditGroupsService.SiteHasNoGroup, System.Text.Json.JsonSerializer.Serialize(refused.Value));
        Assert.IsType<NotFoundObjectResult>(missing);
        Assert.IsType<BadRequestObjectResult>(tooLong);
    }

    [Fact]
    public async Task Naming_several_notices_confirms_each_but_leaves_site_ads()
    {
        var (a, b, site) = (AddNotice(suggested: "ASURA SCANS"), AddNotice(suggested: "Asura Scans"), AddNotice(kind: CreditNoticeKind.Site));

        await using var db = _db.NewContext();
        var result = Assert.IsType<OkObjectResult>(await Controller(db).Name(new NameCreditNoticesRequest([a, b, site], "Asura Scans"), default));

        Assert.Contains("\"named\":2", System.Text.Json.JsonSerializer.Serialize(result.Value).Replace(" ", string.Empty),
            StringComparison.OrdinalIgnoreCase);
        await using var check = _db.NewContext();
        Assert.All(check.CreditNotices.Where(n => n.Id == a || n.Id == b).ToList(),
            n => Assert.True(n is { GroupName: "Asura Scans", GroupSource: CreditGroupSource.Confirmed, Kind: CreditNoticeKind.Group }));
        Assert.Null(check.CreditNotices.Single(n => n.Id == site).GroupName);
        Assert.IsType<BadRequestObjectResult>(await Controller(db).Name(new NameCreditNoticesRequest([a], " "), default));
    }

    [Fact]
    public void Every_action_is_admin_only()
    {
        var policies = typeof(CreditGroupsController).GetCustomAttributes(typeof(AuthorizeAttribute), true)
            .Cast<AuthorizeAttribute>().Select(a => a.Policy).ToList();
        Assert.Equal([Policies.Admin], policies);
        Assert.DoesNotContain(typeof(CreditGroupsController).GetMethods(),
            m => m.IsDefined(typeof(AllowAnonymousAttribute), true) || m.GetCustomAttributes(typeof(AuthorizeAttribute), true).Length > 0);
    }

    /// <summary>A page whose top half is black and bottom half white, as a CBZ of two such pages.</summary>
    private static byte[] HalfAndHalf()
    {
        var pixels = new byte[200 * 100];
        Array.Fill(pixels, (byte)255, 100 * 100, 100 * 100);
        using var image = NetVips.Image.NewFromMemory(pixels, 100, 200, 1, NetVips.Enums.BandFormat.Uchar);
        var png = image.PngsaveBuffer();
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, true))
        {
            foreach (var name in new[] { "001.png", "002.png" })
            {
                using var entry = zip.CreateEntry(name).Open();
                entry.Write(png);
            }
        }

        return buffer.ToArray();
    }

    private static (int Width, int Height, double Mean) Measure(byte[] jpeg)
    {
        using var image = NetVips.Image.NewFromBuffer(jpeg);
        return (image.Width, image.Height, image.Avg());
    }

    [Fact]
    public async Task A_notice_image_is_the_top_share_of_a_head_and_the_bottom_share_of_a_tail()
    {
        var chapter = AddChapter(1, content: HalfAndHalf());
        var head = AddNotice(whole: false, share: 0.3);
        var tail = AddNotice(whole: false, share: 0.3);
        var whole = AddNotice();
        var unreadable = AddNotice();
        Link(head, chapter, share: 300);
        Link(tail, chapter, tail: true, share: 300);
        Link(whole, chapter, tail: true);

        await using var db = _db.NewContext();
        var reader = new ReaderService(db, _archives, null!, null!, new ReadingSessionService(db), NullLogger<ReaderService>.Instance);
        var controller = Controller(db);
        async Task<byte[]?> Image(int id) => await controller.Image(id, reader, _archives, default) is FileContentResult file ? file.FileContents : null;

        var (headWidth, headHeight, headMean) = Measure((await Image(head))!);
        var (_, tailHeight, tailMean) = Measure((await Image(tail))!);
        var (_, wholeHeight, _) = Measure((await Image(whole))!);

        Assert.Equal((100, 60), (headWidth, headHeight));
        Assert.True(headMean < 20, $"head mean {headMean}");
        Assert.Equal(60, tailHeight);
        Assert.True(tailMean > 235, $"tail mean {tailMean}");
        Assert.Equal(200, wholeHeight);
        Assert.Equal("private, max-age=86400", controller.Response.Headers.CacheControl.ToString());
        Assert.IsType<NotFoundResult>(await controller.Image(unreadable, reader, _archives, default));
    }
}
