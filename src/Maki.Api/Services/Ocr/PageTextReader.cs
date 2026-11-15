using Maki.Core.Images;
using Maki.Metadata.Embedding;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using NetVips;
using VipsImage = NetVips.Image;

namespace Maki.Api.Services.Ocr;

/// <summary>Reads the text on a page, for telling a scanlator's credit page from a publisher's.</summary>
public interface IPageTextReader
{
    /// <summary>False once the models turned out to be missing or broken; true before the first read.</summary>
    bool Available { get; }

    /// <summary>
    /// The text lines read off the top <paramref name="share"/> of the page (1 for all of it), joined
    /// with <c>" | "</c>, or null when the reader is not available or the page could not be read.
    /// Never throws for either.
    /// </summary>
    Task<string?> ReadAsync(byte[] image, double share, CancellationToken ct);

    /// <summary>
    /// <see cref="ReadAsync"/> on the bottom <paramref name="share"/> of the page instead of the top: a
    /// banner fused into the bottom of a chapter's last page.
    /// </summary>
    Task<string?> ReadBottomAsync(byte[] image, double share, CancellationToken ct);

    /// <summary>Loads the models now, so a read that follows does not wait for them. Never throws.</summary>
    Task PrepareAsync();
}

/// <summary>
/// PP-OCRv3 text detection and recognition (PaddleOCR, Apache-2.0; the ONNX files as RapidOCR 1.2.3
/// ships them) on the CPU, without OpenCV: connected components on the detector's map, each turned
/// into an oriented box by its principal direction, cut out upright and read in batches.
/// <para>
/// Ported from the reference in the git-ignored <c>research/credit-pages/2026-11-12-probe</c>
/// (<c>ocr_ref.py</c>, <c>OCR_SPEC.md</c>), which matched RapidOCR's own pipeline on the
/// hand-labelled credit pages. Change a constant only against that set.
/// </para>
/// <para>
/// Both sessions load on the first read and go after <see cref="IdleWindow"/> without one
/// (<see cref="ReleaseIfIdle"/>, from <c>UpscalerIdleUnloadJob</c>). One read runs at a time, and
/// only its decode runs under <see cref="ImageWorkGate"/>, taken after the reader's own lock.
/// </para>
/// </summary>
public sealed class PageTextReader : IPageTextReader, IDisposable
{
    public const string FolderName = "OcrModels";

    /// <summary>Bumped whenever the models or the reading steps change what text comes out; stored text below it is read again.</summary>
    public const int Version = 1;
    public const string DetectionFile = "ch_PP-OCRv3_det_infer.onnx";
    public const string RecognitionFile = "ch_PP-OCRv3_rec_infer.onnx";

    public static readonly TimeSpan IdleWindow = TimeSpan.FromMinutes(5);

    private const int ShortSide = 736;
    private const int LongSide = 1600;
    private const double MaxUpscale = 2.0;
    private const float Threshold = 0.3f;
    private const double BoxThreshold = 0.5;
    private const double Unclip = 1.6;
    private const double MinSide = 5;
    private const double AspectForAngle = 1.5;
    private const int MinComponentSide = 3;
    private const int LineHeight = 48;
    private const int LineWidth = 320;
    private const int Batch = 6;
    private const double TextScore = 0.5;

    /// <summary>A region taller than this many widths is read in tiles <see cref="TileWidths"/> widths tall.</summary>
    private const double TileAbove = 2.2;

    private const double TileWidths = 2.0;
    private const double TileStep = 1.8;

    /// <summary>
    /// Measured on 300 real regions (2026-11-13, dev machine, 4 threads): without the arena a read took
    /// 471 ms at the median and the process held 187 MB; with it, 197 ms and 698 MB; with it and this
    /// shrinkage, 201 ms and 182 MB.
    /// </summary>
    private static readonly RunOptions ShrinkAfterRun = CreateShrinkAfterRun();

    private static readonly float[] DetectionMean = [0.485f, 0.456f, 0.406f];
    private static readonly float[] DetectionStd = [0.229f, 0.224f, 0.225f];

    private readonly string _folder;
    private readonly ILogger<PageTextReader> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Models? _models;
    private volatile bool _failed;
    private DateTime _lastUsedUtc;

    public PageTextReader(ILogger<PageTextReader> logger)
        : this(logger, Path.Combine(AppContext.BaseDirectory, FolderName))
    {
    }

    public PageTextReader(ILogger<PageTextReader> logger, string folder)
    {
        _logger = logger;
        _folder = folder;
    }

    public bool Available => !_failed;

    public async Task PrepareAsync()
    {
        if (_failed || _models is not null)
        {
            return;
        }

        await _gate.WaitAsync();
        try
        {
            _models ??= Load();
            _lastUsedUtc = DateTime.UtcNow;
        }
        finally
        {
            _gate.Release();
        }
    }

    public Task<string?> ReadAsync(byte[] image, double share, CancellationToken ct) => ReadAsync(image, share, bottom: false, ct);

    public Task<string?> ReadBottomAsync(byte[] image, double share, CancellationToken ct) => ReadAsync(image, share, bottom: true, ct);

    private async Task<string?> ReadAsync(byte[] image, double share, bool bottom, CancellationToken ct)
    {
        if (_failed)
        {
            return null;
        }

        await _gate.WaitAsync(ct);
        try
        {
            var models = _models ??= Load();
            if (models is null)
            {
                return null;
            }

            _lastUsedUtc = DateTime.UtcNow;
            try
            {
                // Only the decode holds an image permit; inference runs outside the gate.
                var page = await ImageWorkGate.RunAsync(
                    () => Task.FromResult(Decode(image, share, bottom)), ImageWorkGate.Weigh(image), ct);
                return Read(models, page);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                _logger.LogDebug(e, "Could not read the text on a page");
                return null;
            }
            finally
            {
                _lastUsedUtc = DateTime.UtcNow;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Disposes both sessions when nothing has read for <paramref name="idleFor"/>. True when they went.</summary>
    public bool ReleaseIfIdle(TimeSpan idleFor)
    {
        if (!_gate.Wait(0))
        {
            return false;
        }

        try
        {
            if (_models is null || DateTime.UtcNow - _lastUsedUtc < idleFor)
            {
                return false;
            }

            _models.Dispose();
            _models = null;
            _logger.LogInformation("Unloaded the OCR models after {Minutes:F0} idle minute(s)", idleFor.TotalMinutes);
            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose()
    {
        _models?.Dispose();
        _models = null;
        _gate.Dispose();
    }

    private static RunOptions CreateShrinkAfterRun()
    {
        var options = new RunOptions();
        options.AddRunConfigEntry("memory.enable_memory_arena_shrinkage", "cpu:0");
        return options;
    }

    private Models? Load()
    {
        var detection = Path.Combine(_folder, DetectionFile);
        var recognition = Path.Combine(_folder, RecognitionFile);
        if (!File.Exists(detection) || !File.Exists(recognition))
        {
            _failed = true;
            _logger.LogInformation("OCR models not found in {Folder}; credit pages are judged by colour alone", _folder);
            return null;
        }

        InferenceSession? det = null;
        InferenceSession? rec = null;
        try
        {
            using var options = OnnxMemoryOptions.Create(
                intraOpThreads: Math.Clamp(Environment.ProcessorCount / 2, 1, 4), fixedShapes: false);
            // Unlike the embedder and the upscaler, these models run about 2.5 times faster with the
            // arena; ShrinkAfterRun hands its memory back after every run, so nothing is held between reads.
            options.EnableCpuMemArena = true;
            det = new InferenceSession(detection, options);
            rec = new InferenceSession(recognition, options);
            if (!rec.ModelMetadata.CustomMetadataMap.TryGetValue("character", out var list))
            {
                throw new InvalidDataException("The recognition model carries no character list");
            }

            // Class 0 is the CTC blank, then the model's list, then a space.
            string[] characters = ["", .. list.Split('\n'), " "];
            return new Models(det, det.InputMetadata.Keys.First(), rec, rec.InputMetadata.Keys.First(), characters);
        }
        catch (Exception e)
        {
            det?.Dispose();
            rec?.Dispose();
            _failed = true;
            _logger.LogWarning(e, "Could not load the OCR models from {Folder}; credit pages are judged by colour alone", _folder);
            return null;
        }
    }

    /// <summary>
    /// The page as 8-bit sRGB with alpha flattened on white, cut to its top <paramref name="share"/>, or its
    /// bottom one. Like the top, the bottom is cut from the stored orientation (no EXIF rotation) and is at
    /// least 8 rows tall.
    /// </summary>
    private static Rgb Decode(byte[] image, double share, bool bottom)
    {
        using var source = ImageCodec.Load(image);
        using var srgb = source.Colourspace(Enums.Interpretation.Srgb);
        using var flat = srgb.HasAlpha() ? srgb.Flatten(background: [255, 255, 255]) : srgb.Copy();
        using var cast = flat.Cast(Enums.BandFormat.Uchar);
        // The bottom rounds its top edge down, as the research read it, so it never starts inside the region.
        var rows = bottom ? cast.Height - (int)Math.Floor(cast.Height * (1 - share)) : (int)(cast.Height * share);
        var height = share >= 1 ? cast.Height : Math.Clamp(rows, Math.Min(8, cast.Height), cast.Height);
        using var cut = height == cast.Height ? cast.Copy() : cast.Crop(0, bottom ? cast.Height - height : 0, cast.Width, height);
        using var rgb = cut.Bands switch
        {
            3 => cut.Copy(),
            > 3 => cut.ExtractBand(0, 3),
            _ => cut.Bandjoin(cut, cut),
        };
        return new Rgb(rgb.WriteToMemory<byte>(), rgb.Width, rgb.Height);
    }

    private static string Read(Models models, Rgb page)
    {
        var lines = new List<string>();
        foreach (var (top, bottom) in Tiles(page.Width, page.Height))
        {
            var tile = page.Rows(top, bottom);
            var crops = Boxes(models, tile).Select(box => Crop(tile, box)).ToList();
            foreach (var (text, score) in Recognise(models, crops))
            {
                if (score >= TextScore && !string.IsNullOrWhiteSpace(text))
                {
                    lines.Add(text);
                }
            }
        }

        return string.Join(" | ", lines);
    }

    private static List<(int Top, int Bottom)> Tiles(int width, int height)
    {
        if (height <= TileAbove * width)
        {
            return [(0, height)];
        }

        var step = (int)(TileStep * width);
        var tall = (int)(TileWidths * width);
        var end = Math.Max(1, height - (int)((TileWidths - TileStep) * width));
        var tiles = new List<(int, int)>();
        for (var y = 0; y < end; y += step)
        {
            tiles.Add((y, Math.Min(height, y + tall)));
        }

        return tiles;
    }

    /// <summary>Oriented text boxes in the image's own pixels, top to bottom.</summary>
    private static List<Box> Boxes(Models models, Rgb image)
    {
        var (w, h) = (image.Width, image.Height);
        var s = Math.Min(Math.Min((double)ShortSide / Math.Min(h, w), (double)LongSide / Math.Max(h, w)), MaxUpscale);
        var dw = Math.Max(32, (int)Math.Round(w * s / 32) * 32);
        var dh = Math.Max(32, (int)Math.Round(h * s / 32) * 32);
        var small = Resample.Resize(image.Pixels, w, h, dw, dh);

        // BGR, as PaddleOCR feeds it.
        var input = new DenseTensor<float>([1, 3, dh, dw]);
        var plane = dw * dh;
        var buffer = input.Buffer.Span;
        for (var i = 0; i < plane; i++)
        {
            for (var c = 0; c < 3; c++)
            {
                buffer[c * plane + i] = (small[i * 3 + (2 - c)] / 255f - DetectionMean[c]) / DetectionStd[c];
            }
        }

        float[] prob;
        int mapW, mapH;
        using (var results = models.Detection.Run(
            [NamedOnnxValue.CreateFromTensor(models.DetectionInput, input)], models.Detection.OutputNames, ShrinkAfterRun))
        {
            var output = results[0].AsTensor<float>();
            mapH = output.Dimensions[2];
            mapW = output.Dimensions[3];
            prob = output.ToArray();
        }

        var mask = new bool[mapW * mapH];
        for (var y = 0; y < mapH; y++)
        {
            for (var x = 0; x < mapW; x++)
            {
                bool At(int yy, int xx) => yy >= 0 && xx >= 0 && prob[yy * mapW + xx] > Threshold;
                mask[y * mapW + x] = At(y, x) || At(y, x - 1) || At(y - 1, x) || At(y - 1, x - 1);
            }
        }

        double fx = (double)w / mapW, fy = (double)h / mapH;
        var boxes = new List<Box>();
        var seen = new bool[mask.Length];
        var pixels = new List<int>();
        var stack = new Stack<int>();
        for (var start = 0; start < mask.Length; start++)
        {
            if (!mask[start] || seen[start])
            {
                continue;
            }

            pixels.Clear();
            seen[start] = true;
            stack.Push(start);
            while (stack.Count > 0)
            {
                var at = stack.Pop();
                pixels.Add(at);
                int ax = at % mapW, ay = at / mapW;
                for (var ny = Math.Max(0, ay - 1); ny <= Math.Min(mapH - 1, ay + 1); ny++)
                {
                    for (var nx = Math.Max(0, ax - 1); nx <= Math.Min(mapW - 1, ax + 1); nx++)
                    {
                        var next = ny * mapW + nx;
                        if (mask[next] && !seen[next])
                        {
                            seen[next] = true;
                            stack.Push(next);
                        }
                    }
                }
            }

            if (Component(pixels, prob, mapW, fx, fy) is { } box)
            {
                boxes.Add(box);
            }
        }

        return [.. boxes.OrderBy(b => b.Cy - b.Thickness / 2).ThenBy(b => b.Cx)];
    }

    private static Box? Component(List<int> pixels, float[] prob, int mapW, double fx, double fy)
    {
        int x0 = int.MaxValue, y0 = int.MaxValue, x1 = int.MinValue, y1 = int.MinValue;
        double score = 0, mx = 0, my = 0;
        foreach (var at in pixels)
        {
            int x = at % mapW, y = at / mapW;
            (x0, x1, y0, y1) = (Math.Min(x0, x), Math.Max(x1, x), Math.Min(y0, y), Math.Max(y1, y));
            score += prob[at];
            mx += (x + 0.5) * fx;
            my += (y + 0.5) * fy;
        }

        if (Math.Min(x1 - x0 + 1, y1 - y0 + 1) < MinComponentSide || score / pixels.Count < BoxThreshold)
        {
            return null;
        }

        mx /= pixels.Count;
        my /= pixels.Count;
        double sxx = 0, syy = 0, sxy = 0;
        foreach (var at in pixels)
        {
            var dx = (at % mapW + 0.5) * fx - mx;
            var dy = (at / mapW + 0.5) * fy - my;
            sxx += dx * dx;
            syy += dy * dy;
            sxy += dx * dy;
        }

        var theta = 0.5 * Math.Atan2(2 * sxy / pixels.Count, (sxx - syy) / pixels.Count);
        var (ux, uy) = (Math.Cos(theta), Math.Sin(theta));
        var (minU, maxU, minV, maxV) = Spans(pixels, mapW, fx, fy, mx, my, ux, uy);
        double length = maxU - minU + fx, thickness = maxV - minV + fy;
        if (length < AspectForAngle * thickness)
        {
            // Too square for its direction to mean anything: keep it upright.
            (ux, uy) = (1, 0);
            (minU, maxU, minV, maxV) = Spans(pixels, mapW, fx, fy, mx, my, ux, uy);
            (length, thickness) = (maxU - minU + fx, maxV - minV + fy);
        }

        double cu = (maxU + minU) / 2, cv = (maxV + minV) / 2;
        var cx = mx + cu * ux - cv * uy;
        var cy = my + cu * uy + cv * ux;
        var grow = length * thickness * Unclip / (2 * (length + thickness));
        length += 2 * grow;
        thickness += 2 * grow;
        if (Math.Min(length, thickness) < MinSide * fx)
        {
            return null;
        }

        if (thickness > length)
        {
            // A tall upright box is vertical text: read it top to bottom.
            (ux, uy, length, thickness) = (0, 1, thickness, length);
        }

        return new Box(cx, cy, ux, uy, length, thickness);
    }

    private static (double MinU, double MaxU, double MinV, double MaxV) Spans(
        List<int> pixels, int mapW, double fx, double fy, double mx, double my, double ux, double uy)
    {
        double minU = double.MaxValue, maxU = double.MinValue, minV = double.MaxValue, maxV = double.MinValue;
        foreach (var at in pixels)
        {
            var dx = (at % mapW + 0.5) * fx - mx;
            var dy = (at / mapW + 0.5) * fy - my;
            var u = dx * ux + dy * uy;
            var v = -dx * uy + dy * ux;
            (minU, maxU, minV, maxV) = (Math.Min(minU, u), Math.Max(maxU, u), Math.Min(minV, v), Math.Max(maxV, v));
        }

        return (minU, maxU, minV, maxV);
    }

    /// <summary>The box cut out upright: its length along x, its thickness along y, sampled bilinearly with edges repeated.</summary>
    private static Rgb Crop(Rgb image, Box box)
    {
        var wd = Math.Max(1, (int)Math.Round(box.Length));
        var ht = Math.Max(1, (int)Math.Round(box.Thickness));
        double vx = -box.Uy, vy = box.Ux;
        var ox = box.Cx - box.Ux * box.Length / 2 - vx * box.Thickness / 2;
        var oy = box.Cy - box.Uy * box.Length / 2 - vy * box.Thickness / 2;
        var output = new byte[wd * ht * 3];
        var source = image.Pixels;
        var (w, h) = (image.Width, image.Height);
        for (var y = 0; y < ht; y++)
        {
            for (var x = 0; x < wd; x++)
            {
                var sx = box.Ux * x + vx * y + ox;
                var sy = box.Uy * x + vy * y + oy;
                var ix = (int)Math.Floor(sx);
                var iy = (int)Math.Floor(sy);
                var (ax, ay) = (sx - ix, sy - iy);
                int xa = Math.Clamp(ix, 0, w - 1), xb = Math.Clamp(ix + 1, 0, w - 1);
                int ya = Math.Clamp(iy, 0, h - 1), yb = Math.Clamp(iy + 1, 0, h - 1);
                for (var c = 0; c < 3; c++)
                {
                    var top = source[(ya * w + xa) * 3 + c] * (1 - ax) + source[(ya * w + xb) * 3 + c] * ax;
                    var bottom = source[(yb * w + xa) * 3 + c] * (1 - ax) + source[(yb * w + xb) * 3 + c] * ax;
                    output[(y * wd + x) * 3 + c] = (byte)Math.Clamp(Math.Round(top * (1 - ay) + bottom * ay), 0, 255);
                }
            }
        }

        return new Rgb(output, wd, ht);
    }

    /// <summary>Text and confidence per crop, in batches of lines of similar shape.</summary>
    private static (string Text, double Score)[] Recognise(Models models, List<Rgb> crops)
    {
        var results = new (string, double)[crops.Count];
        var order = Enumerable.Range(0, crops.Count).OrderBy(i => (double)crops[i].Width / crops[i].Height).ToArray();
        for (var b = 0; b < order.Length; b += Batch)
        {
            var batch = order.AsSpan(b, Math.Min(Batch, order.Length - b)).ToArray();
            var ratio = Math.Max((double)LineWidth / LineHeight, batch.Max(i => (double)crops[i].Width / crops[i].Height));
            var width = (int)(LineHeight * ratio);
            var input = new DenseTensor<float>([batch.Length, 3, LineHeight, width]);
            var buffer = input.Buffer.Span;
            var plane = LineHeight * width;
            for (var j = 0; j < batch.Length; j++)
            {
                var crop = crops[batch[j]];
                var rw = Math.Min(width, (int)Math.Ceiling((double)LineHeight * crop.Width / crop.Height));
                var line = Resample.Resize(crop.Pixels, crop.Width, crop.Height, rw, LineHeight);
                for (var y = 0; y < LineHeight; y++)
                {
                    for (var x = 0; x < rw; x++)
                    {
                        for (var c = 0; c < 3; c++)
                        {
                            buffer[(j * 3 + c) * plane + y * width + x] = line[(y * rw + x) * 3 + (2 - c)] / 127.5f - 1f;
                        }
                    }
                }
            }

            using var output = models.Recognition.Run(
                [NamedOnnxValue.CreateFromTensor(models.RecognitionInput, input)], models.Recognition.OutputNames, ShrinkAfterRun);
            var probs = output[0].AsTensor<float>();
            var steps = probs.Dimensions[1];
            var classes = probs.Dimensions[2];
            var flat = probs.ToArray();
            for (var j = 0; j < batch.Length; j++)
            {
                results[batch[j]] = Decode(flat.AsSpan(j * steps * classes, steps * classes), steps, classes, models.Characters);
            }
        }

        return results;
    }

    /// <summary>Greedy CTC: the best class per step, blanks and repeats dropped; the score is the mean of the kept steps.</summary>
    private static (string Text, double Score) Decode(ReadOnlySpan<float> probs, int steps, int classes, string[] characters)
    {
        var text = new System.Text.StringBuilder();
        double total = 0;
        var kept = 0;
        var previous = 0;
        for (var t = 0; t < steps; t++)
        {
            var row = probs.Slice(t * classes, classes);
            var best = 0;
            for (var k = 1; k < classes; k++)
            {
                if (row[k] > row[best])
                {
                    best = k;
                }
            }

            if (best != 0 && best != previous)
            {
                text.Append(best < characters.Length ? characters[best] : "");
                total += row[best];
                kept++;
            }

            previous = best;
        }

        return (text.ToString(), kept == 0 ? 0 : total / kept);
    }

    private sealed record Box(double Cx, double Cy, double Ux, double Uy, double Length, double Thickness);

    /// <summary>Interleaved 8-bit RGB pixels.</summary>
    private sealed record Rgb(byte[] Pixels, int Width, int Height)
    {
        public Rgb Rows(int top, int bottom) =>
            top == 0 && bottom == Height
                ? this
                : new Rgb(Pixels.AsSpan(top * Width * 3, (bottom - top) * Width * 3).ToArray(), Width, bottom - top);
    }

    private sealed class Models(
        InferenceSession detection, string detectionInput, InferenceSession recognition, string recognitionInput, string[] characters)
        : IDisposable
    {
        public InferenceSession Detection { get; } = detection;
        public string DetectionInput { get; } = detectionInput;
        public InferenceSession Recognition { get; } = recognition;
        public string RecognitionInput { get; } = recognitionInput;
        public string[] Characters { get; } = characters;

        public void Dispose()
        {
            Detection.Dispose();
            Recognition.Dispose();
        }
    }
}
