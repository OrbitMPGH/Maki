using Jeffijoe.MessageFormat;
using Maki.Api.Controllers;
using Maki.Api.Localization;
using Maki.Api.Services;
using Maki.Core.Localization;
using Microsoft.Extensions.Logging.Abstractions;

namespace Maki.Api.Tests;

/// <summary>
/// The rail subtitles name series titles, which the client joins with <c>Intl.ListFormat</c>. The
/// rendered message has to keep the marker and the controller has to hand over the titles unjoined.
/// </summary>
public class RailSubtitleTests
{
    private static readonly Localizer Localizer = new(
        new MessageCatalog(
            new ServerCatalogs(NullLogger<ServerCatalogs>.Instance),
            new MessageFormatter(),
            NullLogger<MessageCatalog>.Instance),
        new FixedLocale(SupportedLanguages.Default));

    [Fact]
    public void The_recent_activity_subtitle_keeps_the_list_marker()
    {
        Assert.Equal("Because you read {list}", Localizer.Get("discover.rail.becauseYouRead", new { list = "{list}" }));
    }

    [Fact]
    public void The_side_interest_subtitle_keeps_the_titles_marker()
    {
        var text = Localizer.Get("discover.rail.sideInterestSubtitle", new { count = 4, titles = "{titles}" });

        Assert.Contains("{titles}", text);
        Assert.Contains("4 titles", text);
    }

    [Fact]
    public void Unnamed_seeds_become_a_localized_last_entry()
    {
        var rail = new DiscoverRail("k", "t", "f", null, [], SubtitleTitles: ["A", "B", "C"], SubtitleMore: 3);

        Assert.Equal(["A", "B", "C", "3 more"], RecommendationController.SubtitleTitlesFor(rail, Localizer));
    }

    [Fact]
    public void Titles_are_passed_through_when_every_seed_is_named()
    {
        var rail = new DiscoverRail("k", "t", "f", null, [], SubtitleTitles: ["A"]);

        Assert.Equal(["A"], RecommendationController.SubtitleTitlesFor(rail, Localizer));
        Assert.Null(RecommendationController.SubtitleTitlesFor(rail with { SubtitleTitles = null }, Localizer));
    }

    private sealed class FixedLocale(string locale) : IRequestLocale
    {
        public string Locale { get; } = locale;
    }
}
