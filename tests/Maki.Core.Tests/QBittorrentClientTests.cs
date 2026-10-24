using System.Net;
using Maki.Core.Download;

namespace Maki.Core.Tests;

public class QBittorrentClientTests
{
    private sealed class LoginHandler(HttpStatusCode status, string? body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var response = new HttpResponseMessage(status);
            if (body is not null) response.Content = new StringContent(body);
            return Task.FromResult(response);
        }
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public List<string> Paths { get; } = [];
        public int Forbidden { get; set; }
        public string Torrents { get; set; } = "[]";

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Paths.Add(request.RequestUri!.AbsolutePath);
            if (request.RequestUri.AbsolutePath.EndsWith("/auth/login"))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent));
            }

            var status = Forbidden-- > 0 ? HttpStatusCode.Forbidden : HttpStatusCode.OK;
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(Torrents) });
        }
    }

    private static TorrentClientSettings Settings(string username = "admin") =>
        new(TorrentClientKind.QBittorrent, "http://qbt.test:8080", username, "secret", "maki");

    [Fact]
    public async Task A_changed_username_on_the_same_url_logs_in_again()
    {
        var handler = new RecordingHandler();
        var client = new QBittorrentClient(handler);

        await client.ListAsync(Settings());
        await client.ListAsync(Settings());
        await client.ListAsync(Settings("other"));

        Assert.Equal(2, handler.Paths.Count(p => p.EndsWith("/auth/login")));
    }

    [Fact]
    public async Task An_expired_session_logs_in_once_and_repeats_the_request()
    {
        var handler = new RecordingHandler { Forbidden = 1 };
        var client = new QBittorrentClient(handler);

        await client.ListAsync(Settings());

        Assert.Equal(
            ["/api/v2/auth/login", "/api/v2/torrents/info", "/api/v2/auth/login", "/api/v2/torrents/info"],
            handler.Paths);
    }

    [Fact]
    public async Task Listed_torrents_are_normalised_and_only_a_healthy_finished_one_is_complete()
    {
        var handler = new RecordingHandler
        {
            Torrents = """
                [
                  {"hash":"ABCDEF","name":"done","state":"stalledUP","progress":1.0,"content_path":"/dl/done","save_path":"/dl","added_on":1700000000},
                  {"hash":"b","name":"missing","state":"missingFiles","progress":1.0,"content_path":"/dl/missing","save_path":"/dl","added_on":1700000000},
                  {"hash":"c","name":"checking","state":"checkingUP","progress":1.0,"content_path":"/dl/checking","save_path":"/dl","added_on":1700000000},
                  {"hash":"d","name":"half","state":"downloading","progress":0.5,"content_path":"/dl/half","save_path":"/dl","added_on":1700000000}
                ]
                """
        };

        var torrents = await new QBittorrentClient(handler).ListAsync(Settings());

        Assert.Equal(["abcdef", "b", "c", "d"], torrents.Select(t => t.Hash));
        Assert.Equal([true, false, false, false], torrents.Select(t => t.IsComplete));
        Assert.Equal([false, true, false, false], torrents.Select(t => t.IsErrored));
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1700000000), torrents[0].AddedAt);
        Assert.Equal("/dl/done", torrents[0].ContentPath);
    }

    [Fact]
    public async Task A_url_add_reports_no_hash()
    {
        Assert.Null(await new QBittorrentClient(new RecordingHandler()).AddAsync(Settings(), "http://indexer.test/1.torrent"));
    }

    /// <summary>Refuses every add, as qBittorrent does a torrent it already has: 409 on 5.x, "Fails." on 4.x.</summary>
    private sealed class DuplicateHandler(string existing, bool legacy = false) : HttpMessageHandler
    {
        public string? LookedUp { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/torrents/info"))
            {
                LookedUp = request.RequestUri.Query;
            }

            var response = path.EndsWith("/auth/login") ? new HttpResponseMessage(HttpStatusCode.NoContent)
                : path.EndsWith("/torrents/add") && legacy ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("Fails.") }
                : path.EndsWith("/torrents/add") ? new HttpResponseMessage(HttpStatusCode.Conflict)
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(existing) };
            return Task.FromResult(response);
        }
    }

    private static string Existing(string category) =>
        $$"""[{"hash":"abc","name":"x","state":"stalledUP","progress":1.0,"content_path":"/dl/x","added_on":1700000000,"category":"{{category}}"}]""";

    [Fact]
    public async Task A_duplicate_upload_in_makis_category_counts_as_added()
    {
        await new QBittorrentClient(new DuplicateHandler(Existing("maki"))).AddFileAsync(Settings(), [1, 2, 3], "abc");
    }

    [Fact]
    public async Task A_duplicate_upload_in_another_category_fails_the_grab()
    {
        var ex = await Assert.ThrowsAsync<TorrentClientException>(() =>
            new QBittorrentClient(new DuplicateHandler(Existing("manga"))).AddFileAsync(Settings(), [1, 2, 3], "abc"));

        Assert.Equal(TorrentClientFailure.HeldOutsideLabel, ex.Failure);
    }

    [Fact]
    public async Task A_refused_upload_the_client_does_not_have_is_rejected()
    {
        var ex = await Assert.ThrowsAsync<TorrentClientException>(() =>
            new QBittorrentClient(new DuplicateHandler("[]")).AddFileAsync(Settings(), [1, 2, 3], "abc"));

        Assert.Equal(TorrentClientFailure.Rejected, ex.Failure);
    }

    private const string Base32Magnet = "magnet:?xt=urn:btih:AERUKZ4JVPG66AJDIVTYTK6N54ASGRLH&dn=x";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_duplicate_magnet_in_makis_category_counts_as_added(bool legacy)
    {
        var handler = new DuplicateHandler(Existing("maki"), legacy);

        Assert.Null(await new QBittorrentClient(handler).AddAsync(Settings(), Base32Magnet));
        Assert.Equal("?hashes=0123456789abcdef0123456789abcdef01234567", handler.LookedUp);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_duplicate_magnet_in_another_category_fails_the_grab(bool legacy)
    {
        var ex = await Assert.ThrowsAsync<TorrentClientException>(() =>
            new QBittorrentClient(new DuplicateHandler(Existing("manga"), legacy)).AddAsync(Settings(), Base32Magnet));

        Assert.Equal(TorrentClientFailure.HeldOutsideLabel, ex.Failure);
    }

    [Fact]
    public async Task A_refused_magnet_the_client_does_not_have_is_rejected()
    {
        var ex = await Assert.ThrowsAsync<TorrentClientException>(() =>
            new QBittorrentClient(new DuplicateHandler("[]")).AddAsync(Settings(), Base32Magnet));

        Assert.Equal(TorrentClientFailure.Rejected, ex.Failure);
    }

    [Fact]
    public async Task A_refused_url_has_no_hash_to_look_up_and_is_rejected()
    {
        var handler = new DuplicateHandler(Existing("maki"), legacy: true);
        var ex = await Assert.ThrowsAsync<TorrentClientException>(() =>
            new QBittorrentClient(handler).AddAsync(Settings(), "http://indexer.test/1.torrent"));

        Assert.Equal(TorrentClientFailure.Rejected, ex.Failure);
        Assert.Null(handler.LookedUp);
    }

    [Fact]
    public async Task An_unreachable_client_fails_as_unreachable()
    {
        var ex = await Assert.ThrowsAsync<TorrentClientException>(() =>
            new QBittorrentClient(new LoginHandler(HttpStatusCode.InternalServerError, null)).PingAsync(Settings()));

        Assert.Equal(TorrentClientFailure.Unreachable, ex.Failure);
    }

    private static Task PingAsync(HttpStatusCode status, string? body) =>
        new QBittorrentClient(new LoginHandler(status, body)).PingAsync(Settings());

    [Fact]
    public async Task A_204_login_succeeds()
    {
        await PingAsync(HttpStatusCode.NoContent, null);
    }

    [Fact]
    public async Task A_qbittorrent_4_ok_body_succeeds()
    {
        await PingAsync(HttpStatusCode.OK, "Ok.");
    }

    [Theory]
    [InlineData(HttpStatusCode.OK, "Fails.")]
    [InlineData(HttpStatusCode.OK, "")]
    [InlineData(HttpStatusCode.Unauthorized, null)]
    [InlineData(HttpStatusCode.Forbidden, "Your IP address has been banned")]
    public async Task A_rejected_login_fails(HttpStatusCode status, string? body)
    {
        var ex = await Assert.ThrowsAsync<TorrentClientException>(() => PingAsync(status, body));

        Assert.Equal(TorrentClientFailure.LoginFailed, ex.Failure);
    }
}
