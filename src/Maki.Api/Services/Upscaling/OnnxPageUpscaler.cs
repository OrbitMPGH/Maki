using System.Globalization;
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
    /// of 48 channels in ORT's blocked layout), Real-ESRGAN 18 MB, Maki 2x 11 to 12 MB. Maki 2x HQ is
    /// Real-ESRGAN's graph at 48 of its 64 channels, so 3/4 of its cost (estimated, not measured).
    /// </summary>
    private static int WorkerBytesPerPixel(UpscaleModelInfo info) =>
        info.Id == UpscaleModelId.Maki2x ? 960
        : info.Id == UpscaleModelId.Maki2xHq ? 1200
        : 1600;

    /// <summary>
    /// Model metadata: the output is already cropped by this many source px per side of the context
    /// tile (Maki 2x, built by <c>distribution/upscale-models/crop_maki_2x.py</c>). Absent means 0.
    /// The crop is baked into the file for one pad, so it may not exceed <see cref="UpscaleTiling.ContextPad"/>.
    /// </summary>
    internal const string ContextCropKey = "maki.context_crop";

    private readonly object _gate = new();
    private readonly Lock _hooks = new();
    private readonly Dictionary<UpscaleModelId, LoadedModel> _sessions = [];

    /// <summary>One streamed JPEG XL encode working at a time; see <see cref="StreamedJpegXlPage"/>.</summary>
    private readonly SemaphoreSlim _encoderSlot = new(1);

    /// <summary>Called after each tile with its index. Tests only.</summary>
    internal Action<int>? TileCompleted { get; set; }

    /// <summary>Called with the index of each tile filled without inference. Tests only.</summary>
    internal Action<int>? TileSkipped { get; set; }

    /// <summary>Called as each tile worker of a run starts. Tests only.</summary>
    internal Action<int>? WorkerStarted { get; set; }

    /// <summary>Called with each streamed JPEG XL page before its encode starts. Tests only.</summary>
    internal Action<StreamedJpegXlPage>? StreamStarted { get; set; }

    /// <summary>How a streamed page takes its <see cref="ImageWorkGate"/> share. Tests replace it to count or hold it.</summary>
    internal Func<int, CancellationToken, Task<IDisposable>> HoldGate { get; set; } = ImageWorkGate.HoldAsync;

    public Task<UpscaleResult> UpscaleAsync(byte[] page, string modelId, CancellationToken ct) =>
        UpscaleModelId.TryParse(modelId, out var id)
            ? UpscaleAsync(page, id, ct)
            : Task.FromResult(UpscaleResult.Failed(UpscaleReasons.UnknownModel));

    public async Task<UpscaleResult> UpscaleAsync(byte[] page, UpscaleModelId model, CancellationToken ct) =>
        await (await StartAsync(page, model, ct)).FinishAsync(ct);

    public Task<PendingUpscale> StartAsync(byte[] page, string modelId, UpscaleRun run, CancellationToken ct) =>
        UpscaleModelId.TryParse(modelId, out var id)
            ? StartAsync(page, id, run, ct)
            : Task.FromResult(PendingUpscale.Done(UpscaleResult.Failed(UpscaleReasons.UnknownModel)));

    public Task<PendingUpscale> StartAsync(byte[] page, UpscaleModelId model, CancellationToken ct) =>
        StartAsync(page, model, UpscaleRun.Default, ct);

    /// <summary>Decode and inference. The colour pass and encode wait for <see cref="PendingUpscale.FinishAsync"/>.</summary>
    public async Task<PendingUpscale> StartAsync(byte[] page, UpscaleModelId model, UpscaleRun run, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!catalog.TryGet(model, out var info))
        {
            return PendingUpscale.Done(UpscaleResult.Failed(UpscaleReasons.UnknownModel));
        }

        if (!store.IsReady(model))
        {
            return PendingUpscale.Done(UpscaleResult.Failed(UpscaleReasons.ModelUnavailable));
        }

        var (source, skip) = await ImageWorkGate.RunAsync(
            () => Task.FromResult(ImageCodec.DecodeForUpscale(page, preserveGrey: true)), ImageWorkGate.Weigh(page), ct);
        if (source is null)
        {
            logger.LogDebug("Skipped upscaling a page with {Model}: {Reason}", model, skip);
            return PendingUpscale.Done(UpscaleResult.Skipped(skip ?? UpscaleReasons.Unreadable));
        }

        var grey = source.Bands == 1 || UpscalePixels.IsGreyscale(source.Pixels);
        var luma = source.Bands == 1 || (grey && UpscalePixels.IsExactGrey(source.Pixels));
        var options = await UpscaleOptions.LoadAsync(settings, ct);
        var threads = options.Threads;
        if (!grey && options.JpegXl && ImageCodec.FitsJpegXl(source.Width * info.OutputScale, source.Height * info.OutputScale)
            && (run.JpegXl?.Invoke() ?? true))
        {
            return await StartStreamedAsync(source, info, threads, model, run, ct);
        }

        return await StartWholeAsync(source, info, grey, luma, threads, options.JpegXl, model, run, ct);
    }

    /// <summary>The tiles into one buffer for the whole page; <see cref="FinishAsync"/> colours and encodes it.</summary>
    private async Task<PendingUpscale> StartWholeAsync(
        Raster source, UpscaleModelInfo info, bool grey, bool luma, int threads, bool jpegXl, UpscaleModelId model,
        UpscaleRun run, CancellationToken ct)
    {
        NativeStitchedPage stitched;
        try
        {
            stitched = await Task.Run(() => RunNative(source, info, threads, luma, ct, run), ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (ModelLoadException ex)
        {
            logger.LogInformation(ex.InnerException, "Upscale model {Model} could not be loaded", model);
            return PendingUpscale.Done(UpscaleResult.Failed(UpscaleReasons.ModelUnavailable));
        }
        catch (Exception ex)
        {
            logger.LogInformation(ex, "Upscaling a {Width}x{Height} page with {Model} failed", source.Width, source.Height, model);
            return PendingUpscale.Done(UpscaleResult.Failed(UpscaleReasons.InferenceFailed));
        }

        return new PendingUpscale(finish => FinishAsync(source, stitched, grey, luma, threads, jpegXl, model, run, finish));
    }

    /// <summary>
    /// A colour page that will be JPEG XL, encoded strip by strip while its tiles run, so the whole upscaled page
    /// never sits in one buffer. The finish waits for the strips the tiles completed last. A failed encode has no
    /// pixels left to fall back on, so the page is run again whole, which can still fall back to WebP.
    /// </summary>
    private async Task<PendingUpscale> StartStreamedAsync(
        Raster source, UpscaleModelInfo info, int threads, UpscaleModelId model, UpscaleRun run, CancellationToken ct)
    {
        var page = new StreamedJpegXlPage(source, info.OutputScale, _encoderSlot,
            () => run.NarrowNow ? UpscaleRun.NarrowWorkers(threads) : threads, HoldGate);
        StreamStarted?.Invoke(page);
        page.StartEncode();
        try
        {
            await Task.Run(() => RunInto(page, bands: 3, source, info, threads, run, ct), ct);
        }
        catch (Exception ex)
        {
            page.Fail(ex);
            await page.Encoded.ContinueWith(_ => { }, TaskScheduler.Default);
            page.Dispose();
            if (ex is OperationCanceledException && ct.IsCancellationRequested)
            {
                throw;
            }

            if (EncodeFailure(page) is { } failed)
            {
                return await RetryWholeAsync(source, info, threads, model, run, failed, ct);
            }

            if (ex is ModelLoadException)
            {
                logger.LogInformation(ex.InnerException, "Upscale model {Model} could not be loaded", model);
                return PendingUpscale.Done(UpscaleResult.Failed(UpscaleReasons.ModelUnavailable));
            }

            logger.LogInformation(ex, "Upscaling a {Width}x{Height} page with {Model} failed", source.Width, source.Height, model);
            return PendingUpscale.Done(UpscaleResult.Failed(UpscaleReasons.InferenceFailed));
        }

        _ = page.Encoded.ContinueWith(_ => page.Dispose(), TaskScheduler.Default);
        if (EncodeFailure(page) is { } early)
        {
            return await RetryWholeAsync(source, info, threads, model, run, early, ct);
        }

        return new PendingUpscale(finish => FinishStreamedAsync(page, source, info, threads, model, run, finish));
    }

    /// <summary>The encode's own failure, or null when it has not failed or only stopped because the page did.</summary>
    private static Exception? EncodeFailure(StreamedJpegXlPage page) =>
        page.Encoded.IsFaulted && page.Encoded.Exception?.InnerException is { } ex and not OperationCanceledException ? ex : null;

    private Task<PendingUpscale> RetryWholeAsync(
        Raster source, UpscaleModelInfo info, int threads, UpscaleModelId model, UpscaleRun run, Exception failure,
        CancellationToken ct)
    {
        logger.LogInformation(failure, "Streaming a {Width}x{Height} page from {Model} into JPEG XL failed; upscaling it again whole",
            source.Width, source.Height, model);
        return StartWholeAsync(source, info, grey: false, luma: false, threads, jpegXl: true, model, run, ct);
    }

    /// <remarks>
    /// Readers who said no to JPEG XL only after the tiles started get the page converted on each GET, as any JPEG XL
    /// page is: converting it here would store a second lossy generation under the name of a direct encode.
    /// </remarks>
    private async Task<UpscaleResult> FinishStreamedAsync(
        StreamedJpegXlPage page, Raster source, UpscaleModelInfo info, int threads, UpscaleModelId model, UpscaleRun run,
        CancellationToken ct)
    {
        UpscaledImage encoded;
        await using (ct.Register(() => page.Fail(new OperationCanceledException(ct))))
        {
            try
            {
                encoded = await page.Encoded;
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                // The tiles run again in the finish, beside the next page's: rare enough not to matter.
                var pending = await RetryWholeAsync(source, info, threads, model, run, ex, ct);
                return await pending.FinishAsync(ct);
            }
        }

        ct.ThrowIfCancellationRequested();
        return UpscaleResult.Ready(encoded);
    }

    private async Task<UpscaleResult> FinishAsync(
        Raster source, NativeStitchedPage stitched, bool grey, bool luma, int threads, bool jpegXl, UpscaleModelId model,
        UpscaleRun run, CancellationToken ct)
    {
        try
        {
            if (run.NarrowNow)
            {
                threads = UpscaleRun.NarrowWorkers(threads);
            }

            // The slot a streamed page holds from its first strip to its last, taken before the colour pass so the next
            // page's first strip never gets in first: its encode then starts after this one, as when every encode ran
            // in its page's finish, and its tiles carry on meanwhile.
            await _encoderSlot.WaitAsync(ct);
            try
            {
                var outputWeight = ImageWorkGate.Weigh(stitched.Width, stitched.Height);
                if (luma)
                {
                    return UpscaleResult.Ready(await ImageWorkGate.RunAsync(
                        () => Task.FromResult(ImageCodec.EncodeUpscaledGrey(stitched.Buffer, stitched.ByteLength, stitched.Width, stitched.Height)),
                        outputWeight, ct));
                }

                UpscalePixels.TakeColourFrom(stitched.Buffer, stitched.ByteLength, stitched.Width, stitched.Height, source.Pixels,
                    source.Width, source.Height, threads);
                jpegXl = jpegXl && (run.JpegXl?.Invoke() ?? true);
                var encoded = await ImageWorkGate.RunAsync(
                    () => Task.FromResult(ImageCodec.EncodeUpscaled(stitched.Buffer, stitched.ByteLength, stitched.Width, stitched.Height, grey, jpegXl)),
                    outputWeight, ct);
                return UpscaleResult.Ready(encoded);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogInformation(ex, "Encoding an upscaled page from {Model} failed", model);
                return UpscaleResult.Failed(UpscaleReasons.EncodeFailed);
            }
            finally
            {
                _encoderSlot.Release();
            }
        }
        finally
        {
            stitched.Dispose();
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
    internal StitchedPage Run(Raster source, UpscaleModelInfo info, int threads, bool luma, CancellationToken ct,
        UpscaleRun? run = null)
    {
        var scale = info.OutputScale;
        var width = source.Width * scale;
        var height = source.Height * scale;
        var bands = luma ? 1 : 3;
        if ((long)width * height * bands > Array.MaxLength)
        {
            throw new InvalidDataException("The upscaled page does not fit in one buffer.");
        }

        var page = new StitchedPage(new byte[width * height * bands], width, height, bands);
        RunInto(page, bands, source, info, threads, run ?? UpscaleRun.Default, ct);
        return page;
    }

    private NativeStitchedPage RunNative(Raster source, UpscaleModelInfo info, int threads, bool luma, CancellationToken ct,
        UpscaleRun run)
    {
        var scale = info.OutputScale;
        var width = source.Width * scale;
        var height = source.Height * scale;
        var bands = luma ? 1 : 3;
        var byteLength = (long)width * height * bands;
        if (byteLength > Array.MaxLength)
        {
            throw new InvalidDataException("The upscaled page does not fit in one buffer.");
        }

        var page = new NativeStitchedPage(width, height, bands);
        try
        {
            RunInto(page, bands, source, info, threads, run, ct);
            return page;
        }
        catch
        {
            page.Dispose();
            throw;
        }
    }

    private void RunInto(
        IStitchTarget output, int bands, Raster source, UpscaleModelInfo info, int threads, UpscaleRun run, CancellationToken ct)
    {
        var loaded = Acquire(info);
        try
        {
            RunTiles(loaded, source, info, threads, output, bands, run, ct);
        }
        finally
        {
            Release(loaded);
        }
    }

    private void RunTiles(
        LoadedModel loaded, Raster source, UpscaleModelInfo info, int threads, IStitchTarget output, int bands, UpscaleRun run,
        CancellationToken ct)
    {
        var tiles = UpscaleTiling.Plan(source.Width, source.Height,
            multiple: info.Adapter == UpscaleAdapter.Plain4xThenHalve ? 1 : 4);
        var scale = info.OutputScale;
        output.Planned(tiles);
        var flats = new byte?[tiles.Count];
        var inferred = 0;
        for (var i = 0; i < tiles.Count; i++)
        {
            if (FlatValue(source, tiles[i]) is { } flat && KeepsFlat(info, flat))
            {
                flats[i] = flat;
            }
            else
            {
                inferred++;
            }
        }

        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        // A streamed page holds tiles back until its encoder catches up, and must let them go when they stop.
        using var stopped = stop.Token.Register(output.Stopped);

        // Flat tiles are filled in tile order with the rest, not all first, so the page is stitched from the top.
        bool Fill(int i)
        {
            if (flats[i] is not { } flat)
            {
                return false;
            }

            FillTile(tiles[i], scale, output, bands, flat);
            output.Stitched(tiles[i]);
            Report(TileSkipped, i);
            Report(TileCompleted, i);
            return true;
        }

        var full = Math.Min(Math.Min(threads, MaxWorkers(tiles[0], info)), inferred);
        if (full == 0)
        {
            for (var i = 0; i < tiles.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                Fill(i);
            }

            return;
        }

        var workers = run.NarrowNow ? Math.Min(full, UpscaleRun.NarrowWorkers(threads)) : full;
        var next = -1;
        Exception? failure = null;

        void Work()
        {
            try
            {
                Report(WorkerStarted, 0);
                using var buffers = new TileBuffers(loaded, tiles, info.ModelScale, scale);
                using var runOptions = new RunOptions();
                using var terminate = stop.Token.Register(() => runOptions.Terminate = true);
                while (!stop.IsCancellationRequested)
                {
                    var n = Interlocked.Increment(ref next);
                    if (n >= tiles.Count)
                    {
                        return;
                    }

                    if (Fill(n))
                    {
                        continue;
                    }

                    RunTile(loaded, buffers, runOptions, source, info, tiles[n], output, bands, stop.Token);
                    output.Stitched(tiles[n]);
                    Report(TileCompleted, n);
                }
            }
            catch (Exception ex)
            {
                Interlocked.CompareExchange(ref failure, ex, null);
                stop.Cancel();
            }
        }

        // The calling thread is the first worker. A narrow page gets the rest once a reader waits on it.
        var helpers = new List<Task>(full - 1);
        void StartHelpers(int upTo)
        {
            lock (helpers)
            {
                while (helpers.Count < upTo - 1)
                {
                    helpers.Add(Task.Factory.StartNew(Work, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default));
                }
            }
        }

        StartHelpers(workers);
        var widen = workers < full ? run.Widen.Register(() => StartHelpers(full)) : default;
        Work();
        widen.Dispose();
        Task[] started;
        lock (helpers)
        {
            started = [.. helpers];
        }

        Task.WaitAll(started);
        ct.ThrowIfCancellationRequested();
        if (failure is not null)
        {
            ExceptionDispatchInfo.Throw(failure);
        }
    }

    /// <summary>How many tiles of this shape may run at once inside <see cref="WorkerBudgetBytes"/>.</summary>
    internal static int MaxWorkers(UpscaleTile tile, UpscaleModelInfo info) =>
        (int)Math.Max(1, WorkerBudgetBytes / ((long)tile.ContextWidth * tile.ContextHeight * WorkerBytesPerPixel(info)));

    private static void RunTile(
        LoadedModel loaded, TileBuffers buffers, RunOptions runOptions, Raster source, UpscaleModelInfo info,
        UpscaleTile tile, IStitchTarget output, int bands, CancellationToken ct)
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
        var crop = loaded.ContextCrop;
        if (info.ModelScale != scale)
        {
            var halved = buffers.Resized;
            (tileWidth, tileHeight) = ImageWorkGate.RunAsync(
                () => Task.FromResult(ImageCodec.ResizeRgb(rgb, tileWidth, tileHeight, (double)scale / info.ModelScale, halved)),
                ct).GetAwaiter().GetResult();
            if ((tileWidth, tileHeight) != ((tile.ContextWidth - 2 * crop) * scale, (tile.ContextHeight - 2 * crop) * scale))
            {
                throw new InvalidDataException($"Resizing a tile gave {tileWidth}x{tileHeight}.");
            }

            rgb = halved;
        }

        StitchTile(rgb, tileWidth, tile, crop, scale, output, bands);
        SnapFlat(source, info, tile, scale, output, bands);
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
        var bands = source.Bands;
        var first = tile.SourceY(0, source.Height) * source.Stride + tile.SourceX(0, source.Width) * bands;
        var value = pixels[first];
        if (bands == 1)
        {
            for (var y = 0; y < tile.ContextHeight; y++)
            {
                var row = tile.SourceY(y, source.Height) * source.Stride;
                for (var x = 0; x < tile.ContextWidth; x++)
                {
                    if (pixels[row + tile.SourceX(x, source.Width)] != value)
                    {
                        return null;
                    }
                }
            }

            return value;
        }

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
    // further than the 12 px pad), so filling is closer to the model than running it is. Maki 2x HQ drifts
    // like Real-ESRGAN instead, up to 6 levels across the whole tile on light greys, so only 0 and 255 stay exact.
    private static bool KeepsFlat(UpscaleModelInfo info, byte value) =>
        info.Id == UpscaleModelId.SpanS || info.Id == UpscaleModelId.Maki2x || value is 0 or 255;

    /// <summary>
    /// Sets each output pixel whose source pixel and its eight neighbours are one grey value the model
    /// keeps flat to exactly that value, the same value <see cref="FillTile"/> gives a whole flat tile.
    /// Maki 2x answers flats about a level off (black comes back with green at 1), so on an OLED screen
    /// a black panel showed every inferred tile as a lit box beside the filled ones.
    /// </summary>
    internal static void SnapFlat(
        Raster source, UpscaleModelInfo info, UpscaleTile tile, int scale, IStitchTarget output, int bands)
    {
        for (var y = tile.Y; y < tile.Y + tile.Height; y++)
        {
            for (var x = tile.X; x < tile.X + tile.Width; x++)
            {
                if (FlatAround(source, x, y) is not { } value || !KeepsFlat(info, value))
                {
                    continue;
                }

                for (var dy = 0; dy < scale; dy++)
                {
                    output.Row(y * scale + dy).Slice(x * scale * bands, scale * bands).Fill(value);
                }
            }
        }
    }

    private static byte? FlatAround(Raster source, int x, int y)
    {
        var pixels = source.Pixels;
        var bands = source.Bands;
        var value = pixels[y * source.Stride + x * bands];
        var bottom = Math.Min(source.Height - 1, y + 1);
        var right = Math.Min(source.Width - 1, x + 1);
        if (bands == 1)
        {
            for (var ny = Math.Max(0, y - 1); ny <= bottom; ny++)
            {
                var row = ny * source.Stride;
                for (var nx = Math.Max(0, x - 1); nx <= right; nx++)
                {
                    if (pixels[row + nx] != value)
                    {
                        return null;
                    }
                }
            }

            return value;
        }

        for (var ny = Math.Max(0, y - 1); ny <= bottom; ny++)
        {
            var row = ny * source.Stride;
            for (var nx = Math.Max(0, x - 1); nx <= right; nx++)
            {
                var p = row + nx * 3;
                if (pixels[p] != value || pixels[p + 1] != value || pixels[p + 2] != value)
                {
                    return null;
                }
            }
        }

        return value;
    }

    private static void FillTile(UpscaleTile tile, int scale, IStitchTarget output, int bands, byte value)
    {
        var rowBytes = tile.Width * scale * bands;
        for (var y = 0; y < tile.Height * scale; y++)
        {
            output.Row(tile.Y * scale + y).Slice(tile.X * scale * bands, rowBytes).Fill(value);
        }
    }

    /// <summary>
    /// Copies the tile's interior into the page, past the <c>(Pad - crop) × scale</c> px of context
    /// the model's output still carries: as RGB, or as its luma when the page has one band.
    /// </summary>
    private static void StitchTile(
        byte[] rgb, int rgbWidth, UpscaleTile tile, int crop, int scale, IStitchTarget output, int bands)
    {
        var offset = (tile.Pad - crop) * scale;
        var rowPixels = tile.Width * scale;
        for (var y = 0; y < tile.Height * scale; y++)
        {
            var from = ((offset + y) * rgbWidth + offset) * 3;
            var to = output.Row(tile.Y * scale + y).Slice(tile.X * scale * bands, rowPixels * bands);
            if (bands == 3)
            {
                rgb.AsSpan(from, rowPixels * 3).CopyTo(to);
                continue;
            }

            for (var x = 0; x < rowPixels; x++, from += 3)
            {
                to[x] = (byte)UpscalePixels.Luma(rgb[from], rgb[from + 1], rgb[from + 2]);
            }
        }
    }

    private static readonly float[] FloatLevels = Enumerable.Range(0, 256).Select(v => v / 255f).ToArray();
    private static readonly Half[] HalfLevels = FloatLevels.Select(v => (Half)v).ToArray();

    /// <summary>
    /// One page's tensors and scratch. The input and output tensors live in pinned arrays sized to the
    /// largest context tile, with one <see cref="OrtValue"/> pair over them per tile shape, so ORT
    /// writes each result in place instead of allocating it. The output is the context tile less the
    /// model's <see cref="LoadedModel.ContextCrop"/> per side.
    /// </summary>
    private sealed class TileBuffers : IDisposable
    {
        private readonly LoadedModel _model;
        private readonly int _modelScale;
        private readonly int _crop;
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
            _crop = model.ContextCrop;
            var maxWidth = tiles.Max(t => t.ContextWidth);
            var maxHeight = tiles.Max(t => t.ContextHeight);
            var input = maxWidth * maxHeight * 3;
            var result = (maxWidth - 2 * _crop) * (maxHeight - 2 * _crop) * 3 * modelScale * modelScale;
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
            var bands = source.Bands;
            for (var x = 0; x < w; x++)
            {
                _columns[x] = tile.SourceX(x, source.Width) * bands;
            }

            var pixels = source.Pixels;
            if (_halfInput is not null)
            {
                var halves = MemoryMarshal.Cast<Float16, Half>(_halfInput.AsSpan(0, plane * 3));
                if (bands == 1)
                {
                    for (var y = 0; y < h; y++)
                    {
                        var row = tile.SourceY(y, source.Height) * source.Stride;
                        var o = y * w;
                        for (var x = 0; x < w; x++)
                        {
                            var v = HalfLevels[pixels[row + _columns[x]]];
                            halves[o + x] = v;
                            halves[plane + o + x] = v;
                            halves[2 * plane + o + x] = v;
                        }
                    }

                    return;
                }

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
            if (bands == 1)
            {
                for (var y = 0; y < h; y++)
                {
                    var row = tile.SourceY(y, source.Height) * source.Stride;
                    var o = y * w;
                    for (var x = 0; x < w; x++)
                    {
                        var v = FloatLevels[pixels[row + _columns[x]]];
                        floats[o + x] = v;
                        floats[plane + o + x] = v;
                        floats[2 * plane + o + x] = v;
                    }
                }

                return;
            }

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
                var (resultWidth, resultHeight) = ResultSize(tile);
                var resultLength = resultWidth * resultHeight * 3;
                long[] inputShape = [1, 3, tile.ContextHeight, tile.ContextWidth];
                long[] outputShape = [1, 3, resultHeight, resultWidth];
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

        private (int Width, int Height) ResultSize(UpscaleTile tile) =>
            ((tile.ContextWidth - 2 * _crop) * _modelScale, (tile.ContextHeight - 2 * _crop) * _modelScale);

        /// <summary>What the model returned for the tile, as interleaved 8-bit RGB at the model's scale.</summary>
        public (byte[] Rgb, int Width, int Height) Read(UpscaleTile tile)
        {
            var (width, height) = ResultSize(tile);
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
            var crop = ContextCrop(session);
            logger.LogInformation("Loaded upscale model {Model} ({Type})", info.Id, inputType);
            return new LoadedModel(session, info.InputName, output, inputType, outputType, crop);
        }
        catch (Exception ex)
        {
            session?.Dispose();
            throw new ModelLoadException(ex);
        }
    }

    private static int ContextCrop(InferenceSession session)
    {
        if (!session.ModelMetadata.CustomMetadataMap.TryGetValue(ContextCropKey, out var text))
        {
            return 0;
        }

        if (!int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var crop))
        {
            throw new InvalidDataException($"The model's {ContextCropKey} '{text}' is not a number.");
        }

        return crop <= UpscaleTiling.ContextPad
            ? crop
            : throw new InvalidDataException(
                $"The model crops {crop} px of context per side but tiles carry {UpscaleTiling.ContextPad}.");
    }

    private static TensorElementType ElementType(NodeMetadata metadata) =>
        metadata.ElementDataType is TensorElementType.Float or TensorElementType.Float16
            ? metadata.ElementDataType
            : throw new InvalidDataException($"Unsupported tensor type {metadata.ElementDataType}.");

    /// <summary>Where a run's tiles go, a row at a time, and when each tile is in.</summary>
    internal interface IStitchTarget
    {
        /// <summary>Called once, before any tile is stitched.</summary>
        void Planned(IReadOnlyList<UpscaleTile> tiles);

        /// <summary>Output row <paramref name="y"/>, the whole width.</summary>
        Span<byte> Row(int y);

        /// <summary>Every row of the tile's interior is written. Called from the tile workers.</summary>
        void Stitched(UpscaleTile tile);

        /// <summary>The tiles stopped early: cancelled, or one failed. No more will be stitched.</summary>
        void Stopped();
    }

    /// <param name="Bands">3 for interleaved RGB, 1 for luma.</param>
    internal sealed record StitchedPage(byte[] Pixels, int Width, int Height, int Bands) : IStitchTarget
    {
        public void Planned(IReadOnlyList<UpscaleTile> tiles)
        {
        }

        public Span<byte> Row(int y) => Pixels.AsSpan(y * Width * Bands, Width * Bands);

        public void Stitched(UpscaleTile tile)
        {
        }

        public void Stopped()
        {
        }
    }

    private sealed unsafe class NativeStitchedPage : IStitchTarget, IDisposable
    {
        private IntPtr _buffer;

        public NativeStitchedPage(int width, int height, int bands)
        {
            Width = width;
            Height = height;
            Bands = bands;
            ByteLength = (long)width * height * bands;
            _buffer = Marshal.AllocHGlobal((nint)ByteLength);
        }

        public IntPtr Buffer => _buffer;
        public long ByteLength { get; }
        public int Width { get; }
        public int Height { get; }
        public int Bands { get; }

        public void Planned(IReadOnlyList<UpscaleTile> tiles)
        {
        }

        public Span<byte> Row(int y) => new((byte*)_buffer + (long)y * Width * Bands, Width * Bands);

        public void Stitched(UpscaleTile tile)
        {
        }

        public void Stopped()
        {
        }

        public void Dispose()
        {
            var ptr = Interlocked.Exchange(ref _buffer, IntPtr.Zero);
            if (ptr != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(ptr);
            }

            GC.SuppressFinalize(this);
        }

        ~NativeStitchedPage()
        {
            var ptr = Interlocked.Exchange(ref _buffer, IntPtr.Zero);
            if (ptr != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(ptr);
            }
        }
    }

    private sealed class ModelLoadException(Exception inner) : Exception(inner.Message, inner);

    private sealed class LoadedModel(
        InferenceSession session, string inputName, string outputName, TensorElementType inputType,
        TensorElementType outputType, int contextCrop)
    {
        public InferenceSession Session { get; } = session;
        public int ContextCrop { get; } = contextCrop;
        public string InputName { get; } = inputName;
        public string OutputName { get; } = outputName;
        public TensorElementType InputType { get; } = inputType;
        public TensorElementType OutputType { get; } = outputType;
        public int InUse { get; set; }
        public DateTime LastUsedUtc { get; set; } = DateTime.UtcNow;
        public bool Retired { get; set; }
    }
}
