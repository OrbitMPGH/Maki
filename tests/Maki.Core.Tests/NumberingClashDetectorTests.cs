using Maki.Core.Sources;

namespace Maki.Core.Tests;

public class NumberingClashDetectorTests
{
    private static IReadOnlyDictionary<string, IReadOnlyCollection<decimal?>> Sources(
        params (string Name, decimal?[] Numbers)[] sources) =>
        sources.ToDictionary(s => s.Name, s => (IReadOnlyCollection<decimal?>)s.Numbers);

    [Fact]
    public void SubChaptersVsWholeChapters()
    {
        // The "At Home with a Girl in Her Cute Pajamas" case: MangaDex has 1.1/1.2-style
        // sub-chapters, WeebCentral covers the same range with integers.
        var clash = NumberingClashDetector.Detect(Sources(
            ("mangadex", [1.1m, 1.2m, 2.1m, 2.2m, 3.1m, 3.2m, 4.1m]),
            ("weebcentral", [1m, 2m, 3m, 4m, 5m])));

        Assert.NotNull(clash);
        Assert.Equal("mangadex", clash.SubChapterSource);
        Assert.Equal("weebcentral", clash.WholeChapterSource);
    }

    [Fact]
    public void OmakeDecimalsAreNotAClash()
    {
        // 10.5-style specials legitimately coexist with whole chapters everywhere.
        Assert.Null(NumberingClashDetector.Detect(Sources(
            ("mangadex", [9m, 9.5m, 10m, 10.5m, 11m, 11.5m, 12m]),
            ("weebcentral", [9m, 10m, 11m, 12m]))));
    }

    [Fact]
    public void AgreeingSourcesAreNotAClash()
    {
        Assert.Null(NumberingClashDetector.Detect(Sources(
            ("mangadex", [1m, 2m, 3m, 4m]),
            ("weebcentral", [1m, 2m, 3m, 4m, 5m]))));
    }

    [Fact]
    public void SubChaptersAlongsideOwnWholesAreNotAClash()
    {
        // A source with both 32 and 32.1 is publishing extras, not a different scheme.
        Assert.Null(NumberingClashDetector.Detect(Sources(
            ("mangadex", [31m, 31.1m, 32m, 32.1m, 33m, 33.1m]),
            ("weebcentral", [31m, 32m, 33m]))));
    }

    [Fact]
    public void RealWorldShape()
    {
        // The actual "Cute Pajamas" lists as synced: 6 sub-chapters over just two
        // integer parts on MangaDex, 1..14 whole on WeebCentral.
        var clash = NumberingClashDetector.Detect(Sources(
            ("mangadex", [1.1m, 1.2m, 2.1m, 2.2m, 2.3m, 2.4m]),
            ("weebcentral", [1m, 2m, 3m, 4m, 5m, 6m, 7m, 8m, 9m, 10m, 11m, 12m, 13m, 14m])));

        Assert.NotNull(clash);
        Assert.Equal("mangadex", clash.SubChapterSource);
        Assert.Equal("weebcentral", clash.WholeChapterSource);
    }

    [Fact]
    public void OneSplitChapterIsNotAClash()
    {
        // A single chapter released in parts (10.1/10.2) happens on any source.
        Assert.Null(NumberingClashDetector.Detect(Sources(
            ("mangadex", [8m, 9m, 10.1m, 10.2m, 10.3m, 11m]),
            ("weebcentral", [8m, 9m, 10m, 11m]))));
    }

    [Fact]
    public void SingleSourceNeverClashes()
    {
        Assert.Null(NumberingClashDetector.Detect(Sources(
            ("mangadex", [1.1m, 1.2m, 2.1m, 2.2m, 3.1m, 3.2m]))));
    }

    [Fact]
    public void OneShotsAreIgnored()
    {
        Assert.Null(NumberingClashDetector.Detect(Sources(
            ("mangadex", [null, null, null]),
            ("weebcentral", [1m, 2m, 3m]))));
    }
}
