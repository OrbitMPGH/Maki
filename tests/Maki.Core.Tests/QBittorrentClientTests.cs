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
