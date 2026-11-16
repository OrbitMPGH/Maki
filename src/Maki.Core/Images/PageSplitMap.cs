using System.Globalization;

namespace Maki.Core.Images;

/// <summary>
/// Which pages of an archive a split turned into several, by their index before the split. Every
/// other page maps one to one, so a stored page index (progress, bookmarks, notes) can be carried
/// across the split and back again by an undo. Stored as "12:3,40:2".
/// </summary>
public sealed class PageSplitMap
{
    private readonly SortedDictionary<int, int> _pieces;

    public PageSplitMap(IEnumerable<KeyValuePair<int, int>> pieces)
    {
        _pieces = new SortedDictionary<int, int>();
        foreach (var (page, count) in pieces)
        {
            if (page < 0 || count < 1)
            {
                throw new ArgumentOutOfRangeException(nameof(pieces), $"{page}:{count}");
            }

            if (count > 1)
            {
                _pieces[page] = count;
            }
        }
    }

    /// <summary>Old page index to the number of pages it became; only pages that became more than one.</summary>
    public IReadOnlyDictionary<int, int> Pieces => _pieces;

    public bool IsEmpty => _pieces.Count == 0;

    public int PiecesOf(int oldIndex) => _pieces.GetValueOrDefault(oldIndex, 1);

    /// <summary>The index of the first page <paramref name="oldIndex"/> became. Also takes the old page count, giving the new one.</summary>
    public int NewStart(int oldIndex)
    {
        var start = oldIndex;
        foreach (var (page, count) in _pieces)
        {
            if (page >= oldIndex) break;
            start += count - 1;
        }

        return start;
    }

    /// <summary>The page before the split that <paramref name="newIndex"/> is a piece of.</summary>
    public int OldIndex(int newIndex)
    {
        var shift = 0;
        foreach (var (page, count) in _pieces)
        {
            if (page + shift > newIndex) break;
            if (newIndex < page + shift + count) return page;
            shift += count - 1;
        }

        return newIndex - shift;
    }

    /// <summary>
    /// A position on an old page, as a page and a share of its height, on the split pages: the piece the
    /// share falls in and the share of that piece. Pieces are taken as equally tall.
    /// </summary>
    public (int Page, double Offset) Forward(int oldIndex, double offset)
    {
        var count = PiecesOf(oldIndex);
        var start = NewStart(oldIndex);
        if (count == 1)
        {
            return (start, offset);
        }

        var scaled = Math.Clamp(offset, 0, 1) * count;
        var piece = Math.Min((int)Math.Floor(scaled), count - 1);
        return (start + piece, Math.Clamp(scaled - piece, 0, 1));
    }

    /// <summary>The inverse of <see cref="Forward"/>.</summary>
    public (int Page, double Offset) Backward(int newIndex, double offset)
    {
        var old = OldIndex(newIndex);
        var count = PiecesOf(old);
        if (count == 1)
        {
            return (old, offset);
        }

        var piece = newIndex - NewStart(old);
        return (old, Math.Clamp((piece + Math.Clamp(offset, 0, 1)) / count, 0, 1));
    }

    /// <summary>This split followed by <paramref name="later"/>, which was made on the pages this one produced.</summary>
    public PageSplitMap Then(PageSplitMap later)
    {
        if (later.IsEmpty) return this;
        if (IsEmpty) return later;

        var composed = new Dictionary<int, int>();
        var oldPages = new HashSet<int>(_pieces.Keys);
        foreach (var (page, count) in later.Pieces)
        {
            oldPages.Add(OldIndex(page));
        }

        foreach (var old in oldPages)
        {
            var start = NewStart(old);
            var total = 0;
            for (var i = 0; i < PiecesOf(old); i++)
            {
                total += later.PiecesOf(start + i);
            }

            composed[old] = total;
        }

        return new PageSplitMap(composed);
    }

    public override string ToString() =>
        string.Join(',', _pieces.Select(p =>
            $"{p.Key.ToString(CultureInfo.InvariantCulture)}:{p.Value.ToString(CultureInfo.InvariantCulture)}"));

    public static PageSplitMap? Parse(string? value)
    {
        if (value is null)
        {
            return null;
        }

        var pieces = new Dictionary<int, int>();
        foreach (var part in value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var pair = part.Split(':');
            if (pair.Length != 2 ||
                !int.TryParse(pair[0], NumberStyles.None, CultureInfo.InvariantCulture, out var page) ||
                !int.TryParse(pair[1], NumberStyles.None, CultureInfo.InvariantCulture, out var count) ||
                count < 1 || !pieces.TryAdd(page, count))
            {
                return null;
            }
        }

        return new PageSplitMap(pieces);
    }
}
