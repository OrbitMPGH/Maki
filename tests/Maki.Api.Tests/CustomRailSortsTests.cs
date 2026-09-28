using Maki.Api.Services;
using Maki.Core.Configuration;

namespace Maki.Api.Tests;

/// <summary>
/// The catalogue rail sorts are passed straight through to <see cref="DiscoverService.GetFeedAsync"/>
/// as a <see cref="BrowseSort"/> value, so the two vocabularies have to agree exactly.
/// </summary>
public class CustomRailSortsTests
{
    [Fact]
    public void Catalogue_sorts_are_exactly_the_BrowseSort_constants()
    {
        Assert.Equal(
            [BrowseSort.Popular, BrowseSort.Rating, BrowseSort.Newest, BrowseSort.Oldest],
            CustomRailSorts.For(CustomRailSources.Catalogue));
    }
}
