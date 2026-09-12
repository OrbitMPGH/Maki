using Maki.Api.Jobs;
using Xunit;

namespace Maki.Api.Tests;

/// <summary>
/// The idle windows are read from environment variables, so a typo in one is a deployment mistake
/// rather than a compile error. Both jobs treat an unparseable value as "use the default" rather
/// than as "never unload": the failure that matters is silently pinning hundreds of megabytes on a
/// machine that was configured to give them back.
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
    /// Zero is the documented way to keep everything loaded, so it has to survive as zero rather
    /// than falling back to the default the way a bad value does. Pinned separately because the two
    /// are one keystroke apart in the parser.
    /// </summary>
    [Fact]
    public void Zero_DisablesRatherThanFallingBack()
    {
        Assert.Equal(0, ArtifactIdleUnloadJob.Resolve("0"));
        Assert.Equal(0, EmbedderIdleUnloadJob.Resolve("0"));
    }
}
