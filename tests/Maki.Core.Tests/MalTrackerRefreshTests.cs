using System.Net;
using Maki.Core.Configuration;
using Maki.Core.Entities;
using Maki.Core.Scrobbling;
using Microsoft.Extensions.Logging.Abstractions;

namespace Maki.Core.Tests;

/// <summary>
/// MAL refresh tokens are single use. Two calls that both find the access token near expiry must not
/// both spend the same refresh token, because the loser's rejection used to delete the token the
/// winner had just stored.
/// </summary>
public class MalTrackerRefreshTests
{
    private sealed class Handler : HttpMessageHandler
    {
        private readonly HashSet<string> _spent = [];
        private int _issued;

        public int TokenCalls;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/token", StringComparison.Ordinal))
            {
                Interlocked.Increment(ref TokenCalls);
                var form = await request.Content!.ReadAsStringAsync(ct);
                // Long enough that an unserialised second refresh would overlap this one.
                await Task.Delay(100, ct);
                lock (_spent)
                {
                    if (!_spent.Add(form))
                    {
                        return new HttpResponseMessage(HttpStatusCode.BadRequest)
                        {
                            Content = new StringContent("""{"error":"invalid_grant"}"""),
                        };
                    }

                    _issued++;
                }

                return Json($$"""{"access_token":"fresh{{_issued}}","refresh_token":"refresh{{_issued}}","expires_in":2678400}""");
            }

            return Json("""{"id":1,"title":"Test"}""");
        }

        private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK)
        {
            Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json"),
        };
    }

    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class Settings : IAppSettings
    {
        public Task<string?> GetAsync(string key, CancellationToken ct = default) => Task.FromResult<string?>("x");
        public Task SetAsync(string key, string? value, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class TokenStore : IScrobbleTokenStore
    {
        private ScrobbleToken? _token;

        public TokenStore(ScrobbleToken token) => _token = token;

        public ScrobbleToken? Current => _token;

        public Task<ScrobbleToken?> GetAsync(int userId, string service, CancellationToken ct = default) =>
            Task.FromResult(_token is null ? null : Copy(_token));

        public Task SaveAsync(ScrobbleToken token, CancellationToken ct = default)
        {
            _token = Copy(token);
            return Task.CompletedTask;
        }

        public Task DeleteAsync(int userId, string service, CancellationToken ct = default)
        {
            _token = null;
            return Task.CompletedTask;
        }

        private static ScrobbleToken Copy(ScrobbleToken t) => new()
        {
            UserId = t.UserId,
            Service = t.Service,
            AccessToken = t.AccessToken,
            RefreshToken = t.RefreshToken,
            ExpiresAt = t.ExpiresAt,
            Username = t.Username,
        };
    }

    [Fact]
    public async Task Two_concurrent_refreshes_spend_the_refresh_token_once_and_keep_the_connection()
    {
        // A user id no other test uses, since the refresh lock is process wide.
        const int userId = 91_001;
        var handler = new Handler();
        var store = new TokenStore(new ScrobbleToken
        {
            UserId = userId,
            Service = "mal",
            AccessToken = "stale",
            RefreshToken = "refresh0",
            ExpiresAt = DateTime.UtcNow.AddMinutes(5),
        });
        var tracker = new MalTracker(new Factory(handler), new Settings(), store,
            new ScrobbleTrackerOptions(
                "https://anilist.test/graphql", "https://anilist.test/oauth",
                "https://mal.test", "https://mal.test/oauth",
                "https://mangabaka.test",
                "https://kitsu.test/api/edge", "https://kitsu.test/api/oauth"),
            NullLogger<MalTracker>.Instance);

        await Task.WhenAll(
            tracker.GetEntryAsync(userId, "1"),
            tracker.GetEntryAsync(userId, "1"));

        Assert.Equal(1, handler.TokenCalls);
        Assert.NotNull(store.Current);
        Assert.Equal("fresh1", store.Current!.AccessToken);
        Assert.Equal("refresh1", store.Current.RefreshToken);
    }
}
