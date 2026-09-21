namespace Maki.Sources.Common;

/// <summary>
/// A source that keeps a headless browser alive between calls and can be asked to give it back.
///
/// <para>
/// Both implementations launch lazily and then hold the browser for the life of the process, which
/// is right while a sync is running and wrong for the rest of the day. It is not a small amount:
/// measured inside the container on a NAS, the Playwright Node driver held 96 MB of anonymous
/// memory and the headless shell's four processes another 65 MB, none of it visible to the managed
/// heap and none of it needed by an instance nobody is scraping with.
/// </para>
///
/// <para>
/// Relaunching costs a couple of seconds plus a fresh Cloudflare clearance, so this is for an
/// instance that has been quiet for a while, not something to do between two chapters.
/// </para>
/// </summary>
public interface IIdleBrowser
{
    /// <summary>The source this browser belongs to, for the log line.</summary>
    string BrowserName { get; }

    /// <summary>Whether a browser is launched right now, for the memory diagnostics.</summary>
    bool IsRunning { get; }

    /// <summary>
    /// Closes the browser when nothing has used it for <paramref name="idleFor"/>, and reports
    /// whether it did. Never waits on an in-flight scrape.
    /// </summary>
    Task<bool> ReleaseIfIdleAsync(TimeSpan idleFor);
}
