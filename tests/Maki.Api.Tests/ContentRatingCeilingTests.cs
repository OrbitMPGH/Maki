using Maki.Metadata.MangaBaka;

namespace Maki.Api.Tests;

/// <summary>
/// The single-series ceiling check the detail card, its reviews and the chapter preview share. It
/// has to agree with the list queries, whose <c>content_rating IN (...)</c> never matches a null.
/// </summary>
public class ContentRatingCeilingTests
{
    [Theory]
    [InlineData(ContentRating.Safe)]
    [InlineData(ContentRating.Suggestive)]
    [InlineData(ContentRating.Erotica)]
    public void An_unrated_series_is_refused_under_any_restricted_ceiling(string ceiling) =>
        Assert.False(ContentRating.Permits(null, ceiling));

    [Fact]
    public void An_unrated_series_is_shown_when_the_ceiling_allows_everything() =>
        Assert.True(ContentRating.Permits(null, ContentRating.Pornographic));

    [Theory]
    [InlineData(ContentRating.Safe, ContentRating.Safe, true)]
    [InlineData(ContentRating.Suggestive, ContentRating.Safe, false)]
    [InlineData(ContentRating.Erotica, ContentRating.Erotica, true)]
    [InlineData(ContentRating.Pornographic, ContentRating.Erotica, false)]
    [InlineData(ContentRating.Suggestive, null, false)]
    public void A_rated_series_follows_the_ceiling(string rating, string? ceiling, bool expected) =>
        Assert.Equal(expected, ContentRating.Permits(rating, ceiling));
}
