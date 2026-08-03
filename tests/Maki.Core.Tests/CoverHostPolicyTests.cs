using Maki.Core.Sources;

namespace Maki.Core.Tests;

/// <summary>
/// The allowlist behind the cover proxy. That endpoint fetches a URL the caller supplied, from the
/// server, so this function is the only thing between it and the host's private network — worth
/// testing directly rather than through a controller.
/// </summary>
public class CoverHostPolicyTests
{
    private sealed class StubSource(string baseUrl, params string[] coverHosts) : ISource
    {
        public string Name => "stub";
        public string DisplayName => "Stub";
        public string BaseUrl { get; } = baseUrl;
        public SourceCapabilities Capabilities => SourceCapabilities.None;
        public IReadOnlyList<string> CoverHosts { get; } = coverHosts;

        public Task<IReadOnlyList<SourceSeriesResult>> SearchAsync(string title, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<SourceSeriesDetail> GetSeriesAsync(string id, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<IReadOnlyList<SourceChapter>> ListChaptersAsync(
            string id, string? languageFilter = null, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<ChapterPages> GetPagesAsync(SourceChapter chapter, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }

    private static bool Allows(string baseUrl, string target, params string[] coverHosts) =>
        CoverHostPolicy.Allows(new StubSource(baseUrl, coverHosts), new Uri(target));

    [Fact]
    public void TheSourcesOwnHostIsAllowed()
    {
        Assert.True(Allows("https://mangadex.org", "https://mangadex.org/cover.jpg"));
    }

    [Fact]
    public void ASubdomainOfTheSourcesHostIsAllowed()
    {
        // Most sources serve images from a CDN subdomain of their own name, which is why the common
        // case needs no CoverHosts entry at all.
        Assert.True(Allows("https://mangadex.org", "https://uploads.mangadex.org/covers/a.jpg"));
        Assert.True(Allows("https://flamecomics.xyz", "https://cdn.flamecomics.xyz/x.png"));
    }

    [Fact]
    public void TheWwwPrefixOnTheBaseUrlDoesNotNarrowTheMatch()
    {
        // WebtoonsSource's BaseUrl is https://www.webtoons.com; images on the bare domain must pass.
        Assert.True(Allows("https://www.webtoons.com", "https://webtoons.com/a.jpg"));
        Assert.True(Allows("https://www.webtoons.com", "https://img.webtoons.com/a.jpg"));
    }

    [Fact]
    public void ADeclaredCdnDomainIsAllowedIncludingItsSubdomains()
    {
        Assert.True(Allows("https://www.webtoons.com",
            "https://webtoon-phinf.pstatic.net/a.jpg", "pstatic.net"));
    }

    [Fact]
    public void AnUndeclaredHostIsRefused()
    {
        Assert.False(Allows("https://mangadex.org", "https://example.com/a.jpg"));
    }

    [Fact]
    public void ASuffixThatIsNotADomainBoundaryIsRefused()
    {
        // The attack a naive EndsWith would allow: register a domain that merely ends with the
        // source's name and the proxy fetches from it. The leading dot in the comparison is what
        // stops this.
        Assert.False(Allows("https://mangadex.org", "https://evil-mangadex.org/a.jpg"));
        Assert.False(Allows("https://mangadex.org", "https://notmangadex.org/a.jpg"));
    }

    [Fact]
    public void APrefixMatchIsRefused()
    {
        Assert.False(Allows("https://mangadex.org", "https://mangadex.org.evil.com/a.jpg"));
    }

    [Theory]
    [InlineData("http://169.254.169.254/latest/meta-data/")]
    [InlineData("http://127.0.0.1:8990/api/v1/settings/prowlarr")]
    [InlineData("http://localhost/")]
    [InlineData("http://192.168.1.1/")]
    [InlineData("http://[::1]/")]
    public void TheUsualSsrfTargetsAreRefused(string target)
    {
        // None of these are on any source's domain, so the allowlist rejects them without needing a
        // separate private-address blocklist. Listed explicitly because they are what an attacker
        // actually reaches for, and a regression here would be silent.
        Assert.False(Allows("https://mangadex.org", target));
    }

    [Theory]
    [InlineData("file:///etc/passwd")]
    [InlineData("ftp://mangadex.org/a.jpg")]
    public void NonHttpSchemesAreRefusedEvenOnAnAllowedHost(string target)
    {
        Assert.False(Allows("https://mangadex.org", target));
    }

    [Fact]
    public void HostMatchingIsCaseInsensitive()
    {
        // DNS is case-insensitive, so a mixed-case host is the same host — refusing it would be a
        // bug, not extra safety.
        Assert.True(Allows("https://mangadex.org", "https://MangaDex.ORG/a.jpg"));
    }
}
