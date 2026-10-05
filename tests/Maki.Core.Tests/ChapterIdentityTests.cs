using Maki.Core.Entities;
using Maki.Core.Sources;

namespace Maki.Core.Tests;

public class ChapterIdentityTests
{
    private static SourceChapter Unnumbered(string label) =>
        new("test", "series-1", label, label, null, null, null, "en", null);

    [Fact]
    public void Untitled_Unnumbered_Chapter_Matches_On_Its_Label()
    {
        var special = new Chapter { Number = null, IsOneShot = true, Title = "Special", Language = "en" };

        Assert.True(ChapterIdentity.Matches(special, Unnumbered("special")));
        Assert.False(ChapterIdentity.Matches(special, Unnumbered("Extra")));
    }

    [Fact]
    public void Labelled_Leaves_Numbered_And_Titled_Chapters_Alone()
    {
        var numbered = Unnumbered("Chapter 3") with { Number = 3m };
        var titled = Unnumbered("Extra") with { Title = "Afterword" };

        Assert.Null(ChapterIdentity.Labelled(numbered).Title);
        Assert.Equal("Afterword", ChapterIdentity.Labelled(titled).Title);
        Assert.Equal("Extra", ChapterIdentity.Labelled(Unnumbered(" Extra ")).Title);
    }
}
