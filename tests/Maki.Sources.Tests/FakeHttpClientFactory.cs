using System.Net;

namespace Maki.Sources.Tests;

/// <summary>
/// IHttpClientFactory whose clients answer from canned responses keyed by
/// URL substring. Used to run scraper parsers against recorded fixtures.
///
/// Binary fixtures go in the second map: MANGA Plus answers protobuf, which has no
/// lossless string round trip.
/// </summary>
public class FakeHttpClientFactory(
    Dictionary<string, string> responsesByUrlSubstring,
    Dictionary<string, byte[]>? binaryResponsesByUrlSubstring = null) : IHttpClientFactory
{
    public HttpClient CreateClient(string name)
    {
        return new HttpClient(new FakeHandler(responsesByUrlSubstring, binaryResponsesByUrlSubstring))
        {
            BaseAddress = new Uri("https://fixture.test/")
        };
    }

    public static string Fixture(string fileName) =>
        File.ReadAllText(FixturePath(fileName));

    public static byte[] BinaryFixture(string fileName) =>
        File.ReadAllBytes(FixturePath(fileName));

    private static string FixturePath(string fileName) =>
        Path.Combine(AppContext.BaseDirectory, "Fixtures", fileName);

    private class FakeHandler(
        Dictionary<string, string> responses,
        Dictionary<string, byte[]>? binaryResponses) : HttpMessageHandler
    {
        /// <summary>
        /// A JSON fixture is served as application/json: HttpClient's JSON extensions refuse a body
        /// whose media type isn't JSON, so a source read through GetFromJsonAsync can't be tested
        /// against a text/plain response. HTML fixtures are read with GetStringAsync and don't care.
        /// </summary>
        private static StringContent TextContent(string body) =>
            body.TrimStart().StartsWith('{') || body.TrimStart().StartsWith('[')
                ? new StringContent(body, System.Text.Encoding.UTF8, "application/json")
                : new StringContent(body);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var url = request.RequestUri!.ToString();
            foreach (var (substring, body) in responses)
            {
                if (url.Contains(substring, StringComparison.OrdinalIgnoreCase))
                {
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = TextContent(body)
                    });
                }
            }

            foreach (var (substring, bytes) in binaryResponses ?? [])
            {
                if (url.Contains(substring, StringComparison.OrdinalIgnoreCase))
                {
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new ByteArrayContent(bytes)
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
