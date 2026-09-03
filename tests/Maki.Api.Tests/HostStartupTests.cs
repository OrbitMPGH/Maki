using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Maki.Api.Tests;

/// <summary>
/// Boots the real host and asks it for the services the recommendation path is built from.
///
/// <para>
/// Every other test in this project constructs its services by hand, which is what makes them fast
/// and focused, and also what makes them blind to the whole class of failure this covers: a service
/// that is never registered, or registered as the wrong type. The host validates its container on
/// build in Development, so that failure is fatal at startup and invisible to `dotnet build` and
/// `dotnet test` alike. It shipped that way for five commits once - a rename moved
/// <c>TasteTuning</c> out of the way of <c>TasteVectorTuning</c> and took the registration with it,
/// and the only symptom was that the application would not start.
/// </para>
///
/// <para>
/// The config directory is a fresh temp one per run, so this creates its own SQLite database and
/// migrates it rather than touching a real install. Quartz triggers all fire minutes out, so
/// nothing scheduled runs inside the lifetime of the test.
/// </para>
/// </summary>
public class HostStartupTests : IDisposable
{
    private readonly string _configDir;
    private readonly string? _previousConfigDir;

    public HostStartupTests()
    {
        _configDir = Path.Combine(Path.GetTempPath(), "maki-hoststartup-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_configDir);
        _previousConfigDir = Environment.GetEnvironmentVariable("MAKI_CONFIG_DIR");
        Environment.SetEnvironmentVariable("MAKI_CONFIG_DIR", _configDir);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("MAKI_CONFIG_DIR", _previousConfigDir);
        try
        {
            Directory.Delete(_configDir, recursive: true);
        }
        catch (IOException)
        {
            // A file the host still holds open. The directory is under TEMP either way.
        }

        GC.SuppressFinalize(this);
    }

    [Fact]
    public void Host_BuildsItsServiceGraph()
    {
        using var factory = new WebApplicationFactory<Program>();

        // Resolving anything forces the host to build, which is where container validation happens.
        // The recommendation services are named explicitly rather than left to a blanket sweep
        // because they are the ones with a tuning record per channel, and a tuning record is exactly
        // the kind of registration a rename can quietly redirect.
        using var scope = factory.Services.CreateScope();
        var services = scope.ServiceProvider;

        Assert.NotNull(services.GetRequiredService<Maki.Api.Services.BehavioralTasteService>());
        Assert.NotNull(services.GetRequiredService<Maki.Api.Services.RecommendationService>());
        Assert.NotNull(services.GetRequiredService<Maki.Api.Services.RecentActivityRailService>());
        Assert.NotNull(services.GetRequiredService<Maki.Api.Services.SimilarSeriesService>());
    }

    /// <summary>
    /// The two tuning records whose names differ by one word and whose meanings do not overlap at
    /// all: <c>TasteTuning</c> weights a reader's own series as recommendation seeds,
    /// <c>TasteVectorTuning</c> is the behavioural channel. Both are registered; asserting on the
    /// type rather than on the value is the point, since the failure was one standing in for the
    /// other and a value check would have passed.
    /// </summary>
    [Fact]
    public void Host_RegistersBothTuningRecords_AndNotOneTwice()
    {
        using var factory = new WebApplicationFactory<Program>();
        using var scope = factory.Services.CreateScope();
        var services = scope.ServiceProvider;

        Assert.NotNull(services.GetRequiredService<Maki.Core.Recommendations.TasteTuning>());
        Assert.NotNull(services.GetRequiredService<Maki.Metadata.Taste.TasteVectorTuning>());
    }

    /// <summary>
    /// The chapter "wanted" and download routes, read off the host's own endpoint table.
    /// <para>
    /// The controller tests call these actions as methods, so a wrong or duplicated
    /// <c>[HttpPost]</c> template is invisible to them — the frontend calls these by URL string and
    /// would just get a 404. Two of these are new and two were renamed away from "monitor", which is
    /// exactly when a template typo is easiest to ship.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData("PUT", "api/v1/chapter/{id:int}/wanted")]
    [InlineData("PUT", "api/v1/chapter/wanted")]
    [InlineData("POST", "api/v1/chapter/download")]
    [InlineData("POST", "api/v1/series/{id:int}/download/next")]
    [InlineData("POST", "api/v1/series/{id:int}/searchmissing")]
    public void Host_MapsTheChapterWantedAndDownloadRoutes(string method, string template)
    {
        using var factory = new WebApplicationFactory<Program>();

        var matches = factory.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            .Where(e => e.RoutePattern.RawText == template &&
                        e.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods.Contains(method) == true)
            .ToList();

        Assert.Single(matches);
    }

    /// <summary>The pre-rename routes must be gone, not silently left behind alongside the new ones.</summary>
    [Theory]
    [InlineData("api/v1/chapter/{id:int}/monitor")]
    [InlineData("api/v1/chapter/monitor")]
    public void Host_NoLongerMapsTheOldMonitorRoutes(string template)
    {
        using var factory = new WebApplicationFactory<Program>();

        Assert.DoesNotContain(
            template,
            factory.Services.GetRequiredService<EndpointDataSource>().Endpoints
                .OfType<RouteEndpoint>()
                .Select(e => e.RoutePattern.RawText));
    }
}
