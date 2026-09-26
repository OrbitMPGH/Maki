using System.Collections.Concurrent;
using System.IO.Compression;
using Maki.Core.Reading;

namespace Maki.Api.Services;

/// <summary>
/// Keeps the parsed zip directory of recently read archives, so paging through a chapter reads
/// one entry per request instead of reopening and re-parsing the whole archive.
/// <para>
/// No file handle survives a request. Each read opens the file, checks its length and write time
/// against the cached directory (rebuilding it on any difference), reads the one entry and closes
/// the file again. Keeping the handle open would only save that open, and on Windows it blocks a
/// <c>File.Move</c> over the archive (re-download, ComicInfo rewrite) and any rename of its series
/// folder, even with <see cref="FileShare.Delete"/>.
/// </para>
/// <para>
/// <see cref="ZipArchive"/> is not thread-safe, so reads of one archive are serialized and the
/// page is copied into memory before the lock is released.
/// </para>
/// </summary>
public sealed class ReaderArchiveHandles(TimeProvider? time = null)
{
    public const int Capacity = 8;
    public static readonly TimeSpan IdleTimeout = TimeSpan.FromMinutes(2);

    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly ConcurrentDictionary<string, Handle> _handles = new(StringComparer.Ordinal);
    private int _parses;

    /// <summary>How many times a zip directory has been parsed. For tests.</summary>
    internal int Parses => Volatile.Read(ref _parses);

    internal int Count => _handles.Count;

    /// <summary>One page copied into memory, or null when the entry is missing or unreadable.</summary>
    public Task<MemoryStream?> ReadPageAsync(string path, string entryName, CancellationToken ct) =>
        WithArchiveAsync(path, archive => CbzReader.ReadEntry(archive, entryName), ct);

    /// <summary>Page names in reading order, or null when the file is not a readable zip.</summary>
    public Task<List<string>?> PageNamesAsync(string path, CancellationToken ct) =>
        WithArchiveAsync(path, CbzReader.PageNames, ct);

    public void Invalidate(string path) => _handles.TryRemove(path, out _);

    private async Task<T?> WithArchiveAsync<T>(string path, Func<ZipArchive, T?> read, CancellationToken ct)
        where T : class
    {
        var handle = _handles.GetOrAdd(path, _ => new Handle());
        handle.LastUsed = _time.GetTimestamp();
        Sweep();

        await handle.Gate.WaitAsync(ct);
        try
        {
            var archive = handle.Attach(path, this);
            return archive is null ? null : read(archive);
        }
        finally
        {
            handle.Park();
            handle.Gate.Release();
        }
    }

    /// <summary>
    /// Drops idle entries, then the least recently used ones over capacity. An entry dropped while
    /// a request is using it is harmless: that request parks it on the way out and nothing refers
    /// to it afterwards. It holds no file handle while parked, so there is nothing to dispose.
    /// </summary>
    private void Sweep()
    {
        foreach (var (path, handle) in _handles)
        {
            if (_time.GetElapsedTime(handle.LastUsed) > IdleTimeout)
            {
                _handles.TryRemove(new KeyValuePair<string, Handle>(path, handle));
            }
        }

        var excess = _handles.Count - Capacity;
        if (excess <= 0) return;

        foreach (var entry in _handles.OrderBy(kv => kv.Value.LastUsed).Take(excess).ToList())
        {
            _handles.TryRemove(entry);
        }
    }

    private sealed class Handle
    {
        public readonly SemaphoreSlim Gate = new(1, 1);
        public long LastUsed;

        private ZipArchive? _archive;
        private ParkedFileStream? _stream;
        private long _length;
        private DateTime _written;

        /// <summary>
        /// Opens the file and returns the archive over it, re-parsed when the file is not the one
        /// the cached directory came from. Null when it is not a readable zip. The length and
        /// write time are read off the opened handle, so a replace racing this call cannot pair
        /// the old directory with the new bytes.
        /// </summary>
        public ZipArchive? Attach(string path, ReaderArchiveHandles owner)
        {
            var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            try
            {
                var length = RandomAccess.GetLength(file.SafeFileHandle);
                var written = File.GetLastWriteTimeUtc(file.SafeFileHandle);
                if (_archive is not null && _stream is not null && length == _length && written == _written)
                {
                    _stream.Attach(file);
                    return _archive;
                }

                _archive = null;
                _stream = null;
                var stream = new ParkedFileStream(file, length);
                Interlocked.Increment(ref owner._parses);
                ZipArchive archive;
                try
                {
                    archive = new ZipArchive(stream, ZipArchiveMode.Read);
                }
                catch (InvalidDataException)
                {
                    // A corrupt central directory, same "not readable" outcome as a bad entry.
                    stream.Park();
                    return null;
                }

                (_archive, _stream, _length, _written) = (archive, stream, length, written);
                return archive;
            }
            catch
            {
                file.Dispose();
                throw;
            }
        }

        public void Park() => _stream?.Park();
    }

    /// <summary>
    /// A seekable read-only view of a file whose handle can be closed between uses and swapped
    /// for a freshly opened one, so a <see cref="ZipArchive"/> can outlive the handle it was
    /// parsed from.
    /// </summary>
    private sealed class ParkedFileStream(FileStream file, long length) : Stream
    {
        private FileStream? _file = file;
        private long _position;

        public void Attach(FileStream file) => _file = file;

        public void Park()
        {
            _file?.Dispose();
            _file = null;
        }

        private FileStream Open => _file ?? throw new InvalidOperationException("Archive read while parked");

        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => length;

        public override long Position
        {
            get => _position;
            set => _position = value;
        }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            var open = Open;
            open.Position = _position;
            var read = open.Read(buffer);
            _position += read;
            return read;
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            var target = origin switch
            {
                SeekOrigin.Begin => offset,
                SeekOrigin.Current => _position + offset,
                SeekOrigin.End => length + offset,
                _ => throw new ArgumentOutOfRangeException(nameof(origin))
            };

            // Same as FileStream, which ZipArchive relies on to reject a file too short to be a zip.
            if (target < 0) throw new IOException("An attempt was made to move the position before the beginning of the stream.");
            return _position = target;
        }

        public override void Flush() { }
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing) Park();
            base.Dispose(disposing);
        }
    }
}
