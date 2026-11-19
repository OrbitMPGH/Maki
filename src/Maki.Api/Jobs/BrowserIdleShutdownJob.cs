using Maki.Core.Io;
using Maki.Sources.Common;
using Quartz;

namespace Maki.Api.Jobs;

/// <summary>
/// Closes the headless browsers the scrapers keep alive once nothing has scraped for a while.
///
/// <para>
/// Two sources need a real browser (MangaFire's request signature, TopManhua's image CDN), and both
/// launched lazily and then held the browser for the life of the process. Measured inside the
/// container on a NAS: the headless shell's four processes sat on 65 MB, and the Playwright Node
/// driver that drove them then (gone since) on another 96 MB. None of that is on the managed heap,
/// so none of it showed up in any of the heap work, and a user who once downloaded one MangaFire
/// chapter paid for it until the next restart.
/// </para>
///
/// <para>
/// The price of being wrong is a relaunch on the next scrape: well under a second for the shell,
/// likely one or two from a hard disk. The Cloudflare clearance is not lost with it:
/// <c>ChallengeAwareFetcher</c> caches it per host until a challenge or a 403/503 proves it stale,
/// so a relaunch reuses it. The window is per-browser idleness rather than a fixed timer - a
/// download that walks a hundred chapters keeps stamping its browser and never meets it. <c>MAKI_BROWSER_IDLE_MINUTES=0</c> keeps them
/// alive forever.
/// </para>
///
/// <para>
/// One minute. It started at fifteen, went to ten and then three; with the clearance surviving a
/// relaunch, the only cost of a shorter window is the relaunch (0.3 to 0.7 s measured, likely 1 to
/// 2 s from a hard disk), against 300 to 460 MB of browser held after a burst. A scrape holds the
/// browser's gate for its whole length, so the window only ever falls between two calls.
/// </para>
/// </summary>
[DisallowConcurrentExecution]
public class BrowserIdleShutdownJob(
    IEnumerable<IIdleBrowser> browsers,
    ILogger<BrowserIdleShutdownJob> logger) : IJob
{
    public static readonly JobKey Key = new("browser-idle-shutdown");

    public const string IdleMinutesVariable = "MAKI_BROWSER_IDLE_MINUTES";

    private const int DefaultIdleMinutes = 1;

    public async Task Execute(IJobExecutionContext context)
    {
        var minutes = Resolve(Environment.GetEnvironmentVariable(IdleMinutesVariable));
        if (minutes <= 0)
        {
            return;
        }

        var idleFor = TimeSpan.FromMinutes(minutes);
        var closed = false;
        foreach (var browser in browsers)
        {
            try
            {
                closed |= await browser.ReleaseIfIdleAsync(idleFor);
            }
            catch (Exception ex)
            {
                // A browser that will not close is a reason to leave it alone, not to skip the
                // others. It stays resident until the next pass or a restart.
                logger.LogWarning(ex, "Could not close the idle {Browser} browser", browser.BrowserName);
            }
        }

        if (closed)
        {
            NativeHeap.Trim();
        }
    }

    /// <inheritdoc cref="IdleWindow.Resolve"/>
    internal static int Resolve(string? value) => IdleWindow.Resolve(value, DefaultIdleMinutes);
}
