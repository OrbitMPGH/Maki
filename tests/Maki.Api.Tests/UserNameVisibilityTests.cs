using Maki.Api.Services;
using Maki.Core.Security;

namespace Maki.Api.Tests;

public class UserNameVisibilityTests
{
    [Theory]
    [InlineData("Ed", "ed-login", MakiPermission.None, 2, "Ed")]
    [InlineData(null, "ed-login", MakiPermission.None, 2, null)]
    [InlineData(null, "ed-login", MakiPermission.Admin, 2, "ed-login")]
    [InlineData(null, "ed-login", MakiPermission.None, 1, "ed-login")]
    public void A_login_name_is_shown_only_to_an_admin_or_its_owner(
        string? display, string login, MakiPermission reader, int subject, string? expected)
    {
        Assert.Equal(expected, UserNameVisibility.For(new TestCurrentUser(1, permissions: reader), subject, display, login));
    }
}
