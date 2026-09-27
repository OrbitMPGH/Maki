using System.Reflection;
using System.Text.Json;
using Maki.Api.Auth;
using Maki.Api.Controllers;
using Maki.Api.Dtos;
using Maki.Api.Services;
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

    private async Task<IActionResult> ScanAsync(UpgradeScanRequest? request, MakiPermission permissions)
    {
        using var db = _world.Db.NewContext();
        using var batches = _world.Batches();
        var controller = new UpgradesController(new UpgradeEvaluationService(db, TestQuality.Create(_world.Registry)), db,
            new TestLocalizer(), NullLogger<UpgradesController>.Instance);
        return await controller.Scan(request, _world.Scanner(db, batches), _scheduler,
            new TestCurrentUser(1, permissions: permissions), CancellationToken.None);
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
    public async Task A_series_scan_needs_only_download_permission()
    {
        _world.Chapter(1);

        var result = await ScanAsync(new UpgradeScanRequest(_world.SeriesId), MakiPermission.DownloadChapters);

        var dto = Assert.IsType<UpgradeScanResultDto>(Assert.IsType<OkObjectResult>(result).Value);
        Assert.Equal(1, dto.Enqueued);
        Assert.Empty(dto.Candidates);
        Assert.Null(dto.QueuedFromMappingId);
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
