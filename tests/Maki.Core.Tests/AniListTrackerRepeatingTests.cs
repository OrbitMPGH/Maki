using System.Net;
using Maki.Core.Configuration;
using Maki.Core.Entities;
using Maki.Core.Scrobbling;
using Microsoft.Extensions.Logging.Abstractions;

namespace Maki.Core.Tests;

/// <summary>
/// A re-read is REPEATING on AniList. Pushing progress on it must not flip the entry to CURRENT.
/// </summary>
public class AniListTrackerRepeatingTests
{
    private sealed class ScriptedHandler(string entryStatusJson) : HttpMessageHandler
    {
        public List<string> Bodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = await request.Content!.ReadAsStringAsync(ct);
            Bodies.Add(body);
            var json = body.Contains("mutation", StringComparison.Ordinal)
                ? """{"data":{"SaveMediaListEntry":{"id":1}}}"""
                : entryStatusJson;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json"),
            };
        }
    }

    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class Settings : IAppSettings
    {
        public Task<string?> GetAsync(string key, CancellationToken ct = default) => Task.FromResult<string?>(null);
        public Task SetAsync(string key, string? value, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class TokenStore : IScrobbleTokenStore
    {
        public Task<ScrobbleToken?> GetAsync(int userId, string service, CancellationToken ct = default) =>
            Task.FromResult<ScrobbleToken?>(new ScrobbleToken
            {
                UserId = userId,
                Service = service,
                AccessToken = "token",
                ExpiresAt = DateTime.UtcNow.AddDays(1),
            });
        public Task SaveAsync(ScrobbleToken token, CancellationToken ct = default) => Task.CompletedTask;
        public Task DeleteAsync(int userId, string service, CancellationToken ct = default) => Task.CompletedTask;
    }

    private static AniListTracker Build(HttpMessageHandler handler) => new(
        new Factory(handler),
        new Settings(),
        new TokenStore(),
        new ScrobbleTrackerOptions(
            "https://anilist.test/graphql", "https://anilist.test/oauth",
            "https://mal.test", "https://mal.test/oauth",
            "https://mangabaka.test",
            "https://kitsu.test/api/edge", "https://kitsu.test/api/oauth"),
        NullLogger<AniListTracker>.Instance);

    [Fact]
    public async Task A_push_on_a_repeating_entry_leaves_the_status_alone()
    {
        var handler = new ScriptedHandler("""{"data":{"Media":{"mediaListEntry":{"status":"REPEATING"}}}}""");

        await Build(handler).UpdateAsync(1, "42", 12, 0, ScrobbleStatus.Reading);

        var mutation = handler.Bodies.Single(b => b.Contains("mutation", StringComparison.Ordinal));
        Assert.DoesNotContain("CURRENT", mutation, StringComparison.Ordinal);
        Assert.DoesNotContain("$status", mutation, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_push_on_a_current_entry_still_writes_the_status()
    {
        var handler = new ScriptedHandler("""{"data":{"Media":{"mediaListEntry":{"status":"CURRENT"}}}}""");

        await Build(handler).UpdateAsync(1, "42", 12, 0, ScrobbleStatus.Reading);

        var mutation = handler.Bodies.Single(b => b.Contains("mutation", StringComparison.Ordinal));
        Assert.Contains("CURRENT", mutation, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Completing_a_repeating_entry_still_writes_completed()
    {
        var handler = new ScriptedHandler("""{"data":{"Media":{"mediaListEntry":{"status":"REPEATING"}}}}""");

        await Build(handler).UpdateAsync(1, "42", 30, 0, ScrobbleStatus.Completed);

        var mutation = handler.Bodies.Single(b => b.Contains("mutation", StringComparison.Ordinal));
        Assert.Contains("COMPLETED", mutation, StringComparison.Ordinal);
    }
}
