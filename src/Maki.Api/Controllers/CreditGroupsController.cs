using Maki.Api.Auth;
using Maki.Api.Dtos;
using Maki.Api.Localization;
using Maki.Api.Services;
using Maki.Core.Localization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Maki.Api.Controllers;

/// <summary>
/// The release groups behind credit pages and ads, for the groups view under Settings > Library > Credit pages.
/// Admin only, like the credit scan setting: a decision here changes what every reader has hidden.
/// </summary>
[ApiController]
[Route("api/v1/creditgroups")]
[Authorize(Policy = Policies.Admin)]
public class CreditGroupsController(CreditGroupsService groups, ILocalizer localizer) : ControllerBase
{
    /// <summary>
    /// The groups, the Site bucket and the Unnamed bucket. By default only notices readers have hidden or
    /// that someone named or marked; <paramref name="all"/> lists every notice a chapter links to.
    /// </summary>
    [HttpGet]
    public async Task<ActionResult<CreditGroupListDto>> List([FromQuery] bool all, CancellationToken ct) =>
        await groups.ListAsync(all, ct);

    /// <summary>The listed notices and the series and chapters that carry them. Changes nothing; a POST for the body.</summary>
    [HttpPost("notices")]
    public async Task<ActionResult<CreditGroupDetailDto>> Notices([FromBody] CreditNoticeIdsRequest request, CancellationToken ct) =>
        await groups.DetailAsync(request.NoticeIds ?? [], ct);

    /// <summary>The notice's region, cut from the page of a chapter that carries it.</summary>
    [HttpGet("notices/{id:int}/image")]
    public async Task<IActionResult> Image(
        int id, [FromServices] ReaderService reader, [FromServices] ReaderArchiveCache archives, CancellationToken ct)
    {
        var image = await groups.ImageAsync(id, reader, archives, ct);
        if (image is null)
        {
            return NotFound();
        }

        Response.Headers.CacheControl = "private, max-age=86400";
        return File(image, "image/jpeg");
    }

    [HttpPut("notices/{id:int}")]
    public async Task<IActionResult> Update(int id, [FromBody] UpdateCreditNoticeRequest request, CancellationToken ct)
    {
        var (notice, error) = await groups.UpdateAsync(id, request, ct);
        return error switch
        {
            null => Ok(notice),
            CreditGroupsService.NoticeNotFound => this.NotFoundMessage(localizer, error),
            _ => this.Fail(localizer, error),
        };
    }

    /// <summary>Confirms one name for several notices, such as every notice that suggests it.</summary>
    [HttpPut("name")]
    public async Task<IActionResult> Name([FromBody] NameCreditNoticesRequest request, CancellationToken ct)
    {
        var (named, error) = await groups.NameAsync(request.NoticeIds ?? [], request.Name, ct);
        return error switch
        {
            null => Ok(new { named }),
            CreditGroupsService.NoticeNotFound => this.NotFoundMessage(localizer, error),
            _ => this.Fail(localizer, error),
        };
    }

    /// <summary>Gives the notices' group to every chapter they carry that has none, then renames as the format asks.</summary>
    [HttpPost("apply")]
    public async Task<IActionResult> Apply([FromBody] CreditNoticeIdsRequest request, CancellationToken ct)
    {
        if (request.NoticeIds is not { Count: > 0 })
        {
            return this.Fail(localizer, CreditGroupsService.NoNotices);
        }

        return Ok(await groups.ApplyAsync(request.NoticeIds, ct));
    }
}
