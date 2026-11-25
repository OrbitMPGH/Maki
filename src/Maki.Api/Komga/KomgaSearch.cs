using System.Text.RegularExpressions;

namespace Maki.Api.Komga;

/// <summary>The body of Komga's list API (<c>POST series/list</c>, <c>POST books/list</c>, Komga 1.19+).</summary>
public sealed record KomgaSearch(KomgaCondition? Condition = null, string? FullTextSearch = null);

/// <summary>
/// One node of a Komga search condition: either a group (<c>allOf</c>, <c>anyOf</c>) or a single
/// field test. Fields Maki has no equivalent for are not declared and so are ignored.
/// </summary>
public sealed record KomgaCondition(
    List<KomgaCondition>? AllOf = null,
    List<KomgaCondition>? AnyOf = null,
    KomgaOperator<string>? Deleted = null,
    KomgaOperator<string>? LibraryId = null,
    KomgaOperator<string>? CollectionId = null,
    KomgaOperator<string>? ReadListId = null,
    KomgaOperator<string>? ReadStatus = null,
    KomgaOperator<string>? SeriesStatus = null,
    KomgaOperator<string>? Genre = null,
    KomgaOperator<string>? Tag = null,
    KomgaOperator<string>? Publisher = null,
    KomgaOperator<KomgaAuthorMatch>? Author = null);

public sealed record KomgaOperator<T>(string? Operator = null, T? Value = default);

public sealed record KomgaAuthorMatch(string? Name = null, string? Role = null);

/// <summary>
/// Turns a list API search into the flat filters the <c>GET</c> lists take: every <c>allOf</c> term
/// narrows, and an <c>anyOf</c> over one field is that field's list of accepted values, which is
/// the shape the Mihon extension sends. Terms with no flat form (an <c>anyOf</c> across fields, an
/// operator other than <c>is</c>) are left out, so the result is wider rather than wrongly empty.
/// </summary>
public static partial class KomgaSearchQuery
{
    private const string LibraryKey = "libraryId";
    private const string ReadStatusKey = "readStatus";
    private const string StatusKey = "seriesStatus";
    private const string GenreKey = "genre";
    private const string TagKey = "tag";
    private const string PublisherKey = "publisher";
    private const string AuthorKey = "author";

    public static KomgaSeriesQuery Series(KomgaSearch? search, string? sort)
    {
        var terms = Collect(search?.Condition);
        return new KomgaSeriesQuery(
            Text(search?.FullTextSearch), terms.Libraries, terms[ReadStatusKey], terms[StatusKey], terms[GenreKey],
            terms[TagKey], terms[PublisherKey], terms[AuthorKey], sort);
    }

    public static KomgaBookQuery Books(KomgaSearch? search, string? sort)
    {
        var terms = Collect(search?.Condition);
        return new KomgaBookQuery(Text(search?.FullTextSearch), terms.Libraries, terms[ReadStatusKey], sort);
    }

    /// <summary>Komga's full text search is Lucene, so clients escape its syntax; Maki matches the plain text.</summary>
    private static string? Text(string? fullTextSearch) =>
        string.IsNullOrWhiteSpace(fullTextSearch) ? null : LuceneEscape().Replace(fullTextSearch, "$1").Trim();

    private static Terms Collect(KomgaCondition? condition)
    {
        var terms = new Terms();
        if (condition is not null)
        {
            Visit(condition, terms);
        }

        return terms;
    }

    private static void Visit(KomgaCondition condition, Terms terms)
    {
        foreach (var child in condition.AllOf ?? [])
        {
            Visit(child, terms);
        }

        if (condition.AnyOf is { Count: > 0 } alternatives)
        {
            var leaves = alternatives.Select(Leaf).ToList();
            if (leaves.All(l => l is not null) && leaves.Select(l => l!.Value.Key).Distinct().Count() == 1)
            {
                foreach (var (key, value) in leaves.Select(l => l!.Value))
                {
                    terms.Add(key, value);
                }
            }
        }

        // Maki serves no deleted series and has no collections or read lists.
        if (condition.Deleted?.Operator == "isTrue" || Is(condition.CollectionId) is not null ||
            Is(condition.ReadListId) is not null)
        {
            terms.MatchesNothing = true;
        }

        if (Leaf(condition) is { } leaf)
        {
            terms.Add(leaf.Key, leaf.Value);
        }
    }

    private static (string Key, string Value)? Leaf(KomgaCondition c)
    {
        if (c.AllOf is { Count: > 0 } || c.AnyOf is { Count: > 0 })
        {
            return null;
        }

        if (Is(c.Author) is { Name: { Length: > 0 } name } author)
        {
            return (AuthorKey, $"{name},{author.Role}");
        }

        return (Is(c.LibraryId), Is(c.ReadStatus), Is(c.SeriesStatus), Is(c.Genre), Is(c.Tag), Is(c.Publisher)) switch
        {
            ({ } v, _, _, _, _, _) => (LibraryKey, v),
            (_, { } v, _, _, _, _) => (ReadStatusKey, v),
            (_, _, { } v, _, _, _) => (StatusKey, v),
            (_, _, _, { } v, _, _) => (GenreKey, v),
            (_, _, _, _, { } v, _) => (TagKey, v),
            (_, _, _, _, _, { } v) => (PublisherKey, v),
            _ => null,
        };
    }

    private static T? Is<T>(KomgaOperator<T>? op) where T : class =>
        op is { Operator: "is", Value: { } value } ? value : null;

    [GeneratedRegex(@"\\(.)")]
    private static partial Regex LuceneEscape();

    private sealed class Terms
    {
        private readonly Dictionary<string, List<string>> _values = [];

        public bool MatchesNothing { get; set; }

        public List<string>? this[string key] => _values.GetValueOrDefault(key);

        /// <summary>An id no library has stands in for "nothing matches", as an unparseable one already does.</summary>
        public List<int>? Libraries => MatchesNothing
            ? [-1]
            : this[LibraryKey]?.Select(v => int.TryParse(v, out var id) ? id : -1).ToList();

        public void Add(string key, string value)
        {
            if (!_values.TryGetValue(key, out var list))
            {
                _values[key] = list = [];
            }

            list.Add(value);
        }
    }
}
