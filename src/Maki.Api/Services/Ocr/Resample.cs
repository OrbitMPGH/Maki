namespace Maki.Api.Services.Ocr;

/// <summary>
/// Bilinear resize of interleaved RGB the way Pillow's <c>Image.resize(BILINEAR)</c> does it: a
/// triangle filter whose support widens with the scale when shrinking, so a downscale averages
/// instead of skipping pixels. The OCR models were checked against Pillow's resampling.
/// </summary>
internal static class Resample
{
    public static byte[] Resize(byte[] source, int width, int height, int newWidth, int newHeight)
    {
        var columns = Coefficients(width, newWidth);
        var rows = Coefficients(height, newHeight);

        var across = new float[height * newWidth * 3];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < newWidth; x++)
            {
                var (start, weights) = columns[x];
                float r = 0, g = 0, b = 0;
                for (var k = 0; k < weights.Length; k++)
                {
                    var at = (y * width + start + k) * 3;
                    r += source[at] * weights[k];
                    g += source[at + 1] * weights[k];
                    b += source[at + 2] * weights[k];
                }

                var to = (y * newWidth + x) * 3;
                (across[to], across[to + 1], across[to + 2]) = (r, g, b);
            }
        }

        var output = new byte[newHeight * newWidth * 3];
        for (var y = 0; y < newHeight; y++)
        {
            var (start, weights) = rows[y];
            for (var i = 0; i < newWidth * 3; i++)
            {
                float sum = 0;
                for (var k = 0; k < weights.Length; k++)
                {
                    sum += across[(start + k) * newWidth * 3 + i] * weights[k];
                }

                output[y * newWidth * 3 + i] = (byte)Math.Clamp(MathF.Round(sum), 0, 255);
            }
        }

        return output;
    }

    private static (int Start, float[] Weights)[] Coefficients(int size, int newSize)
    {
        var scale = (double)size / newSize;
        var filterScale = Math.Max(scale, 1.0);
        var support = filterScale;
        var table = new (int, float[])[newSize];
        for (var i = 0; i < newSize; i++)
        {
            var centre = (i + 0.5) * scale;
            var start = Math.Max((int)(centre - support + 0.5), 0);
            var end = Math.Min((int)(centre + support + 0.5), size);
            var weights = new float[Math.Max(1, end - start)];
            double total = 0;
            for (var k = 0; k < end - start; k++)
            {
                var distance = Math.Abs((start + k - centre + 0.5) / filterScale);
                var weight = distance < 1 ? 1 - distance : 0;
                weights[k] = (float)weight;
                total += weight;
            }

            if (total > 0)
            {
                for (var k = 0; k < weights.Length; k++)
                {
                    weights[k] = (float)(weights[k] / total);
                }
            }
            else
            {
                start = Math.Min(start, size - 1);
                weights = [1f];
            }

            table[i] = (start, weights);
        }

        return table;
    }
}
