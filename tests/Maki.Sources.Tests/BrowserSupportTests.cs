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

    [Theory]
    [InlineData(Ua, "Windows", "10.0", "x86")]
    [InlineData("Mozilla/5.0 (X11; Linux x86_64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/149.0.0.0 Safari/537.36", "Linux", "", "x86")]
    [InlineData("Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/149.0.0.0 Safari/537.36", "macOS", "10_15_7", "x86")]
    public void User_agent_metadata_reads_the_platform_off_the_user_agent_as_playwright_did(
        string userAgent, string platform, string version, string architecture)
    {
        var metadata = BrowserSupport.UserAgentMetadataFor(userAgent);

        Assert.Equal(platform, metadata["platform"]);
        Assert.Equal(version, metadata["platformVersion"]);
        Assert.Equal(architecture, metadata["architecture"]);
        Assert.Equal(false, metadata["mobile"]);
    }

    /// <summary>
    /// Puppeteer's own defaults are not used: <c>--enable-automation</c> sets navigator.webdriver and
    /// a startup tab costs a renderer for nothing. The sandbox flag is what lets it start in a container.
    /// </summary>
    [Fact]
    public void Launch_flags_are_playwrights_and_never_puppeteers_automation_switch()
    {
        Assert.DoesNotContain("--enable-automation", BrowserSupport.ChromiumArgs);
        Assert.Contains("--no-sandbox", BrowserSupport.ChromiumArgs);
        Assert.Contains("--no-startup-window", BrowserSupport.ChromiumArgs);
        Assert.Contains("--headless", BrowserSupport.ChromiumArgs);
        Assert.All(BrowserSupport.LaunchArgs, arg => Assert.Contains(arg, BrowserSupport.ChromiumArgs));
        Assert.DoesNotContain(BrowserSupport.ChromiumArgs, arg => arg.StartsWith("--user-data-dir", StringComparison.Ordinal));
    }
}
