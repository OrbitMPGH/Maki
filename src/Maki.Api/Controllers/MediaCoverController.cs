using Maki.Api.Configuration;
using Maki.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Maki.Api.Controllers;

[ApiController]
[Route("api/v1/mediacover")]
public class MediaCoverController(AppPaths paths, MakiDbContext db) : ControllerBase
{
    /// <summary>
    /// Serves a series' poster.
    /// <para>
    /// The existence check goes through EF and not through the filesystem, and that is the whole
    /// point of the query: the path is derived from a caller-supplied id, so serving the file
    /// directly hands every cover in the instance to anyone with an account, including one granted a
    /// single root folder. Resolving the series first puts the request under the <c>Series</c> global
    /// query filter, which is where library access is decided for every other read in the app —
    /// nothing here has to know what a root-folder grant is.
    /// </para>
    /// <para>
    /// A series the caller cannot see answers <b>404</b> and not 403, so the endpoint does not
    /// confirm which ids exist.
    /// </para>
    /// </summary>
    [HttpGet("{seriesId:int}/cover.jpg")]
    public async Task<IActionResult> Cover(int seriesId, CancellationToken ct)
    {
        if (!await db.Series.AnyAsync(s => s.Id == seriesId, ct))
        {
            return NotFound();
        }

        var path = Path.Combine(paths.MediaCoverDir, seriesId.ToString(), "cover.jpg");
        if (!System.IO.File.Exists(path))
        {
            return NotFound();
        }

        return PhysicalFile(path, "image/jpeg");
    }
}
