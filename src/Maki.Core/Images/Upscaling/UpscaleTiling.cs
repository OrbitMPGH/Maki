namespace Maki.Core.Images.Upscaling;

/// <summary>
/// One tile of a page. The interior (<see cref="X"/>, <see cref="Y"/>, <see cref="Width"/>,
/// <see cref="Height"/>) is what the tile contributes to the output. The model sees the interior
/// plus <see cref="Pad"/> px of context per side, rounded up to the model's multiple
/// (<see cref="ContextWidth"/>, <see cref="ContextHeight"/>). Any context past that runs off the
/// right and bottom.
/// </summary>
public readonly record struct UpscaleTile(
    int X, int Y, int Width, int Height, int Pad, int ContextWidth, int ContextHeight)
{
    /// <summary>The page column (or row) the model input reads at <paramref name="i"/>, edges reflected.</summary>
    public int SourceX(int i, int pageWidth) => UpscaleTiling.Reflect(X - Pad + i, pageWidth);

    /// <inheritdoc cref="SourceX"/>
    public int SourceY(int i, int pageHeight) => UpscaleTiling.Reflect(Y - Pad + i, pageHeight);
}

/// <summary>
/// Tiles with a reflected context pad, inferred one at a time and stitched back by their interiors.
/// Each axis takes as many tiles as fixed <see cref="TileSize"/> tiles would, split into equal spans,
/// and every tile of a page shares one context size: ORT plans memory per input shape, and a page
/// of equal tiles measured 9% faster with 60 MB less peak than 384 px tiles plus a remainder.
/// </summary>
public static class UpscaleTiling
{
    public const int TileSize = 384;
    public const int ContextPad = 16;

    public static int RoundUp(int value, int multiple) =>
        multiple <= 1 ? value : (value + multiple - 1) / multiple * multiple;

    /// <summary>
    /// Mirrors <paramref name="i"/> into [0, n) without repeating the edge pixel (numpy "reflect"),
    /// as many times as it takes.
    /// </summary>
    public static int Reflect(int i, int n)
    {
        if (n <= 1)
        {
            return 0;
        }

        var period = 2 * (n - 1);
        i %= period;
        if (i < 0)
        {
            i += period;
        }

        return i < n ? i : period - i;
    }

    /// <param name="multiple">The context tile is rounded up to this (4 for SPAN-S, else 1).</param>
    public static IReadOnlyList<UpscaleTile> Plan(
        int width, int height, int multiple = 1, int tile = TileSize, int pad = ContextPad)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(width, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(height, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(tile, 1);
        var columns = Spans(width, tile);
        var rows = Spans(height, tile);
        var contextWidth = RoundUp(columns[0].Length + 2 * pad, multiple);
        var contextHeight = RoundUp(rows[0].Length + 2 * pad, multiple);
        var tiles = new List<UpscaleTile>(columns.Count * rows.Count);
        foreach (var (y, th) in rows)
        foreach (var (x, tw) in columns)
        {
            tiles.Add(new UpscaleTile(x, y, tw, th, pad, contextWidth, contextHeight));
        }

        return tiles;
    }

    /// <summary><c>ceil(length / tile)</c> spans whose lengths differ by at most one, longest first.</summary>
    private static List<(int Start, int Length)> Spans(int length, int tile)
    {
        var count = (length + tile - 1) / tile;
        var size = length / count;
        var longer = length % count;
        var spans = new List<(int, int)>(count);
        for (int i = 0, start = 0; i < count; i++)
        {
            var span = size + (i < longer ? 1 : 0);
            spans.Add((start, span));
            start += span;
        }

        return spans;
    }
}
