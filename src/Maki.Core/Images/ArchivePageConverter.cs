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

    /// <summary>A page did not decode or an entry failed its checksum; the file is the health scan's business.</summary>
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
                foreach (var entry in source.Entries)
                {
                    ct.ThrowIfCancellationRequested();
                    if (entry.FullName.EndsWith('/'))
                    {
                        target.CreateEntry(entry.FullName).LastWriteTime = entry.LastWriteTime;
                        continue;
                    }

                    var bytes = ReadAll(entry);
                    var name = entry.FullName;
                    var level = CompressionLevel.Optimal;
                    if (pages.Contains(name))
                    {
                        var extension = Path.GetExtension(name);
                        var page = await ImageWorkGate.RunAsync(
                            () => Task.FromResult(PageConversion.Convert(bytes, extension, settings)), ct);
                        sourceFormats.Add(page.SourceFormat);
                        if (page.Converted)
                        {
                            converted++;
                            name = name[..^extension.Length] + page.Extension;
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
    }

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
    /// its header declares.
    /// </summary>
    private static byte[] ReadAll(ZipArchiveEntry entry)
    {
        if (entry.Length > int.MaxValue)
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
