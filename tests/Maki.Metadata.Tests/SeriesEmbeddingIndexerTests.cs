using Maki.Metadata.Embedding;
using Xunit;

namespace Maki.Metadata.Tests;

public class SeriesEmbeddingIndexerTests
{
    private const string TagsV2 = """
        [
          {"id": 1, "name": "Time Travel", "weight": "core", "is_spoiler": false, "series_count": 800,
           "name_path": "Themes > Time Travel"},
          {"id": 2, "name": "Dead Friends", "weight": "defining", "is_spoiler": true, "series_count": 300,
           "name_path": "Narrative Tropes > Death > Dead Friends"},
          {"id": 3, "name": "School", "weight": "incidental", "is_spoiler": false, "series_count": 40000,
           "name_path": "Settings > School"},
          {"id": 4, "name": "Magic", "weight": "defining", "is_spoiler": false, "series_count": 9000,
           "name_path": "Themes"},
          {"id": 5, "name": "Webtoons", "is_spoiler": false, "series_count": 20000}
        ]
        """;

    private const string FacetTagsV2 = """
        [
          {"id": 10, "name": "Shounen", "weight": "defining", "is_spoiler": false, "series_count": 29237,
           "name_path": "Audience Demographics > Male Oriented > Shounen"},
          {"id": 11, "name": "Longstrip", "weight": "core", "is_spoiler": false, "series_count": 30000,
           "name_path": "Work Info > Page Layout > Longstrip"},
          {"id": 12, "name": "Webtoon", "weight": "core", "is_spoiler": false, "series_count": 25000,
           "name_path": "Work Info > Publication Medium > Webtoon"},
          {"id": 13, "name": "Revenge", "weight": "core", "is_spoiler": false, "series_count": 4000,
           "name_path": "Themes > Revenge"},
          {"id": 14, "name": "Cohabitation", "weight": "core", "is_spoiler": false, "series_count": 900,
           "name_path": "Themes > Cohabitation"},
          {"id": 15, "name": "School", "weight": "incidental", "is_spoiler": false, "series_count": 40000,
           "name_path": "Settings > School"},
          {"id": 16, "name": "Dead Sibling", "weight": "core", "is_spoiler": true, "series_count": 120,
           "name_path": "Themes > Death > Dead Sibling"},
          {"id": 17, "name": "Tsundere", "weight": "core", "is_spoiler": false, "series_count": 5000,
           "name_path": "Character Archetype > Dere Types > Tsundere"}
        ]
        """;

    private const string Publishers = """
        [{"name": "Yen Press", "type": "English"},
         {"name": "Naver", "type": "Original"},
         {"name": "Shueisha", "type": "Original"},
         {"name": "KADOKAWA", "type": "Original"}]
        """;

    [Fact]
    public void BuildFacets_NamesDemographicFormatHouseAndPremiseTags()
    {
        var facets = SeriesEmbeddingIndexer.BuildFacets(
            SeriesEmbeddingIndexer.ParseTags(FacetTagsV2), Publishers);

        Assert.Equal(
            "Shounen. Longstrip, Webtoon. Published by Naver and Shueisha. Cohabitation, Revenge.",
            facets);
    }

    [Fact]
    public void BuildFacets_ExcludesSpoilerCastAndIncidentalTags()
    {
        var facets = SeriesEmbeddingIndexer.BuildFacets(
            SeriesEmbeddingIndexer.ParseTags(FacetTagsV2), Publishers);

        // Spoiler: excluded everywhere, and a facet clause is no exception.
        Assert.DoesNotContain("Dead Sibling", facets);
        // Cast, not premise. Same StoryCategories definition the tag channel scores with.
        Assert.DoesNotContain("Tsundere", facets);
        // Below Defining: the tail of a tag list is trope noise and this clause is short on purpose.
        Assert.DoesNotContain("School", facets);
        // An English licensor is a fact about a market, not about the work.
        Assert.DoesNotContain("Yen Press", facets);
    }

    [Fact]
    public void BuildFacets_IsEmptyWhenNothingIsKnown()
    {
        Assert.Equal(string.Empty, SeriesEmbeddingIndexer.BuildFacets([], null));
        Assert.Equal(string.Empty, SeriesEmbeddingIndexer.BuildFacets([], "not json"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("{}")]
    [InlineData("[{\"name\": \"Yen Press\", \"type\": \"English\"}]")]
    public void OriginalPublishers_IsEmptyWithoutAnOriginalHouse(string? json) =>
        Assert.Empty(SeriesEmbeddingIndexer.OriginalPublishers(json));

    [Fact]
    public void OriginalPublishers_DeduplicatesAndCapsAtTwo()
    {
        var names = SeriesEmbeddingIndexer.OriginalPublishers(
            """
            [{"name": "Naver", "type": "Original"}, {"name": "naver", "type": "Original"},
             {"name": "Shueisha", "type": "Original"}, {"name": "KADOKAWA", "type": "Original"}]
            """);

        Assert.Equal(["Naver", "Shueisha"], names);
    }

    [Fact]
    public void BuildText_PutsFacetsBetweenTitleAndDescription()
    {
        // The title keeps the lead position (MangaBaka titles are often descriptive) and truncation
        // still eats the tail of the plot rather than the facets.
        Assert.Equal(
            "Solo Leveling. Shounen. A hunter levels up.",
            SeriesEmbeddingIndexer.BuildText("Solo Leveling", "A hunter levels up.", "Shounen."));

        // Absent facets have to reproduce the old text byte for byte, or every stored hash moves.
        Assert.Equal(
            "Solo Leveling. A hunter levels up.",
            SeriesEmbeddingIndexer.BuildText("Solo Leveling", "A hunter levels up."));
        Assert.Equal(
            "A hunter levels up.",
            SeriesEmbeddingIndexer.BuildText(null, "A hunter levels up."));
    }

    [Fact]
    public void ParseTags_ReadsIdNameClassSpoilerCountAndCategory()
    {
        var tags = SeriesEmbeddingIndexer.ParseTags(TagsV2);
        Assert.Equal(5, tags.Count);
        Assert.Equal(
            new SeriesEmbeddingIndexer.ParsedTag(
                1, "Time Travel", TagMath.Core, false, 800, "Themes", "Themes > Time Travel"),
            tags[0]);
        Assert.True(tags[1].IsSpoiler);
        Assert.Equal(TagMath.Unweighted, tags[4].Class); // no weight field
    }

    [Fact]
    public void ParseTags_TakesTheCategoryFromTheRootOfNamePath()
    {
        var tags = SeriesEmbeddingIndexer.ParseTags(TagsV2);

        // Only the root separates what a tag is ABOUT; the deeper segments narrow within that kind.
        Assert.Equal("Narrative Tropes", tags[1].Category);
        Assert.Equal("Settings", tags[2].Category);
        // A path with no separator is itself the root.
        Assert.Equal("Themes", tags[3].Category);
        // And a tag the dump gives no path for is uncategorised rather than guessed at, which
        // CategoryWeight then treats as neutral.
        Assert.Equal(string.Empty, tags[4].Category);
    }

    [Fact]
    public void ParseTags_KeepsTheWholeNamePathAndNotOnlyItsRoot()
    {
        var tags = SeriesEmbeddingIndexer.ParseTags(TagsV2);

        // The category is the root; the path is what TagMath.TagTree needs to know that Dead
        // Friends sits under Death, which is the part that used to be read and discarded.
        Assert.Equal("Narrative Tropes > Death > Dead Friends", tags[1].NamePath);
        Assert.Equal("Themes", tags[3].NamePath);
        // No path in the dump means no path stored, which the tree reads as "no ancestors".
        Assert.Equal(string.Empty, tags[4].NamePath);
    }

    [Theory]
    [InlineData("Themes > Marriage > Arranged Marriage", "Themes")]
    [InlineData("Character Traits > Attractiveness > Beautiful Female Lead", "Character Traits")]
    [InlineData("Themes", "Themes")]
    [InlineData("", "")]
    public void RootOf_TakesTheFirstSegment(string path, string expected) =>
        Assert.Equal(expected, SeriesEmbeddingIndexer.RootOf(path));

    [Fact]
    public void ParseTags_BadOrEmptyJson_IsEmpty()
    {
        Assert.Empty(SeriesEmbeddingIndexer.ParseTags(null));
        Assert.Empty(SeriesEmbeddingIndexer.ParseTags(""));
        Assert.Empty(SeriesEmbeddingIndexer.ParseTags("not json"));
        Assert.Empty(SeriesEmbeddingIndexer.ParseTags("{\"a\":1}")); // not an array
    }

    [Fact]
    public void BuildText_IsTitleThenDescription()
    {
        // Just title + description — genres/themes were measured to dilute retrieval and were dropped.
        var text = SeriesEmbeddingIndexer.BuildText("Steins;Gate", "A microwave sends texts to the past.");
        Assert.Equal("Steins;Gate. A microwave sends texts to the past.", text);
    }

    [Fact]
    public void BuildText_NoTitle_IsJustDescription()
    {
        Assert.Equal("Desc.", SeriesEmbeddingIndexer.BuildText(null, "Desc."));
        Assert.Equal("Desc.", SeriesEmbeddingIndexer.BuildText("  ", "Desc."));
    }

    [Theory]
    [InlineData("<p>From Kodansha:</p> giant <br>humanoids", "From Kodansha: giant humanoids")]
    [InlineData("plain text", "plain text")]
    [InlineData(null, null)]
    public void CleanHtml_StripsTagsAndCollapsesWhitespace(string? input, string? expected) =>
        Assert.Equal(expected, SeriesEmbeddingIndexer.CleanHtml(input));
}
