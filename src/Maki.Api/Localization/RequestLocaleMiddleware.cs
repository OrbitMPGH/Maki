using System.Globalization;
using Maki.Core.Localization;

namespace Maki.Api.Localization;

/// <summary>
/// Reads the language the request itself asked for and hands it to
/// <see cref="RequestLocaleContext"/>. Nothing here touches the database, so it costs a couple of
/// header reads on every request and nothing more.
/// <para>
/// Runs after <c>CurrentUserMiddleware</c>, which is what decides whether there is a user whose
/// stored preference could be consulted when none of these is present.
/// </para>
/// </summary>
public class RequestLocaleMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context, RequestLocaleContext locale)
    {
        locale.SetRequested(Chosen(context.Request), Negotiated(context.Request));
        await next(context);
    }

    /// <summary>
    /// A language somebody asked for on purpose. Outranks everything, including a stored preference,
    /// because it is the most specific thing anyone said about this particular request.
    /// </summary>
    private static string? Chosen(HttpRequest request)
    {
        // An explicit ?lang=. OPDS readers cannot be told to send a header, and it is the easiest
        // way to look at a page in another language without changing a setting.
        if (SupportedLanguages.Match(request.Query["lang"].FirstOrDefault()) is { } fromQuery)
        {
            return fromQuery;
        }

        // The SPA's own header, set in one place in the client, so it covers the signed-out pages
        // too where there is no stored preference to read.
        return SupportedLanguages.Match(request.Headers["X-Maki-Language"].FirstOrDefault());
    }

    /// <summary>
    /// The best match against what the client says it can read. Ranked strictly BELOW the user's
    /// stored preference, and that distinction is load-bearing rather than pedantic: browsers attach
    /// Accept-Language to every request, so treating it as a choice means a Swede whose browser is
    /// in English never sees the language they actually selected.
    /// </summary>
    private static string? Negotiated(HttpRequest request)
    {
        foreach (var tag in Accepted(request.Headers.AcceptLanguage.ToString()))
        {
            if (SupportedLanguages.Match(tag) is { } match) return match;
        }
        return null;
    }

    /// <summary>
    /// Language tags from an Accept-Language header, best q-value first. Hand-parsed because the
    /// framework's parser arrives with <c>UseRequestLocalization</c>, which is the thing this whole
    /// design exists to avoid: it sets ambient <c>CurrentCulture</c>, and this codebase parses
    /// chapter numbers with <c>InvariantCulture</c> on purpose.
    /// </summary>
    private static IEnumerable<string> Accepted(string header)
    {
        if (string.IsNullOrWhiteSpace(header)) yield break;

        var ranked = header
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(part =>
            {
                var bits = part.Split(';', StringSplitOptions.TrimEntries);
                var quality = 1.0;
                foreach (var bit in bits.Skip(1))
                {
                    if (bit.StartsWith("q=", StringComparison.OrdinalIgnoreCase) &&
                        double.TryParse(bit[2..], NumberStyles.Float, CultureInfo.InvariantCulture, out var q))
                    {
                        quality = q;
                    }
                }
                return (Tag: bits[0], Quality: quality);
            })
            .Where(x => x.Quality > 0 && x.Tag != "*")
            .OrderByDescending(x => x.Quality)
            .ToList();

        foreach (var entry in ranked) yield return entry.Tag;
    }
}
