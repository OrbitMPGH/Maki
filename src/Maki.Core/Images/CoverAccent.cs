namespace Maki.Core.Images;

/// <summary>
/// Picks the one colour a cover can lend to the chrome around it: its dominant saturated hue,
/// re-set to a fixed lightness and a tamed chroma so white text reads on it and a row of them
/// sits at one weight. Measured in OKLab, where equal steps look equal across hues; a mean in
/// sRGB lands on brown for almost every cover.
/// </summary>
public static class CoverAccent
{
    private const int SampleWidth = 48;
    private const int HueBins = 24;

    /// <summary>
    /// Lightness every accent is set to: where the theme's own accents sit (indigo and rose both
    /// measure about 0.62), so a cover's colour and the brand carry the same weight on a page.
    /// </summary>
    private const double Lightness = 0.62;
    private const double MinChroma = 0.13;
    private const double MaxChroma = 0.21;

    /// <summary>Pixels that count as coloured: enough chroma, and neither near-black nor near-white.</summary>
    private const double ColouredChroma = 0.05;

    /// <summary>Below this share of coloured pixels the art is effectively greyscale and lends nothing.</summary>
    private const double MinColouredShare = 0.04;

    /// <returns>A <c>#rrggbb</c> colour, or null when the cover has no hue to lend.</returns>
    public static string? FromImage(byte[] bytes)
    {
        var small = ImageCodec.JpegThumbnail(bytes, SampleWidth, 85, enlarge: false);
        return FromRaster(Raster.Decode(small, bands: 3));
    }

    public static string? FromRaster(Raster raster)
    {
        var weight = new double[HueBins];
        var cos = new double[HueBins];
        var sin = new double[HueBins];
        var chroma = new double[HueBins];
        var coloured = 0;
        var total = raster.Width * raster.Height;

        for (var y = 0; y < raster.Height; y++)
        {
            var row = raster.Row(y);
            for (var x = 0; x < raster.Width; x++)
            {
                var o = x * raster.Bands;
                var (l, a, b) = OkLab.FromSrgb(row[o], row[o + 1], row[o + 2]);
                var c = Math.Sqrt(a * a + b * b);
                if (c < ColouredChroma || l < 0.22 || l > 0.95)
                {
                    continue;
                }

                coloured++;
                var h = Math.Atan2(b, a);
                var bin = (int)Math.Floor((h + Math.PI) / (2 * Math.PI) * HueBins) % HueBins;
                weight[bin] += c;
                cos[bin] += Math.Cos(h) * c;
                sin[bin] += Math.Sin(h) * c;
                chroma[bin] += c * c;
            }
        }

        if (total == 0 || (double)coloured / total < MinColouredShare)
        {
            return null;
        }

        // The winning bin together with its neighbours, so a hue straddling a bin edge is not split.
        var best = 0;
        var bestWeight = -1.0;
        for (var i = 0; i < HueBins; i++)
        {
            var w = weight[i] + weight[(i + 1) % HueBins] + weight[(i + HueBins - 1) % HueBins];
            if (w > bestWeight)
            {
                bestWeight = w;
                best = i;
            }
        }

        double sumCos = 0, sumSin = 0, sumW = 0, sumC = 0;
        foreach (var i in new[] { best, (best + 1) % HueBins, (best + HueBins - 1) % HueBins })
        {
            sumCos += cos[i];
            sumSin += sin[i];
            sumW += weight[i];
            sumC += chroma[i];
        }

        if (sumW <= 0)
        {
            return null;
        }

        var hue = Math.Atan2(sumSin, sumCos);
        var meanChroma = sumC / sumW;
        var c0 = Math.Clamp(meanChroma * 1.4, MinChroma, MaxChroma);
        return OkLab.ToHex(Lightness, c0, hue);
    }
}

/// <summary>Just enough of OKLab (Björn Ottosson, 2020) for <see cref="CoverAccent"/>.</summary>
internal static class OkLab
{
    public static (double L, double A, double B) FromSrgb(byte r8, byte g8, byte b8)
    {
        var r = ToLinear(r8 / 255.0);
        var g = ToLinear(g8 / 255.0);
        var b = ToLinear(b8 / 255.0);

        var l = Math.Cbrt(0.4122214708 * r + 0.5363325363 * g + 0.0514459929 * b);
        var m = Math.Cbrt(0.2119034982 * r + 0.6806995451 * g + 0.1073969566 * b);
        var s = Math.Cbrt(0.0883024619 * r + 0.2817188376 * g + 0.6299787005 * b);

        return (
            0.2104542553 * l + 0.7936177850 * m - 0.0040720468 * s,
            1.9779984951 * l - 2.4285922050 * m + 0.4505937099 * s,
            0.0259040371 * l + 0.7827717662 * m - 0.8086757660 * s);
    }

    /// <summary>
    /// OKLCH to <c>#rrggbb</c>. A chroma the sRGB gamut cannot hold at this lightness is walked
    /// down until it fits, so the hue and lightness are kept and only the saturation gives.
    /// </summary>
    public static string ToHex(double lightness, double chroma, double hue)
    {
        for (var c = chroma; c >= 0; c -= 0.005)
        {
            var (r, g, b) = ToLinearRgb(lightness, c * Math.Cos(hue), c * Math.Sin(hue));
            if (r is >= 0 and <= 1 && g is >= 0 and <= 1 && b is >= 0 and <= 1)
            {
                return $"#{Byte(r):x2}{Byte(g):x2}{Byte(b):x2}";
            }
        }

        var (gr, gg, gb) = ToLinearRgb(lightness, 0, 0);
        return $"#{Byte(gr):x2}{Byte(gg):x2}{Byte(gb):x2}";
    }

    private static (double R, double G, double B) ToLinearRgb(double lightness, double a, double b)
    {
        var l = Math.Pow(lightness + 0.3963377774 * a + 0.2158037573 * b, 3);
        var m = Math.Pow(lightness - 0.1055613458 * a - 0.0638541728 * b, 3);
        var s = Math.Pow(lightness - 0.0894841775 * a - 1.2914855480 * b, 3);

        return (
            4.0767416621 * l - 3.3077115913 * m + 0.2309699292 * s,
            -1.2684380046 * l + 2.6097574011 * m - 0.3413193965 * s,
            -0.0041960863 * l - 0.7034186147 * m + 1.7076147010 * s);
    }

    private static double ToLinear(double c) => c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);

    private static byte Byte(double linear)
    {
        var c = linear <= 0.0031308 ? 12.92 * linear : 1.055 * Math.Pow(linear, 1 / 2.4) - 0.055;
        return (byte)Math.Clamp(Math.Round(c * 255), 0, 255);
    }
}
