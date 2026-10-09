using Microsoft.AspNetCore.Authorization;
using Maki.Api.Auth;
using Maki.Api.Dtos;
using Maki.Api.Localization;
using Maki.Api.Services;
using Maki.Core.Download;
using Maki.Core.Entities;
using Maki.Core.Quality;
using Maki.Core.Security;
using Maki.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Maki.Api.Controllers;

[ApiController]
[Route("api/v1/release")]
// Both actions: the search hits the instance's Prowlarr indexers, and the grab pushes a torrent to
// qBittorrent. Neither is something a read-only account should reach.
[Authorize(Policy = Policies.DownloadChapters)]
public class ReleaseController(
    ReleaseService releaseService, ReleaseSearchCache searches, ILocalizer localizer, ILogger<ReleaseController> logger)
    : ControllerBase
{
    public record GrabRequest(int SeriesId, ReleaseDto Release);

    /// <summary>
    /// Each row carries <c>parsed</c>: its span, tier, score and what the volume search would do with
    /// it. Null when the series has no upgrade profile.
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> Search(
        [FromQuery] int seriesId, [FromQuery] string? query, [FromServices] TorrentUpgradeService torrents,
        CancellationToken ct)
    {
        try
        {
            var result = await releaseService.SearchAsync(seriesId, query, ct);
            searches.Remember(seriesId, result.Releases);
            var views = await torrents.EvaluateAsync(seriesId, result.Releases, ct);
            var byGuid = views
                .Where(v => !v.Verdict.Reasons.Contains(SpanVerdictReasons.NoProfile))
                .GroupBy(v => v.Release.Guid)
                .ToDictionary(g => g.Key, g => g.First());
            return Ok(result with
            {
                Releases = [.. result.Releases.Select(r => r with
                {
                    Parsed = byGuid.TryGetValue(r.Guid, out var v) ? Parsed(v) : null
                })]
            });
        }
        catch (ReleaseRefusedException ex)
        {
            return this.Fail(localizer, ex.Key);
        }
        catch (InvalidOperationException ex)
        {
            logger.LogWarning(ex, "Release search failed for series {SeriesId}", seriesId);
            return this.BadGateway(localizer, "error.release.searchFailed");
        }
    }

    [HttpPost("grab")]
    public async Task<IActionResult> Grab(
        [FromBody] GrabRequest request, [FromServices] ICurrentUser user, [FromServices] MakiDbContext db,
        CancellationToken ct)
    {
        // Only the guid is taken from the request: the link, title and indexer come from the search.
        if (searches.Find(request.SeriesId, request.Release.Guid) is not { } release)
        {
            return this.Fail(localizer, "error.release.searchExpired");
        }

        try
        {
            var item = await releaseService.GrabAsync(request.SeriesId, release,
                DownloadOrigin.Manual, user.UserId, null, ct);

            // Grabbed by hand from the search: a pending proposal for the same release is settled by it.
            if (await db.TorrentProposals.FirstOrDefaultAsync(p => p.SeriesId == request.SeriesId &&
                    p.ReleaseGuid == request.Release.Guid && p.Status == TorrentProposalStatus.Pending, ct) is { } proposal)
            {
                proposal.Status = TorrentProposalStatus.Accepted;
                proposal.QueueItemId = item.Id;
                proposal.ResolvedByUserId = user.UserId;
                proposal.ResolvedAtUtc = DateTime.UtcNow;
                await db.SaveChangesAsync(ct);
            }

            return Ok(new { queueItemId = item.Id });
        }
        catch (ReleaseRefusedException ex)
        {
            return this.Fail(localizer, ex.Key);
        }
        catch (QBittorrentException ex)
        {
            logger.LogWarning(ex, "qBittorrent refused the grab for series {SeriesId}", request.SeriesId);
            return this.BadGateway(localizer, ex.Failure switch
            {
                QBittorrentFailure.LoginFailed => "error.release.qbittorrentLoginFailed",
                _ => "error.release.qbittorrentRejected",
            });
        }
        catch (Exception ex) when (ex is InvalidOperationException or HttpRequestException)
        {
            logger.LogWarning(ex, "Release grab failed for series {SeriesId}", request.SeriesId);
            return this.BadGateway(localizer, "error.release.grabFailed");
        }
    }

    private static ReleaseParsedDto Parsed(TorrentCandidateView v) => new(
        ReleaseSpanDto.From(v.Parsed.Span, v.WholeSeries),
        QualityNames.Tier(v.Tier),
        v.Score,
        v.Verdict.Outcome switch
        {
            SpanOutcome.AutoGrab => "autoGrab",
            SpanOutcome.Proposal => "proposal",
            _ => "ignore"
        },
        v.Verdict.Reasons,
        v.Verdict.UpgradeCount,
        v.Verdict.AlreadyMetCount,
        v.Verdict.MissingCount,
        v.TitleMatched);
}
