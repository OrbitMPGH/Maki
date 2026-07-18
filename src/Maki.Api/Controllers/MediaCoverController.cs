using Maki.Api.Configuration;
using Microsoft.AspNetCore.Mvc;

namespace Maki.Api.Controllers;

[ApiController]
[Route("api/v1/mediacover")]
public class MediaCoverController(AppPaths paths) : ControllerBase
{
    [HttpGet("{seriesId:int}/cover.jpg")]
    public IActionResult Cover(int seriesId)
    {
        var path = Path.Combine(paths.MediaCoverDir, seriesId.ToString(), "cover.jpg");
        if (!System.IO.File.Exists(path))
        {
            return NotFound();
        }

        return PhysicalFile(path, "image/jpeg");
    }
}
