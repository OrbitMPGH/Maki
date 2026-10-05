using System.IO.Compression;
using Maki.Core.Reading;
using Maki.Core.Storage;
using SharpCompress.Archives;
using SharpCompress.Common;

namespace Maki.Core.Import;

/// <summary>
/// Turns a <see cref="ComicSource"/> into a CBZ in the library. Nothing is ever removed from where
/// it came: a torrent keeps seeding, and an import folder is the user's own copy.
/// </summary>
public static class ComicSourceConverter
{
    /// <summary>Zip entries are separated with "/" by spec; a RAR written on Windows is not.</summary>
    private const char Backslash = (char)92;

    /// <summary>
    /// Writes <paramref name="source"/> to <paramref name="targetPath"/>, hardlinking instead of
    /// copying where the bytes can be shared. Returns how the file got there.
    /// </summary>
    public static FilePlacement Materialize(ComicSource source, string targetPath, bool useHardlinks = true)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);

        // A zip already is a CBZ container, so the file itself is what goes in the library and the
        // only thing that changes is the name it goes in under.
        if (source.IsReadyToPlace)
        {
            return FileLinker.Place(source.Path, targetPath, useHardlinks);
        }

        if (source.Entry is null && IsSameFile(source.Path, targetPath))
        {
            RepackOverOwnName(source, targetPath);
            return FilePlacement.Copied;
        }

        Pack(source, targetPath);
        return FilePlacement.Copied;
    }

    /// <summary>
    /// Rebuilds a 7z, RAR or tar shipped under a ".cbz" name into a real CBZ under that name.
    /// Returns false when the file already is a zip, or is nothing this can name. Every reader in
    /// Maki opens a CBZ as a zip, so such a file links and then reads as having no pages.
    /// </summary>
    public static bool RepackMislabelled(string cbzPath)
    {
        if (ComicExtensionOf(cbzPath) is null)
        {
            return false;
        }

        var source = new ComicSource(
            Path.GetFileName(cbzPath), ComicSourceKind.Repack, cbzPath, new FileInfo(cbzPath).Length, []);
        RepackOverOwnName(source, cbzPath);
        return true;
    }

    /// <summary>
    /// The original keeps its bytes under the extension that says what it is (".cb7", ".cbr",
    /// ".cbt"), and its name goes to the CBZ built from it. Put back if the build fails.
    /// </summary>
    private static void RepackOverOwnName(ComicSource source, string targetPath)
    {
        var extension = ComicExtensionOf(source.Path)
            ?? throw new InvalidDataException($"{Path.GetFileName(source.Path)} is not an archive that can be repacked");
        var aside = Path.ChangeExtension(source.Path, extension);
        if (File.Exists(aside))
        {
            throw new IOException($"{Path.GetFileName(aside)} already exists");
        }

        File.Move(source.Path, aside);
        try
        {
            Pack(source with { Path = aside }, targetPath);
        }
        catch
        {
            if (!File.Exists(source.Path))
            {
                File.Move(aside, source.Path);
            }

            throw;
        }
    }

    /// <summary>
    /// Off the leading bytes where there are any; an old-style tar has none, so SharpCompress's
    /// own autodetect, the one the scanner read it with, decides the rest.
    /// </summary>
    private static string? ComicExtensionOf(string path)
    {
        var format = ArchiveSignature.Detect(path);
        if (format != ArchiveSignature.Format.Unknown)
        {
            return ArchiveSignature.ComicExtension(format);
        }

        try
        {
            using var archive = ArchiveFactory.OpenArchive(path);
            return archive.Type switch
            {
                ArchiveType.SevenZip => ".cb7",
                ArchiveType.Rar => ".cbr",
                ArchiveType.Tar => ".cbt",
                _ => null
            };
        }
        catch
        {
            return null;
        }
    }

    public static bool IsSameFile(string a, string b) =>
        string.Equals(Path.GetFullPath(a), Path.GetFullPath(b),
            OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal);

    /// <summary>
    /// Page names are carried over exactly, never renumbered: a compilation's chapter markers live
    /// in them ("... - c049 (v05) - p113 ...jpg") and renaming the pages would throw away the only
    /// record of which chapters the file holds. Images are stored uncompressed, as Maki's own
    /// downloads are — they are already compressed formats.
    /// </summary>
    private static void Pack(ComicSource source, string targetPath)
    {
        var partialPath = targetPath + ".partial";
        string? scratch = null;

        try
        {
            using (var archive = ZipFile.Open(partialPath, ZipArchiveMode.Create))
            {
                if (source.Kind == ComicSourceKind.LooseImages)
                {
                    foreach (var file in Directory
                                 .GetFiles(source.Path)
                                 .Where(CbzReader.IsImage)
                                 .OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
                    {
                        archive.CreateEntryFromFile(
                            file, Path.GetFileName(file), CompressionLevel.NoCompression);
                    }
                }
                else
                {
                    // A nested archive has to come out to its own file first: SharpCompress reads
                    // from a stream, and the RAR reader needs one it can seek.
                    var readFrom = source.Path;
                    if (source.Entry is not null)
                    {
                        scratch = ExtractToTemp(source.Path, source.Entry);
                        readFrom = scratch;
                    }

                    CopyImages(readFrom, archive);
                }
            }

            File.Move(partialPath, targetPath, overwrite: true);
        }
        catch
        {
            if (File.Exists(partialPath))
            {
                File.Delete(partialPath);
            }

            throw;
        }
        finally
        {
            if (scratch is not null && File.Exists(scratch))
            {
                File.Delete(scratch);
            }
        }
    }

    private static void CopyImages(string archivePath, ZipArchive target)
    {
        using var source = ArchiveFactory.OpenArchive(archivePath);

        // A solid archive shares one compression window across its entries, so reaching the tenth
        // means decompressing the nine before it. Picking entries out of one by hand is quadratic
        // at best and unsupported at worst; the forward-only reader walks it once instead. Entries
        // then arrive in stored order rather than sorted, which the reader sorts by name anyway.
        if (source.IsSolid)
        {
            using var reader = source.ExtractAllEntries();
            while (reader.MoveToNextEntry())
            {
                if (reader.Entry.IsDirectory || reader.Entry.Key is not { Length: > 0 } solidKey ||
                    !CbzReader.IsImage(solidKey))
                {
                    continue;
                }

                using var writingSolid = Entry(target, solidKey);
                using var readingSolid = reader.OpenEntryStream();
                readingSolid.CopyTo(writingSolid);
            }

            return;
        }

        foreach (var entry in source.Entries
                     .Where(e => !e.IsDirectory && e.Key is { Length: > 0 } key && CbzReader.IsImage(key))
                     .OrderBy(e => e.Key, StringComparer.OrdinalIgnoreCase))
        {
            using var writing = Entry(target, entry.Key!);
            using var reading = entry.OpenEntryStream();
            reading.CopyTo(writing);
        }
    }

    private static Stream Entry(ZipArchive target, string key) =>
        target.CreateEntry(key.Replace(Backslash, '/'), CompressionLevel.NoCompression).Open();

    private static string ExtractToTemp(string archivePath, string entryName)
    {
        var scratch = Path.Combine(
            Path.GetTempPath(), $"maki-import-{Guid.NewGuid():N}{Path.GetExtension(entryName)}");

        using var source = ArchiveFactory.OpenArchive(archivePath);
        var entry = source.Entries.FirstOrDefault(
            e => string.Equals(e.Key, entryName, StringComparison.Ordinal))
            ?? throw new FileNotFoundException($"'{entryName}' is no longer in {Path.GetFileName(archivePath)}");

        using (var writing = File.Create(scratch))
        using (var reading = entry.OpenEntryStream())
        {
            reading.CopyTo(writing);
        }

        return scratch;
    }
}
