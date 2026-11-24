using System.Runtime.InteropServices;
using Maki.Core.Images;

namespace Maki.Core.Tests;

public class JpegXlRowsTests
{
    private static byte[] Pattern(int width, int height)
    {
        var rgb = new byte[width * height * 3];
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            var i = (y * width + x) * 3;
            var n = (int)((uint)(x * 73856093 ^ y * 19349663) % 9);
            rgb[i] = (byte)((x / 3 + y / 7 + n) & 255);
            rgb[i + 1] = (byte)(((x ^ y) % 97 < 3 ? 20 : 200) + n);
            rgb[i + 2] = (byte)((y / 5) & 255);
        }

        return rgb;
    }

    /// <summary>Rows from one pinned buffer, recording what libjxl asks for.</summary>
    private sealed class Rows(byte[] pixels, int width, int failAt = -1, Func<Exception>? failure = null, int failReleaseAt = -1)
        : IJpegXlRows, IDisposable
    {
        private GCHandle _pinned = GCHandle.Alloc(pixels, GCHandleType.Pinned);
        private int _released;

        public List<(int Top, int Count)> Asked { get; } = [];

        public int Outstanding { get; private set; }

        public int MostOutstanding { get; private set; }

        public IntPtr Acquire(int top, int count, out int stride)
        {
            if (Asked.Count == failAt)
            {
                throw failure?.Invoke() ?? new TimeoutException("rows never came");
            }

            Asked.Add((top, count));
            MostOutstanding = Math.Max(MostOutstanding, ++Outstanding);
            stride = width * 3;
            return _pinned.AddrOfPinnedObject() + top * width * 3;
        }

        public void Release(IntPtr rows)
        {
            Outstanding--;
            if (_released++ == failReleaseAt)
            {
                throw new InvalidOperationException("release failed");
            }
        }

        public void Dispose() => _pinned.Free();
    }

    [JpegXlTheory]
    [InlineData(64, 48)]
    [InlineData(1880, 3670)]
    [InlineData(2800, 2100)]
    [InlineData(600, 5000)]
    public void Rows_read_as_libjxl_asks_give_the_bytes_of_a_whole_buffer(int width, int height)
    {
        var pixels = Pattern(width, height);
        using var rows = new Rows(pixels, width);

        var streamed = JpegXl.EncodeRows(rows, width, height, 3, 1.5f, 3);

        Assert.Equal(JpegXl.Encode(pixels, width, height, 3, 1.5f, 3), streamed);
        Assert.Equal(0, rows.Outstanding);
        Assert.All(rows.Asked, asked => Assert.InRange(asked.Count, 1, 2048 + 16));
    }

    [JpegXlFact]
    public void A_tall_image_is_asked_for_in_strips_from_the_top_one_at_a_time()
    {
        var (width, height) = (300, 7000);
        using var rows = new Rows(Pattern(width, height), width);

        JpegXl.EncodeRows(rows, width, height, 3, 1.5f, 3);

        // StreamedJpegXlPage frees the rows above each strip on this order.
        Assert.Equal(1, rows.MostOutstanding);
        Assert.Equal(rows.Asked.OrderBy(asked => asked.Top), rows.Asked);
        Assert.Equal(4, rows.Asked.Count);
        Assert.Equal((0, 2056), rows.Asked[0]);
        Assert.Equal((2040, 2064), rows.Asked[1]);
        Assert.Equal(height, rows.Asked[^1].Top + rows.Asked[^1].Count);
    }

    [JpegXlFact]
    public void Rows_that_never_come_fail_the_encode_with_their_reason()
    {
        var (width, height) = (300, 7000);
        using var rows = new Rows(Pattern(width, height), width, failAt: 1);

        Assert.Throws<TimeoutException>(() => JpegXl.EncodeRows(rows, width, height, 3, 1.5f, 3));
        Assert.Equal(0, rows.Outstanding);
    }

    [JpegXlFact]
    public void An_out_of_memory_in_a_callback_comes_back_to_the_caller_instead_of_crossing_into_libjxl()
    {
        var (width, height) = (300, 7000);
        using var rows = new Rows(Pattern(width, height), width, failAt: 2, failure: () => new OutOfMemoryException("strip"));

        Assert.Throws<OutOfMemoryException>(() => JpegXl.EncodeRows(rows, width, height, 3, 1.5f, 3));
        Assert.Equal(0, rows.Outstanding);
        Assert.Equal(2, rows.Asked.Count);
    }

    [JpegXlFact]
    public void A_release_that_throws_fails_the_encode_with_its_reason()
    {
        var (width, height) = (300, 7000);
        using var rows = new Rows(Pattern(width, height), width, failReleaseAt: 0);

        Assert.Throws<InvalidOperationException>(() => JpegXl.EncodeRows(rows, width, height, 3, 1.5f, 3));
        Assert.Equal(0, rows.Outstanding);
        Assert.Single(rows.Asked);
    }
}

/// <summary><see cref="JpegXlFactAttribute"/> for a theory.</summary>
public sealed class JpegXlTheoryAttribute : TheoryAttribute
{
    public JpegXlTheoryAttribute()
    {
        if (!JpegXl.IsAvailable && Environment.GetEnvironmentVariable("MAKI_REQUIRE_LIBJXL") != "1")
        {
            Skip = "libjxl is not installed beside the tests";
        }
    }
}
