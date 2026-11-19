namespace Maki.Core.Images.Upscaling;

/// <summary>
/// <see cref="UpscalePixels.TakeColourFrom(byte[], int, int, byte[], int, int, int)"/> for an output that is
/// stitched and encoded a few rows at a time, so the whole page never has to be in one buffer. Rows are
/// read and written through <see cref="OutputRow"/>. Every pixel gets the same arithmetic as the
/// whole-page pass, so the bytes are the same.
/// </summary>
public sealed class UpscaleColourRows
{
    /// <summary>Output row <c>y</c>, interleaved RGB.</summary>
    public delegate Span<byte> OutputRow(int y);

    /// <summary>Output rows per parallel band.</summary>
    private const int BandRows = 64;

    private readonly byte[] _source;
    private readonly int _sourceWidth;
    private readonly int _sourceHeight;
    private readonly int _scale;
    private readonly int _width;
    private readonly int _height;
    private readonly byte[] _guide;
    private int _guideRows;

    public UpscaleColourRows(byte[] source, int sourceWidth, int sourceHeight, int scale)
    {
        if (scale < 1 || source.Length != sourceWidth * sourceHeight * 3)
        {
            throw new ArgumentException("The source must be interleaved RGB of its size, the scale whole.", nameof(source));
        }

        _source = source;
        _sourceWidth = sourceWidth;
        _sourceHeight = sourceHeight;
        _scale = scale;
        _width = sourceWidth * scale;
        _height = sourceHeight * scale;
        _guide = new byte[sourceWidth * sourceHeight];
    }

    private static int Reach => UpscalePixels.SmoothReach * UpscalePixels.SmoothStep;

    /// <summary>How many output rows from the top must be stitched before the rows above <paramref name="end"/> can be coloured.</summary>
    public int RowsNeededFor(int end)
    {
        if (end <= 0)
        {
            return 0;
        }

        var last = LastSourceRow(end);
        return Math.Min(_height, Math.Min(_sourceHeight, last + Reach + 1) * _scale);
    }

    /// <summary>
    /// Takes the model's luma at source size from the output rows above <paramref name="stitched"/>, which must
    /// all be stitched and none of them coloured yet. Rows already taken are skipped.
    /// </summary>
    public void AddGuide(int stitched, OutputRow rows)
    {
        var end = Math.Min(_sourceHeight, stitched / _scale);
        var area = _scale * _scale;
        for (; _guideRows < end; _guideRows++)
        {
            var y = _guideRows;
            for (var x = 0; x < _sourceWidth; x++)
            {
                var sum = 0;
                for (var dy = 0; dy < _scale; dy++)
                {
                    var row = rows(y * _scale + dy);
                    var o = x * _scale * 3;
                    for (var dx = 0; dx < _scale; dx++, o += 3)
                    {
                        sum += UpscalePixels.Luma(row[o], row[o + 1], row[o + 2]);
                    }
                }

                _guide[y * _sourceWidth + x] = (byte)((sum + area / 2) / area);
            }
        }
    }

    /// <summary>
    /// Colours output rows <paramref name="from"/> up to <paramref name="end"/> in place. The guide must reach
    /// <see cref="RowsNeededFor"/> of <paramref name="end"/>.
    /// </summary>
    public void Colour(int from, int end, OutputRow rows, int threads)
    {
        if (end <= from)
        {
            return;
        }

        if (_guideRows * _scale < RowsNeededFor(end))
        {
            throw new InvalidOperationException("The guide does not reach the rows to colour yet.");
        }

        var bands = (end - from + BandRows - 1) / BandRows;
        var sourceRows = BandRows / _scale + 2;
        var options = new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, threads) };
        Parallel.For(0, bands, options,
            () => (Chroma: new short[(sourceRows + 2 * Reach) * _sourceWidth * 2], Smooth: new float[sourceRows * _sourceWidth * 2]),
            (band, _, buffers) =>
            {
                var top = from + band * BandRows;
                var bottom = Math.Min(end, top + BandRows);
                var first = (int)UpscalePixels.SourceY(top, _scale, _sourceHeight);
                var last = LastSourceRow(bottom);
                var chromaTop = Math.Max(0, first - Reach);
                for (var y = chromaTop; y < Math.Min(_sourceHeight, last + Reach + 1); y++)
                {
                    UpscalePixels.ChromaRow(_source, buffers.Chroma, _sourceWidth, y, chromaTop);
                }

                UpscalePixels.SmoothRows(buffers.Chroma, chromaTop, _guide, buffers.Smooth, first, _sourceWidth, _sourceHeight,
                    first, last + 1);
                for (var y = top; y < bottom; y++)
                {
                    UpscalePixels.ColourRow(rows(y), _width, buffers.Smooth, first, _sourceWidth, _sourceHeight, _scale, y);
                }

                return buffers;
            },
            _ => { });
    }

    /// <summary>The last source row the bilinear of output rows above <paramref name="end"/> reads.</summary>
    private int LastSourceRow(int end) =>
        Math.Min(_sourceHeight - 1, (int)UpscalePixels.SourceY(end - 1, _scale, _sourceHeight) + 1);
}
