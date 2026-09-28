using Maki.Core.Recommendations;
using Maki.Metadata.Embedding;
using Maki.Metadata.MangaBaka;
using Xunit;

namespace Maki.Metadata.Tests;

public class CatalogueRuleTests
{
    private const int Dim = 4;

    private static readonly (int Id, string Name, string Path)[] Vocab =
    [
        (1, "School", "Locations > School"),
        (2, "College", "Locations > School > College"),
        (3, "High School", "Locations > School > High School"),
        (4, "Primarily Adult Cast", "Character Types > Cast > Primarily Adult Cast"),
        (5, "Primarily Teen Cast", "Character Types > Cast > Primarily Teen Cast"),
        (6, "Adult", "Sexual Content > Intensity > Adult"),
    ];

    // Row ids are 100 + position.
    private static readonly (string[] Genres, (string Tag, string Weight)[] Tags)[] Rows =
    [
        (["Romance"], [("College", "core"), ("Primarily Adult Cast", "defining")]),
        (["Romance"], [("High School", "core"), ("Primarily Teen Cast", "core")]),
        (["Romance", "Comedy"], [("College", "incidental")]),
        (["Action"], [("School", "unweighted")]),
        (["Romance"], [("Adult", "core")]),
    ];

    [Fact]
    public void Any_rule_needs_one_term_and_none_rule_needs_zero()
    {
        var matched = Match(
            new CatalogueRule(CatalogueRules.All, [Genre("Romance")]),
            new CatalogueRule(CatalogueRules.Any, [Tag("College"), Tag("High School")]),
            new CatalogueRule(CatalogueRules.None, [Tag("Primarily Adult Cast")]));

        Assert.Equal([101, 102], matched);
    }

    [Fact]
    public void Subtags_widen_a_parent_to_its_children()
    {
        Assert.Equal([103], Match(new CatalogueRule(CatalogueRules.All, [Tag("School")])));
        Assert.Equal(
            [100, 101, 102, 103],
            Match(new CatalogueRule(CatalogueRules.All, [Tag("School", subtags: true)])));
    }

    [Fact]
    public void Central_only_counts_core_and_defining_weights()
    {
        Assert.Equal(
            [100],
            Match(new CatalogueRule(CatalogueRules.All, [Tag("College", central: true)])));
    }

    [Fact]
    public void Unknown_names_sink_all_rules_and_drop_out_of_any_and_none()
    {
        var index = BuildIndex();
        Assert.True(index.Plan(Filters(new CatalogueRule(CatalogueRules.All, [Tag("Nope")]))).Impossible);
        Assert.True(index.Plan(Filters(new CatalogueRule(CatalogueRules.Any, [Tag("Nope")]))).Impossible);
        Assert.Equal(5, Match(new CatalogueRule(CatalogueRules.None, [Tag("Nope")])).Count);
        Assert.Equal(
            [100, 102],
            Match(new CatalogueRule(CatalogueRules.Any, [Tag("Nope"), Tag("College")])));
    }

    [Fact]
    public void Hidden_terms_survive_the_exact_title_bypass()
    {
        var index = BuildIndex();
        var plan = index.Plan(new RecommendationFilters(
            Rules: [new CatalogueRule(CatalogueRules.All, [Genre("Action")])],
            Hidden: [Tag("Adult")]));
        var fused = new Dictionary<int, double>();

        var ranked = SemanticSearcher.RankCandidates(index, plan, fused, new HashSet<long> { 100, 104 }, 10);

        Assert.Contains(100L, ranked);
        Assert.DoesNotContain(104L, ranked);
    }

    [Fact]
    public void Normalize_drops_blank_terms_unknown_modes_and_empty_rules()
    {
        var normalized = CatalogueRules.Normalize(
        [
            new CatalogueRule("ANY", [new CatalogueTerm("tag", " College "), new CatalogueTerm("tag", "college")]),
            new CatalogueRule("sometimes", [Genre("Romance")]),
            new CatalogueRule(CatalogueRules.None, [new CatalogueTerm("tag", ""), new CatalogueTerm("mood", "x")]),
            new CatalogueRule(CatalogueRules.All, [new CatalogueTerm("genre", "Romance", Subtags: true)]),
        ]);

        Assert.NotNull(normalized);
        Assert.Equal(2, normalized.Count);
        Assert.Equal(CatalogueRules.Any, normalized[0].Mode);
        Assert.Single(normalized[0].Terms);
        Assert.False(normalized[1].Terms[0].Subtags);
    }

    [Fact]
    public void Sql_clause_widens_rules_it_cannot_express()
    {
        using var conn = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=:memory:");
        using var cmd = conn.CreateCommand();
        var clause = Filters(
            new CatalogueRule(CatalogueRules.Any, [Genre("Romance"), Tag("College")]),
            new CatalogueRule(CatalogueRules.None, [Genre("Horror"), Tag("Adult")])).BuildClause(cmd, "s");

        Assert.DoesNotContain("Romance", string.Join(",", cmd.Parameters.Cast<Microsoft.Data.Sqlite.SqliteParameter>().Select(p => p.Value)));
        Assert.Contains("NOT (", clause);
    }

    private static CatalogueTerm Genre(string name) => new(CatalogueRules.Genre, name);

    private static CatalogueTerm Tag(string name, bool subtags = false, bool central = false) =>
        new(CatalogueRules.Tag, name, subtags, central);

    private static RecommendationFilters Filters(params CatalogueRule[] rules) => new(Rules: rules);

    private static List<long> Match(params CatalogueRule[] rules)
    {
        var index = BuildIndex();
        var plan = index.Plan(Filters(rules));
        return Enumerable.Range(0, index.Count)
            .Where(row => index.Matches(row, plan))
            .Select(index.IdAt)
            .ToList();
    }

    private static VectorIndex BuildIndex()
    {
        var count = Rows.Length;
        var ids = Enumerable.Range(0, count).Select(i => 100L + i).ToArray();
        var data = new sbyte[count * Dim];
        var scales = new float[count];
        for (var i = 0; i < count; i++)
        {
            var vector = new float[Dim];
            vector[i % Dim] = 1;
            scales[i] = EmbeddingMath.Quantize(vector, data.AsSpan(i * Dim, Dim));
        }

        var genreIds = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var genres = Rows.Select(r => r.Genres.Select(g =>
        {
            if (!genreIds.TryGetValue(g, out var id))
            {
                genreIds[g] = id = genreIds.Count;
            }

            return id;
        }).ToArray()).ToArray();
        var tagIdByName = Vocab.ToDictionary(v => v.Name, v => v.Id, StringComparer.OrdinalIgnoreCase);
        var blobs = Rows
            .Select(r => TagMath.Pack([.. r.Tags.Select(t => (tagIdByName[t.Tag], TagMath.ClassOf(t.Weight)))]))
            .ToArray();

        return VectorIndex.FromQuantized(
            ids,
            data,
            scales,
            Dim,
            new VectorIndexColumns(
                Enumerable.Repeat(2010, count).ToArray(),
                Enumerable.Repeat(75f, count).ToArray(),
                Enumerable.Repeat(100, count).ToArray(),
                new byte[count],
                new byte[count],
                JaggedInts.From(genres),
                JaggedInts.From(new int[count][].Select(_ => Array.Empty<int>()).ToArray()),
                JaggedInts.From(new int[count][].Select(_ => Array.Empty<int>()).ToArray()),
                Enumerable.Repeat(1000, count).ToArray(),
                blobs,
                new byte[count],
                Enumerable.Repeat(VectorIndex.Unknown, count).ToArray()),
            new VectorIndexVocabularies(
                new Dictionary<string, byte>(StringComparer.OrdinalIgnoreCase) { ["manga"] = 0 },
                new Dictionary<string, byte>(StringComparer.OrdinalIgnoreCase) { ["releasing"] = 0 },
                genreIds,
                new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase),
                Vocab.ToDictionary(v => v.Name, v => new[] { v.Id }, StringComparer.OrdinalIgnoreCase),
                new Dictionary<string, byte>(StringComparer.OrdinalIgnoreCase) { ["safe"] = 0 },
                TagSubtrees.Build(Vocab)));
    }
}
