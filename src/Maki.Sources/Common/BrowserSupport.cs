using System.Text.RegularExpressions;
using Microsoft.Playwright;

namespace Maki.Sources.Common;

internal static class BrowserSupport
{
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
