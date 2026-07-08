using System.Net;

namespace Mangarr.Sources.Tests;

/// <summary>
/// IHttpClientFactory whose clients answer from canned responses keyed by
/// URL substring. Used to run scraper parsers against recorded fixtures.
/// </summary>
public class FakeHttpClientFactory(Dictionary<string, string> responsesByUrlSubstring) : IHttpClientFactory
{
    public HttpClient CreateClient(string name)
    {
        return new HttpClient(new FakeHandler(responsesByUrlSubstring))
        {
            BaseAddress = new Uri("https://fixture.test/")
        };
    }

    public static string Fixture(string fileName) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", fileName));

    private class FakeHandler(Dictionary<string, string> responses) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var url = request.RequestUri!.ToString();
            foreach (var (substring, body) in responses)
            {
                if (url.Contains(substring, StringComparison.OrdinalIgnoreCase))
                {
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(body)
                    });
                }
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)
            {
                Content = new StringContent($"No fixture for {url}")
            });
        }
    }
}
