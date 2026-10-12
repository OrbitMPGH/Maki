using Maki.Api.Controllers;
using Maki.Api.Services;
using Maki.Core.Configuration;
using Maki.Core.Metadata;
using Maki.Core.Sources;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;

namespace Maki.Api.Tests;

public class SearchControllerSourceSwitchTests
{
    private static SearchController Build(bool disabled)
    {
        var registry = new SourceRegistry(
        [
            new FakeSource
            {
                Name = "src",
                OnResolveUrl = url => url.Host == "src.test" ? "abc" : null,
            }
        ]);
        var settings = new FakeAppSettings();
        if (disabled)
        {
            settings.Set(SettingKeys.SourcesDisabled, "src");
        }

        return new SearchController(
            new TestLocalizer(), Array.Empty<IMetadataProvider>(), registry,
            new SourceAvailability(settings, registry), new TestCurrentUser(1), null!, settings,
            NullLogger<SearchController>.Instance);
    }

    private static string Code(IActionResult result)
    {
        var value = ((ObjectResult)result).Value!;
        return (string)value.GetType().GetProperty("code")!.GetValue(value)!;
    }

    [Fact]
    public async Task A_pasted_url_from_a_switched_off_source_says_the_source_is_disabled()
    {
        var result = await Build(disabled: true).ResolveSource("https://src.test/series/abc", default);

        Assert.Equal("error.sourceMapping.sourceDisabled", Code(result));
    }

    [Fact]
    public async Task A_url_no_source_recognises_is_still_not_recognised()
    {
        var result = await Build(disabled: true).ResolveSource("https://other.test/series/abc", default);

        Assert.Equal("error.search.noSourceRecognizesUrl", Code(result));
    }

    [Fact]
    public async Task Searching_a_switched_off_source_is_refused()
    {
        var result = await Build(disabled: true).SearchSource("src", "berserk", default);

        Assert.Equal("error.sourceMapping.sourceDisabled", Code(result));
    }

    [Theory]
    [InlineData("suwayomi-100", "/api/v1/suwayomi/thumbnail/100/7", true)]
    [InlineData("src", "/api/v1/suwayomi/thumbnail/100/7", false)]
    [InlineData("suwayomi-100", "/api/v1/suwayomi/thumbnail/../../series/1/cover", false)]
    [InlineData("suwayomi-100", "/api/v1/suwayomi/thumbnail/100/7?x=1", false)]
    [InlineData("suwayomi-100", "https://cdn.src.test/b.jpg", false)]
    public async Task Only_a_suwayomi_sources_exact_relay_path_skips_the_cover_proxy(
        string sourceName, string cover, bool passesThrough)
    {
        var registry = new SourceRegistry(
        [
            new FakeSource
            {
                Name = sourceName,
                OnSearch = _ => [new SourceSeriesResult("a", "A", "https://src.test/a", cover)]
            }
        ]);
        var settings = new FakeAppSettings();
        var controller = new SearchController(
            new TestLocalizer(), Array.Empty<IMetadataProvider>(), registry,
            new SourceAvailability(settings, registry), new TestCurrentUser(1), null!, settings,
            NullLogger<SearchController>.Instance);

        var result = Assert.Single(Assert.IsAssignableFrom<IEnumerable<SourceSeriesResult>>(
            Assert.IsType<OkObjectResult>(await controller.SearchSource(sourceName, "x", default)).Value));

        if (passesThrough)
        {
            Assert.Equal(cover, result.CoverUrl);
        }
        else
        {
            Assert.StartsWith($"/api/v1/search/cover?sourceName={sourceName}&url=", result.CoverUrl);
        }
    }
}
