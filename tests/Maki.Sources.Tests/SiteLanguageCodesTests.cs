using Maki.Sources.Common;
using Maki.Sources.MangaFire;

namespace Maki.Sources.Tests;

public class SiteLanguageCodesTests
{
    [Fact]
    public void Simplified_chinese_is_asked_for_as_plain_zh()
    {
        Assert.Equal(["zh"], SiteLanguageCodes.Parse("zh-Hans"));
    }

    [Fact]
    public void Other_codes_pass_through_lowercased_and_deduplicated()
    {
        Assert.Equal(["en", "pt-br", "zh"], SiteLanguageCodes.Parse("en,pt-BR,zh,zh-Hans"));
    }

    [Fact]
    public void An_unset_filter_is_english()
    {
        Assert.Equal(["en"], SiteLanguageCodes.Parse(null));
    }

    [Fact]
    public void MangaFire_keeps_a_zh_item_for_a_zh_hans_filter()
    {
        var chapters = MangaFireSource.BuildChapters(
            "7wypj-some-slug",
            ["""{"id":1,"number":1,"name":"Ch. 1","type":"official","language":"zh"}"""],
            SiteLanguageCodes.Parse("zh-Hans"));

        Assert.Equal("zh", Assert.Single(chapters).Language);
    }
}
