using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Maki.Core.Tests;

/// <summary>
/// A loopback HTTP proxy that records what it is asked for and answers every request itself with
/// <see cref="Status"/>, so tests never reach the target and never need DNS.
/// </summary>
internal sealed class FakeConnectProxy : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _loop;

    public FakeConnectProxy(int status = 403)
    {
        Status = status;
        _listener.Start();
        _loop = Task.Run(AcceptLoopAsync);
    }

    public int Status { get; set; }

    public Uri Url => new($"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}");

    /// <summary>Request lines as received, e.g. "CONNECT 8.8.8.8:443 HTTP/1.1".</summary>
    public List<string> Requests { get; } = [];

    private async Task AcceptLoopAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(_stop.Token);
            }
            catch (Exception)
            {
                return;
            }

            _ = Task.Run(() => ServeAsync(client));
        }
    }

    private async Task ServeAsync(TcpClient client)
    {
        using (client)
        {
            try
            {
                var stream = client.GetStream();
                var head = await ReadHeadAsync(stream);
                if (head is null)
                {
                    return;
                }

                lock (Requests)
                {
                    Requests.Add(head.Split("\r\n")[0]);
                }

                var reason = Status == 407 ? "Proxy Authentication Required" : "Status";
                var extra = Status == 407 ? "Proxy-Authenticate: Basic realm=\"test\"\r\n" : "";
                var response = $"HTTP/1.1 {Status} {reason}\r\n{extra}Content-Length: 0\r\nConnection: close\r\n\r\n";
                await stream.WriteAsync(Encoding.ASCII.GetBytes(response));
            }
            catch (Exception)
            {
            }
        }
    }

    private static async Task<string?> ReadHeadAsync(NetworkStream stream)
    {
        var buffer = new byte[8192];
        var total = 0;
        while (total < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(total));
            if (read == 0)
            {
                return null;
            }

            total += read;
            var text = Encoding.ASCII.GetString(buffer, 0, total);
            if (text.Contains("\r\n\r\n"))
            {
                return text;
            }
        }

        return null;
    }

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        _listener.Stop();
        try
        {
            await _loop;
        }
        catch (Exception)
        {
        }

        _stop.Dispose();
    }
}
