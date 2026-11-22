using Maki.Core.Http;
using Maki.Core.Sources;
using Maki.Sources.Custom;
using Maki.Sources.AnimeSama;
using Maki.Sources.CuuTruyen;
using Maki.Sources.FlameComics;
using Maki.Sources.Mangakakalot;
using Maki.Sources.MangaWorld;
using Maki.Sources.Manhwa18Net;
using Maki.Sources.Olympus;
using Maki.Sources.Rawkuma;
using Maki.Sources.TeamX;
using Maki.Sources.Toonily;
using Maki.Sources.WeebCentral;

namespace Maki.Sources.Tests;

/// <summary>
/// A source's HTML goes through its proxy only if it binds the fetcher to its own name; a typo in
/// that name would send it direct without anything failing.
/// </summary>
public class SourceFetcherBindingTests
{
    private sealed class BindingRecorder() : FakeHtmlFetcher([])
    {
        public List<string> Bound { get; } = [];

        public override IHtmlFetcher For(string sourceName)
        {
            Bound.Add(sourceName);
            return this;
        }
    }

    private static readonly string[] Names =
    [
        "animesama", "cuutruyen", "flamecomics", "mangakakalot", "mangaworld", "manhwa18net", "olympus",
        "rawkuma", "teamx", "toonily", "weebcentral",
    ];

    public static TheoryData<string> Sources() => new(Names);

    private static ISource Create(string name, IHtmlFetcher fetcher) => name switch
    {
        "animesama" => new AnimeSamaSource(fetcher),
        "cuutruyen" => new CuuTruyenSource(fetcher, new FakeHttpClientFactory([])),
        "flamecomics" => new FlameComicsSource(fetcher),
        "mangakakalot" => new MangakakalotSource(fetcher),
        "mangaworld" => new MangaWorldSource(fetcher),
        "manhwa18net" => new Manhwa18NetSource(fetcher),
        "olympus" => new OlympusSource(fetcher),
        "rawkuma" => new RawkumaSource(fetcher),
        "teamx" => new TeamXSource(fetcher),
        "toonily" => new ToonilySource(fetcher),
        "weebcentral" => new WeebCentralSource(fetcher),
        _ => throw new ArgumentOutOfRangeException(nameof(name)),
    };

    [Theory]
    [MemberData(nameof(Sources))]
    public void Every_fetcher_source_binds_its_fetcher_to_its_own_name(string name)
    {
        var recorder = new BindingRecorder();
        var source = Create(name, recorder);

        Assert.Equal([source.Name], recorder.Bound);
    }

    // A new source that takes IHtmlFetcher has to be added above, or a forgotten For(Name) passes.
    // CustomSource binds to its definition's name instead; CustomSourceTests checks that.
    [Fact]
    public void Every_source_that_takes_a_fetcher_is_checked()
    {
        var takesFetcher = typeof(OlympusSource).Assembly.GetTypes()
            .Where(t => t is { IsClass: true, IsAbstract: false } && typeof(ISource).IsAssignableFrom(t) && t != typeof(CustomSource))
            .Where(t => t.GetConstructors().Any(c => c.GetParameters().Any(p => p.ParameterType == typeof(IHtmlFetcher))))
            .Select(t => t.FullName)
            .Order()
            .ToList();
        var checkedTypes = Names.Select(name => Create(name, new BindingRecorder()).GetType().FullName).Order().ToList();

        Assert.Equal(takesFetcher, checkedTypes);
    }
}
