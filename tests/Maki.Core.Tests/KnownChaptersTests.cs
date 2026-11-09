using Maki.Core.Sources;

namespace Maki.Core.Tests;

public class KnownChaptersTests
{
    private static SourceChapter Listed(string id, decimal? number, string? title = null) =>
        new("src", "s", id, number?.ToString(), number, null, title, "en", null);

    [Fact]
    public void A_reupload_of_a_numbered_chapter_is_known_by_its_number()
    {
        var known = new KnownChapters(["c1"], [(1m, null, "en")]);

        Assert.True(known.Knows(Listed("c1-reupload", 1m)));
    }

    [Fact]
    public void A_new_unnumbered_extra_is_not_known_because_another_extra_is()
    {
        var known = new KnownChapters(["extra-1"], [(null, null, "en")]);

        Assert.True(known.Knows(Listed("extra-1", null, "Extra")));
        Assert.False(known.Knows(Listed("extra-2", null, "Side story")));
        Assert.False(known.CoversPage([Listed("extra-2", null, "Side story")]));
    }
}
