using Mangarr.Api.Configuration;
using Microsoft.AspNetCore.Mvc;

namespace Mangarr.Api.Controllers;

[ApiController]
[Route("api/v1/system")]
public class SystemController(AppPaths paths) : ControllerBase
{
    [HttpGet("status")]
    public IActionResult Status()
    {
        return Ok(new
        {
            appName = "Mangarr",
            version = typeof(Program).Assembly.GetName().Version?.ToString(3) ?? "0.0.0",
            osName = Environment.OSVersion.Platform.ToString(),
            configDir = paths.ConfigDir,
            startTime = System.Diagnostics.Process.GetCurrentProcess().StartTime.ToUniversalTime()
        });
    }
}
