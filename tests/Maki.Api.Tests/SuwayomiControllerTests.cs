using System.Net;
using System.Text;
using System.Text.Json;
using Maki.Api.Controllers;
using Maki.Api.Services;
using Maki.Core.Configuration;
using Maki.Core.Entities;
using Maki.Core.Sources;
using Maki.Sources.Suwayomi;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;

namespace Maki.Api.Tests;

public sealed class SuwayomiControllerTests : IDisposable
{
    private const string Base = "http://192.168.1.20:4567";

    private static readonly byte[] Png =
        [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x00, 0x00, 0x0D, 0x49, 0x48, 0x44, 0x52];

    private readonly TestDb _db = new();
    private readonly FakeAppSettings _settings = new();
    private readonly FakeSuwayomiServer _server = new();
    private readonly SuwayomiSettings _suwayomiSettings;
    private readonly SuwayomiTokenStore _tokens;
    private readonly SuwayomiClient _client;
    private readonly SuwayomiMangaIdCache _cache = new();
    private readonly SourceRegistry _registry;

    public SuwayomiControllerTests()
    {
        _suwayomiSettings = new SuwayomiSettings(_settings);
        var factory = new SuwayomiChainFactory(_suwayomiSettings, () => _tokens, _server);
        _tokens = new SuwayomiTokenStore(_suwayomiSettings, factory);
        _client = new SuwayomiClient(factory, _suwayomiSettings, TimeProvider.System);
        var provider = new SuwayomiSourceProvider(_suwayomiSettings, _client, _cache, NullLogger<SuwayomiSourceProvider>.Instance);
        _registry = new SourceRegistry([], [provider]);

        _server.Ops["AboutServer"] = _ => FakeSuwayomiServer.Data(new { aboutServer = new { name = "Suwayomi", version = "v2.4.2366", buildType = "Stable" } });
        LiveSources(("100", "MangaDex", "MangaDex (EN)", "en"), ("200", "Comick", "Comick (ALL)", "all"));
    }

    public void Dispose() => _db.Dispose();

    private SuwayomiController Controller()
    {
        var controller = new SuwayomiController(
            new TestLocalizer(), _settings, _suwayomiSettings, _client, _tokens, _cache,
            new SuwayomiConnectionTester(() => _server), _registry, new SourceAvailability(_settings, _registry), _db.NewContext(), new SuwayomiChainFactory(_suwayomiSettings, () => _tokens, _server),
            NullLogger<SuwayomiController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };
        return controller;
    }

    private void Configure(string authMode = "none", string? username = null, string? password = null)
    {
        _settings.Set(SettingKeys.SuwayomiUrl, Base).Set(SettingKeys.SuwayomiAuthMode, authMode);
        if (username is not null)
        {
            _settings.Set(SettingKeys.SuwayomiUsername, username);
        }

        if (password is not null)
        {
            _settings.Set(SettingKeys.SuwayomiPassword, password);
        }
    }

    private void LiveSources(params (string Id, string Name, string DisplayName, string Lang)[] sources) =>
        _server.Sources(sources);

    private Task<List<string>> DisabledAsync() => new SourceAvailability(_settings, _registry).DisabledAsync();

    private void SwitchOn(params string[] names) =>
        _settings.Set(SettingKeys.SourcePriorityOrder, string.Join(',', names)).Set(SettingKeys.SourcesDisabled, "");

    private static string Error(IActionResult result) =>
        JsonSerializer.SerializeToElement(Assert.IsAssignableFrom<ObjectResult>(result).Value).GetProperty("code").GetString()!;

    private static T Body<T>(IActionResult result) => Assert.IsType<T>(Assert.IsType<OkObjectResult>(result).Value);

    [Fact]
    public async Task The_password_is_never_returned_and_null_leaves_it_alone()
    {
        Configure("basic", "maki", "secret");

        var read = Body<SuwayomiController.SuwayomiConnectionSettings>(await Controller().GetConnection(default));
        Assert.Null(read.Password);
        Assert.True(read.PasswordSet);

        await Controller().SetConnection(new(Base, "basic", "maki", Password: null), default);
        Assert.Equal("secret", await _settings.GetAsync(SettingKeys.SuwayomiPassword));

        await Controller().SetConnection(new(Base, "basic", "maki", Password: ""), default);
        Assert.Null(await _settings.GetAsync(SettingKeys.SuwayomiPassword));
    }

    [Fact]
    public async Task Switching_to_no_authentication_clears_the_stored_password()
    {
        Configure("basic", "maki", "secret");

        await Controller().SetConnection(new(Base, "none", null, Password: null), default);

        Assert.Null(await _settings.GetAsync(SettingKeys.SuwayomiPassword));
        Assert.False(Body<SuwayomiController.SuwayomiConnectionSettings>(await Controller().GetConnection(default)).PasswordSet);
    }

    [Fact]
    public async Task A_new_url_without_a_new_password_does_not_carry_the_old_one_over()
    {
        Configure("basic", "maki", "secret");

        await Controller().SetConnection(new("http://192.168.1.99:4567", "basic", "maki", Password: null), default);
        Assert.Null(await _settings.GetAsync(SettingKeys.SuwayomiPassword));

        await Controller().SetConnection(new("http://192.168.1.99:4567", "basic", "maki", Password: "other"), default);
        await Controller().SetConnection(new("", "none", null), default);
        await Controller().SetConnection(new("http://192.168.1.99:4567", "basic", "maki"), default);
        Assert.Null(await _settings.GetAsync(SettingKeys.SuwayomiPassword));
    }

    [Theory]
    [InlineData("suwayomi:4567", "none", null, "error.settings.urlInvalid")]
    [InlineData(Base, "token", null, "error.suwayomi.authModeInvalid")]
    [InlineData(Base, "uilogin", " ", "error.suwayomi.usernameRequired")]
    public async Task Bad_connection_settings_are_refused(string url, string mode, string? username, string expected) =>
        Assert.Equal(expected, Error(await Controller().SetConnection(new(url, mode, username), default)));

    [Fact]
    public async Task Saving_the_connection_reloads_the_sources_and_clearing_the_url_turns_them_off()
    {
        _settings.Set(SettingKeys.SuwayomiSources,
            SuwayomiSettings.Serialize([new SuwayomiStoredSource("100", "MangaDex", "MangaDex (EN)", "en")]));

        await Controller().SetConnection(new(Base + "/", "none", null), default);
        Assert.Equal(Base, await _settings.GetAsync(SettingKeys.SuwayomiUrl));
        Assert.NotNull(_registry.Find("suwayomi-100"));

        await Controller().SetConnection(new("", "none", null), default);
        Assert.Null(_registry.Find("suwayomi-100"));
        Assert.NotNull(await _settings.GetAsync(SettingKeys.SuwayomiSources));
    }

    [Fact]
    public async Task The_test_reports_version_and_source_count_using_the_stored_password()
    {
        Configure("uilogin", "maki", "secret");
        _server.Ops["Login"] = vars =>
            vars.GetProperty("password").GetString() == "secret"
                ? FakeSuwayomiServer.Data(new { login = new { accessToken = "a1", refreshToken = "r1" } })
                : FakeSuwayomiServer.Errors(FakeSuwayomiServer.LoginRefused);

        var result = await Controller().TestConnection(new(Base, "uilogin", "maki"), default);

        var body = JsonSerializer.SerializeToElement(Assert.IsType<OkObjectResult>(result).Value);
        Assert.Equal("v2.4.2366", body.GetProperty("version").GetString());
        Assert.Equal(2, body.GetProperty("sourceCount").GetInt32());
        Assert.Equal(1, _server.Count("Login"));
    }

    [Fact]
    public async Task The_test_says_which_version_a_sign_in_mode_needs()
    {
        _server.Ops["AboutServer"] = _ => FakeSuwayomiServer.Data(new { aboutServer = new { name = "Suwayomi", version = "v2.1.1867", buildType = "Stable" } });

        var result = await Controller().TestConnection(new(Base, "uilogin", "maki", "pw"), default);

        Assert.Equal("error.suwayomi.versionUnsupported", Error(result));
        Assert.Equal(0, _server.Count("Login"));
        Assert.IsType<OkObjectResult>(await Controller().TestConnection(new(Base, "none", null), default));
    }

    [Fact]
    public async Task A_refused_login_reads_as_a_login_failure_not_an_outage()
    {
        _server.Ops["Login"] = _ => FakeSuwayomiServer.Errors(FakeSuwayomiServer.LoginRefused);

        var result = await Controller().TestConnection(new(Base, "uilogin", "maki", "wrong"), default);

        Assert.Equal(StatusCodes.Status502BadGateway, Assert.IsType<ObjectResult>(result).StatusCode);
        Assert.Equal("error.suwayomi.loginFailed", Error(result));
    }

    [Fact]
    public async Task The_stored_password_is_not_sent_to_a_different_server()
    {
        Configure("uilogin", "maki", "secret");
        string? sent = null;
        _server.Ops["Login"] = vars =>
        {
            sent = vars.GetProperty("password").GetString();
            return FakeSuwayomiServer.Errors(FakeSuwayomiServer.LoginRefused);
        };

        await Controller().TestConnection(new("http://192.168.1.99:4567", "uilogin", "maki"), default);

        Assert.Equal("", sent);
    }

    [Fact]
    public async Task Saving_the_connection_stores_every_installed_source_switched_off()
    {
        await Controller().SetConnection(new(Base, "none", null), default);

        Assert.Equal(["Comick (ALL)", "MangaDex (EN)"], (await _suwayomiSettings.StoredAsync()).Select(s => s.DisplayName));
        Assert.NotNull(_registry.Find("suwayomi-100"));
        Assert.True(Assert.IsType<SuwayomiSource>(_registry.Find("suwayomi-200")).NeedsSetup);
        Assert.Equal(["suwayomi-100", "suwayomi-200"], (await DisabledAsync()).Order());
    }

    [Fact]
    public async Task Saving_the_connection_while_suwayomi_is_down_still_saves()
    {
        _server.Ops["Sources"] = _ => new HttpResponseMessage(HttpStatusCode.BadGateway);

        Assert.IsType<OkObjectResult>(await Controller().SetConnection(new(Base, "none", null), default));

        Assert.Equal(Base, await _settings.GetAsync(SettingKeys.SuwayomiUrl));
        Assert.Null(await _settings.GetAsync(SettingKeys.SuwayomiSources));
    }

    [Fact]
    public async Task A_refresh_keeps_chosen_languages_and_drops_uninstalled_sources_nothing_is_linked_to()
    {
        Configure();
        _settings.Set(SettingKeys.SuwayomiSources, SuwayomiSettings.Serialize(
        [
            new SuwayomiStoredSource("200", "Comick", "Comick (ALL)", "all", ChapterLanguage: "fr"),
            new SuwayomiStoredSource("300", "Gone", "Gone (EN)", "en"),
            new SuwayomiStoredSource("400", "Unused", "Unused (EN)", "en")
        ]));
        _db.SeedSeries(mappings: [new SourceMapping { SourceName = "suwayomi-300", SourceSeriesId = "/m/1", Url = "u" }]);

        var result = Body<SuwayomiController.SuwayomiRefreshResult>(await Controller().RefreshSources(default));

        Assert.True(result.Reachable);
        Assert.Equal(2, result.Installed);
        var stored = await _suwayomiSettings.StoredAsync();
        Assert.Equal(["200", "300", "100"], stored.Select(s => s.Id));
        Assert.Equal("fr", stored[0].ChapterLanguage);
        Assert.True(stored[1].Missing);
        Assert.False(stored[2].Missing);
        Assert.Equal(["fr"], _registry.GetRequired("suwayomi-200").SupportedLanguages);
        Assert.Null(_registry.Find("suwayomi-400"));
    }

    [Fact]
    public async Task An_unreachable_server_leaves_the_stored_sources_alone()
    {
        Configure();
        var stored = SuwayomiSettings.Serialize([new SuwayomiStoredSource("100", "MangaDex", "MangaDex (EN)", "en")]);
        _settings.Set(SettingKeys.SuwayomiSources, stored);
        _server.Ops["Sources"] = _ => new HttpResponseMessage(HttpStatusCode.BadGateway);

        var result = Body<SuwayomiController.SuwayomiRefreshResult>(await Controller().RefreshSources(default));

        Assert.False(result.Reachable);
        Assert.Equal("error.suwayomi.noResponse", result.Error);
        Assert.Equal(stored, await _settings.GetAsync(SettingKeys.SuwayomiSources));
    }

    [Fact]
    public async Task A_mixed_language_source_stays_off_until_it_has_a_chapter_language()
    {
        Configure();
        await Controller().RefreshSources(default);
        SwitchOn("suwayomi-100", "suwayomi-200");

        Assert.Equal(["suwayomi-200"], await DisabledAsync());

        Assert.IsType<NoContentResult>(await Controller().SetSourceLanguage("200", new(" pt-BR "), default));

        var source = Assert.IsType<SuwayomiSource>(_registry.Find("suwayomi-200"));
        Assert.False(source.NeedsSetup);
        Assert.Equal(["pt-BR"], source.SupportedLanguages);
        Assert.Empty(await DisabledAsync());

        await Controller().RefreshSources(default);
        Assert.Equal("pt-BR", Assert.Single(await _suwayomiSettings.StoredAsync(), s => s.Id == "200").ChapterLanguage);
    }

    [Theory]
    [InlineData("200", null, "error.suwayomi.chapterLanguageRequired")]
    [InlineData("200", "French", "error.suwayomi.chapterLanguageInvalid")]
    [InlineData("100", "fr", "error.suwayomi.chapterLanguageFixed")]
    [InlineData("999", "fr", "error.suwayomi.unknownSource")]
    public async Task A_chapter_language_that_cannot_apply_is_refused(string id, string? language, string expected)
    {
        Configure();
        await Controller().RefreshSources(default);

        Assert.Equal(expected, Error(await Controller().SetSourceLanguage(id, new(language), default)));
        Assert.All(await _suwayomiSettings.StoredAsync(), s => Assert.Null(s.ChapterLanguage));
    }

    [Fact]
    public async Task A_thumbnail_is_served_only_for_a_manga_of_a_source_that_is_on()
    {
        Configure();
        await Controller().RefreshSources(default);
        SwitchOn("suwayomi-100");
        _server.Ops["MangaSource"] = vars => FakeSuwayomiServer.Data(new
        {
            manga = new { sourceId = vars.GetProperty("id").GetInt32() == 7 ? "100" : "200" }
        });
        _server.OnGet = request => request.RequestUri!.AbsolutePath == "/api/v1/manga/7/thumbnail"
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Png) }
            : new HttpResponseMessage(HttpStatusCode.NotFound);

        var controller = Controller();
        var served = Assert.IsType<FileContentResult>(await controller.Thumbnail(100, 7, default));
        Assert.Equal("image/png", served.ContentType);
        Assert.Equal("default-src 'none'; sandbox", controller.Response.Headers.ContentSecurityPolicy.ToString());
        Assert.Equal("private,max-age=86400", controller.Response.Headers.CacheControl.ToString());

        Assert.IsType<NotFoundResult>(await Controller().Thumbnail(100, 8, default));
        Assert.IsType<NotFoundResult>(await Controller().Thumbnail(200, 8, default));
        Assert.Equal(1, _server.Calls.Count(c => c == "GET /api/v1/manga/7/thumbnail"));
    }

    [Fact]
    public async Task A_thumbnail_that_is_not_an_image_is_refused()
    {
        Configure();
        await Controller().RefreshSources(default);
        SwitchOn("suwayomi-100");
        _cache.SetOwner(new Uri(Base), 7, "100");
        _server.OnGet = _ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("<script>alert(1)</script>", Encoding.UTF8, "text/html")
        };

        Assert.Equal("error.search.notAnImage", Error(await Controller().Thumbnail(100, 7, default)));
    }

    [Fact]
    public void Every_action_but_the_thumbnail_relay_is_admin_only()
    {
        var actions = typeof(SuwayomiController).GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.DeclaredOnly)
            .Where(m => m.GetCustomAttributes(typeof(Microsoft.AspNetCore.Mvc.Routing.HttpMethodAttribute), true).Length > 0)
            .ToList();

        Assert.Equal(6, actions.Count);
        foreach (var action in actions)
        {
            var policies = action.GetCustomAttributes(typeof(Microsoft.AspNetCore.Authorization.AuthorizeAttribute), true)
                .Cast<Microsoft.AspNetCore.Authorization.AuthorizeAttribute>()
                .Select(a => a.Policy)
                .ToArray();
            Assert.Equal(action.Name == nameof(SuwayomiController.Thumbnail) ? [] : [Maki.Api.Auth.Policies.Admin], policies);
        }
    }
}
