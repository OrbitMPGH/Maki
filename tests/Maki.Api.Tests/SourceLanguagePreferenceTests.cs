using Maki.Api.Services;
using Maki.Core.Sources;

namespace Maki.Api.Tests;

/// <summary>
/// The language ranking auto-matching lays over the source priority list: bucketing by the
/// highest-ranked enabled language a source publishes, dropping sources that publish none of them,
/// and what a freshly-created mapping's language filter is seeded with.
/// </summary>
public class SourceLanguagePreferenceTests
{
    private static FakeSource Source(string name, params string[] languages) =>
        new() { Name = name, SupportedLanguages = languages.Length == 0 ? ["en"] : languages };

    private static FakeSource Filtering(string name, params string[] languages) =>
        new()
        {
            Name = name,
            SupportedLanguages = languages,
            Capabilities = SourceCapabilities.SupportsLanguageFilter
        };

    private static List<string> Names(IEnumerable<ISource> sources) => sources.Select(s => s.Name).ToList();

    [Fact]
    public void Japanese_above_English_lifts_the_Japanese_only_sources_to_the_top()
    {
        List<ISource> baseOrder =
        [
            Source("a"), Source("b"), Source("c"), Source("d"),
            Source("senmanga", "ja"), Source("otherja", "ja"),
        ];
        var pref = SourceLanguagePreference.Parse("ja,en", null);

        Assert.Equal(
            ["senmanga", "otherja", "a", "b", "c", "d"],
            Names(SourceLanguagePreference.Rank(baseOrder, pref)));
    }

    [Fact]
    public void Unset_setting_keeps_base_order_and_drops_non_English_sources()
    {
        List<ISource> baseOrder = [Source("a"), Source("senmanga", "ja"), Source("b")];
        var pref = SourceLanguagePreference.Parse(null, null);

        Assert.Equal(["a", "b"], Names(SourceLanguagePreference.Rank(baseOrder, pref)));
        Assert.Equal(["senmanga"], Names(SourceLanguagePreference.Unranked(baseOrder, pref)));
    }

    [Fact]
    public void Everything_disabled_falls_back_to_English()
    {
        List<ISource> baseOrder = [Source("a"), Source("senmanga", "ja")];
        var pref = SourceLanguagePreference.Parse("ja,en", "ja,en");

        Assert.Equal(["en"], pref.EnabledInOrder);
        Assert.Equal(["a"], Names(SourceLanguagePreference.Rank(baseOrder, pref)));
    }

    [Fact]
    public void A_multi_language_source_buckets_with_the_top_enabled_language()
    {
        List<ISource> baseOrder =
        [
            Source("english"),
            Source("mangadex", Core.Localization.SupportedLanguages.All),
        ];
        var pref = SourceLanguagePreference.Parse("ja,en", null);

        Assert.Equal(["mangadex", "english"], Names(SourceLanguagePreference.Rank(baseOrder, pref)));
    }

    [Fact]
    public void A_source_publishing_no_enabled_language_is_unranked()
    {
        List<ISource> baseOrder = [Source("a"), Source("baozi", "zh-Hans")];
        var pref = SourceLanguagePreference.Parse("en,ja", "ja");

        Assert.Equal(["a"], Names(SourceLanguagePreference.Rank(baseOrder, pref)));
        Assert.Equal(["baozi"], Names(SourceLanguagePreference.Unranked(baseOrder, pref)));
    }

    [Fact]
    public void SeedFilter_is_null_without_the_capability()
    {
        var pref = SourceLanguagePreference.Parse("ja,en", null);
        Assert.Null(SourceLanguagePreference.SeedFilter(Source("senmanga", "ja"), pref));
    }

    [Fact]
    public void SeedFilter_is_null_when_the_answer_is_plain_English()
    {
        var pref = SourceLanguagePreference.Parse(null, null);
        Assert.Null(SourceLanguagePreference.SeedFilter(Filtering("mangadex", "en", "ja"), pref));
    }

    [Fact]
    public void SeedFilter_lists_the_enabled_languages_in_preference_order()
    {
        var pref = SourceLanguagePreference.Parse("ja,en,de", "de");
        Assert.Equal("ja,en", SourceLanguagePreference.SeedFilter(Filtering("mangadex", "en", "ja", "de"), pref));
    }

    [Fact]
    public void Offered_puts_English_first_and_dedupes_case_insensitively()
    {
        var offered = SourceLanguagePreference.Offered(
        [
            Source("senmanga", "ja"),
            Source("livre", "pt-BR"),
            Source("plus", "pt-br", "th", "en"),
        ]);

        var codes = offered.ToList();
        Assert.Equal("en", codes[0]);
        Assert.Single(codes, c => c.Equals("pt-br", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("ja", codes);
        // "th" is not a shipped UI language, so it sorts after the ones that are.
        Assert.True(codes.IndexOf("th") > codes.IndexOf("ja"));
    }
}
