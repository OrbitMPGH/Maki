using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Maki.Metadata.MangaBaka;

/// <summary>
/// Which series are the same work. Connected components over the dump's relation edges, so a
/// recommender can stop offering somebody volume two of what they are already reading.
///
/// <para>
/// Measured before it existed: on held-out reading lists, <b>one pick in five</b> sat in a seed's
/// own franchise. Deduplication was an exact normalized-title match, which catches "Berserk" against
/// "BERSERK" and nothing else, and <c>relationships_v2</c> (149,319 typed edges over 22 relation
/// types) had no readers at all. <c>MangaBakaLocalStore.GetRelatedAsync</c> reads five of the eight
/// legacy flat columns, which is a different job: it BUILDS the "Related" list rather than keeping
/// things out of the "Similar" one, and it only looks one hop.
/// </para>
///
/// <para>
/// One definition, deliberately, shared by the ranker and by the eval's franchise metric. Two copies
/// would let the number that measures the problem disagree with the code that fixes it.
/// </para>
/// </summary>
public static class FranchiseGraph
{
    /// <summary>
    /// The relation types that mean "part of the same work".
    ///
    /// <para>
    /// <c>adaptation</c>, <c>source</c> and <c>parody</c> are deliberately OUT. A manga adapted from
    /// the same novel is a legitimately different reading experience, and suppressing it would make
    /// the recommender worse rather than tidier. So is <c>alternative</c>: an alternate version is
    /// often a different serialization somebody may well want.
    /// </para>
    /// </summary>
    private static readonly HashSet<string> SameWork = new(StringComparer.OrdinalIgnoreCase)
    {
        "sequel", "prequel", "side_story", "main", "parent", "contains", "compilation", "spin_off",
    };

    /// <summary>The legacy flat columns, which cover rows <c>relationships_v2</c> does not.</summary>
    private static readonly string[] FlatColumns =
    [
        "relationships_sequel", "relationships_prequel", "relationships_spin_off",
        "relationships_side_story", "relationships_main_story",
    ];

    /// <summary>
    /// Series id to component id, for every series that has at least one same-work edge. A series
    /// missing from the result is in no franchise, which is the common case and must never be
    /// confused with being in component 0.
    ///
    /// <para>
    /// Unions run over every id the dump mentions, indexed or not: a franchise linked through a
    /// volume that was never embedded still has to resolve to one component, or the collapse leaks
    /// exactly the rows it exists to catch.
    /// </para>
    /// </summary>
    public static Dictionary<long, int> Build(SqliteConnection conn, string seriesTable = "series")
    {
        var union = new UnionFind();
        var columns = string.Join(", ", FlatColumns);

        using (var cmd = conn.CreateCommand())
        {
            // Only rows carrying an edge. relationships_v2 covers 15% of the dump, so this reads a
            // small fraction of it rather than every row.
            var anyRelation = string.Join(" OR ", FlatColumns.Select(c => $"{c} IS NOT NULL"));
            cmd.CommandText =
                $"SELECT id, relationships_v2, {columns} FROM {seriesTable} " +
                $"WHERE relationships_v2 IS NOT NULL OR {anyRelation}";
            cmd.CommandTimeout = 600;
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var id = reader.GetInt64(0);
                if (!reader.IsDBNull(1))
                {
                    AddTyped(reader.GetString(1), id, union);
                }

                for (var ordinal = 2; ordinal < 2 + FlatColumns.Length; ordinal++)
                {
                    if (!reader.IsDBNull(ordinal))
                    {
                        AddBare(reader.GetString(ordinal), id, union);
                    }
                }
            }
        }

        // Component ids are assigned densely from the roots, so they are small ints an index column
        // can hold rather than sparse series ids.
        var componentOf = new Dictionary<long, int>(union.Count);
        var idByRoot = new Dictionary<long, int>();
        foreach (var member in union.Members)
        {
            var root = union.Find(member);
            if (!idByRoot.TryGetValue(root, out var component))
            {
                idByRoot[root] = component = idByRoot.Count;
            }

            componentOf[member] = component;
        }

        return componentOf;
    }

    private static void AddTyped(string json, long from, UnionFind union)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
            {
                return;
            }

            foreach (var edge in doc.RootElement.EnumerateArray())
            {
                if (edge.ValueKind == JsonValueKind.Object
                    && edge.TryGetProperty("relation_type", out var type)
                    && type.GetString() is { } relation
                    && SameWork.Contains(relation)
                    && edge.TryGetProperty("to_series_id", out var to)
                    && to.TryGetInt64(out var target))
                {
                    union.Union(from, target);
                }
            }
        }
        catch (JsonException)
        {
            // One malformed blob is a dump defect, not a reason to lose the graph.
        }
    }

    private static void AddBare(string json, long from, UnionFind union)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
            {
                return;
            }

            foreach (var target in doc.RootElement.EnumerateArray())
            {
                if (target.TryGetInt64(out var id))
                {
                    union.Union(from, id);
                }
            }
        }
        catch (JsonException)
        {
            // Same reason.
        }
    }

    /// <summary>Dictionary-backed union-find over sparse MangaBaka ids, with path halving.</summary>
    private sealed class UnionFind
    {
        private readonly Dictionary<long, long> _parent = [];

        public int Count => _parent.Count;

        public IEnumerable<long> Members => _parent.Keys;

        public long Find(long x)
        {
            var root = x;
            while (_parent.TryGetValue(root, out var next) && next != root)
            {
                root = next;
            }

            while (_parent.TryGetValue(x, out var next) && next != x)
            {
                _parent[x] = root;
                x = next;
            }

            return root;
        }

        public void Union(long a, long b)
        {
            _parent.TryAdd(a, a);
            _parent.TryAdd(b, b);
            var ra = Find(a);
            var rb = Find(b);
            if (ra == rb)
            {
                return;
            }

            // Smaller id wins, so a component's representative does not depend on insertion order
            // and two builds over the same dump produce the same components.
            if (ra < rb)
            {
                _parent[rb] = ra;
            }
            else
            {
                _parent[ra] = rb;
            }
        }
    }
}
