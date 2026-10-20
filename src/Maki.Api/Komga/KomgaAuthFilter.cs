using Maki.Api.Services;
using Maki.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace Maki.Api.Komga;

/// <summary>
/// Authenticates every Komga action and narrows the request's <see cref="DataScope"/> to the
/// token's owner before the action runs, the way <c>OpdsController.AuthorizeAsync</c> does per
/// action. Answers 401, not OPDS's 404: Komga clients only send Basic credentials after a 401, and
/// show a login error on one.
/// </summary>
public sealed class KomgaAuthFilter(KomgaAuthenticator authenticator, MakiDbContext db) : IAsyncActionFilter
{
    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        var http = context.HttpContext;
        if (await authenticator.AuthenticateAsync(http, http.RequestAborted) is not { } access)
        {
            context.Result = new UnauthorizedResult();
            return;
        }

        db.Scope.SetUser(access.UserId, access.AllRootFolders);
        http.Items[typeof(OpdsAccess)] = access;
        await next();
    }
}
