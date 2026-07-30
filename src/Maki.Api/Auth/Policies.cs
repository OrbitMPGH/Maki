using Maki.Core.Security;
using Microsoft.AspNetCore.Authorization;

namespace Maki.Api.Auth;

/// <summary>
/// Authorization policy names. One policy per <see cref="MakiPermission"/>, named after the enum
/// member, so registration is a loop over the enum and adding a permission never means remembering
/// to add a matching policy.
/// </summary>
public static class Policies
{
    /// <summary>
    /// Instance administration: settings, root folders, notifications, backups, user management.
    /// The surfaces with no permission flag of their own.
    /// </summary>
    public const string Admin = nameof(MakiPermission.Admin);

    public const string AddSeries = nameof(MakiPermission.AddSeries);
    public const string DeleteSeries = nameof(MakiPermission.DeleteSeries);
    public const string DownloadChapters = nameof(MakiPermission.DownloadChapters);
    public const string ManageDownloadQueue = nameof(MakiPermission.ManageDownloadQueue);
    public const string ManageSources = nameof(MakiPermission.ManageSources);
    public const string EditMetadata = nameof(MakiPermission.EditMetadata);
    public const string ManageTags = nameof(MakiPermission.ManageTags);
    public const string ChangeContentRating = nameof(MakiPermission.ChangeContentRating);
    public const string UseTrackers = nameof(MakiPermission.UseTrackers);
    public const string UseOpds = nameof(MakiPermission.UseOpds);
    public const string ImportLibrary = nameof(MakiPermission.ImportLibrary);

    /// <summary>Registers one policy per permission, plus the fallback "any signed-in user".</summary>
    public static void AddMakiPolicies(this AuthorizationOptions options)
    {
        foreach (var permission in Enum.GetValues<MakiPermission>())
        {
            if (permission == MakiPermission.None) continue;
            options.AddPolicy(permission.ToString(), p => p.AddRequirements(new PermissionRequirement(permission)));
        }

        // Every endpoint requires a signed-in user unless it opts out with [AllowAnonymous]. This is
        // the fallback rather than a convention to remember per controller: a new controller added
        // without an [Authorize] attribute is protected by default instead of wide open, which is
        // the only default that fails safe.
        options.FallbackPolicy = new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build();
    }
}

public sealed class PermissionRequirement(MakiPermission permission) : IAuthorizationRequirement
{
    public MakiPermission Permission { get; } = permission;
}

/// <summary>
/// Checks a permission against <see cref="ICurrentUser"/> — the database-backed snapshot resolved
/// for this request — rather than against a claim baked into the session cookie. Revoking a
/// permission therefore takes effect on the next request, with no security-stamp round trip and no
/// window in which a stale cookie still authorizes a write.
/// </summary>
public sealed class PermissionAuthorizationHandler(ICurrentUser user)
    : AuthorizationHandler<PermissionRequirement>
{
    protected override Task HandleRequirementAsync(
        AuthorizationHandlerContext context, PermissionRequirement requirement)
    {
        // Grants() and not HasFlag(): Admin implies every other permission.
        if (user.IsAuthenticated && user.Permissions.Grants(requirement.Permission))
        {
            context.Succeed(requirement);
        }

        return Task.CompletedTask;
    }
}
