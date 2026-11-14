using Maki.Api.Controllers;
using Maki.Api.Services;
using Maki.Core.Entities;
using Maki.Core.Images;
using Maki.Core.Quality;
using Microsoft.AspNetCore.Mvc;

namespace Maki.Api.Tests;

public sealed partial class PageConversionPreviewTests
{
    private static PageConversionController.ScopeRequest PlanWithAggregatorRule(string action = "convert") =>
        new("webp", "balanced", false,
            [new PageConversionController.RuleDto(null, "Scraped", new PageConversionConditions(Tiers: ["aggregator"]), action,
                action == "convert" ? "high" : null)]);

    private async Task<IReadOnlyList<PageConversionSample>> RowSamples(PageConversionController.ScopeRequest plan, int row,
        string kind)
    {
        var result = Assert.IsType<OkObjectResult>(
            await Controller().RowSamples(new PageConversionController.RowSamplesRequest(plan, row, kind), CancellationToken.None));
        return Assert.IsType<PageConversionController.SamplesDto>(result.Value).Samples;
    }

    [Fact]
    public async Task Row_samples_come_from_the_files_that_row_converts_and_are_of_the_kind_asked_for()
    {
        var manga = AddFile(_world.SeriesId, "Series", 1, QualityTier.Scanlator, Grey);
        var manhwaId = AddSeries("Strip", SeriesTypes.Manhwa);
        var scraped = AddFile(manhwaId, "Strip", 1, QualityTier.Aggregator, Colour);
        AddFile(AddSeries("Scraped manga", SeriesTypes.Manga), "Scraped manga", 1, QualityTier.Aggregator, Grey);
        var plan = PlanWithAggregatorRule();

        var ruleColour = await RowSamples(plan, 0, "colour");
        var ruleGrey = await RowSamples(plan, 0, "greyscale");
        var restGrey = await RowSamples(plan, -1, "greyscale");
        var restColour = await RowSamples(plan, -1, "colour");

        Assert.Equal(scraped.FileId, Assert.Single(ruleColour).FileId);
        Assert.Equal("colour", ruleColour[0].Reason);
        Assert.Equal("Scraped manga", Assert.Single(ruleGrey).SeriesTitle);
        Assert.Equal(manga.FileId, Assert.Single(restGrey).FileId);
        Assert.Equal("greyscale", restGrey[0].Reason);
        Assert.Empty(restColour);
    }

    [Fact]
    public async Task A_skip_rule_has_no_row_samples_and_bad_requests_are_refused()
    {
        AddFile(AddSeries("Strip", SeriesTypes.Manhwa), "Strip", 1, QualityTier.Aggregator, Colour);

        Assert.Empty(await RowSamples(PlanWithAggregatorRule("skip"), 0, "colour"));

        Assert.IsType<BadRequestObjectResult>(await Controller().RowSamples(
            new PageConversionController.RowSamplesRequest(PlanWithAggregatorRule(), 0, "sepia"), CancellationToken.None));
        Assert.IsType<BadRequestObjectResult>(await Controller().RowSamples(
            new PageConversionController.RowSamplesRequest(PlanWithAggregatorRule(), 1, "colour"), CancellationToken.None));
        Assert.IsType<BadRequestObjectResult>(await Controller().RowSamples(
            new PageConversionController.RowSamplesRequest(null, -1, "colour"), CancellationToken.None));
        Assert.IsType<BadRequestObjectResult>(await Controller().RowSamples(
            new PageConversionController.RowSamplesRequest(PlanWithAggregatorRule() with { Stop = "best" }, -1, "colour"),
            CancellationToken.None));
    }
}
