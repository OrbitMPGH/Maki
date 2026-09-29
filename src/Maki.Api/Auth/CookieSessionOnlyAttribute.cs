using Maki.Api.Localization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace Maki.Api.Auth;

/// <summary>
/// Refuses anything but the browser session cookie. For actions that manage the account itself:
/// a leaked API key must not be able to mint another key, enrol an authenticator of its own or link
/// a login, because each of those survives revoking the key that did it.
/// <para>
/// Checked against the principal the default scheme produced rather than through
/// <c>[Authorize(AuthenticationSchemes = ...)]</c>. That attribute re-authenticates with the cookie
/// and would pass a request that carries somebody's key alongside the caller's own cookie, while
/// <see cref="CurrentUserMiddleware"/> had already resolved the key's owner as the current user.
/// </para>
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public sealed class CookieSessionOnlyAttribute : Attribute, IAuthorizationFilter
{
    public void OnAuthorization(AuthorizationFilterContext context)
    {
        var http = context.HttpContext;
        if (http.User.Identity?.IsAuthenticated != true)
        {
            // The fallback policy answers these with its own 401.
            return;
        }

        if (http.User.Identity.AuthenticationType == IdentityConstants.ApplicationScheme &&
            !http.Request.Headers.ContainsKey(ApiKeyAuthenticationHandler.HeaderName))
        {
            return;
        }

        var localizer = http.RequestServices.GetRequiredService<ILocalizer>();
        const string key = "error.account.sessionRequired";
        context.Result = new ObjectResult(new { code = key, error = localizer.Get(key) })
        {
            StatusCode = StatusCodes.Status403Forbidden
        };
    }
}
