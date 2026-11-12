using System.Net;
using Maki.Api.Services;
using Maki.Core.Configuration;
using Maki.Core.Kavita;
using Microsoft.Extensions.Logging.Abstractions;

namespace Maki.Api.Tests;

public sealed class KavitaScanRetryTests : IDisposable
{
    private readonly TestDb _db = new();

    public void Dispose() => _db.Dispose();

    private static readonly DateTime T0 = new(2026, 11, 1, 12, 0, 0, DateTimeKind.Utc);

    private sealed class ScanFactory(int failures) : IHttpClientFactory
    {
        public int ScanCalls;
        public int Failures { get; } = failures;

        public HttpClient CreateClient(string name) => new(new Handler(this));

        private sealed class Handler(ScanFactory owner) : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            {
                var status = HttpStatusCode.OK;
                if (request.RequestUri!.AbsolutePath.EndsWith("scan-folder", StringComparison.Ordinal))
                {
                    owner.ScanCalls++;
                    status = owner.ScanCalls <= owner.Failures ? HttpStatusCode.InternalServerError : HttpStatusCode.OK;
                }

                return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent("{}") });
            }
        }
    }

    private KavitaScanService Service(ScanFactory http)
    {
        var settings = new FakeAppSettings()
            .Set(SettingKeys.KavitaUrl, "http://kavita.test")
            .Set(SettingKeys.KavitaApiKey, "key");
        return new KavitaScanService(new KavitaClient(http), settings, _db.ScopeFactory(),
            NullLogger<KavitaScanService>.Instance);
    }

    [Fact]
    public async Task A_failed_scan_is_retried_with_backoff_then_dropped()
    {
        var http = new ScanFactory(failures: int.MaxValue);
        var service = Service(http);
        service.QueueScan("/library/Series", 1);

        await service.FlushAsync(default, T0);
        Assert.Equal(1, http.ScanCalls);
        Assert.Single(service.PendingScans);

        await service.FlushAsync(default, T0.AddSeconds(30));
        Assert.Equal(1, http.ScanCalls);

        await service.FlushAsync(default, T0.AddMinutes(1));
        await service.FlushAsync(default, T0.AddMinutes(3));
        Assert.Equal(3, http.ScanCalls);

        await service.FlushAsync(default, T0.AddMinutes(7));
        Assert.Equal(KavitaScanService.MaxScanAttempts, http.ScanCalls);
        Assert.Empty(service.PendingScans);
    }

    [Fact]
    public async Task A_scan_that_succeeds_on_retry_leaves_the_queue()
    {
        var http = new ScanFactory(failures: 1);
        var service = Service(http);
        service.QueueScan("/library/Series", 1);

        await service.FlushAsync(default, T0);
        await service.FlushAsync(default, T0.AddMinutes(1));

        Assert.Equal(2, http.ScanCalls);
        Assert.Empty(service.PendingScans);
    }
}
