using Maki.Core.Security;

namespace Maki.Api.Auth;

/// <summary>
/// The per-request <see cref="ICurrentUser"/>, populated once by <see cref="CurrentUserMiddleware"/>
/// and read synchronously everywhere downstream.
/// <para>
/// A mutable scoped holder rather than a service that lazily queries on first property access,
/// because <see cref="ICurrentUser"/>'s members are synchronous and the load is a database round
/// trip — resolving it in middleware keeps that round trip explicit, exactly once per request, and
/// off the property getters.
/// </para>
/// </summary>
public sealed class CurrentUserContext : ICurrentUser
{
    private static readonly IReadOnlySet<int> NoFolders = new HashSet<int>();

    public bool IsAuthenticated { get; private set; }
    public int UserId { get; private set; }
    public string UserName { get; private set; } = string.Empty;
    public MakiPermission Permissions { get; private set; } = MakiPermission.None;
    public bool AllRootFolders { get; private set; }
    public IReadOnlySet<int> RootFolderIds { get; private set; } = NoFolders;
    public string MaxContentRating { get; private set; } = string.Empty;

    public void Set(
        int userId,
        string userName,
        MakiPermission permissions,
        bool allRootFolders,
        IReadOnlySet<int> rootFolderIds,
        string maxContentRating)
    {
        IsAuthenticated = true;
        UserId = userId;
        UserName = userName;
        Permissions = permissions;
        AllRootFolders = allRootFolders;
        RootFolderIds = rootFolderIds;
        MaxContentRating = maxContentRating;
    }

    /// <summary>
    /// Grants a background job or a test full, user-less access. Not reachable from a request: the
    /// middleware only ever calls <see cref="Set"/>.
    /// </summary>
    public void SetSystem()
    {
        IsAuthenticated = true;
        UserId = 0;
        UserName = "system";
        Permissions = MakiPermission.Admin;
        AllRootFolders = true;
        RootFolderIds = NoFolders;
        MaxContentRating = string.Empty;
    }
}
