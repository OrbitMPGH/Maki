using Maki.Core.Sources;
using Maki.Sources.MangaDex;

namespace Maki.Sources.Tests;

/// <summary>Which upload MangaDex lists for a chapter several groups released.</summary>
public class MangaDexGroupTests
{
    private static MangaDexSource Source() => new(new FakeHttpClientFactory(new()
    {
        ["feed"] = FakeHttpClientFactory.Fixture("mangadex-feed-groups.json")
    }));

    private static ScanlationGroupPreference Groups(string[]? preferred = null, string[]? blocked = null) =>
        ScanlationGroupPreference.Of(preferred, blocked);

    [Fact]
    public async Task With_no_preference_the_earliest_upload_is_listed()
    {
        var chapters = await Source().ListChaptersAsync("series");

        Assert.Equal(["c1-early", "c2-early", "c3-early"], chapters.Select(c => c.SourceChapterId));
    }

    [Fact]
    public async Task Uploads_keep_every_group_copy()
    {
        var uploads = await Source().ListUploadsAsync("series");

        Assert.Equal(5, uploads.Count);
        Assert.Contains(uploads, u => u.Group == "Moonlit TL, Paper Lantern");
    }

    [Fact]
    public async Task The_first_preferred_group_with_an_upload_wins()
    {
        var uploads = await Source().ListUploadsAsync("series");

        var chapters = ScanlationGroupChoice.Pick(uploads, Groups(preferred: ["Paper Lantern", "Moonlit TL"]));

        Assert.Equal(["c1-moon", "c2-early", "c3-joint"], chapters.Select(c => c.SourceChapterId));
    }

    [Fact]
    public async Task A_chapter_only_a_blocked_group_released_is_left_out_and_reported_as_withheld()
    {
        var uploads = await Source().ListUploadsAsync("series");
        var groups = Groups(blocked: ["early birds"]);

        var chapters = ScanlationGroupChoice.Pick(uploads, groups);
        var withheld = ScanlationGroupChoice.Withheld(uploads, groups);

        Assert.Equal(["c1-moon", "c3-joint"], chapters.Select(c => c.SourceChapterId));
        Assert.Equal([2m], withheld.Select(c => c.Number));
    }

    [Fact]
    public async Task Blocking_one_member_of_a_joint_release_blocks_the_release()
    {
        var uploads = await Source().ListUploadsAsync("series");

        var chapters = ScanlationGroupChoice.Pick(uploads, Groups(preferred: ["Moonlit TL"], blocked: ["Paper Lantern"]));

        Assert.Equal(["c1-moon", "c2-early", "c3-early"], chapters.Select(c => c.SourceChapterId));
    }

    [Fact]
    public async Task Seen_lists_each_group_once_with_its_chapter_count()
    {
        var seen = ScanlationGroupChoice.Seen(await Source().ListUploadsAsync("series"));

        Assert.Equal([("Early Birds", 3), ("Moonlit TL", 2), ("Paper Lantern", 1)], seen);
    }
}
