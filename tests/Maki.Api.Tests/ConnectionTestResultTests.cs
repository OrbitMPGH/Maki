using System.Net;
using System.Reflection;
using Maki.Api.Auth;
using Maki.Api.Controllers;
using Maki.Api.Services;
using Maki.Core.Configuration;
using Maki.Core.Download;
using Maki.Core.Http;
using Maki.Sources.Suwayomi;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Maki.Api.Tests;

public sealed class ConnectionTestResultTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly SettingsService _settings;
    private HttpStatusCode _flareAnswers = HttpStatusCode.OK;

    public ConnectionTestResultTests() => _settings = new SettingsService(_db.ScopeFactory());

    public void Dispose() => _db.Dispose();

    private ConnectionTestStore Store() =>
        new(_db.NewContext(), _settings, new SuwayomiSettings(_settings), new TestLocalizer());

    private SettingsController Controller(ConnectionTestStore? store = null) => new(
        localizer: new TestLocalizer(), userLocales: new TestUserLocaleResolver(),
        settings: _settings, naming: null!, flareSolverr: new FlareSolverrClient(new Answers(() => _flareAnswers)),
        indexers: null!, torrentClients: new TorrentClientRegistry([new FakeTorrentClient()]),
        kavita: null!, sourceRegistry: null!, sourceAvailability: null!,
        mangaBakaDump: null!, embeddingModel: null!, embeddingStore: null!, embeddingStatus: null!,
        embeddingIndexer: null!, prebuiltIndex: null!, recoGraph: null!,
        recoGraphCache: null!, coReadInstaller: null!, coReadCache: null!, readerCohortInstaller: null!,
        readerCohortCache: null!, tasteVectorInstaller: null!, vectorIndexCache: null!,
        modelSwitcher: null!, db: _db.NewContext(), updateCheck: null!, currentUser: new TestCurrentUser(1),
        userSettings: null!,
        kavitaUser: new KavitaUserResolver(_db.ScopeFactory(), _settings),
        kavitaLive: new KavitaLiveReadSync(_settings, null!, null!, null!, null!, null!,
            NullLogger<KavitaLiveReadSync>.Instance),
        schedulerFactory: null!, scopeFactory: null!,
        logger: NullLogger<SettingsController>.Instance,
        connectionTests: store ?? Store());

    private async Task<List<ConnectionTestStatus>> Results() =>
        Assert.IsAssignableFrom<IEnumerable<ConnectionTestStatus>>(
            Assert.IsType<OkObjectResult>(await Controller().GetConnectionTests(default)).Value).ToList();

    [Fact]
    public async Task Nothing_tested_reads_as_no_results()
    {
        await _settings.SetAsync(SettingKeys.FlareSolverrUrl, "http://flare:8191");

        Assert.Empty(await Results());
    }

    [Fact]
    public async Task A_successful_test_is_recorded()
    {
        await _settings.SetAsync(SettingKeys.FlareSolverrUrl, "http://flare:8191");

        await Controller().TestFlareSolverr(new(null), default);

        var status = Assert.Single(await Results());
        Assert.Equal("flaresolverr", status.Key);
        Assert.True(status.Ok);
        Assert.Null(status.Message);
        Assert.True(DateTime.UtcNow - status.TestedAt < TimeSpan.FromMinutes(1));
    }

    [Fact]
    public async Task A_store_that_cannot_save_does_not_change_the_test_response()
    {
        await _settings.SetAsync(SettingKeys.FlareSolverrUrl, "http://flare:8191");
        var broken = _db.NewContext();
        var store = new ConnectionTestStore(broken, _settings, new SuwayomiSettings(_settings), new TestLocalizer());
        await broken.DisposeAsync();

        var ok = await Controller(store).TestFlareSolverr(new(null), default);
        Assert.IsType<OkObjectResult>(ok);

        _flareAnswers = HttpStatusCode.InternalServerError;
        var failed = await Controller(store).TestFlareSolverr(new(null), default);
        Assert.Equal(StatusCodes.Status502BadGateway, Assert.IsType<ObjectResult>(failed).StatusCode);
    }

    [Fact]
    public async Task A_failed_test_is_recorded_with_its_reason()
    {
        await _settings.SetAsync(SettingKeys.FlareSolverrUrl, "http://flare:8191");
        _flareAnswers = HttpStatusCode.InternalServerError;

        await Controller().TestFlareSolverr(new(null), default);

        var status = Assert.Single(await Results());
        Assert.False(status.Ok);
        Assert.Equal("error.settings.flaresolverrNoResponse", status.Message);
    }

    [Fact]
    public async Task A_later_test_replaces_the_earlier_one()
    {
        await _settings.SetAsync(SettingKeys.FlareSolverrUrl, "http://flare:8191");
        _flareAnswers = HttpStatusCode.InternalServerError;
        await Controller().TestFlareSolverr(new(null), default);
        _flareAnswers = HttpStatusCode.OK;
        await Controller().TestFlareSolverr(new(null), default);

        Assert.True(Assert.Single(await Results()).Ok);
    }

    [Fact]
    public async Task Changing_the_address_makes_the_result_stale()
    {
        await _settings.SetAsync(SettingKeys.FlareSolverrUrl, "http://flare:8191");
        await Controller().TestFlareSolverr(new(null), default);

        await Controller().SetFlareSolverr(new("http://other:8191"), default);

        Assert.Empty(await Results());
    }

    [Fact]
    public async Task Changing_the_password_makes_the_result_stale()
    {
        await Controller().SetDownloadClient(new("qbittorrent", "http://qbt:8080", "admin", "secret", null, null, null), default);
        await Controller().TestDownloadClient(new(null, null, null, null, null, null, null), default);
        Assert.True(Assert.Single(await Results()).Ok);

        await Controller().SetDownloadClient(new("qbittorrent", "http://qbt:8080", "admin", "changed", null, null, null), default);

        Assert.Empty(await Results());
    }

    [Fact]
    public async Task A_test_of_unsaved_values_counts_once_they_are_saved()
    {
        await _settings.SetAsync(SettingKeys.FlareSolverrUrl, "http://flare:8191");

        await Controller().TestFlareSolverr(new("http://new-flare:8191/"), default);
        Assert.Empty(await Results());

        await Controller().SetFlareSolverr(new("http://new-flare:8191"), default);
        Assert.True(Assert.Single(await Results()).Ok);
    }

    [Fact]
    public async Task A_test_refused_before_reaching_the_service_records_nothing()
    {
        await Controller().TestFlareSolverr(new(null), default);

        Assert.Empty(await _db.NewContext().ConnectionTestResults.ToListAsync());
    }

    [Fact]
    public void The_results_are_admin_only()
    {
        var method = typeof(SettingsController).GetMethod(nameof(SettingsController.GetConnectionTests))!;

        Assert.Equal(Policies.Admin, Assert.Single(method.GetCustomAttributes<AuthorizeAttribute>()).Policy);
    }

    private sealed class Answers(Func<HttpStatusCode> status) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(new Handler(status));

        private sealed class Handler(Func<HttpStatusCode> status) : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
                Task.FromResult(new HttpResponseMessage(status()));
        }
    }
}
