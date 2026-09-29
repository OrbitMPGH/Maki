using Maki.Core.Recommendations;
using Maki.Metadata.Catalogue;
using Maki.Metadata.MangaBaka;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace Maki.Metadata.Tests;

/// <summary>
/// <see cref="CreditFilter"/>: a filter's named creators become the MangaBaka ids the scans narrow to.
/// </summary>
public class CreditFilterTests : IDisposable
{
    private readonly DumpDbBuilder _db = new();

    public void Dispose() => _db.Dispose();

    private CreditIndex Build()
    {
        _db.AddSeries(1, "Uzumaki", authorsJson: """["Junji Ito"]""", artistsJson: """["Junji Ito"]""");
        _db.AddSeries(2, "Tomie", authorsJson: """["Junji Ito"]""");
        _db.AddSeries(3, "Berserk", authorsJson: """["Kentaro Miura"]""",
            publishersJson: """[{"name": "Hakusensha", "note": null, "type": "Original"}]""");
        _db.AddSeries(4, "Other", authorsJson: """["Someone Else"]""",
            publishersJson: """[{"name": "Shueisha", "note": null, "type": "Original"}]""");

        using var conn = new SqliteConnection($"Data Source={_db.Path};Mode=ReadOnly;Pooling=False");
        conn.Open();
        return CreditIndex.Build(conn, NullLogger.Instance);
    }

    [Fact]
    public void Credits_of_different_roles_union()
    {
        var filters = new RecommendationFilters(Credits:
        [
            new CatalogueCredit("Junji Ito", CatalogueCredits.Author),
            new CatalogueCredit("Hakusensha", CatalogueCredits.Studio),
        ]);

        var resolved = CreditFilter.Resolve(filters, Build(), maxDistance: 1);

        Assert.Equal([1L, 2L, 3L], resolved.CreditIds!.Order());
    }

    [Fact]
    public void A_role_the_name_does_not_hold_matches_nothing()
    {
        var filters = new RecommendationFilters(Credits: [new CatalogueCredit("Junji Ito", CatalogueCredits.Studio)]);

        Assert.Empty(CreditFilter.Resolve(filters, Build(), maxDistance: 1).CreditIds!);
    }

    [Fact]
    public void An_unknown_name_drops_out_while_the_others_still_resolve()
    {
        var filters = new RecommendationFilters(Credits:
        [
            new CatalogueCredit("Nobody At All"),
            new CatalogueCredit("Ito Junji"),
        ]);

        Assert.Equal([1L, 2L], CreditFilter.Resolve(filters, Build(), maxDistance: 1).CreditIds!.Order());
    }

    [Fact]
    public void No_credits_leaves_the_filter_unrestricted_and_strips_stale_ids()
    {
        var filters = new RecommendationFilters(CreditIds: [9]);

        Assert.Null(CreditFilter.Resolve(filters, Build(), maxDistance: 1).CreditIds);
    }

    [Fact]
    public void Without_an_index_named_credits_match_nothing()
    {
        var filters = new RecommendationFilters(Credits: [new CatalogueCredit("Junji Ito")]);

        Assert.Empty(CreditFilter.Resolve(filters, null, maxDistance: 1).CreditIds!);
    }
}
