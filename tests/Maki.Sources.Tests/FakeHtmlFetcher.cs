using Maki.Core.Http;

namespace Maki.Sources.Tests;

/// <summary>
/// <see cref="IHtmlFetcher"/> answering from canned bodies keyed by URL substring, the counterpart
/// of <see cref="FakeHttpClientFactory"/> for sources that fetch through the challenge fetcher
/// instead of a named HttpClient.
/// </summary>
public class FakeHtmlFetcher(Dictionary<string, string> responsesByUrlSubstring) : IHtmlFetcher
{
    /// <summary>Every URL asked for, in order, so a test can assert on the requests themselves.</summary>
    public List<string> Requested { get; } = [];

    public Task<string> GetHtmlAsync(string url, CancellationToken ct = default)
    {
        Requested.Add(url);

        foreach (var (substring, body) in responsesByUrlSubstring)
        {
            if (url.Contains(substring, StringComparison.OrdinalIgnoreCase))
            {
                return Task.FromResult(body);
            }
        }

        throw new HttpRequestException($"No fixture for {url}");
    }
}
