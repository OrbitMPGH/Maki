using Maki.Sources.Common;

namespace Maki.Sources.Tests;

public class BrowserSupportTests
{
    private const string Ua =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/138.0.0.0 Safari/537.36";

    [Fact]
    public void Client_hints_default_to_the_chromium_chrome_brand_order()
    {
        var hints = BrowserSupport.ClientHintsFor(Ua);

        Assert.Equal("\"Chromium\";v=\"138\", \"Google Chrome\";v=\"138\", \"Not=A?Brand\";v=\"24\"", hints["sec-ch-ua"]);
        Assert.Equal("\"Windows\"", hints["sec-ch-ua-platform"]);
    }

    [Fact]
    public void Client_hints_can_keep_the_grease_brand_second()
    {
        var hints = BrowserSupport.ClientHintsFor(Ua, brandSecond: true);

        Assert.Equal("\"Chromium\";v=\"138\", \"Not_A Brand\";v=\"24\", \"Google Chrome\";v=\"138\"", hints["sec-ch-ua"]);
    }
}
