using Maki.Core.Scrobbling;

namespace Maki.Api.Services;

/// <summary>
/// Indexes library series by normalized name for reverse-matching Kavita's series names. A key that
/// more than one series maps to is dropped entirely instead of keeping whichever came first:
/// silently picking one would write reads or cross-ids against a series that may be the other one.
/// </summary>
internal static class LibraryNameIndex
{
    public static Dictionary<string, T> Build<T>(
        IEnumerable<T> rows, Func<T, int> idOf, Func<T, IEnumerable<string?>> namesOf,
        Action<string>? onCollision = null)
    {
        var index = new Dictionary<string, T>();
        var collisions = new HashSet<string>();
        foreach (var row in rows)
        {
            foreach (var name in namesOf(row))
            {
                var key = ScrobbleMatching.NormalizeTitle(name ?? "");
                if (key.Length == 0)
                {
                    continue;
                }

                if (index.TryGetValue(key, out var existing))
                {
                    if (idOf(existing) != idOf(row))
                    {
                        collisions.Add(key);
                    }
                }
                else
                {
                    index[key] = row;
                }
            }
        }

        foreach (var key in collisions)
        {
            index.Remove(key);
            onCollision?.Invoke(key);
        }

        return index;
    }
}
