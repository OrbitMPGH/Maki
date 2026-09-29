using System.Reflection;
using System.Text.Json;
using Maki.Api.Auth;
using Maki.Api.Controllers;
using Maki.Api.Dtos;
using Maki.Api.Services;
using Maki.Core.Entities;
using Maki.Core.Quality;
using Maki.Core.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Quartz;

namespace Maki.Api.Tests;

[Collection(ConfigDirCollection.Name)]
public class UpgradesControllerTests : IDisposable
{
    private readonly UpgradeWorld _world = new();

    public UpgradesControllerTests() => _world.Seed();

    public void Dispose() => _world.Dispose();

    private sealed class RecordingSchedulerFactory : ISchedulerFactory
    {
        public int Calls { get; private set; }

        public Task<IReadOnlyList<IScheduler>> GetAllSchedulers(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<IScheduler> GetScheduler(CancellationToken cancellationToken = default)
        {
            Calls++;
            throw new NotSupportedException();
        }

        public Task<IScheduler?> GetScheduler(string schedName, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private readonly RecordingSchedulerFactory _scheduler = new();
    private readonly UpgradeScanTracker _tracker = new();

    private async Task<IActionResult> ScanAsync(UpgradeScanRequest? request, MakiPermission permissions)
    {
        using var db = _world.Db.NewContext();
        using var batches = _world.Batches();
        var controller = new UpgradesController(new UpgradeEvaluationService(db, TestQuality.Create(_world.Registry)), db,
            new TestLocalizer(), NullLogger<UpgradesController>.Instance);
        return await controller.Scan(request, _world.Scanner(db, batches), _scheduler,
            new TestCurrentUser(1, permissions: permissions), _tracker, CancellationToken.None);
    }

    private static string Code(IActionResult result) =>
        JsonSerializer.SerializeToElement(((ObjectResult)result).Value).GetProperty("code").GetString()!;

    [Fact]
    public void The_scan_action_is_open_to_anyone_who_may_download()
    {
        var attribute = typeof(UpgradesController).GetMethod(nameof(UpgradesController.Scan))!
            .GetCustomAttribute<AuthorizeAttribute>()!;
        Assert.Equal(Policies.DownloadChapters, attribute.Policy);
    }

    [Fact]
    public async Task A_chapter_scan_needs_only_download_permission_and_reports_its_candidates()
    {
        var (chapterId, _) = _world.Chapter(1);

        var result = await ScanAsync(new UpgradeScanRequest(null, chapterId), MakiPermission.DownloadChapters);

        var dto = Assert.IsType<UpgradeScanResultDto>(Assert.IsType<OkObjectResult>(result).Value);
        Assert.Equal(_world.OfficialMappingId, dto.QueuedFromMappingId);
        var candidate = Assert.Single(dto.Candidates);
        Assert.Equal(UpgradeReasons.Enqueued, candidate.Reason);
        Assert.Equal(1600, candidate.MedianWidth);
        Assert.Equal(0, _scheduler.Calls);
    }

    [Fact]
    public async Task A_series_scan_needs_only_download_permission_and_runs_as_a_job()
    {
        _world.Chapter(1);

        var result = await ScanAsync(new UpgradeScanRequest(_world.SeriesId), MakiPermission.DownloadChapters);

        Assert.IsType<AcceptedResult>(result);
        Assert.Equal(1, _scheduler.Calls);
        Assert.Empty(_world.Http.Requested);
        // This fake scheduler refuses the trigger, so the series page must hear the scan never ran.
        Assert.Equal("failed", _tracker.Status(_world.SeriesId)!.State);
    }

    [Theory]
    [InlineData(MakiPermission.DownloadChapters, false)]
    [InlineData(MakiPermission.DownloadChapters | MakiPermission.ManageDownloadQueue, false)]
    [InlineData(MakiPermission.Admin, true)]
    public async Task The_library_wide_scan_is_admin_only(MakiPermission permissions, bool allowed)
    {
        var result = await ScanAsync(new UpgradeScanRequest(null), permissions);

        if (allowed)
        {
            Assert.IsType<AcceptedResult>(result);
            Assert.Equal(1, _scheduler.Calls);
        }
        else
        {
            Assert.IsType<ForbidResult>(result);
            Assert.Equal(0, _scheduler.Calls);
        }
    }

    [Fact]
    public async Task A_missing_body_is_the_library_wide_scan()
    {
        Assert.IsType<ForbidResult>(await ScanAsync(null, MakiPermission.DownloadChapters));
        Assert.IsType<AcceptedResult>(await ScanAsync(null, MakiPermission.Admin));
    }

    [Fact]
    public async Task Naming_both_a_series_and_a_chapter_is_a_400()
    {
        var (chapterId, _) = _world.Chapter(1);

        var result = await ScanAsync(new UpgradeScanRequest(_world.SeriesId, chapterId), MakiPermission.Admin);

        Assert.IsType<BadRequestObjectResult>(result);
        Assert.Equal("error.upgrades.scanTargetAmbiguous", Code(result));
        Assert.Empty(_world.Http.Requested);
    }

    private readonly FakeReleases _releases = new();

    private UpgradesController Controller(Maki.Data.MakiDbContext db) =>
        new(new UpgradeEvaluationService(db, TestQuality.Create(_world.Registry)), db, new TestLocalizer(),
            NullLogger<UpgradesController>.Instance);

    private TorrentUpgradeService Torrents(Maki.Data.MakiDbContext db) => new(db,
        new UpgradeEvaluationService(db, TestQuality.Create(_world.Registry)), _releases, _world.Inbox, _world.Settings,
        TimeProvider.System, NullLogger<TorrentUpgradeService>.Instance);

    private int Proposal(int seriesId, string guid, Maki.Core.Entities.TorrentProposalStatus status =
        Maki.Core.Entities.TorrentProposalStatus.Pending)
    {
        using var db = _world.Db.NewContext();
        var release = FakeReleases.Release("Series v01 (Digital) (1r0n)", guid: guid);
        var row = new Maki.Core.Entities.TorrentProposal
        {
            SeriesId = seriesId, ReleaseGuid = guid, Title = release.Title, Indexer = release.Indexer,
            ReleaseInfoJson = JsonSerializer.Serialize(release, QualitySnapshot.Json), Status = status,
            SpanJson = JsonSerializer.Serialize(new Maki.Core.Parsing.ReleaseSpan(new Maki.Core.Parsing.NumberRange(1, 1), []),
                QualitySnapshot.Json),
            ReasonsJson = "[\"over_budget\"]", UpgradeCount = 3, CreatedAtUtc = DateTime.UtcNow
        };
        db.TorrentProposals.Add(row);
        db.SaveChanges();
        return row.Id;
    }

    [Fact]
    public async Task Proposals_are_listed_only_for_series_the_caller_can_see()
    {
        var hidden = _world.Db.SeedSeries("Hidden");
        Proposal(_world.SeriesId, "mine");
        Proposal(hidden, "theirs");
        Proposal(_world.SeriesId, "gone", Maki.Core.Entities.TorrentProposalStatus.Dismissed);
        var userId = _world.Db.SeedUser("reader", MakiPermission.DownloadChapters, allRootFolders: false);
        using (var db = _world.Db.NewContext())
        {
            db.UserRootFolders.Add(new Maki.Data.Identity.UserRootFolder
            {
                UserId = userId, RootFolderId = db.Series.Single(s => s.Id == _world.SeriesId).RootFolderId
            });
            db.SaveChanges();
        }

        using var scoped = _world.Db.NewContext(userId, allRootFolders: false);
        var result = await Controller(scoped).Proposals(null, "pending", CancellationToken.None);

        var dto = Assert.Single(Assert.IsType<List<TorrentProposalDto>>(Assert.IsType<OkObjectResult>(result).Value));
        Assert.Equal(_world.SeriesId, dto.SeriesId);
        Assert.Equal("pending", dto.Status);
        Assert.Equal("volume", dto.Tier);
        Assert.Equal(1, dto.Span.VolumeStart);
        Assert.Equal(["over_budget"], dto.Reasons);

        using var all = _world.Db.NewContext();
        var summary = Assert.IsType<UpgradeSummaryDto>(Assert.IsType<OkObjectResult>(
            await Controller(scoped).Summary(new UpgradeTrashService(all, _world.Settings, NullLogger<UpgradeTrashService>.Instance),
                _world.Settings, CancellationToken.None)).Value);
        Assert.Equal(1, summary.PendingProposals);
    }

    [Fact]
    public async Task Grabbing_a_proposal_queues_it_as_the_users_upgrade_and_accepts_it()
    {
        _world.Chapter(1);
        var id = Proposal(_world.SeriesId, "g");
        using var db = _world.Db.NewContext();
        var user = new TestCurrentUser(5, permissions: MakiPermission.DownloadChapters);

        var result = await Controller(db).GrabProposal(id, Torrents(db), user, CancellationToken.None);

        Assert.IsType<OkObjectResult>(result);
        var grab = Assert.Single(_releases.Grabs);
        Assert.Equal(DownloadOrigin.Upgrade, grab.Origin);
        Assert.Equal(5, grab.UserId);
        Assert.Equal(id, TorrentUpgradeInfo.Parse(grab.Info)!.ProposalId);
        using var check = _world.Db.NewContext();
        var row = check.TorrentProposals.Single(p => p.Id == id);
        Assert.Equal(Maki.Core.Entities.TorrentProposalStatus.Accepted, row.Status);
        Assert.Equal(1001, row.QueueItemId);
        Assert.Equal(5, row.ResolvedByUserId);

        var again = await Controller(db).GrabProposal(id, Torrents(db), user, CancellationToken.None);
        Assert.Equal("error.upgrades.proposalResolved", Code(again));
        var missing = await Controller(db).GrabProposal(id + 99, Torrents(db), user, CancellationToken.None);
        Assert.IsType<NotFoundObjectResult>(missing);
        Assert.Equal("error.upgrades.proposalNotFound", Code(missing));
    }

    [Fact]
    public async Task Dismissing_a_proposal_resolves_it_once()
    {
        var id = Proposal(_world.SeriesId, "g");
        using var db = _world.Db.NewContext();
        var user = new TestCurrentUser(5, permissions: MakiPermission.DownloadChapters);

        Assert.IsType<NoContentResult>(await Controller(db).DismissProposal(id, Torrents(db), user, CancellationToken.None));
        var again = await Controller(db).DismissProposal(id, Torrents(db), user, CancellationToken.None);
        Assert.IsType<ConflictObjectResult>(again);
        Assert.IsType<NotFoundObjectResult>(await Controller(db).DismissProposal(id + 99, Torrents(db), user, CancellationToken.None));

        using var check = _world.Db.NewContext();
        Assert.Equal(Maki.Core.Entities.TorrentProposalStatus.Dismissed, check.TorrentProposals.Single().Status);
        Assert.Empty(_releases.Grabs);
    }

    [Theory]
    [InlineData(MakiPermission.DownloadChapters, true, false)]
    [InlineData(MakiPermission.DownloadChapters, false, false)]
    [InlineData(MakiPermission.Admin, false, true)]
    public async Task The_volume_search_permission_matrix(MakiPermission permissions, bool withSeries, bool allowed)
    {
        using var db = _world.Db.NewContext();
        var request = new VolumeSearchRequest(withSeries ? _world.SeriesId : null);

        var result = await Controller(db).VolumeSearch(request, Torrents(db), _scheduler,
            new TestCurrentUser(1, permissions: permissions), CancellationToken.None);

        if (withSeries)
        {
            var dto = Assert.IsType<SeriesVolumeSearchResultDto>(Assert.IsType<OkObjectResult>(result).Value);
            // The manual search skips the instance switches, so the missing Prowlarr is what stops it.
            Assert.Equal(VolumeSearchReasons.NoProwlarr, dto.Reason);
        }
        else if (allowed)
        {
            Assert.IsType<AcceptedResult>(result);
            Assert.Equal(1, _scheduler.Calls);
        }
        else
        {
            Assert.IsType<ForbidResult>(result);
            Assert.Equal(0, _scheduler.Calls);
        }
    }

    [Fact]
    public async Task A_volume_search_for_a_missing_series_is_a_404()
    {
        using var db = _world.Db.NewContext();

        var result = await Controller(db).VolumeSearch(new VolumeSearchRequest(_world.SeriesId + 50), Torrents(db),
            _scheduler, new TestCurrentUser(1, permissions: MakiPermission.DownloadChapters), CancellationToken.None);

        Assert.IsType<NotFoundResult>(result);
    }

    [Fact]
    public async Task A_chapter_without_a_file_or_that_does_not_exist_is_a_404()
    {
        var (chapterId, _) = _world.Chapter(1, withFile: false);

        var noFile = await ScanAsync(new UpgradeScanRequest(null, chapterId), MakiPermission.DownloadChapters);
        var missing = await ScanAsync(new UpgradeScanRequest(null, chapterId + 100), MakiPermission.DownloadChapters);

        Assert.IsType<NotFoundObjectResult>(noFile);
        Assert.Equal("error.upgrades.chapterHasNoFile", Code(noFile));
        Assert.Equal("error.upgrades.chapterHasNoFile", Code(missing));
    }
}
