using System.Net;
using System.Text;
using System.Text.Json;
using Maki.Core.Kavita;

namespace Maki.Core.Tests;

public class KavitaClientTests
{
    private sealed class FakeKavita(Func<int, string> page) : HttpMessageHandler
    {
        public int Pages { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.AbsolutePath;
            var json = path.EndsWith("/Plugin/authenticate")
                ? "{\"token\":\"jwt\"}"
                : page(++Pages);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            });
        }
    }

    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private static string Series(int from, int count) =>
        JsonSerializer.Serialize(Enumerable.Range(from, count).Select(i => new { id = i, name = "S" + i }));

    [Fact]
    public async Task Paging_ends_when_a_page_repeats_the_previous_one()
    {
        var kavita = new FakeKavita(_ => Series(1, 200));
        var client = new KavitaClient(new Factory(kavita));

        var all = await client.GetAllSeriesAsync("http://kavita.test", "key");

        Assert.Equal(200, all.Count);
        Assert.Equal(2, kavita.Pages);
    }

    [Fact]
    public async Task Paging_reads_every_page_until_a_short_one()
    {
        var kavita = new FakeKavita(page => page switch
        {
            1 => Series(1, 200),
            2 => Series(201, 200),
            _ => Series(401, 5)
        });
        var client = new KavitaClient(new Factory(kavita));

        var all = await client.GetAllSeriesAsync("http://kavita.test", "key");

        Assert.Equal(405, all.Count);
    }
}
