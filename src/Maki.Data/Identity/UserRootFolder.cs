namespace Maki.Data.Identity;

/// <summary>
/// Grants one user access to one root folder. Absence is denial: a user with
/// <see cref="MakiUser.AllRootFolders"/> false and no rows here sees an empty library.
/// <para>
/// The schema lands with the identity work so <see cref="Maki.Core.Security.ICurrentUser"/> can
/// report accurate access from the first release; the query filter that <em>enforces</em> it
/// arrives with the per-user data split.
/// </para>
/// </summary>
public class UserRootFolder
{
    public int UserId { get; set; }
    public int RootFolderId { get; set; }
}
