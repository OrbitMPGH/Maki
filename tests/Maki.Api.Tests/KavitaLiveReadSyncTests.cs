using System.Net;
using System.Text;
using Maki.Api.Services;
using Maki.Core.Kavita;
using Microsoft.Extensions.Logging.Abstractions;

namespace Maki.Api.Tests;

/// <summary>
/// A page-turn event is the only signal Kavita sends for a chapter it just finished, so a failed or
/// ill-timed flush must not lose it, and an event for a series Maki has no copy of must not cost a
/// Kavita round trip every time.
/// </summary>
public sealed class KavitaLiveReadSyncTests : IDisposable
{
    private const int UserId = 1;
    private readonly TestDb _db = new();

    public void Dispose() => _db.Dispose();

    private sealed class KavitaHandler(HttpStatusCode series) : HttpMessageHandler
    {
        public int SeriesCalls;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.Contains("Plugin/authenticate"))
            {
                return Json("""{"token":"jwt"}""");
            }

            if (path.Contains("api/Series/") && !path.Contains("volumes"))
            {
                Interlocked.Increment(ref SeriesCalls);
                return series == HttpStatusCode.OK
                    ? Json("""{"id":10,"name":"Nobody","localizedName":null,"libraryId":1,"pages":10,"pagesRead":0}""")
                    : Task.FromResult(new HttpResponseMessage(series));
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }

        private static Task<HttpResponseMessage> Json(string json) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            });
    }

    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private (KavitaLiveReadSync Sync, KavitaReadImportService Import) Build(KavitaHandler handler)
    {
        var scopes = _db.ScopeFactory();
        var settings = new SettingsService(scopes);
        var kavita = new KavitaClient(new Factory(handler));
        var import = new KavitaReadImportService(
            scopes, settings, kavita, new ExternalReadSyncService(scopes), new VolumeBoundaryService(scopes),
            new KavitaUserResolver(scopes, settings), NullLogger<KavitaReadImportService>.Instance);
        var sync = new KavitaLiveReadSync(
            settings, null!, new KavitaUserResolver(scopes, settings), kavita, import, null!,
            NullLogger<KavitaLiveReadSync>.Instance)
        {
            Active = new KavitaLiveReadSync.Target("http://kavita.test", "key", UserId),
            Status = KavitaLiveStatus.Connected,
        };
        return (sync, import);
    }

    [Fact]
    public async Task A_failed_mark_is_retried_after_a_backoff_and_dropped_after_three_attempts()
    {
        _db.SeedUser();
        var handler = new KavitaHandler(HttpStatusCode.InternalServerError);
        var (sync, _) = Build(handler);
        var now = DateTime.UtcNow;
        sync.Enqueue(10, now.AddMinutes(-1));

        await sync.FlushAsync(CancellationToken.None, now);
        Assert.Equal(1, sync.PendingCount);
        Assert.Equal(1, handler.SeriesCalls);

        await sync.FlushAsync(CancellationToken.None, now);
        Assert.Equal(1, handler.SeriesCalls);

        await sync.FlushAsync(CancellationToken.None, now.AddSeconds(11));
        Assert.Equal(1, sync.PendingCount);
        Assert.Equal(2, handler.SeriesCalls);

        await sync.FlushAsync(CancellationToken.None, now.AddSeconds(22));
        Assert.Equal(0, sync.PendingCount);
        Assert.Equal(3, handler.SeriesCalls);
    }

    [Fact]
    public async Task Events_survive_a_reconnect_window_and_flush_once_connected_again()
    {
        _db.SeedUser();
        var handler = new KavitaHandler(HttpStatusCode.NotFound);
        var (sync, _) = Build(handler);
        var now = DateTime.UtcNow;
        sync.Status = KavitaLiveStatus.Connecting;
        sync.Enqueue(10, now.AddMinutes(-1));

        await sync.FlushAsync(CancellationToken.None, now);
        Assert.Equal(1, sync.PendingCount);
        Assert.Equal(0, handler.SeriesCalls);

        sync.Status = KavitaLiveStatus.Connected;
        await sync.FlushAsync(CancellationToken.None, now);
        Assert.Equal(0, sync.PendingCount);
        Assert.Equal(1, handler.SeriesCalls);
    }

    [Fact]
    public async Task A_kavita_series_with_no_local_match_is_not_looked_up_again_for_a_while()
    {
        _db.SeedUser();
        _db.SeedSeries("Something Else");
        var handler = new KavitaHandler(HttpStatusCode.OK);
        var (_, import) = Build(handler);

        Assert.Null(await import.MarkSeriesAsync(UserId, "http://kavita.test", "key", 10, CancellationToken.None));
        Assert.Null(await import.MarkSeriesAsync(UserId, "http://kavita.test", "key", 10, CancellationToken.None));

        Assert.Equal(1, handler.SeriesCalls);
        Assert.True(import.IsKnownUnmatched(10));
    }
}
