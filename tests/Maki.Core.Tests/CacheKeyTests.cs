using Maki.Core.Recommendations;

namespace Maki.Core.Tests;

public class CacheKeyTests
{
    [Fact]
    public void CreditNamesContainingTheKeyDelimitersDoNotCollide()
    {
        var one = CatalogueCredits.Key([new CatalogueCredit("A,author/B", "author")]);
        var two = CatalogueCredits.Key([new CatalogueCredit("A", "author"), new CatalogueCredit("B", "author")]);

        Assert.NotEqual(one, two);
    }

    [Fact]
    public void TermNamesContainingTheKeyDelimitersDoNotCollide()
    {
        var one = CatalogueRules.TermsKey([new CatalogueTerm("tag", "a,tag/b")]);
        var two = CatalogueRules.TermsKey([new CatalogueTerm("tag", "a"), new CatalogueTerm("tag", "b")]);

        Assert.NotEqual(one, two);
    }

    [Fact]
    public void ListedNamesWithADotAreKeptApart()
    {
        Assert.NotEqual(KeyPart.List(["a.b"]), KeyPart.List(["a", "b"]));
    }
}
