using System.Net;
using System.Text;
using System.Text.Json;
using Maki.Sources.Suwayomi;

namespace Maki.Api.Tests;

/// <summary>The real origin and auth handlers in front of a fake server, as the named clients chain them.</summary>
internal sealed class SuwayomiChainFactory(SuwayomiSettings settings, Func<SuwayomiTokenStore> tokens, HttpMessageHandler server)
    : IHttpClientFactory
{
    public HttpClient CreateClient(string name) =>
        new(new SuwayomiOriginHandler(settings)
        {
            InnerHandler = new SuwayomiAuthHandler(tokens()) { InnerHandler = server }
        }, disposeHandler: false);
}

/// <summary>Routes GraphQL calls by operationName, the one thing that tells them apart.</summary>
internal sealed class FakeSuwayomiServer : HttpMessageHandler
{
    public Dictionary<string, Func<JsonElement, HttpResponseMessage>> Ops { get; } = [];
    public Func<HttpRequestMessage, HttpResponseMessage> OnGet { get; set; } = _ => new HttpResponseMessage(HttpStatusCode.NotFound);
    public List<string> Calls { get; } = [];

    public int Count(string op) => Calls.Count(c => c == op);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        if (request.Method == HttpMethod.Get)
        {
            Calls.Add("GET " + request.RequestUri!.AbsolutePath);
            return OnGet(request);
        }

        using var doc = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
        var op = doc.RootElement.GetProperty("operationName").GetString()!;
        Calls.Add(op);
        return Ops.TryGetValue(op, out var respond)
            ? respond(doc.RootElement.GetProperty("variables").Clone())
            : throw new InvalidOperationException($"No answer for {op}");
    }

    public static HttpResponseMessage Data(object data) => Json(new { data });

    /// <summary>What Suwayomi really answers a wrong username or password with: no "Unauthorized" in it.</summary>
    public const string LoginRefused = "Exception while fetching data (/login) : Incorrect username or password.";

    public static HttpResponseMessage Errors(params string[] messages) =>
        Json(new { data = (object?)null, errors = messages.Select(m => new { message = m }).ToArray() });

    private static HttpResponseMessage Json(object body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json") };

    /// <summary>Answers the source list Suwayomi reports, for the picker and the installed check alike.</summary>
    public void Sources(params (string Id, string Name, string DisplayName, string Lang)[] sources) =>
        Ops["Sources"] = _ => Data(new
        {
            sources = new
            {
                nodes = sources.Select(s => new
                {
                    id = s.Id, name = s.Name, displayName = s.DisplayName, lang = s.Lang,
                    isNsfw = false, contentWarning = "SAFE"
                }).ToArray()
            }
        });

    // The connection tester disposes its handler; this one is shared across a test.
    protected override void Dispose(bool disposing)
    {
    }
}
