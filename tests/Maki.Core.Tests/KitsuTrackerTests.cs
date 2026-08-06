using System.Net;
using Maki.Core.Configuration;
using Maki.Core.Entities;
using Maki.Core.Scrobbling;
using Microsoft.Extensions.Logging.Abstractions;

namespace Maki.Core.Tests;

/// <summary>
/// Covers the two things that made Kitsu unusable: pushes addressed with Maki's own user id instead
/// of the Kitsu one (so every write hit a stranger's account, failed, and was retried on every tick),
/// and a Cloudflare challenge answered with an in-band retry rather than a back-off.
/// </summary>
public class KitsuTrackerTests
{
    private const int MakiUserId = 1;
    private const string KitsuUserId = "42603";

    /// <summary>Records every request and answers from the first matching rule.</summary>
    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly List<(string Pattern, Func<HttpResponseMessage> Respond)> _rules = [];

        public List<HttpRequestMessage> Requests { get; } = [];
        public List<string> Bodies { get; } = [];

        public RecordingHandler On(string pattern, string json, HttpStatusCode status = HttpStatusCode.OK)
        {
            _rules.Add((pattern, () => new HttpResponseMessage(status)
            {
                Content = new StringContent(json, System.Text.Encoding.UTF8, "application/vnd.api+json"),
            }));
            return this;
        }

        public RecordingHandler On(string pattern, Func<HttpResponseMessage> respond)
        {
            _rules.Add((pattern, respond));
            return this;
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(request);
            Bodies.Add(request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct));

            var url = request.RequestUri!.ToString();
            foreach (var (pattern, respond) in _rules)
            {
                if (url.Contains(pattern, StringComparison.OrdinalIgnoreCase))
                {
                    return respond();
                }
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound)
            {
                Content = new StringContent("{}", System.Text.Encoding.UTF8, "application/vnd.api+json"),
            };
        }

        public IEnumerable<string> Urls => Requests.Select(r => r.RequestUri!.ToString());
    }

    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class Settings(Dictionary<string, string> values) : IAppSettings
    {
        public Task<string?> GetAsync(string key, CancellationToken ct = default) =>
            Task.FromResult(values.GetValueOrDefault(key));

        public Task SetAsync(string key, string? value, CancellationToken ct = default)
        {
            values[key] = value ?? "";
            return Task.CompletedTask;
        }
    }

    private sealed class UserSettings(Dictionary<string, string> values) : IUserSettingsStore
    {
        public Task<string?> GetAsync(int userId, string key, CancellationToken ct = default) =>
            Task.FromResult(values.GetValueOrDefault(key));

        public Task SetAsync(int userId, string key, string? value, CancellationToken ct = default)
        {
            values[key] = value ?? "";
            return Task.CompletedTask;
        }
    }

    private sealed class TokenStore : IScrobbleTokenStore
    {
        public ScrobbleToken? Token { get; set; }

        public Task<ScrobbleToken?> GetAsync(int userId, string service, CancellationToken ct = default) =>
            Task.FromResult(Token);

        public Task SaveAsync(ScrobbleToken token, CancellationToken ct = default)
        {
            Token = token;
            return Task.CompletedTask;
        }

        public Task DeleteAsync(int userId, string service, CancellationToken ct = default)
        {
            Token = null;
            return Task.CompletedTask;
        }
    }

    private const string ProfileJson =
        """{"data":[{"id":"42603","type":"users","attributes":{"name":"reader"}}]}""";

    private static string EntryJson(string entryId = "14990645", int progress = 3) => $$$"""
        {"data":[{"id":"{{{entryId}}}","type":"libraryEntries",
          "attributes":{"status":"current","progress":{{{progress}}},"ratingTwenty":null}}],
         "included":[{"id":"8","type":"manga",
          "attributes":{"canonicalTitle":"Berserk","chapterCount":374,"volumeCount":41}}]}
        """;

    private const string NoEntryJson = """{"data":[],"included":[]}""";

    private static KitsuTracker Build(RecordingHandler handler, TokenStore? tokens = null)
    {
        tokens ??= new TokenStore
        {
            Token = new ScrobbleToken
            {
                UserId = MakiUserId,
                Service = "kitsu",
                AccessToken = "access",
                ExpiresAt = DateTime.UtcNow.AddDays(20),
            },
        };

        return new KitsuTracker(
            new Factory(handler),
            new Settings(new Dictionary<string, string>
            {
                [SettingKeys.ScrobbleKitsuClientId] = "client",
                [SettingKeys.ScrobbleKitsuClientSecret] = "secret",
            }),
            new UserSettings(new Dictionary<string, string>
            {
                [SettingKeys.ScrobbleKitsuEmail] = "reader@example.test",
                [SettingKeys.ScrobbleKitsuPassword] = "pw",
            }),
            tokens,
            new ScrobbleTrackerOptions(
                "https://anilist.test", "https://anilist.test/oauth",
                "https://mal.test", "https://mal.test/oauth",
                "https://mangabaka.test",
                "https://kitsu.test/api/edge", "https://kitsu.test/api/oauth"),
            NullLogger<KitsuTracker>.Instance);
    }

    private static RecordingHandler HandlerWithProfile() =>
        new RecordingHandler().On("filter[self]=true", ProfileJson);

    [Fact]
    public async Task LibraryReadsAreFilteredByTheKitsuUserId_NotMakisOwn()
    {
        var handler = HandlerWithProfile().On("/library-entries", EntryJson());
        var entry = await Build(handler).GetEntryAsync(MakiUserId, "8");

        Assert.Equal(3, entry.ProgressChapter);
        Assert.Equal(ScrobbleStatus.Reading, entry.Status);
        Assert.Equal(374, entry.TotalChapters);

        var libraryUrl = Assert.Single(handler.Urls, u => u.Contains("/library-entries"));
        Assert.Contains($"filter[userId]={KitsuUserId}", libraryUrl);
        Assert.DoesNotContain($"filter[userId]={MakiUserId}&", libraryUrl);
    }

    [Fact]
    public async Task CreatingAnEntryAddressesTheKitsuUser_NotMakisOwnId()
    {
        var handler = HandlerWithProfile()
            .On("/library-entries?", NoEntryJson)
            .On("/manga/8", """{"data":{"id":"8","type":"manga","attributes":{"canonicalTitle":"Berserk"}}}""")
            .On("/library-entries", """{"data":{"id":"999","type":"libraryEntries"}}""");

        var kitsu = Build(handler);
        await kitsu.GetEntryAsync(MakiUserId, "8");
        await kitsu.UpdateAsync(MakiUserId, "8", chapter: 5, volume: 0, ScrobbleStatus.Reading);

        var post = Assert.Single(handler.Requests, r => r.Method == HttpMethod.Post);
        var body = handler.Bodies[handler.Requests.IndexOf(post)];
        Assert.Contains($"\"id\":\"{KitsuUserId}\"", body);
        Assert.DoesNotContain($"\"id\":{MakiUserId}", body);
    }

    [Fact]
    public async Task AReadFollowedByAWriteCostsTwoRequests_NotFour()
    {
        var handler = HandlerWithProfile()
            .On("/library-entries/14990645", """{"data":{"id":"14990645","type":"libraryEntries"}}""")
            .On("/library-entries", EntryJson());

        var kitsu = Build(handler);
        await kitsu.GetEntryAsync(MakiUserId, "8");
        var afterRead = handler.Requests.Count;
        await kitsu.UpdateAsync(MakiUserId, "8", chapter: 5, volume: 0, ScrobbleStatus.Reading);

        // The read's include=media covers the manga's totals and caches the entry id, so the write
        // is a single PATCH: no /manga fetch and no separate entry lookup.
        Assert.Equal(1, afterRead - 1); // minus the one-off profile fetch
        Assert.Equal(afterRead + 1, handler.Requests.Count);
        Assert.Equal(HttpMethod.Patch, handler.Requests[^1].Method);
        Assert.EndsWith("/library-entries/14990645", handler.Requests[^1].RequestUri!.ToString());
    }

    [Fact]
    public async Task AStaleCachedEntryIdFallsBackToACreate_WithoutReportingTheSeriesMissing()
    {
        var handler = HandlerWithProfile()
            .On("/library-entries/14990645", () => new HttpResponseMessage(HttpStatusCode.NotFound)
            {
                Content = new StringContent("{}", System.Text.Encoding.UTF8, "application/vnd.api+json"),
            })
            .On("/library-entries?", EntryJson())
            .On("/library-entries", """{"data":{"id":"999","type":"libraryEntries"}}""");

        var kitsu = Build(handler);
        await kitsu.GetEntryAsync(MakiUserId, "8");
        await kitsu.UpdateAsync(MakiUserId, "8", chapter: 5, volume: 0, ScrobbleStatus.Reading);

        Assert.Contains(handler.Requests, r => r.Method == HttpMethod.Patch);
        Assert.Contains(handler.Requests, r => r.Method == HttpMethod.Post);
    }

    [Fact]
    public async Task ACloudflareChallengeBacksTheWholeTrackerOff_RatherThanRetryingIntoIt()
    {
        var handler = HandlerWithProfile()
            .On("/library-entries", () => new HttpResponseMessage(HttpStatusCode.Forbidden)
            {
                Content = new StringContent(
                    "<html><head><title>Just a moment...</title></head><body></body></html>",
                    System.Text.Encoding.UTF8, "text/html"),
            });

        var kitsu = Build(handler);
        await Assert.ThrowsAsync<TrackerException>(() => kitsu.GetEntryAsync(MakiUserId, "8"));

        var afterFailure = handler.Requests.Count;

        // The old code slept 30s and retried the same blocked IP. Now the tracker drops out of the
        // active set entirely and costs the rest of the tick nothing.
        Assert.False(await kitsu.AuthenticatedAsync(MakiUserId));
        await Assert.ThrowsAsync<TrackerException>(() => kitsu.GetEntryAsync(MakiUserId, "8"));
        Assert.Equal(afterFailure, handler.Requests.Count);
    }

    [Fact]
    public async Task ChallengeIsRecognisedByTheCfMitigatedHeaderAlone()
    {
        var handler = HandlerWithProfile()
            .On("/library-entries", () =>
            {
                var r = new HttpResponseMessage(HttpStatusCode.Forbidden)
                {
                    Content = new StringContent("<html>irgendwas</html>", System.Text.Encoding.UTF8, "text/html"),
                };
                r.Headers.Add("cf-mitigated", "challenge");
                return r;
            });

        var kitsu = Build(handler);
        await Assert.ThrowsAsync<TrackerException>(() => kitsu.GetEntryAsync(MakiUserId, "8"));
        Assert.False(await kitsu.AuthenticatedAsync(MakiUserId));
    }

    [Fact]
    public async Task ALongRetryAfterBacksOffInsteadOfBlockingTheTick()
    {
        var handler = HandlerWithProfile()
            .On("/library-entries", () =>
            {
                var r = new HttpResponseMessage(HttpStatusCode.TooManyRequests)
                {
                    Content = new StringContent("{}", System.Text.Encoding.UTF8, "application/vnd.api+json"),
                };
                r.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromMinutes(10));
                return r;
            });

        var kitsu = Build(handler);
        var started = DateTime.UtcNow;
        await Assert.ThrowsAsync<TrackerException>(() => kitsu.GetEntryAsync(MakiUserId, "8"));

        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(5));
        Assert.False(await kitsu.AuthenticatedAsync(MakiUserId));
    }
}
