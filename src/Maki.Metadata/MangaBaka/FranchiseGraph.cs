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
        var builder = new Builder();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = EdgeQuery(seriesTable, ranged: false);
        cmd.CommandTimeout = 600;
        builder.Add(cmd);
        return builder.Components();
    }

    /// <summary>
    /// The rows carrying an edge, optionally limited to <c>rowid BETWEEN $lo AND $hi</c> for a caller
    /// walking the table in ranges. Ranges in ascending order feed the same edges in the same order
    /// as one full read, so they produce the same components and the same component ids.
    /// </summary>
    public static string EdgeQuery(string seriesTable, bool ranged)
    {
        var anyRelation = string.Join(" OR ", FlatColumns.Select(c => $"{c} IS NOT NULL"));
        return $"SELECT id, relationships_v2, {string.Join(", ", FlatColumns)} FROM {seriesTable} " +
               $"WHERE (relationships_v2 IS NOT NULL OR {anyRelation})" +
               (ranged ? " AND rowid BETWEEN $lo AND $hi" : string.Empty);
    }

    /// <summary>Whether a table has every column <see cref="EdgeQuery"/> reads.</summary>
    public static bool HasEdgeColumns(IReadOnlySet<string> columns) =>
        columns.Contains("relationships_v2") && FlatColumns.All(columns.Contains);

    /// <summary>
    /// The persisted result of <see cref="Build"/>, written once when a dump is installed, so a load
    /// reads ~150k pairs instead of parsing every relation blob in the 3.5 GB file. Null when the
    /// dump has no such table yet.
    /// </summary>
    public const string TableName = "maki_franchise";

    public static Dictionary<long, int>? Read(SqliteConnection conn, string schema = "main")
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT 1 FROM {schema}.sqlite_master WHERE type = 'table' AND name = '{TableName}'";
        if (cmd.ExecuteScalar() is null)
        {
            return null;
        }

        cmd.CommandText = $"SELECT id, component FROM {schema}.{TableName}";
        var componentOf = new Dictionary<long, int>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            componentOf[reader.GetInt64(0)] = reader.GetInt32(1);
        }

        return componentOf;
    }

    /// <summary>Replaces <see cref="TableName"/> with <paramref name="componentOf"/>.</summary>
    public static void Write(SqliteConnection conn, SqliteTransaction tx, IReadOnlyDictionary<long, int> componentOf)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = $"DROP TABLE IF EXISTS {TableName}";
        cmd.ExecuteNonQuery();
        cmd.CommandText = $"CREATE TABLE {TableName} (id INTEGER PRIMARY KEY, component INTEGER NOT NULL)";
        cmd.ExecuteNonQuery();

        cmd.CommandText = $"INSERT INTO {TableName} (id, component) VALUES ($id, $component)";
        var id = cmd.Parameters.Add("$id", SqliteType.Integer);
        var component = cmd.Parameters.Add("$component", SqliteType.Integer);
        foreach (var (member, of) in componentOf)
        {
            id.Value = member;
            component.Value = of;
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>Accumulates edges from one or more reads of <see cref="EdgeQuery"/>.</summary>
    public sealed class Builder
    {
        private readonly UnionFind _union = new();

        public void Add(SqliteCommand edges)
        {
            using var reader = edges.ExecuteReader();
            while (reader.Read())
            {
                var id = reader.GetInt64(0);
                if (!reader.IsDBNull(1))
                {
                    AddTyped(reader.GetString(1), id, _union);
                }

                for (var ordinal = 2; ordinal < 2 + FlatColumns.Length; ordinal++)
                {
                    if (!reader.IsDBNull(ordinal))
                    {
                        AddBare(reader.GetString(ordinal), id, _union);
                    }
                }
            }
        }

        /// <summary>
        /// Component ids are assigned densely from the roots, so they are small ints an index
        /// column can hold rather than sparse series ids.
        /// </summary>
        public Dictionary<long, int> Components()
        {
            var componentOf = new Dictionary<long, int>(_union.Count);
            var idByRoot = new Dictionary<long, int>();
            foreach (var member in _union.Members)
            {
                var root = _union.Find(member);
                if (!idByRoot.TryGetValue(root, out var component))
                {
                    idByRoot[root] = component = idByRoot.Count;
                }

                componentOf[member] = component;
            }

            return componentOf;
        }
    }

    /// <summary>The flat relation columns that mean the same work, for a caller reading one row.</summary>
    public static IReadOnlyList<string> SameWorkColumns => FlatColumns;

    /// <summary>
    /// The same-work ids one row points at, off its typed blob plus its flat columns, so a caller
    /// walking the dump a few rows at a time uses the same relation-type set the components do.
    /// </summary>
    public static IEnumerable<long> SameWorkTargets(string? typed, IEnumerable<string?> flat) =>
        Targets(typed, flat, SameWork.Contains);

    /// <summary>
    /// Every id one row points at, whatever the relation type. A parody, an adaptation or an
    /// alternative edition is not the same work, but a release title can still name it, which is
    /// what the torrent title matcher asks this for.
    /// </summary>
    public static IEnumerable<long> RelatedTargets(string? typed, IEnumerable<string?> flat) =>
        Targets(typed, flat, _ => true);

    private static IEnumerable<long> Targets(string? typed, IEnumerable<string?> flat, Func<string, bool> relation)
    {
        if (typed is not null)
        {
            foreach (var id in TypedTargets(typed, relation))
            {
                yield return id;
            }
        }

        foreach (var json in flat)
        {
            if (json is null)
            {
                continue;
            }

            foreach (var id in BareTargets(json))
            {
                yield return id;
            }
        }
    }

    private static void AddTyped(string json, long from, UnionFind union)
    {
        foreach (var target in TypedTargets(json, SameWork.Contains))
        {
            union.Union(from, target);
        }
    }

    private static void AddBare(string json, long from, UnionFind union)
    {
        foreach (var target in BareTargets(json))
        {
            union.Union(from, target);
        }
    }

    private static List<long> TypedTargets(string json, Func<string, bool> relation)
    {
        var targets = new List<long>();
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
            {
                return targets;
            }

            foreach (var edge in doc.RootElement.EnumerateArray())
            {
                if (edge.ValueKind == JsonValueKind.Object
                    && edge.TryGetProperty("relation_type", out var type)
                    && type.GetString() is { } kind
                    && relation(kind)
                    && edge.TryGetProperty("to_series_id", out var to)
                    && to.TryGetInt64(out var target))
                {
                    targets.Add(target);
                }
            }
        }
        catch (JsonException)
        {
            // One malformed blob is a dump defect, not a reason to lose the graph.
        }

        return targets;
    }

    private static List<long> BareTargets(string json)
    {
        var targets = new List<long>();
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
            {
                return targets;
            }

            foreach (var target in doc.RootElement.EnumerateArray())
            {
                if (target.TryGetInt64(out var id))
                {
                    targets.Add(id);
                }
            }
        }
        catch (JsonException)
        {
            // Same reason.
        }

        return targets;
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
