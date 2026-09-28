using Maki.Api.Controllers;
using Maki.Api.Services;
using Microsoft.AspNetCore.Mvc;

namespace Maki.Api.Tests;

public class StatsControllerTests
{
    private static StatsController Controller() => new(
        new TestLocalizer(), null!, null!, null!, null!, new UserViewResolver(new TestCurrentUser(1)));

    [Fact]
    public async Task Activity_rejects_a_date_at_the_bottom_of_the_calendar()
    {
        var result = await Controller().Activity(
            DateOnly.MinValue, new DateOnly(2026, 1, 1), utcOffsetMinutes: 60, userId: null, CancellationToken.None);

        var bad = Assert.IsType<BadRequestObjectResult>(result);
        Assert.Equal("error.stats.dateOutOfRange", bad.Value!.GetType().GetProperty("code")!.GetValue(bad.Value));
    }

    [Fact]
    public async Task Activity_rejects_an_offset_that_would_overflow_math_abs()
    {
        // Math.Abs(int.MinValue) throws OverflowException; the range check must not go through it.
        var result = await Controller().Activity(
            new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 2),
            utcOffsetMinutes: int.MinValue, userId: null, CancellationToken.None);

        var bad = Assert.IsType<BadRequestObjectResult>(result);
        Assert.Equal("error.stats.utcOffsetOutOfRange", bad.Value!.GetType().GetProperty("code")!.GetValue(bad.Value));
    }

    [Fact]
    public async Task Years_rejects_an_offset_that_would_overflow_math_abs()
    {
        var result = await Controller().Years(utcOffsetMinutes: int.MinValue, userId: null, CancellationToken.None);

        var bad = Assert.IsType<BadRequestObjectResult>(result);
        Assert.Equal("error.stats.utcOffsetOutOfRange", bad.Value!.GetType().GetProperty("code")!.GetValue(bad.Value));
    }
}
