using Maki.Api.Services;
using Maki.Core.Configuration;
using Maki.Core.Entities;

namespace Maki.Api.Tests;

public class SourceOutageTests : IDisposable
{
    private readonly TestDb fixture = new();
    public void Dispose() => fixture.Dispose();

    private static SourceMapping Mapping(string source, string? error, DateTime? refreshed = null) =>
        new()
        {
            SourceName = source,
            SourceSeriesId = Guid.NewGuid().ToString("N"),
            LastError = error,
            LastRefresh = refreshed ?? (error == null ? DateTime.UtcNow : null)
        };

    [Fact]
    public void Enough_failures_on_one_source_become_a_single_outage()
    {
        var outages = SourceOutages.Detect([
            Mapping("mangadex", "503"),
            Mapping("mangadex", "503"),
            Mapping("mangadex", "timeout"),
            Mapping("mangadex", null)
        ]);

        var outage = Assert.Single(outages);
        Assert.Equal("mangadex", outage.SourceName);
        Assert.Equal(3, outage.Failing);
        Assert.Equal(4, outage.Attempted);
        Assert.False(outage.Unavailable);
        // The message every failing series agrees on, not whichever one happens to come first.
        Assert.Equal("503", outage.Sample);
    }

    [Fact]
    public void A_source_no_attempted_mapping_reaches_is_unavailable_rather_than_unstable()
    {
        var outage = Assert.Single(SourceOutages.Detect([
            Mapping("asura", "403"), Mapping("asura", "403"), Mapping("asura", "403")
        ]));

        Assert.True(outage.Unavailable);
    }

    [Fact]
    public void Two_broken_series_are_not_an_outage()
    {
        Assert.Empty(SourceOutages.Detect([Mapping("mangadex", "404"), Mapping("mangadex", "404")]));
    }

    [Fact]
    public void A_handful_of_failures_across_a_large_library_is_not_an_outage()
    {
        var mappings = Enumerable.Range(0, 4).Select(_ => Mapping("mangadex", "404"))
            .Concat(Enumerable.Range(0, 100).Select(_ => Mapping("mangadex", null)))
            .ToList();

        Assert.Empty(SourceOutages.Detect(mappings));
    }

    [Fact]
    public void Failures_are_grouped_per_source()
    {
        var outages = SourceOutages.Detect([
            Mapping("mangadex", "503"), Mapping("mangadex", "503"), Mapping("mangadex", "503"),
            Mapping("asura", "403"), Mapping("asura", "403"), Mapping("asura", "403")
        ]);

        Assert.Equal(["asura", "mangadex"], outages.Select(o => o.SourceName));
    }

    [Fact]
    public void Mappings_the_refresh_has_never_tried_do_not_hold_a_source_below_the_threshold()
    {
        // Four failing against ten that were never refreshed at all: the untouched ones say nothing
        // about the source, so HealthCheckService never hands them to Detect in the first place.
        var attempted = Enumerable.Range(0, 4).Select(_ => Mapping("mangadex", "503")).ToList();

        Assert.Single(SourceOutages.Detect(attempted));
    }

    private HealthCheckService Service(Maki.Data.MakiDbContext db, FakeAppSettings? settings = null)
    {
        settings ??= new FakeAppSettings();
        // "false" keeps the dump check out, which is the one part of GetIssuesAsync that would need
        // the MangaBaka services.
        settings.Set(SettingKeys.MangaBakaUseLocalDb, "false");
        return new HealthCheckService(db, settings, new SourceAvailability(settings), null!);
    }

    [Fact]
    public async Task An_outage_rolls_up_the_per_series_failures_it_covers()
    {
        for (var i = 0; i < 3; i++)
        {
            fixture.SeedSeries($"Down {i}", mappings: Mapping("mangadex", "503 Service Unavailable"));
        }
        fixture.SeedSeries("Lone", mappings: Mapping("asura", "404 Not Found"));

        using var db = fixture.NewContext();
        var issues = await Service(db).GetIssuesAsync();

        var outage = issues.Single(i => i.Type == "source");
        Assert.Equal("source:mangadex", outage.Key);
        Assert.Contains("unavailable", outage.Message);
        Assert.Equal("error", outage.Severity);

        // Covered failures are still emitted so HealthMonitor can retire the rows it wrote for
        // them, but marked so it does so silently rather than announcing three recoveries.
        Assert.All(issues.Where(i => i.Type == "sourceMapping" && i.Message.Contains("mangadex")),
            i => Assert.True(i.RolledUp));

        // The one failure that is not part of an outage still reports per series, on its own.
        var lone = issues.Single(i => i.Type == "sourceMapping" && !i.RolledUp);
        Assert.Contains("asura", lone.Message);
    }

    [Fact]
    public async Task A_globally_switched_off_source_produces_no_outage()
    {
        for (var i = 0; i < 3; i++)
        {
            fixture.SeedSeries($"Down {i}", mappings: Mapping("mangadex", "503"));
        }

        using var db = fixture.NewContext();
        var settings = new FakeAppSettings().Set(SettingKeys.SourcesDisabled, "mangadex");
        var issues = await Service(db, settings).GetIssuesAsync();

        Assert.DoesNotContain(issues, i => i.Type is "source" or "sourceMapping");
    }
}
