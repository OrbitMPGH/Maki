using Maki.Core.Security;
using Microsoft.AspNetCore.Identity;

namespace Maki.Data.Identity;

/// <summary>
/// A Maki account. Derives from ASP.NET Identity so password hashing, lockout, the security stamp
/// and TOTP two-factor come from the framework rather than from hand-rolled crypto.
/// <para>
/// This type lives in Maki.Data rather than Maki.Core/Entities, which is where every other entity
/// lives: <see cref="IdentityUser{TKey}"/> would drag Microsoft.Extensions.Identity.Stores into
/// Maki.Core, and Maki.Core is deliberately infrastructure-free. Domain code asks about permissions
/// through <see cref="ICurrentUser"/> instead, which does live in Core.
/// </para>
/// </summary>
public class MakiUser : IdentityUser<int>
{
    /// <summary>
    /// Permission bits. Test with <see cref="MakiPermissions.Grants"/>, never a bare
    /// <c>HasFlag</c> — <see cref="MakiPermission.Admin"/> implies everything.
    /// </summary>
    public MakiPermission Permissions { get; set; } = MakiPermission.None;

    /// <summary>
    /// This user's content rating ceiling, from the <c>ContentRating</c> vocabulary
    /// (safe | suggestive | erotica | pornographic). Replaces the instance-wide
    /// <c>discover.maxcontentrating</c> setting.
    /// </summary>
    public string MaxContentRating { get; set; } = string.Empty;

    /// <summary>
    /// Sees every root folder, including ones added later. Off for new users, who then see only
    /// what <see cref="UserRootFolder"/> grants them — an empty library rather than the whole one.
    /// </summary>
    public bool AllRootFolders { get; set; }

    public string? DisplayName { get; set; }

    /// <summary>
    /// Blocks sign-in and invalidates existing sessions (the security stamp validator picks it up
    /// within its validation interval). Kept rather than deleting the account so the user's reading
    /// history survives a temporary suspension.
    /// </summary>
    public bool Disabled { get; set; }

    /// <summary>
    /// The account exists but has never been claimed: no password has been set and it cannot sign
    /// in. Only the placeholder admin the multi-user migration inserts carries this, and clearing
    /// it is what the first-run setup does. Deliberately an explicit flag rather than inferring it
    /// from a null <c>PasswordHash</c>, because an OIDC-only user legitimately has no password.
    /// </summary>
    public bool PendingSetup { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime? LastLoginAt { get; set; }
}
