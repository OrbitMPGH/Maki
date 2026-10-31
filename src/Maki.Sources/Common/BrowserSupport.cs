using System.Text.RegularExpressions;
using Microsoft.Playwright;

namespace Maki.Sources.Common;

internal static class BrowserSupport
{
    /// <summary>
    /// Analytics, share-button and web-font hosts the scrapers' pages load and nothing they read
    /// needs. Recorded from live MangaFire and TopManhua sessions on 2026-10-26. A denylist rather
    /// than an allowlist on purpose: an allowlist breaks the source the day a site moves its CDN.
    /// </summary>
    private static readonly string[] UnneededHosts =
    [
        "googletagmanager.com",
        "google-analytics.com",
        "sharethis.com",
        "amung.us",
        "fonts.googleapis.com",
        "fonts.gstatic.com",
    ];

    public static bool IsUnneeded(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
        UnneededHosts.Any(h => uri.Host == h || uri.Host.EndsWith("." + h, StringComparison.Ordinal));

    /// <summary>
    /// Sec-CH-UA headers consistent with <paramref name="userAgent"/>, replacing the headless shell's own.
    /// <paramref name="brandSecond"/> keeps TopManhua's original brand order, which its fingerprint was tuned on.
    /// </summary>
    public static Dictionary<string, string> ClientHintsFor(string userAgent, bool brandSecond = false)
    {
        var major = Regex.Match(userAgent, @"Chrome/(\d+)").Groups[1].Value;
        var platform = userAgent.Contains("Windows", StringComparison.Ordinal) ? "Windows"
            : userAgent.Contains("Macintosh", StringComparison.Ordinal) ? "macOS"
            : userAgent.Contains("Android", StringComparison.Ordinal) ? "Android"
            : "Linux";

        var headers = new Dictionary<string, string>
        {
            ["sec-ch-ua-mobile"] = "?0",
            ["sec-ch-ua-platform"] = $"\"{platform}\"",
        };

        if (major.Length > 0)
        {
            headers["sec-ch-ua"] = brandSecond
                ? $"\"Chromium\";v=\"{major}\", \"Not_A Brand\";v=\"24\", \"Google Chrome\";v=\"{major}\""
                : $"\"Chromium\";v=\"{major}\", \"Google Chrome\";v=\"{major}\", \"Not=A?Brand\";v=\"24\"";
        }

        return headers;
    }

    /// <summary>Closes a context or browser that may already be gone; a dead process throws on close.</summary>
    public static Task CloseQuietlyAsync(IBrowserContext? context) =>
        CloseQuietlyAsync(context is null ? null : () => context.CloseAsync());

    public static Task CloseQuietlyAsync(IBrowser? browser) =>
        CloseQuietlyAsync(browser is null ? null : () => browser.CloseAsync());

    private static async Task CloseQuietlyAsync(Func<Task>? close)
    {
        if (close is null)
        {
            return;
        }

        try
        {
            await close();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
        }
    }
}
