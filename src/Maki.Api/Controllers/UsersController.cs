using Maki.Api.Auth;
using Maki.Api.Dtos;
using Maki.Api.Services;
using Maki.Core.Security;
using Maki.Data;
using Maki.Data.Identity;
using Maki.Metadata.MangaBaka;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Maki.Api.Controllers;

/// <summary>
/// Admin user management.
/// <para>
/// Three guards run through everything here, and they exist because the failure they prevent is
/// unrecoverable without shell access to the database: the instance must never end up with zero
/// usable admins. An admin cannot drop their own Admin flag, cannot disable or delete themselves,
/// and cannot remove the Admin flag from the last account that has it.
/// </para>
/// </summary>
[ApiController]
[Route("api/v1/users")]
[Authorize(Policy = Policies.Admin)]
public class UsersController(
    MakiDbContext db,
    UserManager<MakiUser> userManager,
    AdminGuard adminGuard,
    ICurrentUser currentUser,
    AuthEventLogger auditLog,
    TimeProvider clock,
    ILogger<UsersController> logger) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> List(CancellationToken ct)
    {
        var users = await db.Users.AsNoTracking().OrderBy(u => u.Id).ToListAsync(ct);
        var grants = await db.UserRootFolders.AsNoTracking().ToListAsync(ct);
        var allFolders = await db.RootFolders.Select(r => r.Id).ToListAsync(ct);

        return Ok(users.Select(u => UserDtoMapper.ToSummary(
            u,
            u.AllRootFolders
                ? allFolders
                : grants.Where(g => g.UserId == u.Id).Select(g => g.RootFolderId).ToList())));
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] SaveUserRequest request, CancellationToken ct)
    {
        var username = request.Username?.Trim();
        if (string.IsNullOrEmpty(username))
        {
            return BadRequest(new { error = "Username is required" });
        }

        if (string.IsNullOrEmpty(request.Password))
        {
            return BadRequest(new { error = "Password is required" });
        }

        var rating = request.MaxContentRating;
        if (rating is not null && !ContentRating.IsValid(rating))
        {
            return BadRequest(new { error = $"Rating must be one of: {string.Join(", ", ContentRating.All)}" });
        }

        var user = new MakiUser
        {
            UserName = username,
            DisplayName = request.DisplayName?.Trim(),
            // A conservative default rather than the instance-wide one: a new account should not
            // silently inherit whatever the admin set for themselves.
            Permissions = request.Permissions ?? MakiPermissions.DefaultForNewUser,
            MaxContentRating = rating ?? ContentRating.Safe,
            // Fail closed. A new user sees an empty library until they are granted a folder, rather
            // than the whole thing until someone remembers to restrict them.
            AllRootFolders = request.AllRootFolders ?? false,
            Disabled = request.Disabled ?? false,
            CreatedAt = clock.GetUtcNow().UtcDateTime
        };

        var created = await userManager.CreateAsync(user, request.Password);
        if (!created.Succeeded)
        {
            return BadRequest(new { error = Describe(created) });
        }

        await ReplaceRootFolderGrantsAsync(user, request.RootFolderIds, ct);

        // Explicit id, not the ambient scope: this is running as the admin who pressed Create.
        await ReadingProfileSeeder.SeedAsync(db, user.Id, ct);

        await auditLog.LogAsync(AuthEventType.UserCreated, currentUser.UserName, currentUser.UserId,
            HttpContext, detail: $"created \"{username}\" with {user.Permissions}", ct: ct);
        logger.LogInformation("User {UserName} created by {Admin}", username, currentUser.UserName);

        return Ok(UserDtoMapper.ToSummary(user, await RootFolderIdsAsync(user, ct)));
    }

    [HttpPut("{id:int}")]
    public async Task<IActionResult> Update(int id, [FromBody] SaveUserRequest request, CancellationToken ct)
    {
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == id, ct);
        if (user is null)
        {
            return NotFound();
        }

        var wasAdmin = user.Permissions.Grants(MakiPermission.Admin);
        var before = user.Permissions;

        if (request.Permissions is { } permissions)
        {
            var losingAdmin = wasAdmin && !permissions.Grants(MakiPermission.Admin);

            if (losingAdmin && user.Id == currentUser.UserId)
            {
                return BadRequest(new { error = "You cannot remove your own administrator permission" });
            }

            if (losingAdmin && await IsLastAdminAsync(user.Id, ct))
            {
                return BadRequest(new { error = "This is the only administrator; promote another account first" });
            }

            user.Permissions = permissions;
        }

        if (request.Disabled is { } disabled)
        {
            if (disabled && user.Id == currentUser.UserId)
            {
                return BadRequest(new { error = "You cannot disable your own account" });
            }

            if (disabled && user.Permissions.Grants(MakiPermission.Admin) && await IsLastAdminAsync(user.Id, ct))
            {
                return BadRequest(new { error = "This is the only administrator; promote another account first" });
            }

            user.Disabled = disabled;
        }

        if (request.Username?.Trim() is { Length: > 0 } username &&
            !string.Equals(username, user.UserName, StringComparison.Ordinal))
        {
            var renamed = await userManager.SetUserNameAsync(user, username);
            if (!renamed.Succeeded)
            {
                return BadRequest(new { error = Describe(renamed) });
            }
        }

        if (request.DisplayName is not null)
        {
            user.DisplayName = request.DisplayName.Trim() is { Length: > 0 } name ? name : null;
        }

        if (request.MaxContentRating is { } rating)
        {
            if (!ContentRating.IsValid(rating))
            {
                return BadRequest(new { error = $"Rating must be one of: {string.Join(", ", ContentRating.All)}" });
            }
            user.MaxContentRating = rating;
        }

        if (request.AllRootFolders is { } allFolders)
        {
            user.AllRootFolders = allFolders;
        }

        if (!string.IsNullOrEmpty(request.Password))
        {
            // An admin reset goes through the token, not through ChangePassword: the admin does not
            // know the current password and must not have to.
            var token = await userManager.GeneratePasswordResetTokenAsync(user);
            var reset = await userManager.ResetPasswordAsync(user, token, request.Password);
            if (!reset.Succeeded)
            {
                return BadRequest(new { error = Describe(reset) });
            }
        }

        await ReplaceRootFolderGrantsAsync(user, request.RootFolderIds, ct);
        await db.SaveChangesAsync(ct);

        // Any change to what the account may do, or whether it may sign in at all, invalidates its
        // existing cookies. Permission checks read the database per request so they are already
        // current; this is about not leaving a disabled user with a live session.
        if (user.Permissions != before || request.Disabled is not null || !string.IsNullOrEmpty(request.Password))
        {
            await userManager.UpdateSecurityStampAsync(user);
        }

        if (user.Permissions != before)
        {
            await auditLog.LogAsync(AuthEventType.PermissionsChanged, currentUser.UserName, currentUser.UserId,
                HttpContext, detail: $"\"{user.UserName}\": {before} -> {user.Permissions}", ct: ct);
        }
        else
        {
            await auditLog.LogAsync(AuthEventType.UserUpdated, currentUser.UserName, currentUser.UserId,
                HttpContext, detail: $"updated \"{user.UserName}\"", ct: ct);
        }

        return Ok(UserDtoMapper.ToSummary(user, await RootFolderIdsAsync(user, ct)));
    }

    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Delete(int id, CancellationToken ct)
    {
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == id, ct);
        if (user is null)
        {
            return NotFound();
        }

        if (user.Id == currentUser.UserId)
        {
            return BadRequest(new { error = "You cannot delete your own account" });
        }

        if (user.Permissions.Grants(MakiPermission.Admin) && await IsLastAdminAsync(user.Id, ct))
        {
            return BadRequest(new { error = "This is the only administrator; promote another account first" });
        }

        var name = user.UserName ?? string.Empty;
        db.Users.Remove(user);
        await db.SaveChangesAsync(ct);

        await auditLog.LogAsync(AuthEventType.UserDeleted, currentUser.UserName, currentUser.UserId,
            HttpContext, detail: $"deleted \"{name}\"", ct: ct);
        logger.LogInformation("User {UserName} deleted by {Admin}", name, currentUser.UserName);

        return NoContent();
    }

    /// <summary>
    /// The security audit trail. Newest first, bounded — the table is capped on write, and an
    /// unbounded read of it would be the largest response the API produces.
    /// </summary>
    [HttpGet("auditlog")]
    public async Task<IActionResult> AuditLog([FromQuery] int limit = 200, CancellationToken ct = default)
    {
        var events = await db.AuthEvents
            .AsNoTracking()
            .OrderByDescending(a => a.Id)
            .Take(Math.Clamp(limit, 1, 1000))
            .Select(a => new AuthEventDto(a.Timestamp, a.Type, a.UserId, a.UserName, a.ClientIp, a.UserAgent, a.Detail))
            .ToListAsync(ct);

        return Ok(events);
    }

    private Task<bool> IsLastAdminAsync(int userId, CancellationToken ct) =>
        adminGuard.IsLastAdminAsync(userId, ct);

    private async Task ReplaceRootFolderGrantsAsync(
        MakiUser user, IReadOnlyList<int>? rootFolderIds, CancellationToken ct)
    {
        if (rootFolderIds is null)
        {
            return;
        }

        var existing = await db.UserRootFolders.Where(g => g.UserId == user.Id).ToListAsync(ct);
        db.UserRootFolders.RemoveRange(existing);

        // Filter against real root folders so a stale id from the client cannot create a grant that
        // the FK would reject on save.
        var valid = await db.RootFolders
            .Where(r => rootFolderIds.Contains(r.Id))
            .Select(r => r.Id)
            .ToListAsync(ct);

        foreach (var folderId in valid)
        {
            db.UserRootFolders.Add(new UserRootFolder { UserId = user.Id, RootFolderId = folderId });
        }

        await db.SaveChangesAsync(ct);
    }

    private async Task<IReadOnlyList<int>> RootFolderIdsAsync(MakiUser user, CancellationToken ct) =>
        user.AllRootFolders
            ? await db.RootFolders.Select(r => r.Id).ToListAsync(ct)
            : await db.UserRootFolders.Where(g => g.UserId == user.Id).Select(g => g.RootFolderId).ToListAsync(ct);

    private static string Describe(IdentityResult result) =>
        string.Join("; ", result.Errors.Select(e => e.Description));
}
