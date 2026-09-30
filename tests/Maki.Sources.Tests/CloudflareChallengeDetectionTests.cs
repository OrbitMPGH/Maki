using Maki.Sources.Common;

namespace Maki.Sources.Tests;

public class CloudflareChallengeDetectionTests
{
    [Fact]
    public void A_healthy_page_with_the_js_detections_script_is_not_a_challenge()
    {
        const string content = """<html><body>chapter</body><script src="/cdn-cgi/challenge-platform/scripts/jsd/main.js"></script></html>""";

        Assert.False(CloudflareChallengeDetection.IsChallenge("Chapter 1", content));
    }

    [Theory]
    [InlineData("Just a moment...", "<html></html>")]
    [InlineData("", """<form id="challenge-form" action="/?__cf_chl_f_tk=abc"></form>""")]
    [InlineData("", """<div id="cf-chl-widget-xyz"></div>""")]
    public void The_interstitial_is_a_challenge(string title, string content)
    {
        Assert.True(CloudflareChallengeDetection.IsChallenge(title, content));
    }
}
