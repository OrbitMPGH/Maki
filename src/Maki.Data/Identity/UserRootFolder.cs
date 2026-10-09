namespace Maki.Data.Identity;

/// <summary>
/// Grants one user access to one root folder. Absence is denial: a user with
/// <see cref="MakiUser.AllRootFolders"/> false and no rows here sees an empty library.
/// <para>
/// The query filter on <c>Series</c> enforces these grants; <see cref="Maki.Core.Security.ICurrentUser"/>
/// reports the same access to code that checks it outside a query.
/// </para>
/// </summary>
public class UserRootFolder
{
    public int UserId { get; set; }
    public int RootFolderId { get; set; }
}
