using System.Text.Json;
using Maki.Sources.Common;

namespace Maki.Sources.Tests;

public class JsonReadTests
{
    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement.Clone();

    [Theory]
    [InlineData("\"12\"", "12")]
    [InlineData("12", "12")]
    [InlineData("12.5", "12.5")]
    [InlineData("true", null)]
    [InlineData("null", null)]
    public void Text_reads_strings_and_numbers(string json, string? expected)
    {
        Assert.Equal(expected, JsonRead.Text(Parse(json)));
    }

    [Theory]
    [InlineData("3", 3)]
    [InlineData("3.0", 3)]
    [InlineData("\"4\"", 4)]
    [InlineData("\"x\"", null)]
    [InlineData("null", null)]
    [InlineData("1e20", null)]
    public void Int_tolerates_floats_and_numeric_strings(string json, int? expected)
    {
        Assert.Equal(expected, JsonRead.Int(Parse(json)));
    }

    [Fact]
    public async Task A_search_hit_with_a_string_id_and_a_missing_id_does_not_fail_the_search()
    {
        var source = new Maki.Sources.CuuTruyen.CuuTruyenSource(
            new FakeHtmlFetcher(new()
            {
                ["search"] = """{"data":[{"id":"7","name":"A"},{"name":"no id"},{"id":8,"name":"B"}]}"""
            }),
            new FakeHttpClientFactory([]));

        var results = await source.SearchAsync("a");

        Assert.Equal(["7", "8"], results.Select(r => r.SourceSeriesId));
    }

    [Theory]
    [InlineData("{\"id\":\"a\"}", "id", "a")]
    [InlineData("{\"id\":7}", "id", "7")]
    [InlineData("{\"id\":null}", "id", null)]
    [InlineData("{}", "id", null)]
    [InlineData("[1]", "id", null)]
    public void Property_reads_a_field_of_an_object_and_tolerates_anything_else(string json, string name, string? expected)
    {
        Assert.Equal(expected, JsonRead.Property(Parse(json), name));
    }

    [Theory]
    [InlineData("<p>Hello <b>there</b></p>", "Hello there")]
    [InlineData("   ", null)]
    [InlineData(null, null)]
    public void Plain_strips_markup_and_drops_blank_input(string? html, string? expected)
    {
        Assert.Equal(expected, BodyText.Plain(html));
    }

    [Fact]
    public void Snippet_cuts_a_long_body_to_100_characters()
    {
        Assert.Equal(100, BodyText.Snippet(new string('x', 300)).Length);
        Assert.Equal("short", BodyText.Snippet("short"));
    }
}
