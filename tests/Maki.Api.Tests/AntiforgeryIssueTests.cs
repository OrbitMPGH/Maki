using System.Security.Claims;
using Maki.Api.Auth;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;

namespace Maki.Api.Tests;

public class AntiforgeryIssueTests
{
    private static HttpContext Context(string path, string? authenticationType)
    {
        var http = new DefaultHttpContext();
        http.Request.Path = path;
        http.User = authenticationType is null
            ? new ClaimsPrincipal(new ClaimsIdentity())
            : new ClaimsPrincipal(new ClaimsIdentity(authenticationType));
        return http;
    }

    [Theory]
    [InlineData("/api/v1/series")]
    [InlineData("/api/v1/auth/me")]
    [InlineData("/api/v1/reader/chapter/5")]
    [InlineData("/api/v1/reader/chapter/5/bookmarks")]
    public void JsonGetsKeepReissuing(string path)
    {
        Assert.True(AntiforgeryTokenMiddleware.ShouldIssue(Context(path, IdentityConstants.ApplicationScheme)));
        Assert.True(AntiforgeryTokenMiddleware.ShouldIssue(Context(path, null)));
    }

    [Theory]
    [InlineData("/api/v1/mediacover/3/cover.jpg")]
    [InlineData("/api/v1/mediacover/3/cover-sm.jpg")]
    [InlineData("/api/v1/reader/chapter/5/page/2")]
    [InlineData("/api/v1/reader/chapter/5/page/2/enhanced")]
    [InlineData("/api/v1/reader/chapter/5/thumb/2")]
    public void ImageGetsSkipTheToken(string path)
    {
        Assert.False(AntiforgeryTokenMiddleware.ShouldIssue(Context(path, IdentityConstants.ApplicationScheme)));
    }

    [Fact]
    public void ApiKeyRequestsSkipTheToken()
    {
        Assert.False(AntiforgeryTokenMiddleware.ShouldIssue(Context("/api/v1/series", AuthSchemes.ApiKey)));
    }
}
