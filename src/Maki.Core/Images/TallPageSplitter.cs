namespace Maki.Core.Images;

/// <summary>How a piece of a split page is written.</summary>
public enum SplitEncoding
{
    Jpeg,
    Png,
    Webp,
    LosslessWebp,
    Avif
}

/// <summary>
/// Cuts a very tall webtoon strip into pages no taller than a set height, which e-readers and KOReader
/// handle far better than one 15000 px page. Each cut goes in the quietest gutter near the height (a
/// run of rows of one flat colour, the space between panels), so a cut does not go through art or a
/// speech bubble; with no gutter in reach it is a hard cut at the height. A short remainder stays on
/// the piece before it rather than becoming a sliver of its own.
/// </summary>
public static class TallPageSplitter
{
    public const int DefaultHeight = 5000;
    public const int MinHeight = 1000;

    /// <summary>A piece can be a fifth over the height when it takes the remainder, and has to stay under <see cref="PageConversion.MaxEdge"/>.</summary>
    public const int MaxHeight = 13000;

    /// <summary>Only strips are split: a page at least this many times as tall as it is wide. A large scan of an ordinary page never is.</summary>
    public const double MinAspect = 2.5;

    /// <summary>How far above the height a gutter is looked for, as a share of the height.</summary>
    public const double SearchWindow = 0.4;

    /// <summary>A remainder shorter than the height over this stays on the piece before it.</summary>
    public const int TailDivisor = 5;

    /// <summary>The most a row's luma may vary, as a standard deviation in levels, to count as gutter.</summary>
    public const double QuietDeviation = 4;

    /// <summary>The thinnest gutter worth cutting in. Thinner flat runs are often the space between two lines of text.</summary>
    public const int MinQuietRows = 4;

    /// <summary>Gutters at least this tall count as equally good, and the one nearest the height wins.</summary>
    private const int GoodGutterRows = 64;

    /// <summary>A JPEG piece of a JPEG page the plan keeps as JPEG.</summary>
    public const int JpegQuality = 90;

    public static bool IsTall(int width, int height, int maxHeight) =>
        height > maxHeight && height >= width * MinAspect;

    /// <summary>
    /// Where to cut a page of <paramref name="rowDeviation"/>.Length rows, as the first row of each piece
    /// after the first. Empty when the page stays whole.
    /// </summary>
    public static IReadOnlyList<int> PlanCuts(ReadOnlySpan<double> rowDeviation, int maxHeight)
    {
        var height = rowDeviation.Length;
        var cuts = new List<int>();
        var window = (int)(maxHeight * SearchWindow);
        var minTail = maxHeight / TailDivisor;
        var start = 0;
        while (height - start > maxHeight)
        {
            var target = start + maxHeight;
            var cut = QuietCut(rowDeviation, target - window, target) ?? target;
            if (height - cut < minTail)
            {
                break;
            }

            cuts.Add(cut);
            start = cut;
        }

        return cuts;
    }

    /// <summary>The middle of the best gutter that starts in [<paramref name="low"/>, <paramref name="high"/>], never below <paramref name="high"/>.</summary>
    private static int? QuietCut(ReadOnlySpan<double> rows, int low, int high)
    {
        low = Math.Max(1, low);
        var end = Math.Min(rows.Length, high + GoodGutterRows);
        int? best = null;
        var bestScore = 0;
        var y = low;
        while (y < end)
        {
            if (rows[y] > QuietDeviation)
            {
                y++;
                continue;
            }

            var runStart = y;
            while (y < end && rows[y] <= QuietDeviation) y++;
            if (runStart > high)
            {
                break;
            }

            var length = y - runStart;
            if (length < MinQuietRows)
            {
                continue;
            }

            var score = Math.Min(length, GoodGutterRows);
            if (score >= bestScore)
            {
                bestScore = score;
                best = Math.Min(runStart + length / 2, high);
            }
        }

        return best;
    }

    /// <summary>The page's size and the rows it would be cut at, or null when it is not a strip over the height or does not decode.</summary>
    public static (int Width, int Height, IReadOnlyList<int> Cuts)? Preview(byte[] bytes, int maxHeight)
    {
        if (ImageCodec.TryShape(bytes) is not { } shape)
        {
            return null;
        }

        if (!IsTall(shape.Width, shape.Height, maxHeight))
        {
            return (shape.Width, shape.Height, []);
        }

        try
        {
            using var image = ImageCodec.LoadForSplit(bytes);
            if (image is null)
            {
                return (shape.Width, shape.Height, []);
            }

            return (image.Width, image.Height,
                IsTall(image.Width, image.Height, maxHeight) ? PlanCuts(ImageCodec.RowDeviation(image), maxHeight) : []);
        }
        catch (NetVips.VipsException)
        {
            return null;
        }
    }

    /// <summary>
    /// The pieces of a strip taller than the settings' split height, top to bottom, or null when the page
    /// stays whole. Where the plan would convert the page the pieces are in that format, unless together
    /// they come out larger than the page; otherwise, and then, each piece is in the page's own format:
    /// JPEG at <see cref="JpegQuality"/>, PNG, lossless WebP for a lossless WebP, lossy WebP and AVIF at
    /// the plan's stop (High when the plan is Lossless). Throws <see cref="NetVips.VipsException"/> when
    /// the page does not decode cleanly.
    /// </summary>
    public static IReadOnlyList<PageConversion.Page>? Split(byte[] bytes, string extension, PageConversionSettings settings)
    {
        if (settings.SplitHeight is not { } maxHeight)
        {
            return null;
        }

        var format = PageConversion.FormatOf(bytes, extension);
        if (format is not ("jpg" or "png" or "webp" or "avif") ||
            (format == "png" && PageConversion.IsAnimatedPng(bytes)) ||
            ImageCodec.TryShape(bytes) is not { } shape || !IsTall(shape.Width, shape.Height, maxHeight))
        {
            return null;
        }

        using var image = ImageCodec.LoadForSplit(bytes);
        if (image is null || !IsTall(image.Width, image.Height, maxHeight))
        {
            return null;
        }

        var cuts = PlanCuts(ImageCodec.RowDeviation(image), maxHeight);
        if (cuts.Count == 0)
        {
            return null;
        }

        var sharpYuv = ImageCodec.SharpYuvHelps(image);
        List<PageConversion.Page> Encode(SplitEncoding encoding, int quality, string pieceExtension)
        {
            var pieces = new List<PageConversion.Page>(cuts.Count + 1);
            var top = 0;
            foreach (var bottom in cuts.Append(image.Height))
            {
                using var piece = image.Crop(0, top, image.Width, bottom - top);
                pieces.Add(new PageConversion.Page(ImageCodec.EncodePiece(piece, encoding, quality, sharpYuv),
                    pieceExtension, format, null));
                top = bottom;
            }

            return pieces;
        }

        if (!settings.SplitOnly && PageConversion.EncodingFor(bytes, extension, settings) is { } converted)
        {
            var quality = converted == PageEncoding.LosslessWebp ? 100 : PageConversionSettings.Quality(settings.Format, settings.Stop);
            var pieces = Encode(converted switch
            {
                PageEncoding.Avif => SplitEncoding.Avif,
                PageEncoding.LosslessWebp => SplitEncoding.LosslessWebp,
                _ => SplitEncoding.Webp
            }, quality, converted == PageEncoding.Avif ? ".avif" : ".webp");
            if (pieces.Sum(p => (long)p.Bytes.Length) <= bytes.Length)
            {
                return pieces;
            }
        }

        var (own, ownQuality) = OwnEncoding(format, bytes, settings);
        return Encode(own, ownQuality, extension);
    }

    /// <summary>How a piece is written in the page's own format.</summary>
    internal static (SplitEncoding Encoding, int Quality) OwnEncoding(string format, byte[] bytes, PageConversionSettings settings)
    {
        var stop = settings.Stop == PageConversionStop.Lossless ? PageConversionStop.High : settings.Stop;
        return format switch
        {
            "jpg" => (SplitEncoding.Jpeg, JpegQuality),
            "png" => (SplitEncoding.Png, 100),
            "webp" when IsLosslessWebp(bytes) => (SplitEncoding.LosslessWebp, 100),
            "webp" => (SplitEncoding.Webp, PageConversionSettings.Quality(PageConversionFormat.Webp, stop)),
            _ => (SplitEncoding.Avif, PageConversionSettings.Quality(PageConversionFormat.Avif, stop))
        };
    }

    /// <summary>What a split of this page weighs on the conversion gate: the encoding its pieces most likely take.</summary>
    internal static PageEncoding WeightEncoding(byte[] bytes, string extension, PageConversionSettings settings) =>
        (settings.SplitOnly ? null : PageConversion.EncodingFor(bytes, extension, settings)) ??
        OwnEncoding(PageConversion.FormatOf(bytes, extension), bytes, settings).Encoding switch
        {
            SplitEncoding.Avif => PageEncoding.Avif,
            SplitEncoding.Png or SplitEncoding.LosslessWebp => PageEncoding.LosslessWebp,
            _ => PageEncoding.Webp
        };

    /// <summary>A simple-format WebP whose first chunk is VP8L, the lossless bitstream.</summary>
    private static bool IsLosslessWebp(ReadOnlySpan<byte> bytes) =>
        bytes.Length >= 16 && bytes[12..16].SequenceEqual("VP8L"u8);
}
