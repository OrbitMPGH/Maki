using Maki.Sources.Common;
using Quartz;

namespace Maki.Api.Jobs;

/// <summary>
/// Closes the headless browsers the scrapers keep alive once nothing has scraped for a while.
///
/// <para>
/// Two sources need a real browser (MangaFire's request signature, TopManhua's image CDN), and both
/// launched lazily and then held the browser for the life of the process. Measured inside the
/// container on a NAS: the Playwright Node driver sat on 96 MB of anonymous memory and the headless
/// shell's four processes on another 65 MB. None of that is on the managed heap, so none of it
/// showed up in any of the heap work, and a user who once downloaded one MangaFire chapter paid for
/// it until the next restart.
/// </para>
///
/// <para>
/// The price of being wrong is a relaunch on the next scrape: a couple of seconds for the driver
/// and the shell, plus a fresh Cloudflare clearance through FlareSolverr, which is the slower half.
/// That is why this is minutes rather than seconds, and why the window is per-browser idleness
/// rather than a fixed timer - a download that walks a hundred chapters keeps stamping its browser
/// and never meets it. <c>MAKI_BROWSER_IDLE_MINUTES=0</c> keeps them alive forever.
/// </para>
/// </summary>
[DisallowConcurrentExecution]
public class BrowserIdleShutdownJob(
    IEnumerable<IIdleBrowser> browsers,
    ILogger<BrowserIdleShutdownJob> logger) : IJob
{
    public static readonly JobKey Key = new("browser-idle-shutdown");

    public const string IdleMinutesVariable = "MAKI_BROWSER_IDLE_MINUTES";

    private const int DefaultIdleMinutes = 15;

    public async Task Execute(IJobExecutionContext context)
    {
        var minutes = Resolve(Environment.GetEnvironmentVariable(IdleMinutesVariable));
        if (minutes <= 0)
        {
            return;
        }

        var idleFor = TimeSpan.FromMinutes(minutes);
        foreach (var browser in browsers)
        {
            try
            {
                await browser.ReleaseIfIdleAsync(idleFor);
            }
            catch (Exception ex)
            {
                // A browser that will not close is a reason to leave it alone, not to skip the
                // others. It stays resident until the next pass or a restart.
                logger.LogWarning(ex, "Could not close the idle {Browser} browser", browser.BrowserName);
            }
        }
    }

    /// <inheritdoc cref="IdleWindow.Resolve"/>
    internal static int Resolve(string? value) => IdleWindow.Resolve(value, DefaultIdleMinutes);
}
