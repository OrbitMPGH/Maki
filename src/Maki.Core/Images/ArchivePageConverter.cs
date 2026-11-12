using System.Globalization;
using System.IO.Compression;
using System.Xml;
using System.Xml.Linq;
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
    IReadOnlyDictionary<PageSkip, int> Skipped,
    PageSplitMap? Split = null,
    IReadOnlyList<string>? PagesBefore = null)
{
    /// <summary>Can be negative for a file whose tall pages were split: the split is kept even when it grows the file.</summary>
    public long SavedBytes => Outcome == ArchiveConversionOutcome.Converted ? SourceBytes - ResultBytes : 0;
}

/// <summary>
/// Rewrites a CBZ with its JPEG and PNG pages converted (<see cref="PageConversion"/>). Every other
/// entry, ComicInfo.xml included, is copied byte for byte in its original order. A page keeps its
/// name and changes only its extension, so chapter markers in volume page names survive, and the
/// result is refused when that would change the page order <see cref="CbzReader.PageNames(ZipArchive)"/>
/// reads. A tall strip the settings split becomes pages named after it with "-01", "-02" and so on,
/// and then ComicInfo.xml moves to the end with its PageCount updated. The source is never touched:
/// the result is written to <c>target.partial</c> and moved onto the target only when it is worth keeping.
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
        var renamed = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        var pieces = new Dictionary<int, int>();
        List<string>? order = null;
        Dictionary<string, int>? indexOf = null;
        (ZipArchiveEntry Entry, byte[] Bytes)? comicInfo = null;
        var converted = 0;
        var kept = 0;
        var expanded = 0L;
        var pending = new Queue<Pending>();
        using var encodes = CancellationTokenSource.CreateLinkedTokenSource(ct);

        ArchiveConversionResult Result(ArchiveConversionOutcome outcome, long resultBytes = 0) => new(
            outcome, sourceBytes, resultBytes, converted, kept, PageConversion.ResolveFormat(sourceFormats),
            PageConversion.ResolveFormat(resultFormats), skipped,
            pieces.Count > 0 ? new PageSplitMap(pieces) : null, pieces.Count > 0 ? order : null);

        try
        {
            using (var source = ZipFile.OpenRead(sourcePath))
            {
                order = CbzReader.PageNames(source);
                if (order.Count == 0)
                {
                    return Result(ArchiveConversionOutcome.NoPages);
                }

                var pages = order.ToHashSet(StringComparer.Ordinal);
                indexOf = order.Select((name, i) => (name, i)).ToDictionary(p => p.name, p => p.i, StringComparer.Ordinal);
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(targetPath))!);
                await using var output = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None);
                using var target = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true);

                async Task WriteAsync(Pending item)
                {
                    var entry = item.Entry;
                    if (item.Bytes is not { } bytes)
                    {
                        target.CreateEntry(entry.FullName).LastWriteTime = entry.LastWriteTime;
                        return;
                    }

                    if (settings.SplitHeight is not null && IsComicInfo(entry))
                    {
                        comicInfo = (entry, bytes);
                        return;
                    }

                    var name = entry.FullName;
                    var level = CompressionLevel.Optimal;
                    if (item.Page is { } encoding)
                    {
                        var result = await encoding;
                        if (result.Count > 1)
                        {
                            await WritePiecesAsync(entry, result);
                            return;
                        }

                        var page = result[0];
                        sourceFormats.Add(page.SourceFormat);
                        if (page.Converted)
                        {
                            converted++;
                            name = name[..^Path.GetExtension(name).Length] + page.Extension;
                            renamed[entry.FullName] = [name];
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

                async Task WritePiecesAsync(ZipArchiveEntry entry, IReadOnlyList<PageConversion.Page> split)
                {
                    var stem = entry.FullName[..^Path.GetExtension(entry.FullName).Length];
                    var digits = split.Count > 99 ? "D3" : "D2";
                    var names = new List<string>(split.Count);
                    converted++;
                    sourceFormats.Add(split[0].SourceFormat);
                    pieces[indexOf![entry.FullName]] = split.Count;
                    for (var i = 0; i < split.Count; i++)
                    {
                        var name = $"{stem}-{(i + 1).ToString(digits, CultureInfo.InvariantCulture)}{split[i].Extension}";
                        names.Add(name);
                        resultFormats.Add(PageConversion.FormatOf(split[i].Bytes, split[i].Extension));
                        var written = target.CreateEntry(name, CompressionLevel.NoCompression);
                        written.LastWriteTime = entry.LastWriteTime;
                        await using var stream = written.Open();
                        await stream.WriteAsync(split[i].Bytes, ct);
                    }

                    renamed[entry.FullName] = names;
                }

                // Pages encode ahead of the writer, as many at once as the conversion limit allows (half as
                // many AVIF pages, each on two threads), and are written in the source order. AV1 is the
                // slow part, and one page at a time left all but one core idle.
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
                    Task<IReadOnlyList<PageConversion.Page>>? page = null;
                    if (pages.Contains(entry.FullName))
                    {
                        var extension = Path.GetExtension(entry.FullName);
                        page = Task.Run(() => ImageWorkGate.RunConversionAsync(
                            () => PageConversion.ConvertPage(bytes, extension, settings),
                            PageConversion.WeightOf(bytes, extension, settings), encodes.Token), encodes.Token);
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

                if (comicInfo is var (infoEntry, infoBytes))
                {
                    var newCount = order.Count + pieces.Values.Sum(n => n - 1);
                    var written = target.CreateEntry(infoEntry.FullName);
                    written.LastWriteTime = infoEntry.LastWriteTime;
                    await using var stream = written.Open();
                    await stream.WriteAsync(pieces.Count > 0 ? WithPageCount(infoBytes, newCount, pieces) : infoBytes, ct);
                }

                // On disk before any rename, so a crash never leaves a renamed archive whose data never
                // landed, and clean, so the caller can drop it from the page cache. A copy that will not
                // be kept is not worth the flush.
                target.Dispose();
                if (converted > 0 && (pieces.Count > 0 || output.Length <= sourceBytes * (1 - PageConversion.MinFileSaving)))
                {
                    output.Flush(flushToDisk: true);
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
            if (pieces.Count == 0 && resultBytes > sourceBytes * (1 - PageConversion.MinFileSaving))
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
    private sealed record Pending(ZipArchiveEntry Entry, byte[]? Bytes, Task<IReadOnlyList<PageConversion.Page>>? Page);

    private static bool IsComicInfo(ZipArchiveEntry entry) =>
        string.Equals(entry.FullName, "ComicInfo.xml", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// ComicInfo.xml with its PageCount set to the new count and its <c>Pages</c> block renumbered:
    /// a split page's first piece keeps its entry (FrontCover and all) minus the size and double-page
    /// attributes that no longer hold, and each further piece gets a bare entry. A block that cannot be
    /// renumbered goes. Anything that does not parse is left as it was.
    /// </summary>
    internal static byte[] WithPageCount(byte[] xml, int pageCount, IReadOnlyDictionary<int, int>? pieces = null)
    {
        try
        {
            XDocument document;
            using (var reader = new MemoryStream(xml))
            {
                document = XDocument.Load(reader, LoadOptions.PreserveWhitespace);
            }

            if (document.Root is not { } root)
            {
                return xml;
            }

            var ns = root.Name.Namespace;
            if (root.Element(ns + "Pages") is { } pagesBlock && !RenumberPages(pagesBlock, pieces ?? new Dictionary<int, int>()))
            {
                pagesBlock.Remove();
            }

            var count = pageCount.ToString(CultureInfo.InvariantCulture);
            if (root.Element(ns + "PageCount") is { } existing)
            {
                existing.Value = count;
            }
            else
            {
                root.Add(new XElement(ns + "PageCount", count));
            }

            using var output = new MemoryStream();
            using (var writer = XmlWriter.Create(output, new XmlWriterSettings
                   {
                       Encoding = new System.Text.UTF8Encoding(false),
                       OmitXmlDeclaration = document.Declaration is null
                   }))
            {
                document.Save(writer);
            }

            return output.ToArray();
        }
        catch (XmlException)
        {
            return xml;
        }
    }

    private static bool RenumberPages(XElement block, IReadOnlyDictionary<int, int> pieces)
    {
        var ns = block.Name.Namespace;
        var entries = new List<(int Index, XElement Page)>();
        foreach (var page in block.Elements(ns + "Page"))
        {
            if (!int.TryParse((string?)page.Attribute("Image"), NumberStyles.None, CultureInfo.InvariantCulture, out var index))
            {
                return false;
            }

            entries.Add((index, page));
        }

        var shift = 0;
        var renumbered = new List<XElement>();
        foreach (var (index, page) in entries.OrderBy(e => e.Index))
        {
            var count = pieces.TryGetValue(index, out var n) ? n : 1;
            var first = new XElement(page);
            first.SetAttributeValue("Image", (index + shift).ToString(CultureInfo.InvariantCulture));
            if (count > 1)
            {
                foreach (var stale in new[] { "DoublePage", "ImageWidth", "ImageHeight", "ImageSize" })
                {
                    first.Attribute(stale)?.Remove();
                }
            }

            renumbered.Add(first);
            for (var i = 1; i < count; i++)
            {
                renumbered.Add(new XElement(ns + "Page", new XAttribute("Image", (index + shift + i).ToString(CultureInfo.InvariantCulture))));
            }

            shift += count - 1;
        }

        block.ReplaceNodes(renumbered);
        return true;
    }

    private const int MaxProbeReads = 12;

    /// <summary>Enough of a page to read its header from; a page whose header is further in is read whole.</summary>
    private const int HeaderPrefixBytes = 64 * 1024;

    /// <summary>Whether any page is a strip taller than <paramref name="maxHeight"/>, read from page headers only.</summary>
    public static bool HasTallPage(ZipArchive zip, int maxHeight, CancellationToken ct) =>
        PageShapes(zip, ct).Any(p => TallPageSplitter.IsTall(p.Width, p.Height, maxHeight));

    /// <summary>
    /// Each page's index in reading order and its size, read from the first bytes of the page where the
    /// header is there and from the whole page otherwise. Pages no permitted loader reads are left out.
    /// </summary>
    public static IEnumerable<(int Index, int Width, int Height)> PageShapes(ZipArchive zip, CancellationToken ct)
    {
        var entries = zip.Entries
            .GroupBy(e => e.FullName, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        var names = CbzReader.PageNames(zip);
        for (var i = 0; i < names.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            if (!entries.TryGetValue(names[i], out var entry))
            {
                continue;
            }

            var prefix = new byte[(int)Math.Min(entry.Length, HeaderPrefixBytes)];
            using (var stream = entry.Open())
            {
                stream.ReadAtLeast(prefix, prefix.Length, throwOnEndOfStream: false);
            }

            var shape = ImageCodec.TryShape(prefix);
            if (shape is null && entry.Length > prefix.Length)
            {
                shape = ImageCodec.TryShape(ReadAll(entry, entry.Length));
            }

            if (shape is { } s)
            {
                yield return (i, s.Width, s.Height);
            }
        }
    }

    /// <summary>
    /// Whether a full conversion is worth running: encodes up to two pages over
    /// <see cref="PageConversion.MinPageBytes"/> from the middle third of the archive, one per source
    /// format when it mixes JPEG and PNG, and answers whether any came out smaller by the page floor.
    /// Anything it cannot read, or a file with nothing to probe, answers true so the full pass decides.
    /// When the settings split, any page that is a strip over the height answers true first.
    /// </summary>
    public static async Task<bool> ProbeAsync(string path, PageConversionSettings settings, CancellationToken ct)
    {
        try
        {
            List<(byte[] Bytes, string Extension)> picked;
            using (var zip = ZipFile.OpenRead(path))
            {
                if (settings.SplitHeight is { } height && HasTallPage(zip, height, ct))
                {
                    return true;
                }

                if (settings.SplitOnly)
                {
                    return false;
                }

                picked = MiddlePages(zip, 2, ct);
            }

            if (picked.Count == 0)
            {
                return true;
            }

            // Both pages encode at once, as the full pass would; the first one that clears the floor
            // answers, and an encode still waiting on the gate is called off.
            using var probes = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var pending = picked
                .Select(p => Task.Run(() => ImageWorkGate.RunConversionAsync(
                    () => PageConversion.Convert(p.Bytes, p.Extension, settings),
                    PageConversion.WeightOf(p.Bytes, p.Extension, settings), probes.Token), probes.Token))
                .ToList();
            try
            {
                while (pending.Count > 0)
                {
                    var done = await Task.WhenAny(pending);
                    pending.Remove(done);
                    if ((await done).Converted)
                    {
                        return true;
                    }
                }

                return false;
            }
            finally
            {
                await probes.CancelAsync();
                await Task.WhenAll(pending.Cast<Task>()).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            }
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
    private static bool SameOrder(List<string> order, Dictionary<string, IReadOnlyList<string>> renamed, string partial)
    {
        using var written = ZipFile.OpenRead(partial);
        var names = written.Entries.Select(e => e.FullName).ToList();
        if (names.Distinct(StringComparer.OrdinalIgnoreCase).Count() != names.Count)
        {
            return false;
        }

        var expected = order.SelectMany(n => renamed.TryGetValue(n, out var names) ? names : [n]);
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
