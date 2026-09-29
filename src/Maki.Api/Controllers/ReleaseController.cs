using Microsoft.AspNetCore.Authorization;
using Maki.Api.Auth;
using Maki.Api.Dtos;
using Maki.Api.Services;
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
public class ReleaseController(ReleaseService releaseService) : ControllerBase
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
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpPost("grab")]
    public async Task<IActionResult> Grab(
        [FromBody] GrabRequest request, [FromServices] ICurrentUser user, [FromServices] MakiDbContext db,
        CancellationToken ct)
    {
        try
        {
            var item = await releaseService.GrabAsync(request.SeriesId, request.Release with { Parsed = null },
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
        catch (Exception ex) when (ex is InvalidOperationException or HttpRequestException)
        {
            return BadRequest(new { error = ex.Message });
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
