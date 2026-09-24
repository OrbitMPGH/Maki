using Maki.Api.Controllers;
using Maki.Api.Dtos;
using Maki.Api.Hubs;
using Maki.Api.Services;
using Maki.Core.Entities;
using Maki.Core.Metadata;
using Maki.Core.Security;
using Maki.Core.Sources;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Maki.Api.Tests;

/// <summary>
/// The request queue a user without <see cref="MakiPermission.AddSeries"/> files into: who can see
/// whose rows, and what approving one actually queues.
/// </summary>
public class SeriesRequestsControllerTests : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private readonly TestDb _db = new();
    private readonly DownloadQueueService _queue;
    private readonly DownloadBatchNotifier _batches;
    private readonly RecordingBroadcaster _events = new();
    private readonly FakeMetadataProvider _metadata = new();

    private readonly int _reader;
    private readonly int _admin;

    // Fed by SeedSeriesWithChapters so the "fake" source below reports exactly the chapters a test
    // seeded, letting EnqueueChapterAsync's resolve-at-enqueue-time lookup actually match them.
    private readonly List<decimal> _fakeChapterNumbers = [];

    public SeriesRequestsControllerTests()
    {
        var fakeSource = new FakeSource
        {
            Name = "fake",
            OnListChapters = _ =>
            [
                .. _fakeChapterNumbers.Select(n =>
                    new SourceChapter("fake", "s", n.ToString(), n.ToString(), n, null, null, "en", null)),
                // Covers the unbounded-request tests, which also queue a numberless one-shot.
                new SourceChapter("fake", "s", "oneshot", null, null, null, null, "en", null)
            ]
        };
        var resolver = Sources.Resolver(new SourceRegistry([fakeSource]));
        _queue = new DownloadQueueService(_db.ScopeFactory(), new StoppedClock(T0), resolver, NullLogger<DownloadQueueService>.Instance);
        _batches = new DownloadBatchNotifier(
            new RecordingNotifications(), _inbox, new TestLocalizer(), new TestUserLocaleResolver(),
            new StoppedClock(T0),
            NullLogger<DownloadBatchNotifier>.Instance);

        _reader = _db.SeedUser("reader", MakiPermission.None);
        _admin = _db.SeedUser("admin", MakiPermission.Admin);
    }

    private readonly RecordingInbox _inbox = new();
    private readonly RecordingNotifications _notifications = new();

    public void Dispose()
    {
        _batches.Dispose();
        _db.Dispose();
    }

    private SeriesRequestsController Controller(int userId, string userName, MakiPermission permissions)
    {
        var db = _db.NewContext(userId);
        var creation = new SeriesCreationService(
            db, [_metadata],
            coverService: null!, sourceMatchService: null!, chapterSyncService: null!,
            sourceMatchQueue: new SourceMatchQueue(),
            stats: null!, identity: null!, appSettings: new FakeAppSettings(),
            naming: new NamingService(new FakeAppSettings()),
            notifications: _notifications, locales: new TestUserLocaleResolver(), catalog: new TestLocalizer(),
            logger: NullLogger<SeriesCreationService>.Instance);

        return new SeriesRequestsController(
            new TestLocalizer(),
            new TestUserLocaleResolver(),
            db, creation, _queue, _batches, _inbox, _notifications,
            new SeriesRequestSubmitter(
                db, [_metadata], _events, _inbox, _notifications, new TestUserLocaleResolver(), new TestLocalizer(),
                NullLogger<SeriesRequestSubmitter>.Instance),
            new TestCurrentUser(userId, userName, permissions),
            NullLogger<SeriesRequestsController>.Instance);
    }

    private SeriesRequestsController AsReader() => Controller(_reader, "reader", MakiPermission.None);

    private SeriesRequestsController AsAdmin() => Controller(_admin, "admin", MakiPermission.Admin);

    private static T Body<T>(IActionResult result) => (T)((ObjectResult)result).Value!;

    /// <summary>Seeds a series with chapters at the given numbers, none of which have a file.</summary>
    private int SeedSeriesWithChapters(params decimal[] numbers)
    {
        var seriesId = _db.SeedSeries(mappings: new SourceMapping
        {
            SourceName = "fake",
            SourceSeriesId = "s",
            Url = "https://fake.test",
            Priority = 1,
            Enabled = true
        });

        using var db = _db.NewContext();
        foreach (var number in numbers)
        {
            db.Chapters.Add(new Chapter { SeriesId = seriesId, Number = number, Language = "en" });
        }

        db.SaveChanges();
        _fakeChapterNumbers.AddRange(numbers);
        return seriesId;
    }

    // ---- visibility ----

    [Fact]
    public async Task A_requester_sees_only_their_own_requests()
    {
        await AsReader().Create(new CreateSeriesRequestBody("NewSeries", MetadataProviderId: "1"), default);

        using (var db = _db.NewContext())
        {
            db.SeriesRequests.Add(new SeriesRequest
            {
                UserId = _admin,
                Kind = SeriesRequestKind.NewSeries,
                MetadataProviderId = "2",
                Title = "Somebody else's",
                Created = T0.UtcDateTime
            });
            await db.SaveChangesAsync();
        }

        var mine = Body<List<SeriesRequestDto>>(await AsReader().List("all", default));

        Assert.Single(mine);
        Assert.Equal(_reader, mine[0].UserId);
    }

    [Fact]
    public async Task An_admin_sees_everybody_s_requests()
    {
        await AsReader().Create(new CreateSeriesRequestBody("NewSeries", MetadataProviderId: "1"), default);

        var all = Body<List<SeriesRequestDto>>(await AsAdmin().List("all", default));

        Assert.Single(all);
        Assert.Equal("reader", all[0].RequestedBy);
    }

    [Fact]
    public async Task Creating_a_request_pings_the_admins()
    {
        await AsReader().Create(new CreateSeriesRequestBody("NewSeries", MetadataProviderId: "1"), default);

        Assert.Single(_events.Requested);
        Assert.Equal("reader", _events.Requested[0].RequestedBy);
    }

    [Fact]
    public async Task Creating_a_request_sends_an_outbound_notification()
    {
        var seriesId = SeedSeriesWithChapters(1, 2, 3);

        await AsReader().Create(
            new CreateSeriesRequestBody("Chapters", SeriesId: seriesId, ChapterStart: 2), default);

        var (type, message) = Assert.Single(_notifications.Sent);
        Assert.Equal(Maki.Core.Notifications.NotificationEventType.RequestSubmitted, type);
        Assert.Equal(seriesId, message.SeriesId);
        Assert.Contains("user=reader", message.Body);
        Assert.Contains("range=from", message.Body);
        Assert.Contains("start=2", message.Body);
    }

    [Fact]
    public async Task Rejecting_sends_a_warning_outbound_naming_the_requester()
    {
        var created = Body<SeriesRequestDto>(await AsReader().Create(
            new CreateSeriesRequestBody("NewSeries", MetadataProviderId: "1"), default));
        _notifications.Sent.Clear();

        await AsAdmin().Reject(created.Id, new RejectSeriesRequestBody(null), default);

        var (type, message) = Assert.Single(_notifications.Sent);
        Assert.Equal(Maki.Core.Notifications.NotificationEventType.RequestResolved, type);
        Assert.Equal(Maki.Core.Notifications.NotificationLevel.Warning, message.Level);
        Assert.Contains("outcome=rejected", message.Body);
        Assert.Contains("user=reader", message.Body);
    }

    [Fact]
    public async Task Approving_a_new_series_request_announces_the_add_and_the_resolution()
    {
        var existing = _db.SeedSeries();
        int rootFolderId;
        using (var db = _db.NewContext())
            rootFolderId = (await db.Series.SingleAsync(s => s.Id == existing)).RootFolderId;
        var created = Body<SeriesRequestDto>(await AsReader().Create(
            new CreateSeriesRequestBody("NewSeries", MetadataProviderId: "987"), default));
        _notifications.Sent.Clear();

        var approved = Body<SeriesRequestDto>(await AsAdmin().Approve(created.Id,
            new ApproveSeriesRequestBody(RootFolderId: rootFolderId), default));

        var added = Assert.Single(_notifications.Sent,
            n => n.Type == Maki.Core.Notifications.NotificationEventType.SeriesAdded).Message;
        Assert.Equal(approved.SeriesId, added.SeriesId);
        Assert.Equal($"/series/{approved.SeriesId}", added.Url);
        Assert.Contains("hasRequester=yes", added.Body);
        Assert.Contains("user=reader", added.Body);
        var resolved = Assert.Single(_notifications.Sent,
            n => n.Type == Maki.Core.Notifications.NotificationEventType.RequestResolved).Message;
        Assert.Equal(Maki.Core.Notifications.NotificationLevel.Info, resolved.Level);
        Assert.Contains("outcome=approved", resolved.Body);
    }

    // ---- validation ----

    [Fact]
    public async Task A_second_identical_pending_request_is_refused()
    {
        var body = new CreateSeriesRequestBody("NewSeries", MetadataProviderId: "1");

        await AsReader().Create(body, default);
        var second = await AsReader().Create(body, default);

        Assert.IsType<ConflictObjectResult>(second);
    }

    [Fact]
    public async Task An_inverted_range_is_refused()
    {
        var result = await AsReader().Create(
            new CreateSeriesRequestBody("NewSeries", MetadataProviderId: "1", ChapterStart: 40, ChapterEnd: 10),
            default);

        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task Requesting_a_series_the_library_already_holds_is_refused()
    {
        _db.SeedSeries(configure: s => s.MangaBakaId = 1);

        var result = await AsReader().Create(
            new CreateSeriesRequestBody("NewSeries", MetadataProviderId: "1"), default);

        Assert.IsType<ConflictObjectResult>(result);
    }

    // ---- approving ----

    [Fact]
    public async Task Approving_a_bounded_chapter_request_queues_only_that_range()
    {
        var seriesId = SeedSeriesWithChapters(1m, 2m, 3m, 4m, 5m);

        var created = Body<SeriesRequestDto>(await AsReader().Create(
            new CreateSeriesRequestBody("Chapters", SeriesId: seriesId, ChapterStart: 2, ChapterEnd: 4), default));

        var approved = Body<SeriesRequestDto>(
            await AsAdmin().Approve(created.Id, new ApproveSeriesRequestBody(), default));

        Assert.Equal("Approved", approved.Status);
        Assert.Equal(3, approved.QueuedCount);

        using var db = _db.NewContext();
        var queuedNumbers = db.DownloadQueue
            .Join(db.Chapters, q => q.ChapterId, c => c.Id, (_, c) => c.Number)
            .OrderBy(n => n)
            .ToList();

        Assert.Equal([2m, 3m, 4m], queuedNumbers);
    }

    [Fact]
    public async Task An_unbounded_chapter_request_queues_everything_including_one_shots()
    {
        var seriesId = SeedSeriesWithChapters(1m, 2m);
        using (var db = _db.NewContext())
        {
            db.Chapters.Add(new Chapter { SeriesId = seriesId, Number = null, Language = "en" });
            await db.SaveChangesAsync();
        }

        var created = Body<SeriesRequestDto>(await AsReader().Create(
            new CreateSeriesRequestBody("Chapters", SeriesId: seriesId), default));

        var approved = Body<SeriesRequestDto>(
            await AsAdmin().Approve(created.Id, new ApproveSeriesRequestBody(), default));

        Assert.Equal(3, approved.QueuedCount);
    }

    /// <summary>
    /// A one-shot has no number to compare, so a bounded request cannot be asking for it — queueing
    /// it anyway would hand somebody who asked for chapters 2–4 an unrelated special.
    /// </summary>
    [Fact]
    public async Task A_bounded_request_skips_unnumbered_chapters()
    {
        var seriesId = SeedSeriesWithChapters(1m, 2m);
        using (var db = _db.NewContext())
        {
            db.Chapters.Add(new Chapter { SeriesId = seriesId, Number = null, Language = "en" });
            await db.SaveChangesAsync();
        }

        var created = Body<SeriesRequestDto>(await AsReader().Create(
            new CreateSeriesRequestBody("Chapters", SeriesId: seriesId, ChapterStart: 1, ChapterEnd: 2), default));

        var approved = Body<SeriesRequestDto>(
            await AsAdmin().Approve(created.Id, new ApproveSeriesRequestBody(), default));

        Assert.Equal(2, approved.QueuedCount);
    }

    [Fact]
    public async Task A_new_series_request_needs_a_root_folder_to_approve()
    {
        var created = Body<SeriesRequestDto>(await AsReader().Create(
            new CreateSeriesRequestBody("NewSeries", MetadataProviderId: "1"), default));

        var result = await AsAdmin().Approve(created.Id, new ApproveSeriesRequestBody(), default);

        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task Approving_a_resolved_request_is_refused()
    {
        var created = Body<SeriesRequestDto>(await AsReader().Create(
            new CreateSeriesRequestBody("NewSeries", MetadataProviderId: "1"), default));

        await AsAdmin().Reject(created.Id, new RejectSeriesRequestBody("No."), default);
        var second = await AsAdmin().Approve(created.Id, new ApproveSeriesRequestBody(RootFolderId: 1), default);

        // The claim is what refuses this now, and it has to say which of the two happened: a
        // resolved request is not a second admin holding the request, and the reader can act on
        // one and not the other.
        var body = Assert.IsType<ConflictObjectResult>(second).Value;
        Assert.Equal("error.requests.alreadyResolved",
            body!.GetType().GetProperty("code")!.GetValue(body));
    }

    [Fact]
    public async Task New_series_approval_credits_the_requester_once_and_resumes_without_recreating()
    {
        var rootSeriesId = _db.SeedSeries();
        int rootFolderId;
        using (var db = _db.NewContext())
            rootFolderId = (await db.Series.SingleAsync(s => s.Id == rootSeriesId)).RootFolderId;
        var created = Body<SeriesRequestDto>(await AsReader().Create(
            new CreateSeriesRequestBody("NewSeries", MetadataProviderId: "987"), default));

        var approved = Body<SeriesRequestDto>(await AsAdmin().Approve(created.Id,
            new ApproveSeriesRequestBody(RootFolderId: rootFolderId), default));
        Assert.Equal("Approved", approved.Status);
        Assert.NotNull(approved.SeriesId);
        using (var db = _db.NewContext())
        {
            var attributed = await db.UserSeriesStates.SingleAsync(s => s.SeriesId == approved.SeriesId);
            Assert.Equal(_reader, attributed.UserId);
            Assert.Equal("request", attributed.AddedFrom);
            Assert.NotNull(attributed.AddedToLibraryAtUtc);
            var request = await db.SeriesRequests.SingleAsync(r => r.Id == created.Id);
            request.Status = SeriesRequestStatus.Processing;
            request.ApprovalClaimedAtUtc = DateTime.UtcNow.AddHours(-1);
            request.ResolvedAt = null;
            await db.SaveChangesAsync();
        }

        var resumed = Body<SeriesRequestDto>(await AsAdmin().Approve(created.Id,
            new ApproveSeriesRequestBody(RootFolderId: rootFolderId), default));
        Assert.Equal(approved.SeriesId, resumed.SeriesId);
        using var check = _db.NewContext();
        Assert.Single(await check.Series.Where(s => s.MangaBakaId == 987).ToListAsync());
        Assert.Single(await check.UserSeriesStates.Where(s => s.SeriesId == approved.SeriesId).ToListAsync());
    }

    [Fact]
    public async Task Approval_keeps_a_receipt_and_refuses_to_recreate_a_series_that_was_deleted()
    {
        var rootSeriesId = _db.SeedSeries();
        int rootFolderId;
        using (var db = _db.NewContext())
            rootFolderId = (await db.Series.SingleAsync(s => s.Id == rootSeriesId)).RootFolderId;
        var created = Body<SeriesRequestDto>(await AsReader().Create(
            new CreateSeriesRequestBody("NewSeries", MetadataProviderId: "654"), default));

        var approved = Body<SeriesRequestDto>(await AsAdmin().Approve(created.Id,
            new ApproveSeriesRequestBody(RootFolderId: rootFolderId), default));

        using (var db = _db.NewContext())
        {
            // Approval is one admin press and its retry is another, so the mutation id comes from
            // the request rather than from a client. Without the receipt the add runs outside the
            // creation transaction too.
            var receipt = await db.RecommendationMutationReceipts.IgnoreQueryFilters().SingleAsync();
            Assert.Equal(_reader, receipt.UserId);
            Assert.Equal("add", receipt.Operation);

            // Deletion nulls the request's SeriesId, so a retry takes the create branch again.
            db.Series.Remove(await db.Series.SingleAsync(s => s.Id == approved.SeriesId));
            await db.SaveChangesAsync();
            var request = await db.SeriesRequests.SingleAsync(r => r.Id == created.Id);
            request.Status = SeriesRequestStatus.Pending;
            request.ResolvedAt = null;
            await db.SaveChangesAsync();
        }

        var retry = await AsAdmin().Approve(created.Id,
            new ApproveSeriesRequestBody(RootFolderId: rootFolderId), default);

        Assert.IsType<ConflictObjectResult>(retry);
        using var check = _db.NewContext();
        Assert.Empty(await check.Series.Where(s => s.MangaBakaId == 654).ToListAsync());
        // Released rather than left in Processing, so the admin can reject it.
        Assert.Equal(SeriesRequestStatus.Pending,
            (await check.SeriesRequests.SingleAsync(r => r.Id == created.Id)).Status);
    }

    // ---- editing ----

    [Fact]
    public async Task An_admin_can_narrow_a_pending_range_and_the_original_is_kept()
    {
        var seriesId = SeedSeriesWithChapters(1m, 2m, 3m, 4m, 5m);

        var created = Body<SeriesRequestDto>(await AsReader().Create(
            new CreateSeriesRequestBody("Chapters", SeriesId: seriesId), default));
        Assert.Null(created.ChapterEnd);

        var edited = Body<SeriesRequestDto>(
            await AsAdmin().Edit(created.Id, new EditSeriesRequestBody(1, 2), default));

        Assert.Equal(1m, edited.ChapterStart);
        Assert.Equal(2m, edited.ChapterEnd);
        Assert.Equal("admin", edited.EditedBy);
        // The requester asked for everything; both original bounds were null and stay null, and
        // EditedAt is what says the row was touched at all.
        Assert.NotNull(edited.EditedAt);
        Assert.Null(edited.OriginalChapterStart);
        Assert.Null(edited.OriginalChapterEnd);

        var approved = Body<SeriesRequestDto>(
            await AsAdmin().Approve(created.Id, new ApproveSeriesRequestBody(), default));

        Assert.Equal(2, approved.QueuedCount);
    }

    /// <summary>A second edit must not overwrite the snapshot with the first edit's range.</summary>
    [Fact]
    public async Task A_second_edit_keeps_the_requester_s_original_bounds()
    {
        var seriesId = SeedSeriesWithChapters(1m, 2m, 3m);

        var created = Body<SeriesRequestDto>(await AsReader().Create(
            new CreateSeriesRequestBody("Chapters", SeriesId: seriesId, ChapterStart: 1, ChapterEnd: 100), default));

        await AsAdmin().Edit(created.Id, new EditSeriesRequestBody(1, 10), default);
        var twice = Body<SeriesRequestDto>(
            await AsAdmin().Edit(created.Id, new EditSeriesRequestBody(1, 3), default));

        Assert.Equal(3m, twice.ChapterEnd);
        Assert.Equal(1m, twice.OriginalChapterStart);
        Assert.Equal(100m, twice.OriginalChapterEnd);
    }

    /// <summary>Saving a dialog without touching anything must not brand the row as adjusted.</summary>
    [Fact]
    public async Task An_edit_that_changes_nothing_records_no_edit()
    {
        var seriesId = SeedSeriesWithChapters(1m, 2m);

        var created = Body<SeriesRequestDto>(await AsReader().Create(
            new CreateSeriesRequestBody("Chapters", SeriesId: seriesId, ChapterStart: 1, ChapterEnd: 2), default));

        var edited = Body<SeriesRequestDto>(
            await AsAdmin().Edit(created.Id, new EditSeriesRequestBody(1, 2), default));

        Assert.Null(edited.EditedAt);
        Assert.Null(edited.EditedBy);
    }

    [Fact]
    public async Task An_inverted_range_is_refused_on_edit()
    {
        var seriesId = SeedSeriesWithChapters(1m, 2m);
        var created = Body<SeriesRequestDto>(await AsReader().Create(
            new CreateSeriesRequestBody("Chapters", SeriesId: seriesId), default));

        Assert.IsType<BadRequestObjectResult>(
            await AsAdmin().Edit(created.Id, new EditSeriesRequestBody(40, 10), default));
    }

    /// <summary>The range on a resolved request records what was actually queued.</summary>
    [Fact]
    public async Task A_resolved_request_cannot_be_edited()
    {
        var created = Body<SeriesRequestDto>(await AsReader().Create(
            new CreateSeriesRequestBody("NewSeries", MetadataProviderId: "1"), default));
        await AsAdmin().Reject(created.Id, new RejectSeriesRequestBody(), default);

        Assert.IsType<ConflictObjectResult>(
            await AsAdmin().Edit(created.Id, new EditSeriesRequestBody(1, 10), default));
    }

    [Fact]
    public async Task Rejecting_records_who_and_why()
    {
        var created = Body<SeriesRequestDto>(await AsReader().Create(
            new CreateSeriesRequestBody("NewSeries", MetadataProviderId: "1"), default));

        var rejected = Body<SeriesRequestDto>(
            await AsAdmin().Reject(created.Id, new RejectSeriesRequestBody("Licensed here."), default));

        Assert.Equal("Rejected", rejected.Status);
        Assert.Equal("admin", rejected.ResolvedBy);
        Assert.Equal("Licensed here.", rejected.ResolutionNote);
    }

    // ---- withdrawing ----

    [Fact]
    public async Task A_requester_can_withdraw_their_own_pending_request()
    {
        var created = Body<SeriesRequestDto>(await AsReader().Create(
            new CreateSeriesRequestBody("NewSeries", MetadataProviderId: "1"), default));

        Assert.IsType<NoContentResult>(await AsReader().Delete(created.Id, default));
        Assert.Empty(Body<List<SeriesRequestDto>>(await AsAdmin().List("all", default)));
    }

    /// <summary>The resolution note is the answer they were given; deleting it would erase it.</summary>
    [Fact]
    public async Task A_requester_cannot_delete_a_resolved_request()
    {
        var created = Body<SeriesRequestDto>(await AsReader().Create(
            new CreateSeriesRequestBody("NewSeries", MetadataProviderId: "1"), default));
        await AsAdmin().Reject(created.Id, new RejectSeriesRequestBody(), default);

        Assert.IsType<ConflictObjectResult>(await AsReader().Delete(created.Id, default));
    }

    [Fact]
    public async Task A_requester_cannot_delete_somebody_else_s_request()
    {
        using (var db = _db.NewContext())
        {
            db.SeriesRequests.Add(new SeriesRequest
            {
                UserId = _admin,
                Kind = SeriesRequestKind.NewSeries,
                MetadataProviderId = "2",
                Title = "Not theirs",
                Created = T0.UtcDateTime
            });
            await db.SaveChangesAsync();
        }

        var id = _db.NewContext().SeriesRequests.IgnoreQueryFilters().Single().Id;

        Assert.IsType<NotFoundResult>(await AsReader().Delete(id, default));
    }

    // ---- doubles ----

    private sealed class RecordingBroadcaster() : EventBroadcaster(null!, null!)
    {
        public List<(int Id, string Title, string RequestedBy)> Requested { get; } = [];

        public override Task SeriesRequested(int requestId, string title, string requestedBy)
        {
            Requested.Add((requestId, title, requestedBy));
            return Task.CompletedTask;
        }
    }

    /// <summary>Answers any provider id as a series whose MangaBaka id is that number.</summary>
    private sealed class FakeMetadataProvider : IMetadataProvider
    {
        public string Name => "fake";

        public Task<IReadOnlyList<MetadataSearchResult>> SearchAsync(
            string query, string maxContentRating, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<MetadataSearchResult>>([]);

        public Task<SeriesMetadata?> GetAsync(string providerId, CancellationToken ct = default) =>
            Task.FromResult<SeriesMetadata?>(new SeriesMetadata
            {
                ProviderId = providerId,
                Title = $"Series {providerId}",
                MangaBakaId = int.Parse(providerId),
                Year = 2020,
            });
    }
}
