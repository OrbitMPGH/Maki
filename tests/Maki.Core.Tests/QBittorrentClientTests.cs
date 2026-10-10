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

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Paths.Add(request.RequestUri!.AbsolutePath);
            if (request.RequestUri.AbsolutePath.EndsWith("/auth/login"))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent));
            }

            var status = Forbidden-- > 0 ? HttpStatusCode.Forbidden : HttpStatusCode.OK;
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent("[]") });
        }
    }

    [Fact]
    public async Task A_changed_username_on_the_same_url_logs_in_again()
    {
        var handler = new RecordingHandler();
        var client = new QBittorrentClient(handler);

        await client.ListAsync("http://qbt.test:8080", "admin", "secret", "maki");
        await client.ListAsync("http://qbt.test:8080", "admin", "secret", "maki");
        await client.ListAsync("http://qbt.test:8080", "other", "secret", "maki");

        Assert.Equal(2, handler.Paths.Count(p => p.EndsWith("/auth/login")));
    }

    [Fact]
    public async Task An_expired_session_logs_in_once_and_repeats_the_request()
    {
        var handler = new RecordingHandler { Forbidden = 1 };
        var client = new QBittorrentClient(handler);

        await client.ListAsync("http://qbt.test:8080", "admin", "secret", "maki");

        Assert.Equal(
            ["/api/v2/auth/login", "/api/v2/torrents/info", "/api/v2/auth/login", "/api/v2/torrents/info"],
            handler.Paths);
    }

    private static Task<bool> PingAsync(HttpStatusCode status, string? body) =>
        new QBittorrentClient(new LoginHandler(status, body)).PingAsync("http://qbt.test:8080", "admin", "secret");

    [Fact]
    public async Task A_204_login_succeeds()
    {
        Assert.True(await PingAsync(HttpStatusCode.NoContent, null));
    }

    [Fact]
    public async Task A_qbittorrent_4_ok_body_succeeds()
    {
        Assert.True(await PingAsync(HttpStatusCode.OK, "Ok."));
    }

    [Theory]
    [InlineData(HttpStatusCode.OK, "Fails.")]
    [InlineData(HttpStatusCode.OK, "")]
    [InlineData(HttpStatusCode.Unauthorized, null)]
    [InlineData(HttpStatusCode.Forbidden, "Your IP address has been banned")]
    public async Task A_rejected_login_fails(HttpStatusCode status, string? body)
    {
        Assert.False(await PingAsync(status, body));
    }
}
