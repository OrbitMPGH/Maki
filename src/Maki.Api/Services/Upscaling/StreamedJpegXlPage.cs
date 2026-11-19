using System.Runtime.InteropServices;
using Maki.Core.Images;
using Maki.Core.Images.Upscaling;

namespace Maki.Api.Services.Upscaling;

/// <summary>
/// A colour page encoded to JPEG XL while its tiles are still running, so neither Maki nor libjxl ever holds
/// the whole upscaled page. Tiles are stitched into chunks of <see cref="StripRows"/> output rows; an encoder
/// thread waits until a strip and the rows the colour pass reaches below it are stitched, colours them,
/// lends the strip to libjxl and frees the chunks above it once libjxl moves on.
///
/// <para>
/// libjxl 0.12 asks for its 2048 px DC groups in reading order, each with <see cref="Border"/> rows of the
/// groups above and below, so chunk <c>k</c> holds rows <c>k * StripRows - Border</c> to
/// <c>(k + 1) * StripRows + Border</c> and every request is one pointer into one chunk. A row in two chunks is
/// stitched and coloured in the chunk that owns it (<c>y / StripRows</c>) and copied to the other once
/// coloured. Chunks are native memory, freed the moment they are done rather than at the next collection.
/// </para>
///
/// <para>
/// Tiles may not start a chunk more than <see cref="Ahead"/> past the strip libjxl is on, so flat tiles, which
/// fill at once, never allocate the whole page ahead of the encoder. The bound lifts while the encoder waits
/// for its turn (the slot or the gate): then the page is held as it would be without streaming, and the tiles
/// never wait on another page's encode.
/// </para>
///
/// <para>
/// The encoder takes <see cref="ImageWorkGate"/> while libjxl works on a strip and gives it back while it
/// waits for the next one, and one slot per upscaler, so a page finishing its last strip and the next page
/// starting its first never hold two encoders' memory (about 110 to 150 MB each) at once.
/// </para>
/// </summary>
internal sealed class StreamedJpegXlPage : OnnxPageUpscaler.IStitchTarget, IJpegXlRows, IDisposable
{
    internal const int StripRows = 2048;
    internal const int Border = 8;

    /// <summary>
    /// Chunks past libjxl's current strip the tiles may start. Two, because the strip after the current one
    /// needs rows of the chunk below it before libjxl asks for it.
    /// </summary>
    internal const int Ahead = 2;

    private readonly object _lock = new();
    private readonly int _width;
    private readonly int _height;
    private readonly int _scale;
    private readonly int _rowBytes;
    private readonly IntPtr[] _chunks;
    private readonly bool[] _freed;
    private readonly int[] _lent;
    private readonly UpscaleColourRows _colour;
    private readonly SemaphoreSlim _encoderSlot;
    private readonly Func<int> _threads;
    private readonly Func<int, CancellationToken, Task<IDisposable>> _holdGate;
    private readonly int _gateWeight;
    private readonly CancellationTokenSource _stop = new();
    private readonly UpscaleColourRows.OutputRow _ownRow;
    private readonly Dictionary<IntPtr, IntPtr> _copies = [];
    private int[] _tileRowTops = [];
    private int[] _remaining = [];
    private int _nextTileRow;
    private int _stitched;
    private int _coloured;
    private int _requested;
    private bool _queued;
    private Exception? _failure;
    private IDisposable? _gate;
    private bool _holdsSlot;
    private Task<UpscaledImage>? _encoded;
    private bool _disposed;

    public StreamedJpegXlPage(Raster source, int scale, SemaphoreSlim encoderSlot, Func<int> threads,
        Func<int, CancellationToken, Task<IDisposable>> holdGate)
    {
        _scale = scale;
        _width = source.Width * scale;
        _height = source.Height * scale;
        _rowBytes = _width * 3;
        var chunks = (_height + StripRows - 1) / StripRows;
        _chunks = new IntPtr[chunks];
        _freed = new bool[chunks];
        _lent = new int[chunks];
        _colour = new UpscaleColourRows(source.Pixels, source.Width, source.Height, scale);
        _encoderSlot = encoderSlot;
        _threads = threads;
        _holdGate = holdGate;
        _gateWeight = ImageWorkGate.Weigh(_width, _height);
        _ownRow = Row;
    }

    public int Width => _width;

    public int Height => _height;

    /// <summary>Most chunks allocated at once. Tests only.</summary>
    internal int MostChunksHeld { get; private set; }

    /// <summary>Called on the encoder thread with each strip libjxl asks for, once its rows are coloured. Tests only.</summary>
    internal Action<int>? StripReady { get; set; }

    /// <summary>The encode, started by <see cref="StartEncode"/>.</summary>
    public Task<UpscaledImage> Encoded => _encoded ?? throw new InvalidOperationException("The encode has not started.");

    public void StartEncode() => _encoded = Task.Factory.StartNew(() =>
    {
        try
        {
            return ImageCodec.EncodeUpscaledJpegXl(this, _width, _height);
        }
        catch (Exception ex)
        {
            // Stops the tiles too: nothing would free the chunks they go on writing.
            Fail(ex);
            throw;
        }
        finally
        {
            ReleaseGate();
            if (_holdsSlot)
            {
                _encoderSlot.Release();
                _holdsSlot = false;
            }
        }
    }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);

    /// <summary>Stops the encode and the tiles: the tiles failed or were cancelled, libjxl failed, or the worker is shutting down.</summary>
    public void Fail(Exception ex)
    {
        lock (_lock)
        {
            _failure ??= ex;
            Monitor.PulseAll(_lock);
        }

        try
        {
            _stop.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The encode ended and the page was disposed; there is nothing left to stop.
        }
    }

    public void Planned(IReadOnlyList<UpscaleTile> tiles)
    {
        var tops = tiles.Select(tile => tile.Y).Distinct().Order().ToArray();
        lock (_lock)
        {
            _tileRowTops = [.. tops, tiles.Max(tile => tile.Y + tile.Height)];
            _remaining = tops.Select(top => tiles.Count(tile => tile.Y == top)).ToArray();
        }
    }

    public void Stitched(UpscaleTile tile)
    {
        lock (_lock)
        {
            _remaining[Array.BinarySearch(_tileRowTops, 0, _remaining.Length, tile.Y)]--;
            var advanced = false;
            while (_nextTileRow < _remaining.Length && _remaining[_nextTileRow] == 0)
            {
                _nextTileRow++;
                advanced = true;
            }

            if (advanced)
            {
                _stitched = _tileRowTops[_nextTileRow] * _scale;
                Monitor.PulseAll(_lock);
            }
        }
    }

    public void Stopped() => Fail(new OperationCanceledException("The page's tiles stopped."));

    /// <summary>Output row <paramref name="y"/> in the chunk that owns it.</summary>
    public Span<byte> Row(int y) => RowIn(y / StripRows, y);

    public IntPtr Acquire(int top, int count, out int stride)
    {
        var chunk = Math.Min(_chunks.Length - 1, (top + Border) / StripRows);
        lock (_lock)
        {
            // A page that already failed keeps its gate until the encode ends, so libjxl's last blank rows are still counted.
            ThrowIfFailed();
            _requested = Math.Max(_requested, chunk);
            Monitor.PulseAll(_lock);
        }

        ReleaseGate();
        stride = _rowBytes;
        var end = top + count;
        if (end > _coloured)
        {
            ColourTo(end);
        }

        StripReady?.Invoke(chunk);
        IntPtr rows;
        lock (_lock)
        {
            ThrowIfFailed();
            if (top >= ChunkTop(chunk) && end <= ChunkEnd(chunk) && !_freed[chunk])
            {
                for (var done = 0; done < chunk; done++)
                {
                    if (_lent[done] == 0)
                    {
                        FreeChunk(done);
                    }
                }

                _lent[chunk]++;
                rows = Chunk(chunk) + (top - ChunkTop(chunk)) * _rowBytes;
            }
            else
            {
                rows = Copy(top, count);
            }
        }

        try
        {
            if (!_holdsSlot)
            {
                if (!_encoderSlot.Wait(0))
                {
                    Queued();
                    _encoderSlot.Wait(_stop.Token);
                }

                _holdsSlot = true;
            }

            var gate = _holdGate(_gateWeight, _stop.Token);
            if (!gate.IsCompleted)
            {
                Queued();
            }

            _gate = gate.GetAwaiter().GetResult();
        }
        finally
        {
            lock (_lock)
            {
                _queued = false;
            }
        }

        return rows;
    }

    /// <summary>The encoder waits for its turn, so the tiles need not wait for it.</summary>
    private void Queued()
    {
        lock (_lock)
        {
            _queued = true;
            Monitor.PulseAll(_lock);
        }
    }

    public void Release(IntPtr rows)
    {
        lock (_lock)
        {
            if (_copies.Remove(rows, out var copy))
            {
                Marshal.FreeHGlobal(copy);
                return;
            }

            for (var chunk = 0; chunk < _chunks.Length; chunk++)
            {
                if (_chunks[chunk] != IntPtr.Zero && rows >= _chunks[chunk] && rows < _chunks[chunk] + ChunkBytes(chunk))
                {
                    _lent[chunk]--;
                    return;
                }
            }
        }
    }

    /// <summary>Frees every chunk. Only once the tiles have stopped and the encode has ended.</summary>
    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            for (var chunk = 0; chunk < _chunks.Length; chunk++)
            {
                FreeChunk(chunk);
            }

            foreach (var copy in _copies.Values)
            {
                Marshal.FreeHGlobal(copy);
            }

            _copies.Clear();
        }

        _stop.Dispose();
    }

    /// <summary>Waits for the rows the colour pass needs, then colours every row above <paramref name="end"/>.</summary>
    private void ColourTo(int end)
    {
        var needed = _colour.RowsNeededFor(end);
        int stitched;
        lock (_lock)
        {
            while (_stitched < needed && _failure is null)
            {
                Monitor.Wait(_lock);
            }

            ThrowIfFailed();
            stitched = _stitched;
        }

        // Stitched rows are complete and nothing writes them again; tiles still running write only below.
        _colour.AddGuide(stitched, _ownRow);
        _colour.Colour(_coloured, end, _ownRow, _threads());
        for (var y = _coloured; y < end; y++)
        {
            var owner = y / StripRows;
            if (owner + 1 < _chunks.Length && y >= ChunkTop(owner + 1))
            {
                Row(y).CopyTo(RowIn(owner + 1, y));
            }
            else if (owner > 0 && y < ChunkEnd(owner - 1) && !_freed[owner - 1])
            {
                Row(y).CopyTo(RowIn(owner - 1, y));
            }
        }

        _coloured = end;
    }

    private unsafe Span<byte> RowIn(int chunk, int y) =>
        new((void*)(Chunk(chunk) + (y - ChunkTop(chunk)) * _rowBytes), _rowBytes);

    private IntPtr Chunk(int chunk)
    {
        var memory = Volatile.Read(ref _chunks[chunk]);
        if (memory != IntPtr.Zero)
        {
            return memory;
        }

        lock (_lock)
        {
            // The encoder's own chunks are never past the bound, so only tiles wait here.
            while (chunk > _requested + Ahead && !_queued && _failure is null && _chunks[chunk] == IntPtr.Zero)
            {
                Monitor.Wait(_lock);
            }

            ThrowIfFailed();
            if (_freed[chunk] || _disposed)
            {
                throw new InvalidOperationException($"Rows of strip {chunk} were asked for after it was encoded.");
            }

            if (_chunks[chunk] == IntPtr.Zero)
            {
                Volatile.Write(ref _chunks[chunk], Marshal.AllocHGlobal(ChunkBytes(chunk)));
                MostChunksHeld = Math.Max(MostChunksHeld, _chunks.Count(held => held != IntPtr.Zero));
            }

            return _chunks[chunk];
        }
    }

    private void FreeChunk(int chunk)
    {
        if (_chunks[chunk] != IntPtr.Zero)
        {
            Marshal.FreeHGlobal(_chunks[chunk]);
            Volatile.Write(ref _chunks[chunk], IntPtr.Zero);
        }

        _freed[chunk] = true;
    }

    /// <summary>A request that is not inside one chunk, copied from the chunks that own its rows. libjxl 0.12 never needs it.</summary>
    private unsafe IntPtr Copy(int top, int count)
    {
        var copy = Marshal.AllocHGlobal(count * _rowBytes);
        try
        {
            for (var y = top; y < top + count; y++)
            {
                Row(y).CopyTo(new Span<byte>((void*)(copy + (y - top) * _rowBytes), _rowBytes));
            }
        }
        catch
        {
            Marshal.FreeHGlobal(copy);
            throw;
        }

        _copies.Add(copy, copy);
        return copy;
    }

    private int ChunkTop(int chunk) => Math.Max(0, chunk * StripRows - Border);

    private int ChunkEnd(int chunk) => Math.Min(_height, (chunk + 1) * StripRows + Border);

    private int ChunkBytes(int chunk) => (ChunkEnd(chunk) - ChunkTop(chunk)) * _rowBytes;

    private void ReleaseGate()
    {
        _gate?.Dispose();
        _gate = null;
    }

    private void ThrowIfFailed()
    {
        if (_failure is not null)
        {
            throw _failure as OperationCanceledException ?? new OperationCanceledException("The page's tiles did not finish.", _failure);
        }
    }
}
