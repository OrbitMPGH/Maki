using System.Net;
using System.Net.Sockets;
using System.Text;
using Maki.Api.Services;
using Maki.Core.Http;
using Maki.Core.Http.Proxy;
using Microsoft.Extensions.Logging.Abstractions;

namespace Maki.Api.Tests;

public class SourceProxyTesterTests
{
    private static SourceProxyTester Tester() =>
        new(new FlareSolverrClient(null!), new FakeAppSettings(), NullLogger<SourceProxyTester>.Instance);

    private static readonly FakeSource Source = new() { Name = "example" };

    /// <summary>Accepts connections and answers each with <paramref name="reply"/>, or hangs up when it is null.</summary>
    private sealed class Listener : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();

        public Listener(string? reply)
        {
            _listener.Start();
            _ = Task.Run(async () =>
            {
                while (!_stop.IsCancellationRequested)
                {
                    using var client = await _listener.AcceptTcpClientAsync(_stop.Token);
                    var stream = client.GetStream();
                    _ = await stream.ReadAsync(new byte[4096], _stop.Token);
                    if (reply is not null)
                    {
                        await stream.WriteAsync(Encoding.ASCII.GetBytes(reply), _stop.Token);
                    }
                }
            });
        }

        public Uri Url => new($"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}");

        public ValueTask DisposeAsync()
        {
            _stop.Cancel();
            _listener.Stop();
            return ValueTask.CompletedTask;
        }
    }

    [Fact]
    public async Task A_closed_port_is_unreachable()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();

        var result = await Tester().TestAsync(new Uri($"http://127.0.0.1:{port}"), Source, default);

        Assert.Equal(SourceProxyFailure.Unreachable, result.Failure);
    }

    // Docker accepts the connection for a stopped container's published port and closes it at once.
    [Fact]
    public async Task A_port_that_hangs_up_is_unreachable_not_an_answering_proxy()
    {
        await using var port = new Listener(reply: null);

        var result = await Tester().TestAsync(port.Url, Source, default);

        Assert.Equal(SourceProxyFailure.Unreachable, result.Failure);
    }

    [Fact]
    public async Task A_proxy_that_answers_with_an_error_could_not_reach_the_site()
    {
        await using var proxy = new Listener("HTTP/1.1 502 Bad Gateway\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");

        var result = await Tester().TestAsync(proxy.Url, Source, default);

        Assert.Equal(SourceProxyFailure.Rejected, result.Failure);
        Assert.False(result.RefusedHere);
    }
}
