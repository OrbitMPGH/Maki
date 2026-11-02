using System.Text.Json;
using Maki.Api.Controllers;
using Maki.Core.Entities;
using Maki.Data;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;

namespace Maki.Api.Tests;

public sealed class HealthHistoryTests : IDisposable
{
    private static readonly JsonSerializerOptions Camel = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private readonly TestDb fixture = new();

    public void Dispose() => fixture.Dispose();

    private static HealthController Controller(MakiDbContext db) =>
        new(db, null!, null!, null!, new TestCurrentUser(1), null!, new TestLocalizer(), null!, null!, NullLogger<HealthController>.Instance)
        { ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() } };

    private static async Task<List<string>> Messages(MakiDbContext db) =>
        JsonSerializer.SerializeToElement(Assert.IsType<OkObjectResult>(await Controller(db).History(1, default)).Value, Camel)
            .GetProperty("items").EnumerateArray().Select(i => i.GetProperty("message").GetString()!).ToList();

    private static async Task<List<string?>> Froms(MakiDbContext db) =>
        JsonSerializer.SerializeToElement(Assert.IsType<OkObjectResult>(await Controller(db).History(1, default)).Value, Camel)
            .GetProperty("items").EnumerateArray().Select(i => i.GetProperty("from").GetString()).ToList();

    private static HealthHistory Transition(string key, object values) =>
        new() { Kind = "transition", MessageKey = key, ParamsJson = JsonSerializer.Serialize(values) };

    [Fact]
    public async Task A_status_change_names_the_checks_problem_not_its_id()
    {
        using var db = fixture.NewContext();
        db.HealthHistory.Add(Transition("health.history.transition", new
        {
            check = "legacy:series:113", status = "healthy",
            detailKey = "health.issue.noSourceMappings", detailParams = """{"series":"Witch Hat Atelier"}""", detailMessage = "",
        }));
        await db.SaveChangesAsync();

        Assert.Equal(["health.history.transition(detail=health.issue.noSourceMappings(series=Witch Hat Atelier))"], await Messages(db));
    }

    [Fact]
    public async Task An_older_row_borrows_a_legacy_checks_message_whatever_its_status_now()
    {
        using var db = fixture.NewContext();
        db.HealthChecks.Add(new HealthCheckRecord
        {
            Id = "legacy:series:113", Category = "library", Status = "healthy",
            MessageKey = "health.issue.noSourceMappings", ParamsJson = """{"series":"Witch Hat Atelier"}""",
        });
        db.HealthHistory.Add(Transition("health.history.degraded", new { check = "legacy:series:113", status = "warning" }));
        await db.SaveChangesAsync();

        Assert.Equal(["health.history.transition(detail=health.issue.noSourceMappings(series=Witch Hat Atelier))"], await Messages(db));
    }

    [Fact]
    public async Task An_older_row_takes_the_status_it_left_from_the_checks_previous_change()
    {
        using var db = fixture.NewContext();
        db.HealthHistory.Add(Transition("health.history.degraded", new { check = "a", status = "error" }));
        db.HealthHistory.Add(Transition("health.history.degraded", new { check = "b", status = "warning" }));
        db.HealthHistory.Add(Transition("health.history.recovered", new { check = "a", status = "healthy" }));
        db.HealthHistory.Add(Transition("health.history.transition", new { check = "b", from = "warning", status = "healthy" }));
        await db.SaveChangesAsync();

        Assert.Equal(["warning", "error", null, null], await Froms(db));
    }

    [Fact]
    public void A_stored_timestamp_formats_as_a_date_and_a_title_that_looks_like_a_number_stays_text()
    {
        var values = Maki.Api.Services.HealthMonitor.HealthParams("""{"at":"2026-10-15T15:56:47.5311782Z","series":"2026"}""");
        var formatter = new Jeffijoe.MessageFormat.MessageFormatter();

        Assert.Equal("2026", values["series"]);
        Assert.Equal("10/15/2026", formatter.FormatMessage("{at, date, short}", values, System.Globalization.CultureInfo.GetCultureInfo("en")));
        Assert.Equal("15.10.2026", formatter.FormatMessage("{at, date, short}", values, System.Globalization.CultureInfo.GetCultureInfo("de")));
    }

    [Fact]
    public async Task An_older_row_keeps_the_id_once_its_check_describes_a_different_state()
    {
        using var db = fixture.NewContext();
        db.HealthChecks.Add(new HealthCheckRecord
        {
            Id = @"disk:C:\", Category = "storage", Status = "healthy",
            MessageKey = "health.check.diskFree", ParamsJson = """{"drive":"C:\\","gib":133.2,"percent":14.3}""",
        });
        db.HealthHistory.Add(Transition("health.history.degraded", new { check = @"disk:C:\", status = "warning" }));
        await db.SaveChangesAsync();

        Assert.Equal([@"health.history.degraded(check=disk:C:\, status=warning)"], await Messages(db));
    }
}
