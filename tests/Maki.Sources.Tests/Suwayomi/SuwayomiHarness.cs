using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using Maki.Core.Configuration;
using Maki.Sources.Suwayomi;

namespace Maki.Sources.Tests.Suwayomi;

/// <summary>
/// A fake Suwayomi server. Every GraphQL call goes to the same URL, so calls are routed by the
/// request's <c>operationName</c>, which the client always sends; GETs (pages, thumbnails) go to
/// <see cref="OnGet"/>.
/// </summary>
internal sealed class FakeSuwayomi : HttpMessageHandler
{
    public ConcurrentDictionary<string, Func<JsonElement, HttpRequestMessage, HttpResponseMessage>> Ops { get; } = new();

    public Func<HttpRequestMessage, HttpResponseMessage> OnGet { get; set; } = _ => new HttpResponseMessage(HttpStatusCode.NotFound);

    public ConcurrentQueue<(string Op, string? Auth, Uri Uri)> Calls { get; } = new();

    public int Count(string op) => Calls.Count(c => c.Op == op);

    /// <summary>Awaited after a call is recorded and before it is answered, to make callers overlap.</summary>
    public Func<string, HttpRequestMessage, Task>? Hold { get; set; }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var auth = request.Headers.Authorization?.ToString();
        if (request.Method == HttpMethod.Get)
        {
            Calls.Enqueue(("GET " + request.RequestUri!.AbsolutePath, auth, request.RequestUri));
            return OnGet(request);
        }

        using var doc = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
        var op = doc.RootElement.GetProperty("operationName").GetString()!;
        Calls.Enqueue((op, auth, request.RequestUri!));
        if (Hold is { } hold)
        {
            await hold(op, request);
        }

        return Ops.TryGetValue(op, out var respond)
            ? respond(doc.RootElement.GetProperty("variables").Clone(), request)
            : throw new InvalidOperationException($"The fake Suwayomi has no answer for {op}");
    }

    public static HttpResponseMessage Data(object data) => Json(new { data });

    /// <summary>What Suwayomi really answers a wrong username or password with: no "Unauthorized" in it.</summary>
    public const string LoginRefused = "Exception while fetching data (/login) : Incorrect username or password.";

    public static HttpResponseMessage Errors(params string?[] messages) =>
        Json(new { data = (object?)null, errors = messages.Select(m => new { message = m }).ToArray() });

    public static HttpResponseMessage Json(object body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json") };
}

internal sealed class InMemorySettings : IAppSettings
{
    private readonly ConcurrentDictionary<string, string?> _values = new();

    public Task<string?> GetAsync(string key, CancellationToken ct = default) =>
        Task.FromResult(_values.GetValueOrDefault(key));

    public Task SetAsync(string key, string? value, CancellationToken ct = default)
    {
        _values[key] = value;
        return Task.CompletedTask;
    }
}

internal sealed class ManualClock(DateTimeOffset start) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = start;

    public override DateTimeOffset GetUtcNow() => Now;
}

/// <summary>The real handler chain (origin check, auth) in front of <see cref="FakeSuwayomi"/>.</summary>
internal sealed class SuwayomiHarness : IHttpClientFactory
{
    public const string Base = "http://192.168.1.20:4567/suwayomi";

    public FakeSuwayomi Server { get; } = new();
    public InMemorySettings Settings { get; } = new();
    public ManualClock Clock { get; } = new(new DateTimeOffset(2026, 10, 12, 12, 0, 0, TimeSpan.Zero));
    public SuwayomiSettings SuwayomiSettings { get; }
    public SuwayomiTokenStore Tokens { get; }
    public SuwayomiClient Client { get; }
    public SuwayomiMangaIdCache Cache { get; } = new();
    public List<string> ClientNames { get; } = [];

    public SuwayomiHarness(string baseUrl = Base, string authMode = "none", string? username = null, string? password = null)
    {
        Settings.SetAsync(SettingKeys.SuwayomiUrl, baseUrl);
        Settings.SetAsync(SettingKeys.SuwayomiAuthMode, authMode);
        Settings.SetAsync(SettingKeys.SuwayomiUsername, username);
        Settings.SetAsync(SettingKeys.SuwayomiPassword, password);
        SuwayomiSettings = new SuwayomiSettings(Settings);
        Tokens = new SuwayomiTokenStore(SuwayomiSettings, this, Clock);
        Client = new SuwayomiClient(this, SuwayomiSettings, Clock);
        Installed("100");
    }

    public HttpClient CreateClient(string name)
    {
        lock (ClientNames)
        {
            ClientNames.Add(name);
        }

        var chain = new SuwayomiOriginHandler(SuwayomiSettings)
        {
            InnerHandler = new SuwayomiAuthHandler(Tokens) { InnerHandler = Server }
        };
        return new HttpClient(chain, disposeHandler: false);
    }

    public SuwayomiSource Source(string id = "100", string lang = "en", string? chapterLanguage = null, string? contentWarning = null) =>
        new(new SuwayomiStoredSource(id, "Fake", "Fake (EN)", lang, chapterLanguage, contentWarning),
            new Uri(Base), Client, Cache);

    public void Installed(params string[] ids) =>
        Server.Ops["InstalledSources"] = (_, _) => FakeSuwayomi.Data(new { sources = new { nodes = ids.Select(id => new { id }).ToArray() } });

    public void MangaByUrl(int? id) =>
        Server.Ops["MangaByUrl"] = (_, _) => FakeSuwayomi.Data(new
        {
            mangas = new { nodes = id is { } found ? new[] { new { id = found } } : [] }
        });

    public static object Manga(int id, string url, string title = "Hajime no Ippo", string? realUrl = null, string? thumbnailUrl = "/api/v1/manga/1/thumbnail") => new
    {
        id,
        sourceId = "100",
        url,
        title,
        thumbnailUrl,
        author = "George Morikawa",
        description = "Boxing.",
        realUrl,
        status = "ONGOING"
    };

    public static object Chapter(int id, string url, string name, double chapterNumber, long uploadDate = 0,
        int sourceOrder = 1, string? scanlator = null) => new
    {
        id,
        url,
        name,
        chapterNumber,
        scanlator,
        uploadDate = uploadDate.ToString(System.Globalization.CultureInfo.InvariantCulture),
        sourceOrder,
        realUrl = (string?)null
    };

    public void Refresh(object manga, params object[] chapters) =>
        Server.Ops["Refresh"] = (_, _) => FakeSuwayomi.Data(new { fetchMangaAndChapters = new { manga, chapters } });
}
