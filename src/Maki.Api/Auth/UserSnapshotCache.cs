using Maki.Core.Security;
using Microsoft.Extensions.Caching.Memory;

namespace Maki.Api.Auth;

public sealed record UserSnapshot(
    int Id,
    string UserName,
    MakiPermission Permissions,
    bool AllRootFolders,
    IReadOnlySet<int> RootFolderIds,
    string MaxContentRating);

/// <summary>
/// Short-lived cache of what <see cref="CurrentUserMiddleware"/> loads per request. Anything that
/// writes a user's permissions, root folder grants, content rating, disabled flag or row calls
/// <see cref="Evict"/> after saving, so those changes still apply on the next request.
/// </summary>
public interface IUserSnapshotCache
{
    UserSnapshot? Get(int userId);

    /// <summary>Read before loading from the database and passed back to <see cref="Set"/>.</summary>
    long Generation { get; }

    /// <summary>Stores nothing if an eviction happened since <paramref name="generation"/> was read.</summary>
    void Set(UserSnapshot snapshot, long generation);

    void Evict(int userId);
}

public sealed class UserSnapshotCache(IMemoryCache cache) : IUserSnapshotCache
{
    private static readonly TimeSpan Ttl = TimeSpan.FromSeconds(10);
    private readonly Lock _gate = new();
    private long _generation;

    public long Generation => Interlocked.Read(ref _generation);

    public UserSnapshot? Get(int userId) =>
        cache.TryGetValue(Key(userId), out UserSnapshot? snapshot) ? snapshot : null;

    public void Set(UserSnapshot snapshot, long generation)
    {
        lock (_gate)
        {
            if (generation == _generation)
            {
                cache.Set(Key(snapshot.Id), snapshot, Ttl);
            }
        }
    }

    public void Evict(int userId)
    {
        lock (_gate)
        {
            Interlocked.Increment(ref _generation);
            cache.Remove(Key(userId));
        }
    }

    private static string Key(int userId) => $"user-snapshot:{userId}";
}
