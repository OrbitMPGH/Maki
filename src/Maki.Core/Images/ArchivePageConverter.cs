using System.IO.Compression;
using Maki.Core.Reading;
using NetVips;

namespace Maki.Core.Images;

public enum ArchiveConversionOutcome
{
    /// <summary>The target holds the converted archive.</summary>
    Converted,

    /// <summary>No page got enough smaller, or the file as a whole would not shrink enough.</summary>
    NotSmaller,

    /// <summary>A page did not decode, an entry failed its checksum or declared more than the health scan allows; the file is the health scan's business.</summary>
    Unreadable,

    /// <summary>The new extensions would reorder the pages or collide with another entry.</summary>
    NamesConflict,

    NoPages
}

public sealed record ArchiveConversionResult(
    ArchiveConversionOutcome Outcome,
    long SourceBytes,
    long ResultBytes,
    int PagesConverted,
    int PagesKept,
    string SourceFormat,
    string ResultFormat,
    IReadOnlyDictionary<PageSkip, int> Skipped)
{
    public long SavedBytes => Outcome == ArchiveConversionOutcome.Converted ? SourceBytes - ResultBytes : 0;
}

/// <summary>
/// Rewrites a CBZ with its JPEG and PNG pages converted (<see cref="PageConversion"/>). Every other
/// entry, ComicInfo.xml included, is copied byte for byte in its original order. A page keeps its
/// name and changes only its extension, so chapter markers in volume page names survive, and the
/// result is refused when that would change the page order <see cref="CbzReader.PageNames(ZipArchive)"/>
/// reads. The source is never touched: the result is written to <c>target.partial</c> and moved onto
/// the target only when it is worth keeping.
/// </summary>
public static class ArchivePageConverter
{
    public static async Task<ArchiveConversionResult> ConvertAsync(
        string sourcePath, string targetPath, PageConversionSettings settings, CancellationToken ct)
    {
        var sourceBytes = new FileInfo(sourcePath).Length;
        var partial = targetPath + ".partial";
        var skipped = new Dictionary<PageSkip, int>();
        var sourceFormats = new List<string>();
        var resultFormats = new List<string>();
        var renamed = new Dictionary<string, string>(StringComparer.Ordinal);
        var converted = 0;
        var kept = 0;
        var expanded = 0L;
        var pending = new Queue<Pending>();
        using var encodes = CancellationTokenSource.CreateLinkedTokenSource(ct);

        ArchiveConversionResult Result(ArchiveConversionOutcome outcome, long resultBytes = 0) => new(
            outcome, sourceBytes, resultBytes, converted, kept, PageConversion.ResolveFormat(sourceFormats),
            PageConversion.ResolveFormat(resultFormats), skipped);

        try
        {
            List<string> order;
            using (var source = ZipFile.OpenRead(sourcePath))
            {
                order = CbzReader.PageNames(source);
                if (order.Count == 0)
                {
                    return Result(ArchiveConversionOutcome.NoPages);
                }

                var pages = order.ToHashSet(StringComparer.Ordinal);
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(targetPath))!);
                await using var output = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None);
                using var target = new ZipArchive(output, ZipArchiveMode.Create);

                async Task WriteAsync(Pending item)
                {
                    var entry = item.Entry;
                    if (item.Bytes is not { } bytes)
                    {
                        target.CreateEntry(entry.FullName).LastWriteTime = entry.LastWriteTime;
                        return;
                    }

                    var name = entry.FullName;
                    var level = CompressionLevel.Optimal;
                    if (item.Page is { } encoding)
                    {
                        var page = await encoding;
                        sourceFormats.Add(page.SourceFormat);
                        if (page.Converted)
                        {
                            converted++;
                            name = name[..^Path.GetExtension(name).Length] + page.Extension;
                            renamed[entry.FullName] = name;
                            bytes = page.Bytes;
                            resultFormats.Add(page.Extension.TrimStart('.'));
                        }
                        else
                        {
                            kept++;
                            skipped[page.Skipped!.Value] = skipped.GetValueOrDefault(page.Skipped.Value) + 1;
                            resultFormats.Add(page.SourceFormat);
                        }

                        level = CompressionLevel.NoCompression;
                    }

                    var written = target.CreateEntry(name, level);
                    written.LastWriteTime = entry.LastWriteTime;
                    await using var stream = written.Open();
                    await stream.WriteAsync(bytes, ct);
                }

                // Pages encode ahead of the writer, as many at once as the conversion limit allows, and are
                // written in the source order. AV1 is the slow part, and one page at a time left
                // all but one core idle.
                foreach (var entry in source.Entries)
                {
                    ct.ThrowIfCancellationRequested();
                    if (entry.FullName.EndsWith('/'))
                    {
                        pending.Enqueue(new Pending(entry, null, null));
                        continue;
                    }

                    expanded += entry.Length;
                    var bytes = ReadAll(entry, expanded);
                    Task<PageConversion.Page>? page = null;
                    if (pages.Contains(entry.FullName))
                    {
                        var extension = Path.GetExtension(entry.FullName);
                        page = Task.Run(() => ImageWorkGate.RunConversionAsync(
                            () => PageConversion.Convert(bytes, extension, settings),
                            ImageWorkGate.Weigh(bytes), encodes.Token), encodes.Token);
                    }

                    pending.Enqueue(new Pending(entry, bytes, page));
                    while (pending.Count(p => p.Page is not null) > ImageWorkGate.ConversionLimit)
                    {
                        await WriteAsync(pending.Peek());
                        pending.Dequeue();
                    }
                }

                while (pending.TryPeek(out var next))
                {
                    await WriteAsync(next);
                    pending.Dequeue();
                }
            }

            if (converted == 0)
            {
                TryDelete(partial);
                return Result(ArchiveConversionOutcome.NotSmaller);
            }

            if (!SameOrder(order, renamed, partial))
            {
                TryDelete(partial);
                return Result(ArchiveConversionOutcome.NamesConflict);
            }

            var resultBytes = new FileInfo(partial).Length;
            if (resultBytes > sourceBytes * (1 - PageConversion.MinFileSaving))
            {
                TryDelete(partial);
                return Result(ArchiveConversionOutcome.NotSmaller, resultBytes);
            }

            File.Move(partial, targetPath, overwrite: true);
            return Result(ArchiveConversionOutcome.Converted, resultBytes);
        }
        catch (Exception ex) when (ex is VipsException or InvalidDataException or EndOfStreamException)
        {
            TryDelete(partial);
            return Result(ArchiveConversionOutcome.Unreadable);
        }
        catch
        {
            TryDelete(partial);
            throw;
        }
        finally
        {
            // Encodes still queued behind a failed page must not outlive the call.
            await encodes.CancelAsync();
            await Task.WhenAll(pending.Select(p => p.Page).OfType<Task>()).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }
    }

    /// <summary>An entry read and waiting to be written; <see cref="Bytes"/> is null for a directory.</summary>
    private sealed record Pending(ZipArchiveEntry Entry, byte[]? Bytes, Task<PageConversion.Page>? Page);

    private const int MaxProbeReads = 12;

    /// <summary>
    /// Whether a full conversion is worth running: encodes up to two pages over
    /// <see cref="PageConversion.MinPageBytes"/> from the middle third of the archive, one per source
    /// format when it mixes JPEG and PNG, and answers whether any came out smaller by the page floor.
    /// Anything it cannot read, or a file with nothing to probe, answers true so the full pass decides.
    /// </summary>
    public static async Task<bool> ProbeAsync(string path, PageConversionSettings settings, CancellationToken ct)
    {
        try
        {
            List<(byte[] Bytes, string Extension)> picked;
            using (var zip = ZipFile.OpenRead(path))
            {
                picked = MiddlePages(zip, 2, ct);
            }

            if (picked.Count == 0)
            {
                return true;
            }

            foreach (var (bytes, extension) in picked)
            {
                var page = await ImageWorkGate.RunConversionAsync(
                    () => PageConversion.Convert(bytes, extension, settings), ImageWorkGate.Weigh(bytes), ct);
                if (page.Converted)
                {
                    return true;
                }
            }

            return false;
        }
        catch (Exception ex) when (ex is VipsException or InvalidDataException or EndOfStreamException or IOException
                                       or UnauthorizedAccessException or NotSupportedException)
        {
            return true;
        }
    }

    /// <summary>
    /// Up to <paramref name="wanted"/> JPEG or PNG pages of at least <see cref="PageConversion.MinPageBytes"/>
    /// from the middle third of the archive, so never a cover or a credits page. When the archive mixes
    /// the two formats the pages alternate between them. Reads at most twelve entries. Throws what
    /// reading a damaged archive throws.
    /// </summary>
    public static List<(byte[] Bytes, string Extension)> MiddlePages(ZipArchive zip, int wanted, CancellationToken ct)
    {
        var order = CbzReader.PageNames(zip);
        var entries = zip.Entries
            .GroupBy(e => e.FullName, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        var formats = new Dictionary<string, List<(byte[] Bytes, string Extension)>>(StringComparer.Ordinal);
        var looked = 0;
        for (var i = order.Count / 3; i < Math.Max(order.Count / 3 + 1, order.Count * 2 / 3); i++)
        {
            ct.ThrowIfCancellationRequested();
            if ((formats.Count == 2 && formats.Values.Sum(l => l.Count) >= wanted) || looked >= MaxProbeReads)
            {
                break;
            }

            if (i >= order.Count || !entries.TryGetValue(order[i], out var entry) || entry.Length < PageConversion.MinPageBytes)
            {
                continue;
            }

            // Enough pages of a format are all it needs of that one; read on only for the other.
            if (ExtensionFormat(entry.FullName) is { } named && formats.TryGetValue(named, out var have) && have.Count >= wanted)
            {
                continue;
            }

            looked++;
            var bytes = ReadAll(entry, entry.Length);
            var extension = Path.GetExtension(entry.FullName);
            var format = PageConversion.FormatOf(bytes, extension);
            if (format is "jpg" or "png")
            {
                var list = formats.TryGetValue(format, out var found) ? found : formats[format] = [];
                if (list.Count < wanted)
                {
                    list.Add((bytes, extension));
                }
            }
        }

        var picked = new List<(byte[] Bytes, string Extension)>();
        for (var round = 0; picked.Count < wanted && formats.Values.Any(l => l.Count > round); round++)
        {
            foreach (var list in formats.Values)
            {
                if (round < list.Count && picked.Count < wanted)
                {
                    picked.Add(list[round]);
                }
            }
        }

        return picked;
    }

    private static string? ExtensionFormat(string name) => Path.GetExtension(name).ToLowerInvariant() switch
    {
        ".jpg" or ".jpeg" => "jpg",
        ".png" => "png",
        _ => null
    };

    /// <summary>
    /// The written archive has to list the same pages in the same order, each under its new name,
    /// and no two entries may share a name.
    /// </summary>
    private static bool SameOrder(List<string> order, Dictionary<string, string> renamed, string partial)
    {
        using var written = ZipFile.OpenRead(partial);
        var names = written.Entries.Select(e => e.FullName).ToList();
        if (names.Distinct(StringComparer.OrdinalIgnoreCase).Count() != names.Count)
        {
            return false;
        }

        var expected = order.Select(n => renamed.GetValueOrDefault(n, n));
        return CbzReader.PageNames(written).SequenceEqual(expected, StringComparer.Ordinal);
    }

    /// <summary>
    /// Reads an entry to the end, so the zip's checksum is checked, into a buffer of exactly the size
    /// its header declares. The health scan's limits are checked before allocating, since the header
    /// of an imported archive can declare anything.
    /// </summary>
    private static byte[] ReadAll(ZipArchiveEntry entry, long expanded)
    {
        if (entry.Length > ArchiveHealthAnalyzer.MaxEntryBytes || expanded > ArchiveHealthAnalyzer.MaxExpandedBytes)
        {
            throw new InvalidDataException($"{entry.FullName} is too large to convert");
        }

        var bytes = new byte[entry.Length];
        using var stream = entry.Open();
        stream.ReadExactly(bytes);
        if (stream.ReadByte() != -1)
        {
            throw new InvalidDataException($"{entry.FullName} is longer than its header says");
        }

        return bytes;
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
