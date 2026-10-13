using Maki.Sources.Common;

namespace Maki.Sources.Tests;

public class PreUnwrapTests
{
    [Fact]
    public void Plain_json_passes_through()
    {
        Assert.Equal("{\"a\":1}", PreUnwrap.Unwrap("{\"a\":1}", "https://x/api"));
    }

    [Fact]
    public void A_flaresolverr_pre_wrapper_is_unwrapped()
    {
        var body = "<html><body><pre>{\"a\":1}</pre></body></html>";
        Assert.Equal("{\"a\":1}", PreUnwrap.Unwrap(body, "https://x/api"));
    }

    [Fact]
    public void Html_without_a_pre_throws_with_the_url()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => PreUnwrap.Unwrap("<html><body>nope</body></html>", "https://x/api"));
        Assert.Contains("https://x/api", ex.Message);
    }
}
