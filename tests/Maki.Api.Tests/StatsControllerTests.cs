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
}
