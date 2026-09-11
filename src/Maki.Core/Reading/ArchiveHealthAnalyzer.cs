using System.Collections.Concurrent;
using System.IO.Compression;
using System.Security.Cryptography;
using SixLabors.ImageSharp;

namespace Maki.Core.Reading;

public record ArchiveProblem(string Kind, string Severity, string Message);
/// <param name="RawHash">SHA-256 of the entry's bytes, or null when the archive was only indexed.
/// Page previews are served against it, so a preview can never hand back content the analysis did
/// not see - and an indexed file has no preview until it is verified.</param>
public record PageFingerprint(string Name, string? RawHash, int Width, int Height);
/// <param name="Verified">Whether the archive's contents were read. An indexed analysis knows what
/// the archive says it holds; a verified one has read every byte of it.</param>
public record ArchiveAnalysis(string Status, string? Hash, List<PageFingerprint> Pages,
    List<ArchiveProblem> Problems, bool Verified = false);

/// <summary>
/// Read-only, bounded archive analysis in two layers. A limit or unsupported decoder is never
/// corruption.
/// <para>
/// Index reads the zip's central directory and nothing else. That record sits at the end of the
/// file, so it costs a seek and a few KB however large the archive is: measured over a 122 GB
/// library of 4007 archives, the whole thing indexes in 1.3 seconds. It knows what the archive
/// claims to hold - whether it is a zip at all, its entry names, their declared sizes - which is
/// enough for missing, empty, corrupt, noPages and ambiguousNames.
/// </para>
/// <para>
/// Verify reads every byte: the file's SHA-256, each entry's checksum, each image's header. That
/// is what catches an archive whose bytes have rotted since it was written, and what produces the
/// content hash the duplicate check and the repair and deletion flows need. It costs a full read
/// of the library - half an hour on a 70 MB/s disk - so it runs on files as they arrive rather
/// than on everything, every night.
/// </para>
/// <para>
/// The analyzer deliberately does not look for repeated or blank pages. It did, and the answer was
/// never actionable: a volume's chapter dividers are the same picture to any comparison loose
/// enough to be useful, and blank pages are how chapter breaks and inserts legitimately look. What
/// is left is damage.
/// </para>
/// </summary>
public static class ArchiveHealthAnalyzer
{
    /// <summary>
    /// Bump when the index layer's results change. Re-indexing a library is seconds, so this is
    /// cheap to move.
    /// </summary>
    public const int IndexVersion = 5;

    /// <summary>
    /// Bump when the verify layer's results change. Kept apart from <see cref="IndexVersion"/>
    /// because re-verifying a library means reading all of it again, and a change to what the
    /// directory tells us has no business forcing that.
    /// </summary>
    public const int VerifyVersion = 1;

    public const int MaxPages = 5000;
    public const long MaxEntryBytes = 128L * 1024 * 1024;

    /// <summary>
    /// Decompressed entry bytes allowed in flight across the whole fan-out. Bounds the workers by
    /// what they hold rather than by how many there are; see the sizing at the Parallel call.
    /// </summary>
    public const long MaxInFlightBytes = 256L * 1024 * 1024;
    public const long MaxExpandedBytes = 4L * 1024 * 1024 * 1024;
    public const long MaxPixels = 40_000_000;

    /// <summary>
    /// How many entries are hashed and header-parsed at once during a verify.
    /// <para>
    /// Half the cores, floored at two and capped at eight. A quarter measured worse than it sounds,
    /// because a two-core NAS then works serially. Anyone who wants it quieter or faster sets
    /// ScanWorkers.
    /// </para>
    /// </summary>
    public static int DefaultWorkers => Math.Clamp(Environment.ProcessorCount / 2, 2, 8);

    /// <param name="workers">Entries processed at once; null or below 1 uses <see cref="DefaultWorkers"/>.</param>
    /// <param name="knownHash">
    /// The file's SHA-256, when the caller has already computed it. A caller that checked the
    /// analysis cache has just read the whole file to do so, and hashing it again here doubles the
    /// read of every archive in the library for nothing.
    /// </param>
    /// <param name="verify">Read the archive's contents, not just its index.</param>
    public static async Task<ArchiveAnalysis> AnalyzeAsync(string path, CancellationToken ct = default,
        int? workers = null, string? knownHash = null, bool verify = false)
    {
        var budget = workers is > 0 ? Math.Min(workers.Value, 32) : DefaultWorkers;
        ct.ThrowIfCancellationRequested();
        var result = new ArchiveAnalysis("complete", null, [], [], verify);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromMinutes(5));
        var token = deadline.Token;
        try
        {
            await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
            if (verify)
                result = result with { Hash = knownHash ?? Convert.ToHexString(await SHA256.HashDataAsync(file, token)) };
            if (file.Length == 0)
                return result with { Problems = [new("empty", "error", "Archive is empty")] };
            file.Position = 0;
            // Constructing this reads the central directory and nothing else, which is the whole
            // index layer. An archive that is not a zip throws here and is reported as corrupt.
            using var archive = new ZipArchive(file, ZipArchiveMode.Read, true);
            if (archive.Entries.Count > 10000 || archive.Entries.Sum(e => (double)e.Length) > MaxExpandedBytes)
                return Partial(result, "Archive exceeds analysis limits");
            var names = CbzReader.PageNames(archive);
            if (names.Count == 0) result.Problems.Add(new("noPages", "error", "Archive contains no reader pages"));
            if (names.Count > MaxPages) return Partial(result, "Archive exceeds page limit");
            if (names.Distinct(StringComparer.OrdinalIgnoreCase).Count() != names.Count)
                result.Problems.Add(new("ambiguousNames", "error", "Archive contains duplicate page names"));

            if (!verify)
            {
                // Names and order, taken at the directory's word. Dimensions and hashes are things
                // only the bytes can answer.
                foreach (var entry in archive.Entries.Where(e => CbzReader.IsImage(e.Name)))
                    result.Pages.Add(new(entry.FullName, null, 0, 0));
                result.Pages.Sort((a, b) => StringComparer.OrdinalIgnoreCase.Compare(a.Name, b.Name));
                return result;
            }

            var problems = new ConcurrentBag<ArchiveProblem>();
            var incomplete = new ConcurrentBag<string>();
            var fingerprints = new ConcurrentBag<PageFingerprint>();
            string? stopped = null;
            long expanded = 0;

            // Decompression and the CRC check stay sequential - a ZipArchive is not thread-safe -
            // while hashing and header parsing fan out. Parallel.ForEachAsync holds the
            // enumerator's own lock across each MoveNext, so the archive is only ever touched by
            // one thread and at most `budget` decompressed entries exist at a time. Reading
            // everything up front would be simpler and would hold the whole archive in memory.
            IEnumerable<(string Name, byte[] Bytes)> Entries()
            {
                foreach (var entry in archive.Entries)
                {
                    token.ThrowIfCancellationRequested();
                    if (entry.Length > MaxEntryBytes)
                    {
                        stopped = "An entry exceeds the analysis limit";
                        yield break;
                    }
                    expanded += entry.Length;
                    if (expanded > MaxExpandedBytes)
                    {
                        stopped = "Expanded archive exceeds analysis limits";
                        yield break;
                    }
                    // One buffer of exactly the declared size. Filling a MemoryStream that doubles
                    // as it grows and then calling ToArray allocated 1.7 GB to read 435 MB of
                    // pages, nearly all of it on the large object heap, and cost 579 gen2
                    // collections across 36 archives against 126 after.
                    var bytes = new byte[entry.Length];
                    int filled;
                    bool overrun;
                    using (var stream = entry.Open())
                    {
                        filled = stream.ReadAtLeast(bytes, bytes.Length, throwOnEndOfStream: false);
                        overrun = stream.ReadByte() != -1;
                    }
                    // Sizing the buffer from the index means trusting it, so check the trust rather
                    // than assume it: an entry that does not hold what the directory claims is a
                    // damaged archive, and saying so beats failing the read.
                    if (filled != bytes.Length || overrun)
                    {
                        problems.Add(new("corrupt", "error", $"Entry size does not match the archive index: {entry.FullName}"));
                        continue;
                    }
                    uint crc = uint.MaxValue;
                    foreach (var b in bytes) crc = CrcTable[(crc ^ b) & 255] ^ (crc >> 8);
                    if (~crc != entry.Crc32) problems.Add(new("corrupt", "error", $"Entry checksum failed: {entry.FullName}"));
                    if (!CbzReader.IsImage(entry.Name)) continue;
                    yield return (entry.FullName, bytes);
                }
            }

            // `budget` alone bounds the COUNT of decompressed entries in flight, not their size, so
            // the ceiling it implied was budget × MaxEntryBytes — a gigabyte at the default worker
            // count and four at the maximum. Entry lengths are already known from the directory, so
            // narrow the fan-out until the bytes fit instead. A library of ordinary 1-3 MB pages is
            // nowhere near the cap and keeps every worker; one archive holding a few very large
            // entries drops to as few as one rather than multiplying them.
            var largestEntry = archive.Entries
                .Where(e => CbzReader.IsImage(e.Name))
                .Aggregate(0L, (max, e) => Math.Max(max, e.Length));
            var affordable = largestEntry > 0
                ? (int)Math.Min(budget, Math.Max(1, MaxInFlightBytes / largestEntry))
                : budget;

            await Parallel.ForEachAsync(Entries(),
                new ParallelOptions { MaxDegreeOfParallelism = affordable, CancellationToken = token },
                async (entry, cancel) =>
                {
                    var (name, bytes) = entry;
                    var rawHash = Convert.ToHexString(SHA256.HashData(bytes));
                    using var stream = new MemoryStream(bytes);
                    try
                    {
                        var info = await Image.IdentifyAsync(stream, cancel);
                        if (info.FrameMetadataCollection.Count > 1)
                            incomplete.Add("Animated images are read from their first frame only");
                        if ((long)info.Width * info.Height > MaxPixels)
                            incomplete.Add("An image exceeds the pixel limit");
                        fingerprints.Add(new(name, rawHash, info.Width, info.Height));
                    }
                    catch (Exception ex) when (ex is UnknownImageFormatException or InvalidImageContentException)
                    {
                        if (ex is UnknownImageFormatException &&
                            Path.GetExtension(name).Equals(".avif", StringComparison.OrdinalIgnoreCase))
                            incomplete.Add("Some pages use an unsupported image decoder");
                        else problems.Add(new("damagedImage", "error", $"Cannot read {name}"));
                        fingerprints.Add(new(name, rawHash, 0, 0));
                    }
                });

            result.Problems.AddRange(problems);
            result.Pages.AddRange(fingerprints);
            result.Pages.Sort((a, b) => StringComparer.OrdinalIgnoreCase.Compare(a.Name, b.Name));
            foreach (var message in incomplete.Distinct()) result = Partial(result, message);
            if (stopped != null) return Partial(result, stopped);
        }
        catch (FileNotFoundException) { result.Problems.Add(new("missing", "error", "File is missing")); }
        catch (DirectoryNotFoundException) { result.Problems.Add(new("missing", "error", "File is missing")); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return Partial(result, "Analysis time limit reached"); }
        catch (InvalidDataException) { result.Problems.Add(new("corrupt", "error", "Archive structure or entry data is corrupt")); }
        catch (IOException) { return Partial(result, "File could not be read; rescan when available"); }
        catch (UnauthorizedAccessException) { return Partial(result, "File cannot be accessed"); }
        return result;
    }

    private static ArchiveAnalysis Partial(ArchiveAnalysis result, string message)
    {
        if (!result.Problems.Any(x => x.Message == message)) result.Problems.Add(new("incomplete", "warning", message));
        return result with { Status = "partial" };
    }

    private static readonly uint[] CrcTable = Enumerable.Range(0, 256).Select(value =>
    {
        var crc = (uint)value;
        for (var bit = 0; bit < 8; bit++) crc = (crc & 1) != 0 ? 0xedb88320 ^ (crc >> 1) : crc >> 1;
        return crc;
    }).ToArray();
}
