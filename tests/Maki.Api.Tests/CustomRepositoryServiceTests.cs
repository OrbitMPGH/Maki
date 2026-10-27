using System.Net;
using System.Security.Cryptography;
using System.Text;
using Maki.Api.Controllers;
using Maki.Api.Services;
using Maki.Core.Entities;
using Maki.Core.Http;
using Maki.Core.Inbox;
using Maki.Core.Sources;
using Maki.Core.Sources.Custom;
using Maki.Sources.Custom;
using Microsoft.Extensions.Logging.Abstractions;

namespace Maki.Api.Tests;

/// <summary>Repositories against a fake site and a fake repository, through the real store, save path, tester and registry.</summary>
public sealed class CustomRepositoryServiceTests : IDisposable
{
    private const string Index = "https://repo.example/index.json";
    private const string Plain = "http://repo.example/index.json";

    private readonly TestDb _db = new();
    private readonly FakeAppSettings _settings = new();
    private readonly FakeWeb _web = new();
    private readonly SourceRegistry _registry;
    private readonly CustomSourceStore _store;
    private readonly CustomSourceFactory _factory;
    private readonly CustomRepositoryStore _repositories;
    private readonly RecordingInbox _inbox = new();

    public CustomRepositoryServiceTests()
    {
        _store = new CustomSourceStore(_settings);
        _factory = new CustomSourceFactory(
            new CustomSourceFetcher(_web, new FlareSolverrClient(_web), _settings, NullLogger<ChallengeAwareFetcher>.Instance),
            _web, new CustomSourceThrottle());
        _registry = new SourceRegistry([], [new CustomSourceProvider(_store, _factory, NullLogger<CustomSourceProvider>.Instance)]);
        _repositories = new CustomRepositoryStore(_settings);

        _web.Text("https://quietpen.example/search/?q=Blue%20Hour", """<html><body><a class="card" href="/series/blue/">Blue Hour</a></body></html>""");
        _web.Text("https://quietpen.example/series/blue/", """<html><body><h1>Blue Hour</h1><ul><a href="/blue-1/">Chapter 1</a></ul></body></html>""");
        _web.Text("https://quietpen.example/blue-1/", """<html><body><img src="https://quietpen.example/p/1.png"></body></html>""");
        _web.Bytes("https://quietpen.example/p/1.png", [0x89, 0x50, 0x4E, 0x47, 0, 0, 0, 0]);
    }

    public void Dispose() => _db.Dispose();

    private static string Definition(string name = "Quiet Pen", string chapterUrl = "/{chapterId}/") => $$"""
        {
          "schema": 1, "id": "quietpen", "name": "{{name}}", "baseUrl": "https://quietpen.example", "minIntervalMs": 250,
          "search": { "url": "/search/?q={query}", "item": { "css": "a.card" }, "id": { "attr": "href", "regex": "^/series/([^/]+)/$" } },
          "series": { "url": "/series/{seriesId}/" },
          "chapters": { "item": { "css": "ul a[href]" }, "id": { "attr": "href", "regex": "^/([^/]+)/$" }, "url": "{{chapterUrl}}" },
          "pages": { "list": { "css": "img" } },
          "sample": { "seriesUrl": "https://quietpen.example/series/blue/", "chapterUrl": "https://quietpen.example/blue-1/", "query": "Blue Hour" }
        }
        """;

    private void Publish(int version, string file, string? sha = null, string index = Index)
    {
        var name = $"quietpen-{version}.json";
        var root = index[..(index.LastIndexOf('/') + 1)];
        _web.Text(root + name, file);
        var hash = sha ?? Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(file))).ToLowerInvariant();
        _web.Text(index, $$"""
            { "name": "Quiet repo", "definitions": [
              { "id": "quietpen", "name": "Quiet Pen", "language": "en", "schema": 1, "version": {{version}}, "url": "{{name}}", "sha256": "{{hash}}" }
            ] }
            """);
    }

    private CustomSourceService Sources() => new(_store, _factory, _registry,
        new SourceChapterListCache(TimeProvider.System, NullLogger<SourceChapterListCache>.Instance), _db.NewContext());

    private CustomRepositoryService Service() => new(_repositories, new CustomRepositoryClient(_web), Sources(), _inbox,
        NullLogger<CustomRepositoryService>.Instance);

    private async Task<CustomSourceDefinition> Installed() => (await _store.ReadAsync()).Single().Definition!;

    private async Task<string> StateOf() => (await Service().ListAsync(default)).Single().Definitions.Single().State;

    [Fact]
    public async Task Installing_tests_the_definition_and_records_where_it_came_from()
    {
        Publish(1, Definition());
        Assert.IsType<CustomRepositoryService.RepositoryView>(await Service().AddAsync(Index, default));
        Assert.Equal("available", await StateOf());

        var result = await Service().InstallAsync(Index, "quietpen", confirmed: false, default);

        Assert.IsType<CustomSourceService.Saved>(result);
        Assert.IsType<CustomSource>(_registry.Find("custom-quietpen"));
        var installed = await Installed();
        Assert.Equal(Index, installed.Origin?.Repository);
        Assert.Equal(1, installed.Origin?.Version);
        Assert.Equal("installed", await StateOf());
    }

    [Fact]
    public async Task A_file_that_does_not_match_its_checksum_is_refused()
    {
        Publish(1, Definition(), sha: new string('0', 64));
        await Service().AddAsync(Index, default);

        var result = await Service().InstallAsync(Index, "quietpen", false, default);

        Assert.Equal("error.customRepository.hashMismatch", Assert.IsType<CustomRepositoryService.Refused>(result).Key);
        Assert.Empty(await _store.ReadAsync());
    }

    [Fact]
    public async Task A_definition_that_fails_its_test_is_not_installed()
    {
        Publish(1, Definition());
        _web.Text("https://quietpen.example/series/blue/", "<html><body><p>Redesigned</p></body></html>");
        await Service().AddAsync(Index, default);

        Assert.IsType<CustomRepositoryService.TestFailed>(await Service().InstallAsync(Index, "quietpen", false, default));
        Assert.Empty(await _store.ReadAsync());
    }

    [Fact]
    public async Task An_update_is_applied_on_its_own_and_can_be_reverted()
    {
        Publish(1, Definition());
        await Service().AddAsync(Index, default);
        await Service().InstallAsync(Index, "quietpen", false, default);
        await Service().SetAutoUpdateAsync(Index, true, default);
        Publish(2, Definition(name: "Quiet Pen Scans"));

        await Service().RunDailyAsync(default);

        var updated = await Installed();
        Assert.Equal("Quiet Pen Scans", updated.Name);
        Assert.Equal(2, updated.Origin?.Version);
        Assert.NotNull(updated.Previous);
        var raised = Assert.Single(_inbox.Raised);
        Assert.Equal(InboxEventType.CustomSourceUpdated, raised.Type);

        Assert.IsType<CustomSourceService.Saved>(await Service().RevertAsync("quietpen", false, default));
        var reverted = await Installed();
        Assert.Equal("Quiet Pen", reverted.Name);
        Assert.Equal(1, reverted.Origin?.Version);
        Assert.Null(reverted.Previous);
    }

    [Fact]
    public async Task An_update_that_could_rekey_linked_series_waits_for_the_admin()
    {
        Publish(1, Definition());
        await Service().AddAsync(Index, default);
        await Service().InstallAsync(Index, "quietpen", false, default);
        await Service().SetAutoUpdateAsync(Index, true, default);
        LinkSeries();
        _web.Text("https://quietpen.example/c/blue-1/", """<html><body><img src="https://quietpen.example/p/1.png"></body></html>""");
        Publish(2, Definition(chapterUrl: "/c/{chapterId}/").Replace("\"regex\": \"^/([^/]+)/$\" }, \"url\"", "\"regex\": \"^/([^/]+)/$\" }, \"url\""));
        _web.Text("https://quietpen.example/series/blue/", """<html><body><h1>Blue Hour</h1><ul><a href="/c/blue-1/">Chapter 1</a></ul></body></html>""");

        await Service().RunDailyAsync(default);

        Assert.Equal(1, (await Installed()).Origin?.Version);
        Assert.Equal("updateAvailable", await StateOf());
        Assert.NotNull((await _repositories.ReadAsync()).Single().LastError);
        Assert.Empty(_inbox.Raised);

        var preview = Assert.IsType<CustomRepositoryService.UpdatePreview>(await Service().PreviewUpdateAsync("quietpen", default));
        Assert.Contains("chapters", preview.ChangedFields);
    }

    [Fact]
    public async Task A_local_edit_detaches_the_source_from_its_updates()
    {
        Publish(1, Definition());
        await Service().AddAsync(Index, default);
        await Service().InstallAsync(Index, "quietpen", false, default);
        await Service().SetAutoUpdateAsync(Index, true, default);

        await Sources().SaveAsync(CustomSourceDefinition.Parse(Definition(name: "Mine")), true, default);
        Publish(2, Definition(name: "Theirs"));
        await Service().RunDailyAsync(default);

        var edited = await Installed();
        Assert.Equal("Mine", edited.Name);
        Assert.True(edited.Detached);
        Assert.Equal(Index, edited.Origin?.Repository);
        Assert.Equal("detached", await StateOf());
    }

    [Fact]
    public async Task Removing_a_repository_leaves_its_sources_as_hand_made()
    {
        Publish(1, Definition());
        await Service().AddAsync(Index, default);
        await Service().InstallAsync(Index, "quietpen", false, default);

        Assert.Equal(1, await Service().RemoveAsync(Index, default));

        var kept = await Installed();
        Assert.Null(kept.Origin);
        Assert.False(kept.Detached);
        Assert.IsType<CustomSource>(_registry.Find("custom-quietpen"));
    }

    [Fact]
    public async Task A_plain_http_repository_can_not_keep_sources_up_to_date()
    {
        Publish(1, Definition(), index: Plain);
        await Service().AddAsync(Plain, default);

        var result = await Service().SetAutoUpdateAsync(Plain, true, default);

        Assert.Equal("error.customRepository.autoUpdateNeedsHttps", Assert.IsType<CustomRepositoryService.Refused>(result).Key);
    }

    [Fact]
    public async Task An_id_another_source_already_has_is_never_overwritten()
    {
        await Sources().SaveAsync(CustomSourceDefinition.Parse(Definition(name: "Hand made")), true, default);
        Publish(1, Definition());
        await Service().AddAsync(Index, default);

        var result = await Service().InstallAsync(Index, "quietpen", false, default);

        Assert.Equal("error.customRepository.idTaken", Assert.IsType<CustomRepositoryService.Refused>(result).Key);
        Assert.Equal("idTaken", await StateOf());
        Assert.Equal("Hand made", (await Installed()).Name);
    }

    [Fact]
    public void Every_action_is_admin_only()
    {
        var actions = typeof(CustomSourceRepositoriesController).GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.DeclaredOnly)
            .Where(m => m.GetCustomAttributes(typeof(Microsoft.AspNetCore.Mvc.Routing.HttpMethodAttribute), true).Length > 0)
            .ToList();

        Assert.Equal(9, actions.Count);
        foreach (var action in actions)
        {
            var policies = action.GetCustomAttributes(typeof(Microsoft.AspNetCore.Authorization.AuthorizeAttribute), true)
                .Cast<Microsoft.AspNetCore.Authorization.AuthorizeAttribute>()
                .Select(a => a.Policy)
                .ToArray();
            Assert.Equal(Maki.Api.Auth.Policies.Admin, Assert.Single(policies));
        }
    }

    private void LinkSeries()
    {
        using var db = _db.NewContext();
        var root = new RootFolder { Path = Path.GetTempPath() };
        db.RootFolders.Add(root);
        db.SaveChanges();
        var series = new Series { Title = "Blue", SortTitle = "Blue", FolderName = "Blue", RootFolderId = root.Id };
        db.Series.Add(series);
        db.SaveChanges();
        db.SourceMappings.Add(new SourceMapping { SeriesId = series.Id, SourceName = "custom-quietpen", SourceSeriesId = "blue", Url = "u" });
        db.SaveChanges();
    }

    /// <summary>Every named client answers from the same table of URLs.</summary>
    private sealed class FakeWeb : IHttpClientFactory
    {
        private readonly Dictionary<string, byte[]> _bodies = [];

        public void Text(string url, string body) => _bodies[url] = Encoding.UTF8.GetBytes(body);

        public void Bytes(string url, byte[] body) => _bodies[url] = body;

        public HttpClient CreateClient(string name) => new(new Handler(this));

        private sealed class Handler(FakeWeb web) : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
                Task.FromResult(web._bodies.TryGetValue(request.RequestUri!.AbsoluteUri, out var body)
                    ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) }
                    : new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }
}
