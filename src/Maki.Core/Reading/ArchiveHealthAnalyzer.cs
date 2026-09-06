using System.Collections.Concurrent;
using System.IO.Compression;
using System.Security.Cryptography;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace Maki.Core.Reading;

public record ArchiveProblem(string Kind, string Severity, string Message);
public record PageFingerprint(string Name, string RawHash, string? PixelHash, int Width, int Height, bool Blank);
/// <summary>
/// One set of pages that hold the same image, as one fact rather than a pair per combination.
/// <para>
/// Pairs were the original shape and they do not survive contact with a real chapter: four blank
/// pages are six pairs, each naming the same pages again. What a reviewer decides is "these pages
/// are the same, is that intentional", which is one question per set however many members it has.
/// </para>
/// </summary>
/// <param name="Kind">blank (near-solid) or exact (identical bytes, or identical pixels after a
/// re-encode). Nothing looser: see <see cref="ArchiveHealthAnalyzer"/>.</param>
public record PageGroup(string Kind, List<int> Pages);
/// <param name="Deep">Whether every page was decoded. A verify-level analysis knows each page's
/// bytes, size and header; it does not know whether the pixels behind that header are intact.</param>
public record ArchiveAnalysis(string Status, string? Hash, List<PageFingerprint> Pages,
    List<ArchiveProblem> Problems, List<PageGroup> Groups, bool Deep = false);

/// <summary>
/// Read-only, bounded content analysis in two layers. A limit or unsupported decoder is never
/// corruption.
/// <para>
/// Verify reads the archive, checksums every entry and parses each image header. Deep also decodes
/// every page. Measured over a 122 GB library, verify costs about 2 seconds of CPU per GB and is
/// bound by the disk; deep costs 45, which is an hour and a half of CPU for that library and
/// several hours on a machine with two slow cores. That gap is the whole reason for the split:
/// almost everything health reports comes from the cheap layer, and decoding buys only damage
/// behind a valid header, blank pages, and duplicates that survived a re-encode.
/// </para>
/// <para>
/// Repetition is only ever reported for pages that are provably the same image. An earlier version
/// also matched pages by perceptual distance, which flagged most volume compilations: they carry a
/// title card between chapters and often a contents page or two, and at the resolution a
/// perceptual hash works at, two chapter dividers differing only in a printed number are the same
/// picture. There is no threshold that separates that from a page written twice by a failed
/// download, so the loose match was dropped rather than tuned.
/// </para>
/// </summary>
public static class ArchiveHealthAnalyzer
{
    /// <summary>
    /// Bump when the verify layer's own results change. Continues the old combined version's
    /// sequence, so rows written by it re-verify once and are then treated as never deep-analysed.
    /// </summary>
    public const int VerifyVersion = 4;

    /// <summary>
    /// Bump when decoding or fingerprinting changes. Kept apart from <see cref="VerifyVersion"/>
    /// because invalidating a deep analysis costs hours on a real library, and a change to, say,
    /// how repeats are grouped has no business forcing every page to be decoded again.
    /// </summary>
    public const int DeepVersion = 1;
    public const int MaxPages = 5000;
    public const long MaxEntryBytes = 128L * 1024 * 1024;
    public const long MaxExpandedBytes = 4L * 1024 * 1024 * 1024;
    public const long MaxPixels = 40_000_000;
    /// <summary>Repeated pages past this are the same finding restated; the archive is already damning.</summary>
    public const int MaxGroups = 200;

    /// <summary>
    /// How many pages are decoded at once when the caller states no preference.
    /// <para>
    /// Decoding is the whole cost of an analysis - on a 375-page volume it was 8.4s of an 11.2s
    /// run - and it is embarrassingly parallel, so this is only a question of how much of the
    /// machine a background job may take. Half the cores, floored at two and capped at eight: a
    /// quarter measured worse than it sounds, because a two-core NAS then decodes serially and
    /// every volume takes ten seconds. Anyone who wants it quieter or faster sets ScanWorkers.
    /// </para>
    /// </summary>
    public static int DefaultWorkers => Math.Clamp(Environment.ProcessorCount / 2, 2, 8);

    /// <param name="workers">Pages decoded at once; null or below 1 uses <see cref="DefaultWorkers"/>.</param>
    /// <param name="knownHash">
    /// The file's SHA-256, when the caller has already computed it. A caller that checked the
    /// analysis cache has just read the whole file to do so, and hashing it again here doubles the
    /// read of every archive in the library for nothing.
    /// </param>
    /// <param name="deep">Decode every page as well as reading its header.</param>
    public static async Task<ArchiveAnalysis> AnalyzeAsync(string path, CancellationToken ct = default,
        int? workers = null, string? knownHash = null, bool deep = false)
    {
        var budget = workers is > 0 ? Math.Min(workers.Value, 32) : DefaultWorkers;
        var result = new ArchiveAnalysis("complete", null, [], [], [], deep);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromMinutes(5));
        var token = deadline.Token;
        try
        {
            await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
            result = result with { Hash = knownHash ?? Convert.ToHexString(await SHA256.HashDataAsync(file, token)) };
            if (file.Length == 0)
                return result with { Problems = [new("empty", "error", "Archive is empty")] };
            file.Position = 0;
            using var archive = new ZipArchive(file, ZipArchiveMode.Read, true);
            if (archive.Entries.Count > 10000 || archive.Entries.Sum(e => (double)e.Length) > MaxExpandedBytes)
                return Partial(result, "Archive exceeds analysis limits");
            var names = CbzReader.PageNames(archive);
            if (names.Count == 0) result.Problems.Add(new("noPages", "error", "Archive contains no reader pages"));
            if (names.Count > MaxPages) return Partial(result, "Archive exceeds page limit");
            if (names.Distinct(StringComparer.OrdinalIgnoreCase).Count() != names.Count)
                result.Problems.Add(new("ambiguousNames", "error", "Archive contains duplicate page names"));

            var problems = new ConcurrentBag<ArchiveProblem>();
            var incomplete = new ConcurrentBag<string>();
            var fingerprints = new ConcurrentBag<PageFingerprint>();
            string? stopped = null;
            long expanded = 0;

            // Decompression and the CRC check stay sequential - a ZipArchive is not thread-safe,
            // and they are cheap next to decoding. Parallel.ForEachAsync holds the enumerator's own
            // lock across each MoveNext, so the archive is only ever touched by one thread and at
            // most `budget` decompressed entries exist at a time. Reading everything up front and
            // then fanning out would be simpler and would hold the whole expanded archive in memory.
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
                    // collections across 36 archives. A central directory that lies about the
                    // length fails the checksum below, which is the answer we want anyway.
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

            await Parallel.ForEachAsync(Entries(),
                new ParallelOptions { MaxDegreeOfParallelism = budget, CancellationToken = token },
                async (entry, cancel) =>
                {
                    var (name, bytes) = entry;
                    var rawHash = Convert.ToHexString(SHA256.HashData(bytes));
                    using var stream = new MemoryStream(bytes);
                    try
                    {
                        var info = await Image.IdentifyAsync(stream, cancel);
                        if (info.FrameMetadataCollection.Count > 1)
                            incomplete.Add("Animated images are compared using their first frame only");
                        if ((long)info.Width * info.Height > MaxPixels)
                        {
                            incomplete.Add("An image exceeds the pixel limit");
                            fingerprints.Add(new(name, rawHash, null, info.Width, info.Height, false));
                            return;
                        }
                        if (!deep)
                        {
                            // The header parsed, so this is an image of these dimensions. Whether
                            // the pixels behind it are intact is the deep layer's question.
                            fingerprints.Add(new(name, rawHash, null, info.Width, info.Height, false));
                            return;
                        }
                        stream.Position = 0;
                        using var image = await Image.LoadAsync<Rgba32>(
                            new SixLabors.ImageSharp.Formats.DecoderOptions { MaxFrames = 1 }, stream, cancel);
                        image.Mutate(x => x.AutoOrient());
                        using var pixelHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                        pixelHash.AppendData(BitConverter.GetBytes(image.Width));
                        pixelHash.AppendData(BitConverter.GetBytes(image.Height));
                        image.ProcessPixelRows(accessor =>
                        {
                            for (var y = 0; y < accessor.Height; y++)
                                pixelHash.AppendData(System.Runtime.InteropServices.MemoryMarshal.AsBytes(accessor.GetRowSpan(y)));
                        });
                        fingerprints.Add(new(name, rawHash, Convert.ToHexString(pixelHash.GetHashAndReset()),
                            image.Width, image.Height, IsBlank(image)));
                    }
                    catch (UnknownImageFormatException)
                    {
                        if (Path.GetExtension(name).Equals(".avif", StringComparison.OrdinalIgnoreCase))
                            incomplete.Add("Some pages use an unsupported image decoder");
                        else problems.Add(new("damagedImage", "error", $"Cannot decode {name}"));
                        fingerprints.Add(new(name, rawHash, null, 0, 0, false));
                    }
                    catch (InvalidImageContentException)
                    {
                        problems.Add(new("damagedImage", "error", $"Cannot decode {name}"));
                        fingerprints.Add(new(name, rawHash, null, 0, 0, false));
                    }
                });

            result.Problems.AddRange(problems);
            result.Pages.AddRange(fingerprints);
            result.Pages.Sort((a, b) => StringComparer.OrdinalIgnoreCase.Compare(a.Name, b.Name));
            foreach (var message in incomplete.Distinct()) result = Partial(result, message);
            if (stopped != null) return Partial(result, stopped);

            // Blank pages are one group, never pairs. Every blank page matches every other one, so
            // pairing them says "page 15 is blank" once per other blank page in the archive - the
            // same fact, restated, burying the content repeats that are worth looking at. Nothing
            // to collect without pixels: a verify pass never sets Blank.
            var blanks = Enumerable.Range(0, result.Pages.Count).Where(i => result.Pages[i].Blank).ToList();
            if (blanks.Count > 1) result.Groups.Add(new("blank", blanks));

            // A page that is byte-identical is also pixel-identical, so one key covers both, and
            // bucketing by it makes this a single pass rather than a comparison of every pair. This
            // is why repeated pages survive the cheap layer: the case worth catching, one image
            // written many times by a failed download, is byte-identical and needs no decoding.
            // Only a repeat that was re-encoded in between needs pixels to see.
            foreach (var repeat in result.Pages
                         .Select((page, index) => (page, index))
                         .Where(x => !x.page.Blank)
                         .GroupBy(x => x.page.PixelHash ?? x.page.RawHash)
                         .Where(g => g.Count() > 1))
            {
                result.Groups.Add(new("exact", repeat.Select(x => x.index).Order().ToList()));
                if (result.Groups.Count >= MaxGroups)
                {
                    result = Partial(result, $"Repetition evidence capped at {MaxGroups} groups");
                    break;
                }
            }
            result.Groups.Sort((a, b) => a.Pages[0].CompareTo(b.Pages[0]));
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

    /// <summary>Near-solid, by the variance of a 32x32 grayscale reduction.</summary>
    private static bool IsBlank(Image<Rgba32> original)
    {
        using var small = original.Clone(x => x.Resize(32, 32).Grayscale());
        double sum = 0, sumSquares = 0;
        for (var y = 0; y < 32; y++)
        for (var x = 0; x < 32; x++)
        {
            var value = small[x, y].R;
            sum += value;
            sumSquares += value * value;
        }
        return sumSquares / 1024 - Math.Pow(sum / 1024, 2) < 4;
    }
}
