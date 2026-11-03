using System.Net;
using Maki.Api.Jobs;
using Maki.Api.Services;
using Maki.Core.Configuration;
using Maki.Core.Entities;
using Maki.Core.Http;
using Maki.Core.Sources;
using Maki.Core.Sources.Custom;
using Maki.Sources.Custom;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Maki.Api.Tests;

[Collection(OneRunPerProcessCollection.Name)]
public sealed class CustomSourceHealthJobTests : IDisposable
{
    private const string Sample = "https://quietpen.example/series/blue/";

    private readonly UpgradeWorld _world = new();
    private readonly PageFetcher _pages = new();
    private readonly SourceRegistry _registry;

    public CustomSourceHealthJobTests()
    {
        _world.Seed();
        var definition = new CustomSourceDefinition
        {
            Id = "quietpen",
            Name = "Quiet Pen",
            BaseUrl = "https://quietpen.example",
            Series = new CustomSeries { Url = "/series/{seriesId}/" },
            Chapters = new CustomChapters
            {
                Item = new ListExtractor { Css = "ul a[href]" },
                Id = new FieldExtractor { Attr = "href", Regex = "^/([^/]+)/$" },
                Url = "/{chapterId}/"
            },
            Pages = new CustomPages { List = new ListExtractor { Css = "img" } },
            Sample = new CustomSample { SeriesUrl = Sample, ChapterUrl = "https://quietpen.example/blue-1/" }
        };
        var source = new CustomSource(definition, _pages, _world.Http, new SpacingGate(TimeSpan.Zero));
        _registry = new SourceRegistry([source], []);
    }

    public void Dispose() => _world.Dispose();

    private const string Listing = """<html><body><ul><a href="/blue-1/">Chapter 1</a></ul></body></html>""";

    private async Task RunAsync()
    {
        using var db = _world.Db.NewContext();
        using var monitorDb = _world.Db.NewContext();
        var monitor = new HealthMonitor(monitorDb, null!, _world.Settings, _world.Paths, _registry,
            new SourceAvailability(_world.Settings, _registry), new ServiceCollection().BuildServiceProvider(),
            new RecordingNotifications(), _world.Inbox, new TestLocalizer(), new TestUserLocaleResolver(), null!);
        var job = new CustomSourceHealthJob(_registry, new SourceAvailability(_world.Settings, _registry), monitor, db,
            NullLogger<CustomSourceHealthJob>.Instance);
        await job.Execute(new TestJobContext());
    }

    private HealthCheckRecord? Row()
    {
        using var db = _world.Db.NewContext();
        return db.HealthChecks.SingleOrDefault(r => r.Id == "custom:source:custom-quietpen");
    }

    private void LinkOtherSeries(string seriesId)
    {
        using var db = _world.Db.NewContext();
        db.SourceMappings.Add(new SourceMapping { SeriesId = _world.SeriesId, SourceName = "custom-quietpen", SourceSeriesId = seriesId, Priority = 3 });
        db.SaveChanges();
    }

    [Fact]
    public async Task A_working_source_is_healthy_and_not_probed_again_within_six_hours()
    {
        _pages.Answer("/series/blue/", Listing);

        await RunAsync();
        await RunAsync();

        Assert.Equal("healthy", Row()?.Status);
        Assert.Equal("health.check.customSourceWorking", Row()?.MessageKey);
        Assert.Equal(CustomSourceHealthJob.Link("custom-quietpen"), Row()?.Url);
        Assert.Single(_pages.Requested);
    }

    [Fact]
    public async Task A_layout_change_reads_as_not_working()
    {
        _pages.Answer("/series/blue/", "<html><body><p>Redesigned</p></body></html>");

        await RunAsync();

        Assert.Equal("error", Row()?.Status);
        Assert.Equal("health.check.customSourceFailing", Row()?.MessageKey);
    }

    [Fact]
    public async Task A_removed_sample_with_another_working_series_only_asks_for_a_new_sample()
    {
        _pages.Missing("/series/blue/");
        _pages.Answer("/series/red/", Listing);
        LinkOtherSeries("red");

        await RunAsync();

        Assert.Equal("warning", Row()?.Status);
        Assert.Equal("health.check.customSourceSample", Row()?.MessageKey);
    }

    [Fact]
    public async Task A_source_switched_off_loses_its_row()
    {
        _pages.Answer("/series/blue/", Listing);
        await RunAsync();
        Assert.NotNull(Row());

        await _world.Settings.SetAsync(SettingKeys.SourcesDisabled, "custom-quietpen");
        await RunAsync();

        Assert.Null(Row());
    }

    private sealed class PageFetcher : IHtmlFetcher
    {
        private readonly Dictionary<string, string?> _bodies = [];

        public List<string> Requested { get; } = [];

        public void Answer(string path, string body) => _bodies[path] = body;

        public void Missing(string path) => _bodies[path] = null;

        public Task<string> GetHtmlAsync(string url, CancellationToken ct = default) => FetchAsync(new HtmlFetchRequest(url), ct);

        public Task<string> FetchAsync(HtmlFetchRequest request, CancellationToken ct = default)
        {
            Requested.Add(request.Url);
            var path = new Uri(request.Url).AbsolutePath;
            return _bodies.TryGetValue(path, out var body) && body is not null
                ? Task.FromResult(body)
                : throw new HttpRequestException("Not found", null, HttpStatusCode.NotFound);
        }
    }
}
