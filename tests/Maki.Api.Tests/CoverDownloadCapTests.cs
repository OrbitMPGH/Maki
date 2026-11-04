using Maki.Api.Services;

namespace Maki.Api.Tests;

public class CoverDownloadCapTests
{
    [Fact]
    public async Task A_body_under_the_cap_is_read_whole()
    {
        var bytes = new byte[300_000];
        Random.Shared.NextBytes(bytes);
        var read = await CoverService.ReadCappedAsync(new MemoryStream(bytes), bytes.Length, 1_000_000, default);
        Assert.Equal(bytes, read);
    }

    [Fact]
    public async Task A_declared_length_over_the_cap_is_refused_before_reading()
    {
        var stream = new MemoryStream(new byte[10]);
        await Assert.ThrowsAsync<InvalidDataException>(
            () => CoverService.ReadCappedAsync(stream, 2_000_000, 1_000_000, default));
        Assert.Equal(0, stream.Position);
    }

    [Fact]
    public async Task A_stream_that_outgrows_the_cap_without_a_length_is_refused()
    {
        await Assert.ThrowsAsync<InvalidDataException>(
            () => CoverService.ReadCappedAsync(new MemoryStream(new byte[300_000]), null, 100_000, default));
    }
}
