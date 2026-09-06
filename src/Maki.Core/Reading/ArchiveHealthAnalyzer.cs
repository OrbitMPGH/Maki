using System.IO.Compression;
using System.Numerics;
using System.Security.Cryptography;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace Maki.Core.Reading;

public record ArchiveProblem(string Kind, string Severity, string Message);
public record PageFingerprint(string Name, string RawHash, string? PixelHash, string? PerceptualHash,
    int Width, int Height, bool Blank);
/// <summary>
/// One set of pages that are the same picture, as one fact rather than a pair per combination.
/// <para>
/// Pairs were the original shape and they do not survive contact with a real chapter: four blank
/// pages are six pairs, each naming the same pages again, and a webtoon chapter sliced into strips
/// with plain backgrounds can produce hundreds. What a reviewer needs to decide is "these pages are
/// the same, is that intentional", which is one question per set however many members it has.
/// </para>
/// </summary>
/// <param name="Kind">blank (all members near-solid), exact (identical bytes or pixels), or
/// similar (perceptually close, which can still be a legitimate repeat).</param>
/// <param name="Distance">Worst perceptual distance inside the set; 0 for exact and blank.</param>
public record PageGroup(string Kind, List<int> Pages, int Distance);
public record ArchiveAnalysis(string Status, string? Hash, List<PageFingerprint> Pages,
    List<ArchiveProblem> Problems, List<PageGroup> Groups);

/// <summary>Read-only, bounded content analysis. A limit or unsupported decoder is never corruption.</summary>
public static class ArchiveHealthAnalyzer
{
    public const int Version = 2;
    public const int MaxPages = 5000;
    public const long MaxEntryBytes = 128L * 1024 * 1024;
    public const long MaxExpandedBytes = 4L * 1024 * 1024 * 1024;
    public const long MaxPixels = 40_000_000;
    /// <summary>Repeated pages past this are the same finding restated; the archive is already damning.</summary>
    public const int MaxGroups = 200;
    private const int MaxEdges = 20_000;

    public static async Task<ArchiveAnalysis> AnalyzeAsync(string path, CancellationToken ct = default)
    {
        var result = new ArchiveAnalysis("complete", null, [], [], []);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromMinutes(5));
        var token = deadline.Token;
        try
        {
            await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
            var hash = Convert.ToHexString(await SHA256.HashDataAsync(file, token));
            result = result with { Hash = hash };
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
            long expanded = 0;
            foreach (var entry in archive.Entries)
            {
                token.ThrowIfCancellationRequested();
                if (entry.Length > MaxEntryBytes) return Partial(result, "An entry exceeds the analysis limit");
                await using var stream = entry.Open();
                using var bytes = new MemoryStream();
                var buffer = new byte[65536];
                uint crc = uint.MaxValue;
                int read;
                while ((read = await stream.ReadAsync(buffer, token)) > 0)
                {
                    expanded += read;
                    if (bytes.Length + read > MaxEntryBytes || expanded > MaxExpandedBytes)
                        return Partial(result, "Expanded archive exceeds analysis limits");
                    await bytes.WriteAsync(buffer.AsMemory(0, read), token);
                    for (var i = 0; i < read; i++) crc = CrcTable[(crc ^ buffer[i]) & 255] ^ (crc >> 8);
                }
                if (~crc != entry.Crc32) result.Problems.Add(new("corrupt", "error", $"Entry checksum failed: {entry.FullName}"));
                if (!CbzReader.IsImage(entry.Name)) continue;
                var rawHash = Convert.ToHexString(SHA256.HashData(bytes.GetBuffer().AsSpan(0, (int)bytes.Length)));
                bytes.Position = 0;
                try
                {
                    var info = await Image.IdentifyAsync(bytes, token);
                    if (info.FrameMetadataCollection.Count > 1) result = Partial(result, "Animated images are compared using their first frame only");
                    if ((long)info.Width * info.Height > MaxPixels)
                    {
                        result = Partial(result, "An image exceeds the pixel limit");
                        result.Pages.Add(new(entry.FullName, rawHash, null, null, info.Width, info.Height, false));
                        continue;
                    }
                    bytes.Position = 0;
                    using var image = await Image.LoadAsync<Rgba32>(new SixLabors.ImageSharp.Formats.DecoderOptions { MaxFrames = 1 }, bytes, token);
                    image.Mutate(x => x.AutoOrient());
                    using var pixelHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                    pixelHash.AppendData(BitConverter.GetBytes(image.Width));
                    pixelHash.AppendData(BitConverter.GetBytes(image.Height));
                    image.ProcessPixelRows(accessor =>
                    {
                        for (var y = 0; y < accessor.Height; y++)
                            pixelHash.AppendData(System.Runtime.InteropServices.MemoryMarshal.AsBytes(accessor.GetRowSpan(y)));
                    });
                    var pixels = Convert.ToHexString(pixelHash.GetHashAndReset());
                    var (perceptual, blank) = Fingerprint(image);
                    result.Pages.Add(new(entry.FullName, rawHash, pixels, perceptual.ToString("X16"), image.Width, image.Height, blank));
                }
                catch (UnknownImageFormatException)
                {
                    var unsupported = Path.GetExtension(entry.Name).Equals(".avif", StringComparison.OrdinalIgnoreCase);
                    if (unsupported) result = Partial(result, "Some pages use an unsupported image decoder");
                    else result.Problems.Add(new("damagedImage", "error", $"Cannot decode {entry.FullName}"));
                    result.Pages.Add(new(entry.FullName, rawHash, null, null, 0, 0, false));
                }
                catch (InvalidImageContentException)
                {
                    result.Problems.Add(new("damagedImage", "error", $"Cannot decode {entry.FullName}"));
                    result.Pages.Add(new(entry.FullName, rawHash, null, null, 0, 0, false));
                }
            }
            result.Pages.Sort((a, b) => StringComparer.OrdinalIgnoreCase.Compare(a.Name, b.Name));

            // Blank pages are one group, never pairs. Every blank page matches every other one, so
            // pairing them says "page 15 is blank" once per other blank page in the archive - the
            // same fact, restated, burying the content repeats that are worth looking at.
            var blanks = Enumerable.Range(0, result.Pages.Count).Where(i => result.Pages[i].Blank).ToList();
            if (blanks.Count > 1) result.Groups.Add(new("blank", blanks, 0));

            var edges = new List<(int A, int B, bool Exact, int Distance)>();
            for (var i = 0; i < result.Pages.Count; i++)
            {
                token.ThrowIfCancellationRequested();
                var a = result.Pages[i];
                for (var j = i + 1; j < result.Pages.Count; j++)
                {
                    var b = result.Pages[j];
                    if (a.Blank && b.Blank) continue;
                    var exact = a.RawHash == b.RawHash || (a.PixelHash != null && a.PixelHash == b.PixelHash);
                    var distance = a.PerceptualHash != null && b.PerceptualHash != null
                        ? BitOperations.PopCount(Convert.ToUInt64(a.PerceptualHash, 16) ^ Convert.ToUInt64(b.PerceptualHash, 16)) : 65;
                    var sameAspect = a.Height > 0 && b.Height > 0 &&
                        Math.Abs((double)a.Width / a.Height / ((double)b.Width / b.Height) - 1) <= .02;
                    if (!exact && (!sameAspect || distance > 6)) continue;
                    edges.Add((i, j, exact, exact ? 0 : distance));
                    if (edges.Count >= MaxEdges) return Partial(result, "Repetition evidence exceeded the comparison limit");
                }
            }

            // Union-find over the matches, so "1 is 2, 2 is 3" lands as one set of three rather than
            // as two pairs the reviewer has to join up themselves.
            var parent = Enumerable.Range(0, result.Pages.Count).ToArray();
            int Root(int x) { while (parent[x] != x) { parent[x] = parent[parent[x]]; x = parent[x]; } return x; }
            foreach (var edge in edges)
            {
                var (left, right) = (Root(edge.A), Root(edge.B));
                if (left != right) parent[left] = right;
            }
            foreach (var component in edges.SelectMany(e => new[] { e.A, e.B }).Distinct().GroupBy(Root))
            {
                var inner = edges.Where(e => Root(e.A) == component.Key).ToList();
                // One weak link makes the whole set a "similar" claim: the reviewer is being told
                // these might be the same picture, not that they provably are.
                result.Groups.Add(new(inner.All(e => e.Exact) ? "exact" : "similar",
                    component.Order().ToList(), inner.Max(e => e.Distance)));
                if (result.Groups.Count >= MaxGroups) { result = Partial(result, $"Repetition evidence capped at {MaxGroups} groups"); break; }
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

    private static (ulong Hash, bool Blank) Fingerprint(Image<Rgba32> original)
    {
        using var small = original.Clone(x => x.Resize(32, 32).Grayscale());
        var values = new double[32, 32];
        double sum = 0, sumSquares = 0;
        for (var y = 0; y < 32; y++)
        for (var x = 0; x < 32; x++)
        {
            var v = small[x, y].R;
            values[x, y] = v;
            sum += v;
            sumSquares += v * v;
        }
        var coefficients = new double[64];
        for (var u = 0; u < 8; u++)
        for (var v = 0; v < 8; v++)
        {
            double coefficient = 0;
            for (var x = 0; x < 32; x++)
            for (var y = 0; y < 32; y++)
                coefficient += values[x, y] * Math.Cos((2 * x + 1) * u * Math.PI / 64) * Math.Cos((2 * y + 1) * v * Math.PI / 64);
            coefficients[u * 8 + v] = coefficient;
        }
        var median = coefficients.Skip(1).Order().ElementAt(31);
        ulong hash = 0;
        for (var i = 1; i < 64; i++) if (coefficients[i] > median) hash |= 1UL << i;
        return (hash, sumSquares / 1024 - Math.Pow(sum / 1024, 2) < 4);
    }
}
