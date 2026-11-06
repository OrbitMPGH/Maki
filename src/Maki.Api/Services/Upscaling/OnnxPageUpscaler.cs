using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using Maki.Core.Configuration;
using Maki.Core.Images;
using Maki.Core.Images.Upscaling;
using Maki.Metadata.Embedding;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace Maki.Api.Services.Upscaling;

/// <summary>
/// Runs pages through the downloaded upscale models on the CPU. One <see cref="InferenceSession"/> per
/// model, loaded on first use and dropped by <see cref="ReleaseIfIdle"/>. Decode and encode take
/// <see cref="ImageWorkGate"/>; inference does not, the caller bounds it.
///
/// <para>
/// A page's tiles go to up to <see cref="UpscaleOptions.Threads"/> workers, each running one tile at
/// a time on one thread and taking the next when it is done. ORT's own threading splits every layer
/// of a tile into equal chunks and waits for the slowest, which on a CPU with efficiency cores is
/// the slow core at every layer; independent tiles balance themselves. Each worker holds its tile's
/// activations, so the count is capped by <see cref="WorkerBudgetBytes"/>.
/// </para>
/// </summary>
public sealed class OnnxPageUpscaler(
    UpscaleModelCatalog catalog,
    UpscaleModelStore store,
    IAppSettings settings,
    ILogger<OnnxPageUpscaler> logger) : IPageUpscaler, IDisposable
{
    /// <summary>What the workers of one run may hold in activations, about what one 384 px tile took.</summary>
    internal const long WorkerBudgetBytes = 240L << 20;

    /// <summary>
    /// A worker's peak per context pixel: SPAN-S measured 22 MB on 120 px contexts (fp32 activations
    /// of 48 channels in ORT's blocked layout), Real-ESRGAN 18 MB.
    /// </summary>
    private const int WorkerBytesPerPixel = 1600;

    private readonly object _gate = new();
    private readonly Lock _hooks = new();
    private readonly Dictionary<UpscaleModelId, LoadedModel> _sessions = [];

    /// <summary>Called after each tile with its index. Tests only.</summary>
    internal Action<int>? TileCompleted { get; set; }

    /// <summary>Called with the index of each tile filled without inference. Tests only.</summary>
    internal Action<int>? TileSkipped { get; set; }

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

        var (source, skip) = await ImageWorkGate.RunAsync(
            () => Task.FromResult(ImageCodec.DecodeForUpscale(page)), ImageWorkGate.Weigh(page), ct);
        if (source is null)
        {
            logger.LogDebug("Skipped upscaling a page with {Model}: {Reason}", model, skip);
            return UpscaleResult.Skipped(skip ?? UpscaleReasons.Unreadable);
        }

        var grey = UpscalePixels.IsGreyscale(source.Pixels);
        var luma = grey && UpscalePixels.IsExactGrey(source.Pixels);
        var threads = (await UpscaleOptions.LoadAsync(settings, ct)).Threads;
        StitchedPage stitched;
        try
        {
            stitched = await Task.Run(() => Run(source, info, threads, luma, ct), ct);
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
            var outputWeight = ImageWorkGate.Weigh(stitched.Width, stitched.Height);
            if (luma)
            {
                return UpscaleResult.Ready(await ImageWorkGate.RunAsync(
                    () => Task.FromResult(ImageCodec.EncodeUpscaledGrey(stitched.Pixels, stitched.Width, stitched.Height)),
                    outputWeight, ct));
            }

            var original = await ImageWorkGate.RunAsync(() => Task.FromResult(ImageCodec.ResizeRgb(
                source.Pixels, source.Width, source.Height, (double)stitched.Width / source.Width)), outputWeight, ct);
            if ((original.Width, original.Height) != (stitched.Width, stitched.Height))
            {
                throw new InvalidDataException("The resized source does not match the upscaled page.");
            }

            UpscalePixels.TakeColourFrom(stitched.Pixels, original.Rgb);
            var encoded = await ImageWorkGate.RunAsync(
                () => Task.FromResult(ImageCodec.EncodeUpscaled(stitched.Pixels, stitched.Width, stitched.Height, grey)),
                outputWeight, ct);
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

    /// <param name="luma">
    /// Stitch one band, the luma of the model output, instead of RGB. Only for an exact-grey source
    /// (<see cref="UpscalePixels.IsExactGrey"/>), where it equals the colour pass and band average.
    /// </param>
    internal StitchedPage Run(Raster source, UpscaleModelInfo info, int threads, bool luma, CancellationToken ct)
    {
        var loaded = Acquire(info);
        try
        {
            return RunTiles(loaded, source, info, threads, luma, ct);
        }
        finally
        {
            Release(loaded);
        }
    }

    private StitchedPage RunTiles(
        LoadedModel loaded, Raster source, UpscaleModelInfo info, int threads, bool luma, CancellationToken ct)
    {
        var tiles = UpscaleTiling.Plan(source.Width, source.Height,
            multiple: info.Adapter == UpscaleAdapter.Plain4xThenHalve ? 1 : 4);
        var scale = info.OutputScale;
        var width = source.Width * scale;
        var height = source.Height * scale;
        var bands = luma ? 1 : 3;
        if ((long)width * height * bands > Array.MaxLength)
        {
            throw new InvalidDataException("The upscaled page does not fit in one buffer.");
        }

        var output = new byte[width * height * bands];
        var page = new StitchedPage(output, width, height, bands);
        var pending = new List<int>(tiles.Count);
        for (var i = 0; i < tiles.Count; i++)
        {
            if (FlatValue(source, tiles[i]) is { } flat && KeepsFlat(info, flat))
            {
                FillTile(tiles[i], scale, output, width, bands, flat);
                Report(TileSkipped, i);
                Report(TileCompleted, i);
            }
            else
            {
                pending.Add(i);
            }
        }

        var workers = Math.Min(Math.Min(threads, MaxWorkers(tiles[0])), pending.Count);
        if (workers == 0)
        {
            return page;
        }

        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var next = -1;
        Exception? failure = null;

        void Work()
        {
            try
            {
                using var buffers = new TileBuffers(loaded, tiles, info.ModelScale, scale);
                using var runOptions = new RunOptions();
                using var terminate = stop.Token.Register(() => runOptions.Terminate = true);
                while (!stop.IsCancellationRequested)
                {
                    var n = Interlocked.Increment(ref next);
                    if (n >= pending.Count)
                    {
                        return;
                    }

                    RunTile(loaded, buffers, runOptions, source, info, tiles[pending[n]], output, width, bands, stop.Token);
                    Report(TileCompleted, pending[n]);
                }
            }
            catch (Exception ex)
            {
                Interlocked.CompareExchange(ref failure, ex, null);
                stop.Cancel();
            }
        }

        var helpers = new Task[workers - 1];
        for (var i = 0; i < helpers.Length; i++)
        {
            helpers[i] = Task.Factory.StartNew(Work, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        }

        Work();
        Task.WaitAll(helpers);
        ct.ThrowIfCancellationRequested();
        if (failure is not null)
        {
            ExceptionDispatchInfo.Throw(failure);
        }

        return page;
    }

    /// <summary>How many tiles of this shape may run at once inside <see cref="WorkerBudgetBytes"/>.</summary>
    internal static int MaxWorkers(UpscaleTile tile) =>
        (int)Math.Max(1, WorkerBudgetBytes / ((long)tile.ContextWidth * tile.ContextHeight * WorkerBytesPerPixel));

    private static void RunTile(
        LoadedModel loaded, TileBuffers buffers, RunOptions runOptions, Raster source, UpscaleModelInfo info,
        UpscaleTile tile, byte[] output, int width, int bands, CancellationToken ct)
    {
        var scale = info.OutputScale;
        buffers.Fill(source, tile);
        try
        {
            loaded.Session.RunWithBinding(runOptions, buffers.Bind(tile));
        }
        catch (OnnxRuntimeException) when (ct.IsCancellationRequested)
        {
            throw new OperationCanceledException(ct);
        }

        var (rgb, tileWidth, tileHeight) = buffers.Read(tile);
        if (info.ModelScale != scale)
        {
            var halved = buffers.Resized;
            (tileWidth, tileHeight) = ImageWorkGate.RunAsync(
                () => Task.FromResult(ImageCodec.ResizeRgb(rgb, tileWidth, tileHeight, (double)scale / info.ModelScale, halved)),
                ct).GetAwaiter().GetResult();
            if ((tileWidth, tileHeight) != (tile.ContextWidth * scale, tile.ContextHeight * scale))
            {
                throw new InvalidDataException($"Resizing a tile gave {tileWidth}x{tileHeight}.");
            }

            rgb = halved;
        }

        StitchTile(rgb, tileWidth, tile, scale, output, width, bands);
    }

    private void Report(Action<int>? hook, int tile)
    {
        if (hook is null)
        {
            return;
        }

        lock (_hooks)
        {
            hook(tile);
        }
    }

    /// <summary>
    /// The grey value of a context tile whose every pixel is that one grey, or null. SPAN-S returns
    /// such a tile unchanged at every level; Real-ESRGAN only at 0 and 255 and drifts a few levels
    /// on mid-grey, measured on a 416 px tile.
    /// </summary>
    private static byte? FlatValue(Raster source, UpscaleTile tile)
    {
        var pixels = source.Pixels;
        var first = tile.SourceY(0, source.Height) * source.Stride + tile.SourceX(0, source.Width) * 3;
        var value = pixels[first];
        for (var y = 0; y < tile.ContextHeight; y++)
        {
            var row = tile.SourceY(y, source.Height) * source.Stride;
            for (var x = 0; x < tile.ContextWidth; x++)
            {
                var p = row + tile.SourceX(x, source.Width) * 3;
                if (pixels[p] != value || pixels[p + 1] != value || pixels[p + 2] != value)
                {
                    return null;
                }
            }
        }

        return value;
    }

    // Maki 2x is within a level of any flat value deep in a tile but up to 6 off near its edges (it reaches
    // further than the 12 px pad), so filling is closer to the model than running it is.
    private static bool KeepsFlat(UpscaleModelInfo info, byte value) =>
        info.Id == UpscaleModelId.SpanS || info.Id == UpscaleModelId.Maki2x || value is 0 or 255;

    private static void FillTile(UpscaleTile tile, int scale, byte[] output, int outputWidth, int bands, byte value)
    {
        var rowBytes = tile.Width * scale * bands;
        for (var y = 0; y < tile.Height * scale; y++)
        {
            output.AsSpan(((tile.Y * scale + y) * outputWidth + tile.X * scale) * bands, rowBytes).Fill(value);
        }
    }

    /// <summary>
    /// Copies the tile's interior, past <c>Pad × scale</c> px of context, into the page: as RGB, or as
    /// its luma when the page has one band.
    /// </summary>
    private static void StitchTile(byte[] rgb, int rgbWidth, UpscaleTile tile, int scale, byte[] output, int outputWidth, int bands)
    {
        var offset = tile.Pad * scale;
        var rowPixels = tile.Width * scale;
        for (var y = 0; y < tile.Height * scale; y++)
        {
            var from = ((offset + y) * rgbWidth + offset) * 3;
            var to = (tile.Y * scale + y) * outputWidth + tile.X * scale;
            if (bands == 3)
            {
                Buffer.BlockCopy(rgb, from, output, to * 3, rowPixels * 3);
                continue;
            }

            for (var x = 0; x < rowPixels; x++, from += 3)
            {
                output[to + x] = (byte)UpscalePixels.Luma(rgb[from], rgb[from + 1], rgb[from + 2]);
            }
        }
    }

    private static readonly float[] FloatLevels = Enumerable.Range(0, 256).Select(v => v / 255f).ToArray();
    private static readonly Half[] HalfLevels = FloatLevels.Select(v => (Half)v).ToArray();

    /// <summary>
    /// One page's tensors and scratch. The input and output tensors live in pinned arrays sized to the
    /// largest context tile, with one <see cref="OrtValue"/> pair over them per tile shape, so ORT
    /// writes each result in place instead of allocating it.
    /// </summary>
    private sealed class TileBuffers : IDisposable
    {
        private readonly LoadedModel _model;
        private readonly int _modelScale;
        private readonly float[]? _floatInput;
        private readonly Float16[]? _halfInput;
        private readonly float[]? _floatOutput;
        private readonly Float16[]? _halfOutput;
        private readonly int[] _columns;
        private readonly byte[] _rgb;
        private readonly OrtIoBinding _binding;
        private readonly Dictionary<(int Width, int Height), (OrtValue Input, OrtValue Output)> _shapes = [];
        private (int Width, int Height)? _bound;

        public TileBuffers(LoadedModel model, IReadOnlyList<UpscaleTile> tiles, int modelScale, int outputScale)
        {
            _model = model;
            _modelScale = modelScale;
            var maxWidth = tiles.Max(t => t.ContextWidth);
            var maxHeight = tiles.Max(t => t.ContextHeight);
            var input = maxWidth * maxHeight * 3;
            var result = input * modelScale * modelScale;
            if (model.InputType == TensorElementType.Float16)
            {
                _halfInput = GC.AllocateUninitializedArray<Float16>(input, pinned: true);
            }
            else
            {
                _floatInput = GC.AllocateUninitializedArray<float>(input, pinned: true);
            }

            if (model.OutputType == TensorElementType.Float16)
            {
                _halfOutput = GC.AllocateUninitializedArray<Float16>(result, pinned: true);
            }
            else
            {
                _floatOutput = GC.AllocateUninitializedArray<float>(result, pinned: true);
            }

            _columns = new int[maxWidth];
            _rgb = GC.AllocateUninitializedArray<byte>(result);
            Resized = modelScale == outputScale ? [] : GC.AllocateUninitializedArray<byte>(input * outputScale * outputScale);
            _binding = model.Session.CreateIoBinding();
        }

        /// <summary>Where a 4x tile is resized to the output scale.</summary>
        public byte[] Resized { get; }

        public void Fill(Raster source, UpscaleTile tile)
        {
            var w = tile.ContextWidth;
            var h = tile.ContextHeight;
            var plane = w * h;
            for (var x = 0; x < w; x++)
            {
                _columns[x] = tile.SourceX(x, source.Width) * 3;
            }

            var pixels = source.Pixels;
            if (_halfInput is not null)
            {
                var halves = MemoryMarshal.Cast<Float16, Half>(_halfInput.AsSpan(0, plane * 3));
                for (var y = 0; y < h; y++)
                {
                    var row = tile.SourceY(y, source.Height) * source.Stride;
                    var o = y * w;
                    for (var x = 0; x < w; x++)
                    {
                        var p = row + _columns[x];
                        halves[o + x] = HalfLevels[pixels[p]];
                        halves[plane + o + x] = HalfLevels[pixels[p + 1]];
                        halves[2 * plane + o + x] = HalfLevels[pixels[p + 2]];
                    }
                }

                return;
            }

            var floats = _floatInput.AsSpan(0, plane * 3);
            for (var y = 0; y < h; y++)
            {
                var row = tile.SourceY(y, source.Height) * source.Stride;
                var o = y * w;
                for (var x = 0; x < w; x++)
                {
                    var p = row + _columns[x];
                    floats[o + x] = FloatLevels[pixels[p]];
                    floats[plane + o + x] = FloatLevels[pixels[p + 1]];
                    floats[2 * plane + o + x] = FloatLevels[pixels[p + 2]];
                }
            }
        }

        public OrtIoBinding Bind(UpscaleTile tile)
        {
            var shape = (tile.ContextWidth, tile.ContextHeight);
            if (_bound == shape)
            {
                return _binding;
            }

            if (!_shapes.TryGetValue(shape, out var values))
            {
                var length = tile.ContextWidth * tile.ContextHeight * 3;
                var resultLength = length * _modelScale * _modelScale;
                long[] inputShape = [1, 3, tile.ContextHeight, tile.ContextWidth];
                long[] outputShape = [1, 3, tile.ContextHeight * _modelScale, tile.ContextWidth * _modelScale];
                var input = _halfInput is not null
                    ? OrtValue.CreateTensorValueFromMemory(OrtMemoryInfo.DefaultInstance, _halfInput.AsMemory(0, length), inputShape)
                    : OrtValue.CreateTensorValueFromMemory(OrtMemoryInfo.DefaultInstance, _floatInput.AsMemory(0, length), inputShape);
                var output = _halfOutput is not null
                    ? OrtValue.CreateTensorValueFromMemory(OrtMemoryInfo.DefaultInstance, _halfOutput.AsMemory(0, resultLength), outputShape)
                    : OrtValue.CreateTensorValueFromMemory(OrtMemoryInfo.DefaultInstance, _floatOutput.AsMemory(0, resultLength), outputShape);
                values = (input, output);
                _shapes[shape] = values;
            }

            _binding.BindInput(_model.InputName, values.Input);
            _binding.BindOutput(_model.OutputName, values.Output);
            _bound = shape;
            return _binding;
        }

        /// <summary>The whole context tile the model returned, as interleaved 8-bit RGB at the model's scale.</summary>
        public (byte[] Rgb, int Width, int Height) Read(UpscaleTile tile)
        {
            var width = tile.ContextWidth * _modelScale;
            var height = tile.ContextHeight * _modelScale;
            var plane = width * height;
            var rgb = _rgb;
            if (_halfOutput is not null)
            {
                var halves = MemoryMarshal.Cast<Float16, Half>(_halfOutput.AsSpan(0, plane * 3));
                for (var s = 0; s < plane; s++)
                {
                    rgb[s * 3] = ToByte((float)halves[s]);
                    rgb[s * 3 + 1] = ToByte((float)halves[plane + s]);
                    rgb[s * 3 + 2] = ToByte((float)halves[2 * plane + s]);
                }
            }
            else
            {
                var floats = _floatOutput.AsSpan(0, plane * 3);
                for (var s = 0; s < plane; s++)
                {
                    rgb[s * 3] = ToByte(floats[s]);
                    rgb[s * 3 + 1] = ToByte(floats[plane + s]);
                    rgb[s * 3 + 2] = ToByte(floats[2 * plane + s]);
                }
            }

            return (rgb, width, height);
        }

        public void Dispose()
        {
            _binding.Dispose();
            foreach (var (input, output) in _shapes.Values)
            {
                input.Dispose();
                output.Dispose();
            }
        }
    }

    private static byte ToByte(float value) =>
        value > 0f ? value < 1f ? (byte)(value * 255f + 0.5f) : (byte)255 : (byte)0;

    private LoadedModel Acquire(UpscaleModelInfo info)
    {
        lock (_gate)
        {
            if (!_sessions.TryGetValue(info.Id, out var loaded))
            {
                loaded = Load(info);
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

    /// <summary>One intra-op thread: the workers of <see cref="RunTiles"/> are the parallelism.</summary>
    private LoadedModel Load(UpscaleModelInfo info)
    {
        InferenceSession? session = null;
        try
        {
            using var options = OnnxMemoryOptions.Create(intraOpThreads: 1, fixedShapes: true);
            session = new InferenceSession(store.PathOf(info.Id), options);
            var output = session.OutputNames[0];
            var inputType = ElementType(session.InputMetadata[info.InputName]);
            var outputType = ElementType(session.OutputMetadata[output]);
            logger.LogInformation("Loaded upscale model {Model} ({Type})", info.Id, inputType);
            return new LoadedModel(session, info.InputName, output, inputType, outputType);
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

    /// <param name="Bands">3 for interleaved RGB, 1 for luma.</param>
    internal sealed record StitchedPage(byte[] Pixels, int Width, int Height, int Bands);

    private sealed class ModelLoadException(Exception inner) : Exception(inner.Message, inner);

    private sealed class LoadedModel(
        InferenceSession session, string inputName, string outputName, TensorElementType inputType,
        TensorElementType outputType)
    {
        public InferenceSession Session { get; } = session;
        public string InputName { get; } = inputName;
        public string OutputName { get; } = outputName;
        public TensorElementType InputType { get; } = inputType;
        public TensorElementType OutputType { get; } = outputType;
        public int InUse { get; set; }
        public DateTime LastUsedUtc { get; set; } = DateTime.UtcNow;
        public bool Retired { get; set; }
    }
}
