namespace Maki.Core.Security;

/// <summary>
/// The user behind the current request. Resolved once per request from the database rather than
/// from claims baked into the session cookie: a permission or root-folder change then takes effect
/// on the very next request, with no security-stamp round trip to reason about and no window where
/// a revoked permission still authorizes a write.
/// <para>
/// Lives in Maki.Core so domain services can ask about permissions without referencing ASP.NET
/// Identity — the implementation and the user entity itself live in Maki.Data/Maki.Api.
/// </para>
/// </summary>
public interface ICurrentUser
{
    bool IsAuthenticated { get; }

    /// <summary>0 when unauthenticated.</summary>
    int UserId { get; }

    string UserName { get; }

    MakiPermission Permissions { get; }

    /// <summary>Whether the user may see every root folder, present and future.</summary>
    bool AllRootFolders { get; }

    /// <summary>
    /// Root folder ids this user may see. Empty with <see cref="AllRootFolders"/> false means an
    /// empty library — access is granted, never assumed.
    /// </summary>
    IReadOnlySet<int> RootFolderIds { get; }

    /// <summary>The user's content rating ceiling, from the <c>ContentRating</c> vocabulary.</summary>
    string MaxContentRating { get; }

    /// <summary>True when the user holds <paramref name="permission"/>, or is an admin.</summary>
    bool Has(MakiPermission permission) => Permissions.Grants(permission);
}
