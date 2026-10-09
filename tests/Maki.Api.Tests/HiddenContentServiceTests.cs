using Maki.Api.Services;
using Maki.Core.Entities;
using Maki.Metadata.MangaBaka;
using Xunit;

namespace Maki.Api.Tests;

/// <summary>
/// The shared post-filter behind the series page's Similar and Related rails and the Discover rows:
/// anything the never-show list matches is dropped, and nothing else is.
/// </summary>
public class HiddenContentServiceTests
{
    private static MangaBakaRecommendation Pick(string providerId) =>
        new(providerId, "Title", null, null, null, SeriesStatus.Completed, 80, null, [], [], false, null, null);

    [Fact]
    public void Hidden_picks_are_dropped_and_the_rest_kept_in_order()
    {
        IReadOnlyList<MangaBakaRecommendation> picks = [Pick("1"), Pick("2"), Pick("3")];

        var kept = HiddenContentService.Without(picks, id => id == 2);

        Assert.Equal(["1", "3"], kept.Select(x => x.ProviderId));
    }

    [Fact]
    public void Without_a_predicate_the_list_is_untouched()
    {
        IReadOnlyList<MangaBakaRecommendation> picks = [Pick("1"), Pick("2")];

        Assert.Same(picks, HiddenContentService.Without(picks, null));
    }

    [Fact]
    public void A_pick_with_a_non_numeric_provider_id_is_never_dropped()
    {
        var kept = HiddenContentService.Without([Pick("abc")], _ => true);

        Assert.Single(kept);
    }
}
