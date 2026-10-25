using System.Runtime.InteropServices;
using Maki.Core.Configuration;
using Maki.Core.Images;
using Maki.Core.Images.Upscaling;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace Maki.Api.Services.Upscaling;

/// <summary>
/// Runs pages through the downloaded upscale models on the CPU. One <see cref="InferenceSession"/> per
/// model, loaded on first use and dropped by <see cref="ReleaseIfIdle"/>. Decode and encode take
/// <see cref="ImageWorkGate"/>; inference does not, the caller bounds it.
/// </summary>
public sealed class OnnxPageUpscaler(
    UpscaleModelCatalog catalog,
    UpscaleModelStore store,
    IAppSettings settings,
    ILogger<OnnxPageUpscaler> logger) : IPageUpscaler, IDisposable
{
    private readonly object _gate = new();
    private readonly Dictionary<UpscaleModelId, LoadedModel> _sessions = [];

    /// <summary>Called after each tile with its index. Tests only.</summary>
    internal Action<int>? TileCompleted { get; set; }

    public Task<UpscaleResult> UpscaleAsync(byte[] page, string modelId, CancellationToken ct) =>
        UpscaleModelId.TryParse(modelId, out var id)
            ? UpscaleAsync(page, id, ct)
            : Task.FromResult(UpscaleResult.Failed(UpscaleReasons.UnknownModel));

    public async Task<UpscaleResult> UpscaleAsync(byte[] page, UpscaleModelId model, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!catalog.TryGet(model, out var info))
        {
            return UpscaleResult.Failed(UpscaleReasons.UnknownModel);
        }

        if (!store.IsReady(model))
        {
            return UpscaleResult.Failed(UpscaleReasons.ModelUnavailable);
        }

        var (source, skip) = await ImageWorkGate.RunAsync(() => Task.FromResult(ImageCodec.DecodeForUpscale(page)), ct);
        if (source is null)
        {
            logger.LogDebug("Skipped upscaling a page with {Model}: {Reason}", model, skip);
            return UpscaleResult.Skipped(skip ?? UpscaleReasons.Unreadable);
        }

        var threads = (await UpscaleOptions.LoadAsync(settings, ct)).Threads;
        StitchedPage stitched;
        try
        {
            stitched = await Task.Run(() => Run(source, info, threads, ct), ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (ModelLoadException ex)
        {
            logger.LogInformation(ex.InnerException, "Upscale model {Model} could not be loaded", model);
            return UpscaleResult.Failed(UpscaleReasons.ModelUnavailable);
        }
        catch (Exception ex)
        {
            logger.LogInformation(ex, "Upscaling a {Width}x{Height} page with {Model} failed", source.Width, source.Height, model);
            return UpscaleResult.Failed(UpscaleReasons.InferenceFailed);
        }

        try
        {
            var encoded = await ImageWorkGate.RunAsync(
                () => Task.FromResult(ImageCodec.EncodeUpscaled(stitched.Rgb, stitched.Width, stitched.Height)), ct);
            return UpscaleResult.Ready(encoded);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogInformation(ex, "Encoding an upscaled page from {Model} failed", model);
            return UpscaleResult.Failed(UpscaleReasons.EncodeFailed);
        }
    }

    /// <summary>Disposes every session nothing has used for <paramref name="idleFor"/>. Returns how many went.</summary>
    public int ReleaseIfIdle(TimeSpan idleFor)
    {
        var released = 0;
        lock (_gate)
        {
            var now = DateTime.UtcNow;
            foreach (var (id, loaded) in _sessions.ToList())
            {
                if (loaded.InUse > 0 || now - loaded.LastUsedUtc < idleFor)
                {
                    continue;
                }

                _sessions.Remove(id);
                loaded.Session.Dispose();
                released++;
                logger.LogInformation("Unloaded upscale model {Model} after {Minutes:F0} idle minute(s)",
                    id, (now - loaded.LastUsedUtc).TotalMinutes);
            }
        }

        return released;
    }

    /// <summary>
    /// Drops the model's session so its file can be deleted. A run still using it keeps it until it
    /// finishes; the next run loads the file again.
    /// </summary>
    public void ReleaseModel(UpscaleModelId id)
    {
        lock (_gate)
        {
            if (_sessions.Remove(id, out var loaded))
            {
                Retire(loaded);
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            foreach (var loaded in _sessions.Values)
            {
                Retire(loaded);
            }

            _sessions.Clear();
        }
    }

    internal bool IsLoaded(UpscaleModelId id)
    {
        lock (_gate)
        {
            return _sessions.ContainsKey(id);
        }
    }

    internal StitchedPage Run(Raster source, UpscaleModelInfo info, int threads, CancellationToken ct)
    {
        var loaded = Acquire(info, threads);
        try
        {
            return RunTiles(loaded, source, info, ct);
        }
        finally
        {
            Release(loaded);
        }
    }

    private StitchedPage RunTiles(LoadedModel loaded, Raster source, UpscaleModelInfo info, CancellationToken ct)
    {
        var tiles = UpscaleTiling.Plan(source.Width, source.Height,
            multiple: info.Adapter == UpscaleAdapter.Plain4xThenHalve ? 1 : 4,
            border: info.Adapter == UpscaleAdapter.Waifu2xSwin ? UpscaleTiling.Waifu2xBorder : 0);
        var scale = info.OutputScale;
        var width = source.Width * scale;
        var height = source.Height * scale;
        if ((long)width * height * 3 > Array.MaxLength)
        {
            throw new InvalidDataException("The upscaled page does not fit in one buffer.");
        }

        var output = new byte[width * height * 3];
        using var runOptions = new RunOptions();
        using var cancel = ct.Register(() => runOptions.Terminate = true);
        for (var i = 0; i < tiles.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var tile = tiles[i];
            using var input = CreateInput(loaded.InputType, source, tile);
            IDisposableReadOnlyCollection<OrtValue> results;
            try
            {
                results = loaded.Session.Run(runOptions, [loaded.InputName], [input], [loaded.OutputName]);
            }
            catch (OnnxRuntimeException) when (ct.IsCancellationRequested)
            {
                throw new OperationCanceledException(ct);
            }

            using (results)
            {
                var (rgb, tileWidth, tileHeight) = ReadTile(results[0], tile, info.ModelScale);
                if (info.ModelScale != scale)
                {
                    (rgb, tileWidth, tileHeight) = ImageWorkGate.RunAsync(
                        () => Task.FromResult(ImageCodec.ResizeRgb(rgb, tileWidth, tileHeight, (double)scale / info.ModelScale)),
                        ct).GetAwaiter().GetResult();
                    if ((tileWidth, tileHeight) != (tile.ContextWidth * scale, tile.ContextHeight * scale))
                    {
                        throw new InvalidDataException($"Resizing a tile gave {tileWidth}x{tileHeight}.");
                    }
                }

                StitchTile(rgb, tileWidth, tile, scale, output, width);
            }

            TileCompleted?.Invoke(i);
        }

        return new StitchedPage(output, width, height);
    }

    private static OrtValue CreateInput(TensorElementType type, Raster source, UpscaleTile tile)
    {
        var w = tile.InputWidth;
        var h = tile.InputHeight;
        var plane = w * h;
        var columns = new int[w];
        for (var x = 0; x < w; x++)
        {
            columns[x] = tile.SourceX(x, source.Width) * 3;
        }

        var values = new float[plane * 3];
        var pixels = source.Pixels;
        for (var y = 0; y < h; y++)
        {
            var row = tile.SourceY(y, source.Height) * source.Stride;
            var o = y * w;
            for (var x = 0; x < w; x++)
            {
                var p = row + columns[x];
                values[o + x] = pixels[p] / 255f;
                values[plane + o + x] = pixels[p + 1] / 255f;
                values[2 * plane + o + x] = pixels[p + 2] / 255f;
            }
        }

        long[] shape = [1, 3, h, w];
        if (type == TensorElementType.Float)
        {
            return OrtValue.CreateTensorValueFromMemory(values, shape);
        }

        var halves = new Float16[values.Length];
        var span = MemoryMarshal.Cast<Float16, Half>(halves.AsSpan());
        for (var i = 0; i < values.Length; i++)
        {
            span[i] = (Half)values[i];
        }

        return OrtValue.CreateTensorValueFromMemory(halves, shape);
    }

    /// <summary>The whole context tile the model returned, as interleaved 8-bit RGB.</summary>
    private static (byte[] Rgb, int Width, int Height) ReadTile(OrtValue result, UpscaleTile tile, int scale)
    {
        var info = result.GetTensorTypeAndShape();
        var shape = info.Shape;
        if (shape.Length != 4 || shape[0] != 1 || shape[1] != 3
            || shape[2] != tile.ContextHeight * scale || shape[3] != tile.ContextWidth * scale)
        {
            throw new InvalidDataException(
                $"Expected [1,3,{tile.ContextHeight * scale},{tile.ContextWidth * scale}], got [{string.Join(',', shape)}].");
        }

        var width = (int)shape[3];
        var height = (int)shape[2];
        var plane = width * height;
        var rgb = new byte[plane * 3];
        if (info.ElementDataType == TensorElementType.Float16)
        {
            var halves = MemoryMarshal.Cast<Float16, Half>(result.GetTensorDataAsSpan<Float16>());
            for (var s = 0; s < plane; s++)
            {
                rgb[s * 3] = ToByte((float)halves[s]);
                rgb[s * 3 + 1] = ToByte((float)halves[plane + s]);
                rgb[s * 3 + 2] = ToByte((float)halves[2 * plane + s]);
            }
        }
        else
        {
            var floats = result.GetTensorDataAsSpan<float>();
            for (var s = 0; s < plane; s++)
            {
                rgb[s * 3] = ToByte(floats[s]);
                rgb[s * 3 + 1] = ToByte(floats[plane + s]);
                rgb[s * 3 + 2] = ToByte(floats[2 * plane + s]);
            }
        }

        return (rgb, width, height);
    }

    /// <summary>Copies the tile's interior, past <c>Pad × scale</c> px of context, into the page.</summary>
    private static void StitchTile(byte[] rgb, int rgbWidth, UpscaleTile tile, int scale, byte[] output, int outputWidth)
    {
        var offset = tile.Pad * scale;
        var rowBytes = tile.Width * scale * 3;
        for (var y = 0; y < tile.Height * scale; y++)
        {
            Buffer.BlockCopy(rgb, ((offset + y) * rgbWidth + offset) * 3,
                output, ((tile.Y * scale + y) * outputWidth + tile.X * scale) * 3, rowBytes);
        }
    }

    private static byte ToByte(float value) =>
        value > 0f ? value < 1f ? (byte)(value * 255f + 0.5f) : (byte)255 : (byte)0;

    private LoadedModel Acquire(UpscaleModelInfo info, int threads)
    {
        lock (_gate)
        {
            if (_sessions.TryGetValue(info.Id, out var loaded) && loaded.Threads != threads && loaded.InUse == 0)
            {
                _sessions.Remove(info.Id);
                loaded.Session.Dispose();
                loaded = null;
            }

            if (loaded is null)
            {
                loaded = Load(info, threads);
                _sessions[info.Id] = loaded;
            }

            loaded.InUse++;
            return loaded;
        }
    }

    private void Release(LoadedModel loaded)
    {
        lock (_gate)
        {
            loaded.InUse--;
            loaded.LastUsedUtc = DateTime.UtcNow;
            if (loaded.Retired && loaded.InUse == 0)
            {
                loaded.Session.Dispose();
            }
        }
    }

    private static void Retire(LoadedModel loaded)
    {
        loaded.Retired = true;
        if (loaded.InUse == 0)
        {
            loaded.Session.Dispose();
        }
    }

    private LoadedModel Load(UpscaleModelInfo info, int threads)
    {
        InferenceSession? session = null;
        try
        {
            using var options = new Microsoft.ML.OnnxRuntime.SessionOptions { IntraOpNumThreads = threads, InterOpNumThreads = 1 };
            options.AddSessionConfigEntry("session.intra_op.allow_spinning", "0");
            session = new InferenceSession(store.PathOf(info.Id), options);
            var output = session.OutputNames[0];
            var inputType = ElementType(session.InputMetadata[info.InputName]);
            ElementType(session.OutputMetadata[output]);
            logger.LogInformation("Loaded upscale model {Model} ({Type}, {Threads} thread(s))", info.Id, inputType, threads);
            return new LoadedModel(session, info.InputName, output, inputType, threads);
        }
        catch (Exception ex)
        {
            session?.Dispose();
            throw new ModelLoadException(ex);
        }
    }

    private static TensorElementType ElementType(NodeMetadata metadata) =>
        metadata.ElementDataType is TensorElementType.Float or TensorElementType.Float16
            ? metadata.ElementDataType
            : throw new InvalidDataException($"Unsupported tensor type {metadata.ElementDataType}.");

    internal sealed record StitchedPage(byte[] Rgb, int Width, int Height);

    private sealed class ModelLoadException(Exception inner) : Exception(inner.Message, inner);

    private sealed class LoadedModel(
        InferenceSession session, string inputName, string outputName, TensorElementType inputType, int threads)
    {
        public InferenceSession Session { get; } = session;
        public string InputName { get; } = inputName;
        public string OutputName { get; } = outputName;
        public TensorElementType InputType { get; } = inputType;
        public int Threads { get; } = threads;
        public int InUse { get; set; }
        public DateTime LastUsedUtc { get; set; } = DateTime.UtcNow;
        public bool Retired { get; set; }
    }
}
