using System.Collections;
using System.Diagnostics.CodeAnalysis;
using Maki.Metadata.Catalogue;

namespace Maki.Metadata.Embedding;

/// <summary>
/// A case-insensitive name to id map over names numbered 0 to n-1, stored as one character blob
/// and an order sorted by <see cref="StringComparison.OrdinalIgnoreCase"/>. For the vector index's
/// 76k author names, where a dictionary of separate strings cost about three times as much. A
/// lookup is a binary search; enumerating materializes strings and is not meant for a hot path.
/// </summary>
internal sealed class NameMap : IReadOnlyDictionary<string, int>
{
    private readonly TextBlob _names;
    private readonly int[] _order;

    /// <param name="namesById">Each name at the index of its id.</param>
    public NameMap(IReadOnlyList<string> namesById)
    {
        _names = new TextBlob(namesById);
        _order = new int[namesById.Count];
        for (var i = 0; i < _order.Length; i++)
        {
            _order[i] = i;
        }

        var names = _names;
        Array.Sort(_order, (a, b) => names[a].CompareTo(names[b], StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Builds the map from an interning dictionary whose values run 0 to n-1.</summary>
    public static NameMap From(IReadOnlyDictionary<string, int> interned)
    {
        var byId = new string[interned.Count];
        foreach (var (name, id) in interned)
        {
            byId[id] = name;
        }

        return new NameMap(byId);
    }

    public int Count => _order.Length;

    public bool TryGetValue(string key, [MaybeNullWhen(false)] out int value)
    {
        var low = 0;
        var high = _order.Length - 1;
        while (low <= high)
        {
            var mid = low + ((high - low) >> 1);
            var order = _names[_order[mid]].CompareTo(key, StringComparison.OrdinalIgnoreCase);
            if (order == 0)
            {
                value = _order[mid];
                return true;
            }

            if (order < 0)
            {
                low = mid + 1;
            }
            else
            {
                high = mid - 1;
            }
        }

        value = -1;
        return false;
    }

    public bool ContainsKey(string key) => TryGetValue(key, out _);

    public int this[string key] => TryGetValue(key, out var value) ? value : throw new KeyNotFoundException(key);

    public IEnumerable<string> Keys => Enumerable.Range(0, Count).Select(i => new string(_names[i]));

    public IEnumerable<int> Values => Enumerable.Range(0, Count);

    public IEnumerator<KeyValuePair<string, int>> GetEnumerator()
    {
        for (var i = 0; i < Count; i++)
        {
            yield return new KeyValuePair<string, int>(new string(_names[i]), i);
        }
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
