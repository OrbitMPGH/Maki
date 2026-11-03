using System.Net;
using System.Net.Sockets;
using System.Text;
using Maki.Core.Http;
using Maki.Core.Indexers;

namespace Maki.Core.Tests;

/// <summary>
/// Runs the download link fetch over real sockets and the handlers Program.cs registers, because a fake
/// handler that returns a 302 never reaches the redirect handling inside SocketsHttpHandler (issue 139).
/// </summary>
public sealed class ReleaseIndexerFetchTests : IAsyncDisposable
{
    private const string Magnet = "magnet:?xt=urn:btih:0123456789abcdef0123456789abcdef01234567&dn=Berserk%20v01&tr=udp%3A%2F%2Ftracker.test%3A1337";

    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop = new();
    private readonly List<string> _paths = [];
    private Func<string, string> _answer = _ => Response("404 Not Found", "");

    public ReleaseIndexerFetchTests()
    {
        _listener.Start();
        _ = ServeAsync();
    }

    private string Origin => $"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}";

    private sealed class Factory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(
            new TransientRetryHandler { InnerHandler = ReleaseIndexerBase.CreateDownloadLinkHandler() });
    }

    private static string Response(string status, string body, string? location = null) =>
        $"HTTP/1.1 {status}\r\nContent-Length: {Encoding.UTF8.GetByteCount(body)}\r\nConnection: close\r\n" +
        (location is null ? "" : $"Location: {location}\r\n") + "\r\n" + body;

    private async Task ServeAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            TcpClient socket;
            try
            {
                socket = await _listener.AcceptTcpClientAsync(_stop.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            using (socket)
            {
                var stream = socket.GetStream();
                var reader = new StreamReader(stream, Encoding.ASCII);
                var requestLine = await reader.ReadLineAsync() ?? "";
                while (!string.IsNullOrEmpty(await reader.ReadLineAsync()))
                {
                }

                var path = requestLine.Split(' ')[1];
                lock (_paths)
                {
                    _paths.Add(path);
                }

                await stream.WriteAsync(Encoding.UTF8.GetBytes(_answer(path)));
            }
        }
    }

    private static ProwlarrClient Indexer() => new(new Factory());

    [Fact]
    public async Task A_proxy_link_redirecting_to_a_magnet_returns_the_magnet()
    {
        _answer = _ => Response("301 Moved Permanently", "", Magnet);

        var link = await Indexer().FetchTorrentAsync($"{Origin}/1/download?apikey=secret&link=abc");

        Assert.Equal(Magnet, link.Magnet);
        Assert.Null(link.File);
    }

    [Fact]
    public async Task A_redirect_to_another_http_link_is_followed_to_the_torrent_file()
    {
        _answer = path => path.StartsWith("/1/download")
            ? Response("302 Found", "", "/files/x.torrent")
            : Response("200 OK", "d4:infod4:name1:xee");

        var link = await Indexer().FetchTorrentAsync($"{Origin}/1/download?apikey=secret&link=abc");

        Assert.Equal("d4:infod4:name1:xee", Encoding.ASCII.GetString(link.File!));
        Assert.Equal(["/1/download?apikey=secret&link=abc", "/files/x.torrent"], _paths);
    }

    [Fact]
    public async Task A_redirect_chain_that_lands_on_a_magnet_returns_it()
    {
        _answer = path => path.StartsWith("/1/download")
            ? Response("302 Found", "", $"{Origin}/hop")
            : Response("302 Found", "", Magnet);

        var link = await Indexer().FetchTorrentAsync($"{Origin}/1/download?apikey=secret");

        Assert.Equal(Magnet, link.Magnet);
    }

    [Fact]
    public async Task A_redirect_loop_gives_up_as_invalid_data()
    {
        _answer = _ => Response("302 Found", "", "/again");

        await Assert.ThrowsAsync<InvalidDataException>(() => Indexer().FetchTorrentAsync($"{Origin}/again"));
    }

    [Fact]
    public async Task A_redirect_to_a_scheme_other_than_http_or_magnet_is_invalid_data()
    {
        _answer = _ => Response("302 Found", "", "ftp://files.test/x.torrent");

        await Assert.ThrowsAsync<InvalidDataException>(() => Indexer().FetchTorrentAsync($"{Origin}/1/download"));
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        _listener.Stop();
        _stop.Dispose();
    }
}
