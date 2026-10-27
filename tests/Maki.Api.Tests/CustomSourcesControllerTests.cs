using System.Text.Json;
using Maki.Api.Controllers;
using Maki.Api.Services;
using Maki.Core.Configuration;
using Maki.Core.Entities;
using Maki.Core.Http;
using Maki.Core.Sources;
using Maki.Sources.Custom;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;

namespace Maki.Api.Tests;

public sealed class CustomSourcesControllerTests : IDisposable
{
    private const string Definition = """
        {
          "schema": 1, "id": "silentquill", "name": "SilentQuill", "baseUrl": "https://silentquill.net",
          "search": { "url": "/search/?q={query}", "item": { "css": "a.block" }, "id": { "attr": "href", "regex": "^/series/([^/]+)/$" } },
          "series": { "url": "/series/{seriesId}/" },
          "chapters": { "item": { "css": "div.columns-1 a[href]" }, "id": { "attr": "href", "regex": "^/([^/]+)/$" }, "url": "/{chapterId}/" },
          "pages": { "list": { "regex": "/img/p/\\d+-[A-Za-z0-9_-]+" } },
          "sample": {
            "seriesUrl": "https://silentquill.net/series/thus-the-seasons-learn-of-blue/",
            "chapterUrl": "https://silentquill.net/thus-the-seasons-learn-of-blue-chapter-2/",
            "query": "Thus"
          }
        }
        """;

    private readonly TestDb _db = new();
    private readonly FakeAppSettings _settings = new();
    private readonly SourceRegistry _registry;
    private readonly CustomSourceStore _store;
    private readonly CustomSourceFactory _factory;

    public CustomSourcesControllerTests()
    {
        var http = new NoNetwork();
        _store = new CustomSourceStore(_settings);
        _factory = new CustomSourceFactory(
            new CustomSourceFetcher(http, new FlareSolverrClient(http), _settings, NullLogger<ChallengeAwareFetcher>.Instance),
            http, new CustomSourceThrottle());
        var provider = new CustomSourceProvider(_store, _factory, NullLogger<CustomSourceProvider>.Instance);
        _registry = new SourceRegistry([new BuiltIn()], [provider]);
    }

    public void Dispose() => _db.Dispose();

    private CustomSourcesController Controller() => new(
        new TestLocalizer(),
        new CustomSourceService(_store, _factory, _registry,
            new SourceChapterListCache(TimeProvider.System, NullLogger<SourceChapterListCache>.Instance), _db.NewContext()))
    {
        ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
    };

    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement;

    private static string Code(IActionResult result) =>
        JsonSerializer.SerializeToElement(Assert.IsAssignableFrom<ObjectResult>(result).Value).GetProperty("code").GetString()!;

    [Fact]
    public void Every_action_is_admin_only()
    {
        var actions = typeof(CustomSourcesController).GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.DeclaredOnly)
            .Where(m => m.GetCustomAttributes(typeof(Microsoft.AspNetCore.Mvc.Routing.HttpMethodAttribute), true).Length > 0)
            .ToList();

        Assert.Equal(5, actions.Count);
        foreach (var action in actions)
        {
            var policies = action.GetCustomAttributes(typeof(Microsoft.AspNetCore.Authorization.AuthorizeAttribute), true)
                .Cast<Microsoft.AspNetCore.Authorization.AuthorizeAttribute>()
                .Select(a => a.Policy)
                .ToArray();
            Assert.Equal(Maki.Api.Auth.Policies.Admin, Assert.Single(policies));
        }
    }

    [Fact]
    public async Task Saving_registers_the_source_and_deleting_removes_it()
    {
        var saved = await Controller().Save("silentquill", Json(Definition), confirm: false, default);
        Assert.IsType<OkObjectResult>(saved);
        Assert.IsType<CustomSource>(_registry.Find("custom-silentquill"));
        Assert.NotNull(await _settings.GetAsync(SettingKeys.CustomSources));

        var deleted = await Controller().Delete("silentquill", default);
        Assert.IsType<NoContentResult>(deleted);
        Assert.Null(_registry.Find("custom-silentquill"));
    }

    [Fact]
    public async Task Deleting_clears_the_errors_its_mappings_carried()
    {
        await Controller().Save("silentquill", Json(Definition), confirm: false, default);
        using (var db = _db.NewContext())
        {
            var root = new RootFolder { Path = Path.GetTempPath() };
            db.RootFolders.Add(root);
            db.SaveChanges();
            var series = new Series { Title = "Blue", SortTitle = "Blue", FolderName = "Blue", RootFolderId = root.Id };
            db.Series.Add(series);
            db.SaveChanges();
            db.SourceMappings.Add(new SourceMapping
            {
                SeriesId = series.Id, SourceName = "custom-silentquill", SourceSeriesId = "blue", Url = "u", LastError = "broken"
            });
            db.SaveChanges();
        }

        await Controller().Delete("silentquill", default);

        using var check = _db.NewContext();
        var mapping = Assert.Single(check.SourceMappings);
        Assert.Null(mapping.LastError);
        Assert.Equal("custom-silentquill", mapping.SourceName);
    }

    [Fact]
    public async Task A_host_another_source_serves_is_refused()
    {
        var json = Definition.Replace("silentquill.net", "builtin.example");

        Assert.Equal("error.customSource.hostTaken", Code(await Controller().Save("silentquill", Json(json), false, default)));
    }

    [Fact]
    public async Task The_id_in_the_path_must_match_the_body()
    {
        Assert.Equal("error.customSource.idMismatch", Code(await Controller().Save("other", Json(Definition), false, default)));
    }

    [Fact]
    public async Task A_broken_definition_answers_every_problem()
    {
        var json = Definition.Replace("\"/series/{seriesId}/\"", "\"/series/\"").Replace("\"silentquill\"", "\"Bad Id\"");

        var result = Assert.IsType<BadRequestObjectResult>(await Controller().Save("Bad Id", Json(json), false, default));
        var problems = JsonSerializer.SerializeToElement(result.Value).GetProperty("problems");
        var codes = problems.EnumerateArray().Select(p => p.GetProperty("Code").GetString()).ToList();
        Assert.Contains("error.customSource.idInvalid", codes);
        Assert.Contains("error.customSource.templatePlaceholder", codes);
    }

    [Fact]
    public async Task Export_leaves_out_where_it_was_installed_from()
    {
        var withOrigin = Definition.TrimEnd().TrimEnd('}') + """, "origin": { "repository": "https://repo.example/index.json", "version": 2 } }""";
        await Controller().Save("silentquill", Json(withOrigin), false, default);

        var exported = Assert.IsType<OkObjectResult>(await Controller().Export("silentquill", default)).Value!;

        Assert.False(JsonSerializer.SerializeToElement(exported).TryGetProperty("origin", out _));
    }

    private sealed class BuiltIn : ISource
    {
        public string Name => "builtin";
        public string DisplayName => "Built in";
        public string BaseUrl => "https://builtin.example";
        public SourceCapabilities Capabilities => SourceCapabilities.None;
        public Task<IReadOnlyList<SourceSeriesResult>> SearchAsync(string title, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<SourceSeriesDetail> GetSeriesAsync(string sourceSeriesId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<SourceChapter>> ListChaptersAsync(string sourceSeriesId, string? languageFilter = null, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<ChapterPages> GetPagesAsync(SourceChapter chapter, CancellationToken ct = default) => throw new NotSupportedException();
    }

    private sealed class NoNetwork : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => throw new InvalidOperationException("No network in this test");
    }
}
