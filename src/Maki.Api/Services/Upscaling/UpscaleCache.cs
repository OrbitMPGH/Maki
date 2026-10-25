using System.Globalization;
using System.Text.RegularExpressions;
using Maki.Api.Configuration;
using Maki.Core.Configuration;

namespace Maki.Api.Services.Upscaling;

/// <summary>
/// Upscaled pages on disk at <c>{ConfigDir}/cache/upscale/{ChapterFileId}/{ArchiveVersion}-{absIndex}.{model}.{webp|jpg}</c>,
/// a root of its own so the reader cache prune never sees them. Eviction is by last access as this
/// index records it (filesystem atime is not updated on read on NTFS or under relatime), seeded from
/// each file's write time by a walk off the request path.
/// </summary>
public sealed partial class UpscaleCache(AppPaths paths, IAppSettings settings, ILogger<UpscaleCache> logger)
{
    private static readonly TimeSpan TmpGrace = TimeSpan.FromHours(1);

    private readonly Lock _gate = new();
    private readonly Dictionary<string, Entry> _index = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _trim = new(1, 1);
    private long _bytes;
    private Task? _seeding;

    /// <summary>
    /// Part of every page's name, raised whenever the pipeline's output changes so pages made before
    /// it stop being served and are pruned as unknown names. Change the regex with it. g2: colour comes
    /// from the source page, so grey pages carry no model tint.
    /// </summary>
    internal const string Generation = "g2";

    [GeneratedRegex(@"^(\d+)-(\d+)-(\d+)\.([a-z0-9-]+)\.g2\.(webp|jpg)$")]
    internal static partial Regex FileName();

    public string Root { get; } = Path.GetFullPath(Path.Combine(paths.CacheDir, "upscale"));

    /// <summary>Replaces the configured cap. Tests only, since the setting cannot go below 128 MB.</summary>
    internal long? CapBytesOverride { get; set; }

    public string PathFor(UpscaleWorkKey key, string extension) => Path.Combine(
        Root, key.ChapterFileId.ToString(CultureInfo.InvariantCulture),
        $"{key.ArchiveVersion}-{key.AbsIndex.ToString(CultureInfo.InvariantCulture)}.{key.Model}.{Generation}.{extension}");

    public static string ContentType(string path) =>
        path.EndsWith(".webp", StringComparison.OrdinalIgnoreCase) ? "image/webp" : "image/jpeg";

    /// <summary>Starts the index walk once; later calls return the same task.</summary>
    public Task EnsureSeededAsync()
    {
        lock (_gate)
        {
            return _seeding ??= Task.Run(Seed);
        }
    }

    /// <summary>The cached file for <paramref name="key"/>, recording the access, or null.</summary>
    public string? TryGet(UpscaleWorkKey key)
    {
        foreach (var extension in (string[])["webp", "jpg"])
        {
            var path = PathFor(key, extension);
            if (File.Exists(path))
            {
                Touch(path);
                return path;
            }

            Forget(path);
        }

        return null;
    }

    /// <summary>Writes the page aside and moves it into place, then evicts down to the cap.</summary>
    public async Task<string> WriteAsync(UpscaleWorkKey key, byte[] bytes, string extension, CancellationToken ct)
    {
        if (extension is not ("webp" or "jpg"))
        {
            throw new ArgumentOutOfRangeException(nameof(extension), extension, "webp or jpg");
        }

        await EnsureSeededAsync();
        var path = PathFor(key, extension);
        var dir = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(dir);
        var tmp = Path.Combine(dir, $"{Guid.NewGuid():N}.tmp");
        try
        {
            await File.WriteAllBytesAsync(tmp, bytes, ct);
            try
            {
                File.Move(tmp, path, overwrite: true);
            }
            catch (Exception moveError) when (moveError is IOException or UnauthorizedAccessException && File.Exists(path))
            {
                // The same page is being served from the old copy (Windows refuses to replace an open
                // file); a page upscaled twice is the same bytes, so keep that one.
                File.Delete(tmp);
            }
        }
        catch
        {
            TryDeleteFile(tmp);
            throw;
        }

        var length = new FileInfo(path).Length;
        lock (_gate)
        {
            if (_index.Remove(path, out var previous))
            {
                _bytes -= previous.Bytes;
            }

            _index[path] = new Entry(length, DateTime.UtcNow);
            _bytes += length;
        }

        await TrimAsync(path, ct);
        return path;
    }

    /// <summary>Evicts the least recently used pages until the cache fits its cap.</summary>
    public Task EnforceCapAsync(CancellationToken ct) => TrimAsync(null, ct);

    public async Task ClearAsync(CancellationToken ct)
    {
        await EnsureSeededAsync();
        DeleteWhere(_ => true, ct);
    }

    /// <summary>Deletes every page made with <paramref name="model"/>.</summary>
    public async Task RemoveModelAsync(string model, CancellationToken ct)
    {
        await EnsureSeededAsync();
        DeleteWhere(name => FileName().Match(name) is { Success: true } m && m.Groups[4].Value == model, ct);
    }

    public (long Bytes, int Files) Stats()
    {
        lock (_gate)
        {
            return (_bytes, _index.Count);
        }
    }

    /// <summary>
    /// The housekeeping pass. Drops folders whose chapter file is gone and, inside the rest, every
    /// page not of the file's current version (its size and the newest write stamp present), the
    /// same rule the reader cache prune applies. Then evicts down to the cap.
    /// </summary>
    public async Task PruneAsync(IReadOnlyDictionary<int, long> sizeByFileId, CancellationToken ct)
    {
        await EnsureSeededAsync();
        if (!Directory.Exists(Root))
        {
            return;
        }

        foreach (var dir in Directory.GetDirectories(Root))
        {
            if (ct.IsCancellationRequested)
            {
                return;
            }

            try
            {
                var name = Path.GetFileName(dir);
                if (!int.TryParse(name, NumberStyles.None, CultureInfo.InvariantCulture, out var fileId)
                    || !sizeByFileId.TryGetValue(fileId, out var size))
                {
                    foreach (var file in Directory.GetFiles(dir))
                    {
                        if (!IsFreshTmp(file))
                        {
                            DeleteAndForget(file);
                        }
                    }

                    if (!Directory.EnumerateFileSystemEntries(dir).Any())
                    {
                        Directory.Delete(dir);
                    }

                    continue;
                }

                PruneDir(dir, size.ToString(CultureInfo.InvariantCulture));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                logger.LogDebug(ex, "Could not clean upscale cache dir {Dir}", dir);
            }
        }

        await EnforceCapAsync(ct);
    }

    private void PruneDir(string dir, string currentSize)
    {
        var files = new List<(string Path, bool Sized, long Stamp)>();
        foreach (var file in Directory.GetFiles(dir))
        {
            var match = FileName().Match(Path.GetFileName(file));
            if (!match.Success)
            {
                if (IsFreshTmp(file))
                {
                    continue;
                }

                files.Add((file, false, -1));
                continue;
            }

            files.Add((file, match.Groups[1].Value == currentSize,
                long.TryParse(match.Groups[2].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var stamp) ? stamp : -1));
        }

        var newest = files.Where(f => f.Sized).Select(f => f.Stamp).DefaultIfEmpty(-1).Max();
        foreach (var (path, sized, stamp) in files)
        {
            if (!sized || stamp != newest)
            {
                DeleteAndForget(path);
            }
        }
    }

    /// <summary>A page a worker may still be writing; it is moved into place or cleaned up by its writer.</summary>
    private static bool IsFreshTmp(string file) =>
        file.EndsWith(".tmp", StringComparison.Ordinal) && File.GetLastWriteTimeUtc(file) > DateTime.UtcNow - TmpGrace;

    private async Task TrimAsync(string? keep, CancellationToken ct)
    {
        var cap = CapBytesOverride ?? (await UpscaleOptions.LoadAsync(settings, ct)).CacheMb * 1024L * 1024L;
        await _trim.WaitAsync(ct);
        try
        {
            List<string> victims;
            lock (_gate)
            {
                if (_bytes <= cap)
                {
                    return;
                }

                victims = _index.Where(e => e.Key != keep).OrderBy(e => e.Value.LastAccessUtc).Select(e => e.Key).ToList();
            }

            foreach (var path in victims)
            {
                lock (_gate)
                {
                    if (_bytes <= cap)
                    {
                        return;
                    }
                }

                DeleteAndForget(path);
            }
        }
        finally
        {
            _trim.Release();
        }
    }

    private void DeleteWhere(Func<string, bool> matches, CancellationToken ct)
    {
        if (!Directory.Exists(Root))
        {
            return;
        }

        foreach (var dir in Directory.GetDirectories(Root))
        {
            if (ct.IsCancellationRequested)
            {
                return;
            }

            try
            {
                foreach (var file in Directory.GetFiles(dir))
                {
                    if (!IsFreshTmp(file) && matches(Path.GetFileName(file)))
                    {
                        DeleteAndForget(file);
                    }
                }

                if (!Directory.EnumerateFileSystemEntries(dir).Any())
                {
                    Directory.Delete(dir);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                logger.LogDebug(ex, "Could not clean upscale cache dir {Dir}", dir);
            }
        }
    }

    private void Seed()
    {
        try
        {
            if (!Directory.Exists(Root))
            {
                return;
            }

            foreach (var file in new DirectoryInfo(Root).EnumerateFiles("*", SearchOption.AllDirectories))
            {
                if (!FileName().IsMatch(file.Name))
                {
                    continue;
                }

                lock (_gate)
                {
                    if (_index.TryAdd(file.FullName, new Entry(file.Length, file.LastWriteTimeUtc)))
                    {
                        _bytes += file.Length;
                    }
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogDebug(ex, "Could not read the upscale cache at {Root}", Root);
        }
    }

    private void Touch(string path)
    {
        lock (_gate)
        {
            if (_index.TryGetValue(path, out var entry))
            {
                _index[path] = entry with { LastAccessUtc = DateTime.UtcNow };
                return;
            }
        }

        try
        {
            var length = new FileInfo(path).Length;
            lock (_gate)
            {
                if (_index.TryAdd(path, new Entry(length, DateTime.UtcNow)))
                {
                    _bytes += length;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private void Forget(string path)
    {
        lock (_gate)
        {
            if (_index.Remove(path, out var entry))
            {
                _bytes -= entry.Bytes;
            }
        }
    }

    /// <summary>
    /// A file still streaming to a reader cannot be deleted on Windows; its entry stays so the next
    /// write tries again.
    /// </summary>
    private void DeleteAndForget(string path)
    {
        if (TryDeleteFile(path))
        {
            Forget(path);
        }
    }

    private bool TryDeleteFile(string path)
    {
        try
        {
            File.Delete(path);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogDebug(ex, "Could not delete {Path}", path);
            return false;
        }
    }

    private readonly record struct Entry(long Bytes, DateTime LastAccessUtc);
}
