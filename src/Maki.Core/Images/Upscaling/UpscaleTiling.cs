namespace Maki.Core.Images.Upscaling;

/// <summary>
/// One tile of a page. The interior (<see cref="X"/>, <see cref="Y"/>, <see cref="Width"/>,
/// <see cref="Height"/>) is what the tile contributes to the output. The model sees the interior
/// plus <see cref="Pad"/> px of context per side, rounded up to the model's multiple
/// (<see cref="ContextWidth"/>, <see cref="ContextHeight"/>), plus <see cref="Border"/> px per side
/// reflected from that context tile itself.
/// </summary>
public readonly record struct UpscaleTile(
    int X, int Y, int Width, int Height, int Pad, int ContextWidth, int ContextHeight, int Border)
{
    public int InputWidth => ContextWidth + 2 * Border;
    public int InputHeight => ContextHeight + 2 * Border;

    /// <summary>The page column (or row) the model input reads at <paramref name="i"/>, edges reflected.</summary>
    public int SourceX(int i, int pageWidth) =>
        UpscaleTiling.Reflect(X - Pad + UpscaleTiling.Reflect(i - Border, ContextWidth), pageWidth);

    /// <inheritdoc cref="SourceX"/>
    public int SourceY(int i, int pageHeight) =>
        UpscaleTiling.Reflect(Y - Pad + UpscaleTiling.Reflect(i - Border, ContextHeight), pageHeight);
}

/// <summary>
/// The tile grid of <c>tiled()</c> in specs/page-enhance/reference/server_models_bench.py: square tiles
/// with a reflected context pad, inferred one at a time and stitched back by their interiors.
/// </summary>
public static class UpscaleTiling
{
    public const int TileSize = 384;
    public const int ContextPad = 16;

    /// <summary>
    /// The extra reflect pad waifu2x swin_unet needs. Its output is <c>2·in − 32</c> per axis, so 8 px
    /// per side of input is exactly what it eats, and the output comes out at 2× the context tile.
    /// </summary>
    public const int Waifu2xBorder = 8;

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

    /// <param name="multiple">The context tile is rounded up to this (4 for SPAN-S and waifu2x, else 1).</param>
    /// <param name="border">Extra reflect pad per side after rounding (<see cref="Waifu2xBorder"/> for waifu2x).</param>
    public static IReadOnlyList<UpscaleTile> Plan(
        int width, int height, int multiple = 1, int border = 0, int tile = TileSize, int pad = ContextPad)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(width, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(height, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(tile, 1);
        var tiles = new List<UpscaleTile>();
        for (var y = 0; y < height; y += tile)
        for (var x = 0; x < width; x += tile)
        {
            var th = Math.Min(tile, height - y);
            var tw = Math.Min(tile, width - x);
            tiles.Add(new UpscaleTile(x, y, tw, th, pad,
                RoundUp(tw + 2 * pad, multiple), RoundUp(th + 2 * pad, multiple), border));
        }

        return tiles;
    }
}
