using Maki.Api.Jobs;
using Maki.Sources.Common;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Maki.Api.Tests;

/// <summary>
/// The idle windows are read from environment variables, so a typo in one is a deployment mistake
/// rather than a compile error. Every job treats an unparseable value as "use the default" rather
/// than as "never unload": the failure that matters is silently pinning hundreds of megabytes on a
/// machine that was configured to give them back. The defaults are asserted per job rather than
/// against the shared parser, because they are what a deployment actually gets.
/// </summary>
public class IdleUnloadJobTests
{
    public static TheoryData<string, Func<string?, int>> Resolvers => new()
    {
        { "artifact", ArtifactIdleUnloadJob.Resolve },
        { "embedder", EmbedderIdleUnloadJob.Resolve },
        { "vector", ArtifactIdleUnloadJob.ResolveVector },
        { "browser", BrowserIdleShutdownJob.Resolve },
    };

    /// <summary>
    /// An unset or unusable value must land on a positive default, never on zero: zero means
    /// "keep it loaded", which is the outcome a typo is not allowed to produce.
    /// </summary>
    [Theory]
    [MemberData(nameof(Resolvers))]
    public void Unusable_values_fall_back_to_a_default_that_still_unloads(string job, Func<string?, int> resolve)
    {
        var fallback = resolve(null);

        Assert.True(fallback > 0, job);
        foreach (var value in new[] { "", "   ", "not-a-number", "-1", "-5", "1.5" })
        {
            Assert.Equal(fallback, resolve(value));
        }
    }

    [Theory]
    [MemberData(nameof(Resolvers))]
    public void A_valid_value_is_used_as_given(string job, Func<string?, int> resolve)
    {
        Assert.Equal(1, resolve("1"));
        Assert.Equal(45, resolve("45"));
        Assert.Equal(45, resolve(" 45 "));
    }

    /// <summary>
    /// The vectors have their own window, defaulting well above the other artifacts' because they
    /// are the most expensive thing in the set to rebuild.
    /// </summary>
    [Fact]
    public void The_vector_window_defaults_above_the_other_artifacts()
    {
        Assert.True(ArtifactIdleUnloadJob.ResolveVector(null) > ArtifactIdleUnloadJob.Resolve(null));
    }

    private sealed class FakeBrowser(string name, bool throws) : IIdleBrowser
    {
        public string BrowserName => name;

        public bool IsRunning => true;

        public TimeSpan? AskedFor { get; private set; }

        public Task<bool> ReleaseIfIdleAsync(TimeSpan idleFor)
        {
            AskedFor = idleFor;
            return throws
                ? Task.FromException<bool>(new InvalidOperationException("driver is wedged"))
                : Task.FromResult(true);
        }
    }

    /// <summary>
    /// One browser refusing to close is a reason to leave that one alone, not to skip the other.
    /// They are separate processes with separate failure modes, and the job runs on a timer, so the
    /// one that threw simply gets asked again in five minutes.
    /// </summary>
    [Fact]
    public async Task BrowserIdleShutdown_AsksEveryBrowserEvenAfterOneThrows()
    {
        var wedged = new FakeBrowser("Wedged", throws: true);
        var healthy = new FakeBrowser("Healthy", throws: false);

        // Set rather than inherited: the job reads the variable from the environment, and a
        // developer who has one exported would otherwise fail this on the window rather than on
        // the behaviour it is about.
        var previous = Environment.GetEnvironmentVariable(BrowserIdleShutdownJob.IdleMinutesVariable);
        Environment.SetEnvironmentVariable(BrowserIdleShutdownJob.IdleMinutesVariable, "7");
        try
        {
            await new BrowserIdleShutdownJob(
                [wedged, healthy], NullLogger<BrowserIdleShutdownJob>.Instance).Execute(null!);
        }
        finally
        {
            Environment.SetEnvironmentVariable(BrowserIdleShutdownJob.IdleMinutesVariable, previous);
        }

        Assert.Equal(TimeSpan.FromMinutes(7), wedged.AskedFor);
        Assert.Equal(TimeSpan.FromMinutes(7), healthy.AskedFor);
    }

    /// <summary>
    /// Zero is the documented way to keep everything loaded, so it has to survive as zero rather
    /// than falling back to the default the way a bad value does. Pinned separately because the two
    /// are one keystroke apart in the parser.
    /// </summary>
    [Fact]
    public void Zero_DisablesRatherThanFallingBack()
    {
        Assert.Equal(0, ArtifactIdleUnloadJob.Resolve("0"));
        Assert.Equal(0, EmbedderIdleUnloadJob.Resolve("0"));
        Assert.Equal(0, BrowserIdleShutdownJob.Resolve("0"));
        Assert.Equal(0, ArtifactIdleUnloadJob.ResolveVector("0"));
    }
}
