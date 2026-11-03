using System.Net;

namespace Maki.Sources.Custom;

/// <summary>
/// Sits inside <c>ProxiedTargetGuardHandler</c>, which follows redirects itself, so it sees every
/// hop: a redirect to another site fails the request before the definition's headers go there. A
/// custom source's pages, and HeanCMS's API, stay on the site or a subdomain of it.
/// </summary>
public sealed class SameSiteRedirectHandler : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var response = await base.SendAsync(request, ct);
        if (response.StatusCode is HttpStatusCode.MovedPermanently or HttpStatusCode.Found or HttpStatusCode.SeeOther
                or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect
            && response.Headers.Location is { } location
            && request.RequestUri is { } from)
        {
            var to = location.IsAbsoluteUri ? location : new Uri(from, location);
            if (!SameSite(from.Host, to.Host))
            {
                response.Dispose();
                throw new HttpRequestException($"{from.Host} redirected to another site ({to.Host})", null, HttpStatusCode.Redirect);
            }
        }

        return response;
    }

    /// <summary>One host, with or without <c>www.</c>, or one a subdomain of the other.</summary>
    public static bool SameSite(string a, string b)
    {
        var x = CustomHost.Strip(a);
        var y = CustomHost.Strip(b);
        return string.Equals(x, y, StringComparison.OrdinalIgnoreCase)
            || x.EndsWith("." + y, StringComparison.OrdinalIgnoreCase)
            || y.EndsWith("." + x, StringComparison.OrdinalIgnoreCase);
    }
}
