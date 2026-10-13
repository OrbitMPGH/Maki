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
    public async Task A_repeating_entry_is_read_as_reading_and_flagged()
    {
        var handler = new ScriptedHandler(
            """{"data":{"Media":{"mediaListEntry":{"status":"REPEATING","progress":4}}}}""");

        var entry = await Build(handler).GetEntryAsync(1, "42");

        Assert.Equal(ScrobbleStatus.Reading, entry.Status);
        Assert.True(entry.Repeating);
    }

    [Fact]
    public async Task A_current_entry_is_not_flagged_as_repeating()
    {
        var handler = new ScriptedHandler(
            """{"data":{"Media":{"mediaListEntry":{"status":"CURRENT","progress":4}}}}""");

        Assert.False((await Build(handler).GetEntryAsync(1, "42")).Repeating);
    }

    [Fact]
    public async Task Keeping_the_status_writes_progress_only()
    {
        var handler = new ScriptedHandler("{}");

        await Build(handler).UpdateAsync(1, "42", 12, 0, ScrobbleStatus.Reading, keepStatus: true);

        var mutation = Assert.Single(handler.Bodies);
        Assert.DoesNotContain("CURRENT", mutation, StringComparison.Ordinal);
        Assert.DoesNotContain("$status", mutation, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_normal_push_still_writes_the_status()
    {
        var handler = new ScriptedHandler("{}");

        await Build(handler).UpdateAsync(1, "42", 12, 0, ScrobbleStatus.Reading);

        Assert.Contains("CURRENT", Assert.Single(handler.Bodies), StringComparison.Ordinal);
    }
}
