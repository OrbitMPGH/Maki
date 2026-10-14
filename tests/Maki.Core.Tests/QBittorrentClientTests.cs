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
