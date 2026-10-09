namespace Maki.Data.Identity;

/// <summary>
/// Grants one user access to one root folder. Absence is denial: a user with
/// <see cref="MakiUser.AllRootFolders"/> false and no rows here sees an empty library.
/// <para>
/// Enforced by the <c>Series</c> query filter in <c>MakiDbContext</c>, which every series query goes
/// through; <see cref="Maki.Core.Security.ICurrentUser"/> reports the same access.
/// </para>
/// </summary>
public class UserRootFolder
{
    public int UserId { get; set; }
    public int RootFolderId { get; set; }
}
