using Maki.Api.Services;

namespace Maki.Api.Tests;

/// <summary>
/// The shape of the plain text pulled out of MAL's reviews page. MAL renders review bodies through
/// nl2br and pretty-prints the surrounding markup, so the raw HTML carries far more whitespace than
/// the author typed; these pin down that the reader sees the author's spacing, not MAL's.
/// </summary>
public class MalReviewParsingTests
{
    /// <summary>Wraps a review body in enough of MAL's markup for <c>ParseReviews</c> to find it.</summary>
    private static string Page(string body) =>
        $$"""
          <div class="review-element js-review-element" data-id="1">
            <div class="username"><a href="/profile/tester" class="review-manga-reviewer">tester</a></div>
            <div class="update_at">Jan 1, 2020</div>
            <a href="https://myanimelist.net/reviews.php?id=12345">more</a>
            <div class="tag recommended">Recommended</div>
            <div class="text">{{body}}</div>
            <div class="rating mt8 mb8">Rating: <span class="num">9</span></div>
          </div>
          """;

    private static string TextOf(string body) => MalReviewClient.ParseReviews(Page(body)).Single().Text;

    [Fact]
    public void Single_line_break_stays_a_single_line_break()
    {
        // nl2br emits the tag *and* keeps the newline the author typed. Converting only the tag
        // used to yield two newlines, turning every soft break into a paragraph gap.
        var text = TextOf("Story - 9<br />\nArt - 8<br />\nOverall - 9");

        Assert.Equal("Story - 9\nArt - 8\nOverall - 9", text);
    }

    [Fact]
    public void Blank_line_between_paragraphs_survives()
    {
        var text = TextOf("First paragraph.<br />\n<br />\nSecond paragraph.");

        Assert.Equal("First paragraph.\n\nSecond paragraph.", text);
    }

    [Fact]
    public void Runs_of_blank_lines_collapse_to_one()
    {
        var text = TextOf("Top.<br />\n<br />\n<br />\n<br />\nBottom.");

        Assert.Equal("Top.\n\nBottom.", text);
    }

    [Fact]
    public void Read_more_seam_rejoins_the_sentence_it_cut()
    {
        // MAL truncates long reviews mid-sentence and hides the remainder behind a "..." toggle,
        // with pretty-printed whitespace on both sides of the seam.
        const string body = "But by\n                  <span class=\"js-visible\" style=\"margin-left: -4px;\">...</span>\n" +
                            "          <span class=\"js-hidden\" style=\"display: none;\">then the rhythm had changed.";

        Assert.Equal("But by then the rhythm had changed.", TextOf(body));
    }

    [Fact]
    public void Read_more_seam_at_a_paragraph_boundary_keeps_the_blank_line()
    {
        const string body = "End of one.<br />\n<br />\n                  <span class=\"js-visible\" style=\"margin-left: -4px;\">...</span>\n" +
                            "          <span class=\"js-hidden\" style=\"display: none;\">Start of two.";

        Assert.Equal("End of one.\n\nStart of two.", TextOf(body));
    }

    [Fact]
    public void Markup_indentation_never_reaches_the_text()
    {
        var text = TextOf("\n                Story - 9.38<br />\n<br />\nIt is good.\n            ");

        Assert.Equal("Story - 9.38\n\nIt is good.", text);
    }

    [Fact]
    public void Double_encoded_entities_decode_all_the_way()
    {
        // Older reviews were stored already-encoded, so MAL serves "&amp;quot;" and one decode
        // leaves a literal "&quot;" in the prose.
        Assert.Equal("I said \"hello\" & left.", TextOf("I said &amp;quot;hello&amp;quot; &amp;amp; left."));
    }

    [Fact]
    public void Single_encoded_entities_are_not_decoded_twice()
    {
        Assert.Equal("it's a 5 < 6 case", TextOf("it&#039;s a 5 &lt; 6 case"));
    }
}
