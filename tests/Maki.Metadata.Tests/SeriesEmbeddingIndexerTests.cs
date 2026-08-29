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

    [Fact]
    public void ParseTags_ReadsIdNameClassSpoilerCountAndCategory()
    {
        var tags = SeriesEmbeddingIndexer.ParseTags(TagsV2);
        Assert.Equal(5, tags.Count);
        Assert.Equal(
            new SeriesEmbeddingIndexer.ParsedTag(1, "Time Travel", TagMath.Core, false, 800, "Themes"),
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
