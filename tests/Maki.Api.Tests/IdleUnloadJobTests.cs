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
    [Theory]
    [InlineData(null, 30)]
    [InlineData("", 30)]
    [InlineData("   ", 30)]
    [InlineData("not-a-number", 30)]
    [InlineData("-5", 30)]
    [InlineData("45", 45)]
    [InlineData(" 45 ", 45)]
    [InlineData("0", 0)]
    public void ArtifactIdleUnload_ResolvesTheWindow(string? value, int expected) =>
        Assert.Equal(expected, ArtifactIdleUnloadJob.Resolve(value));

    [Theory]
    [InlineData(null, 15)]
    [InlineData("garbage", 15)]
    [InlineData("-1", 15)]
    [InlineData("20", 20)]
    [InlineData("0", 0)]
    public void EmbedderIdleUnload_ResolvesTheWindow(string? value, int expected) =>
        Assert.Equal(expected, EmbedderIdleUnloadJob.Resolve(value));

    /// <summary>
    /// The vectors have their own window, defaulting well above the other artifacts' because they
    /// are the most expensive thing in the set to rebuild.
    /// </summary>
    [Theory]
    [InlineData(null, 60)]
    [InlineData("rubbish", 60)]
    [InlineData("-1", 60)]
    [InlineData("90", 90)]
    [InlineData("0", 0)]
    public void VectorIdleUnload_ResolvesItsOwnWindow(string? value, int expected) =>
        Assert.Equal(expected, ArtifactIdleUnloadJob.ResolveVector(value));

    [Theory]
    [InlineData(null, 15)]
    [InlineData("nonsense", 15)]
    [InlineData("-2", 15)]
    [InlineData("60", 60)]
    [InlineData("0", 0)]
    public void BrowserIdleShutdown_ResolvesTheWindow(string? value, int expected) =>
        Assert.Equal(expected, BrowserIdleShutdownJob.Resolve(value));

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
