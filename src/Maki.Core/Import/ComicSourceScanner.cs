using System.IO.Compression;
using Maki.Core.Reading;
using SharpCompress.Archives;

namespace Maki.Core.Import;

/// <summary>
/// Finds the comics in a completed download or an import folder. A release is not always a tidy
/// set of CBZs: plain zip and RAR are both common, an older set is often just the page images in a
/// folder per volume, and a .cbr holding one .rar per chapter happens too. Nothing here extracts
/// anything — it lists entry names, so scanning a whole root folder stays cheap and the import
/// plan can be shown before a single byte is written.
/// </summary>
public static class ComicSourceScanner
{
    /// <summary>Zip entries are separated with "/" by spec; a RAR written on Windows is not.</summary>
    private const char Backslash = (char)92;

    /// <summary>Already a zip container, whatever the extension says.</summary>
    private static readonly HashSet<string> ZipExtensions =
        new(StringComparer.OrdinalIgnoreCase) { ".cbz", ".zip" };

    /// <summary>Readable, but has to be written back out as a zip.</summary>
    private static readonly HashSet<string> RepackExtensions =
        new(StringComparer.OrdinalIgnoreCase) { ".cbr", ".rar", ".cb7", ".7z", ".cbt", ".tar" };

    public static bool IsArchive(string path)
    {
        var extension = System.IO.Path.GetExtension(path);
        return ZipExtensions.Contains(extension) || RepackExtensions.Contains(extension);
    }

    /// <summary>
    /// Every comic under <paramref name="contentPath"/>, one per produced file name.
    /// <para>
    /// The walk is recursive but the import is flat: everything lands in the series folder under
    /// its own name, so two sources that would produce the same name can never both be imported —
    /// the second would find the first already there and link the same bytes twice. Dropping it
    /// here is what keeps the plan honest about that.
    /// </para>
    /// </summary>
    public static IReadOnlyList<ComicSource> Scan(string contentPath)
    {
        List<ComicSource> found = [];
        if (File.Exists(contentPath))
        {
            found.AddRange(FromArchive(contentPath));
        }
        else if (Directory.Exists(contentPath))
        {
            foreach (var file in Directory
                         .GetFiles(contentPath, "*", SearchOption.AllDirectories)
                         .Where(IsArchive)
                         .OrderBy(f => f, StringComparer.Ordinal))
            {
                found.AddRange(FromArchive(file));
            }

            found.AddRange(LooseImageFolders(contentPath));
        }

        return found
            .GroupBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
            // A folder holding both "X.cbr" and the "X.cbz" a previous import made of it wants the
            // finished one, whichever way round the two sort.
            .Select(g => g.OrderBy(s => s.Kind == ComicSourceKind.Cbz ? 0 : 1).First())
            .ToList();
    }

    /// <summary>
    /// What the path actually held, for an error message when none of it was a comic. "No comics
    /// found" on its own reads exactly like a download that arrived empty.
    /// </summary>
    public static string Describe(string contentPath)
    {
        string[] files = File.Exists(contentPath) ? [contentPath]
            : Directory.Exists(contentPath) ? Directory.GetFiles(contentPath, "*", SearchOption.AllDirectories)
            : [];

        var census = files
            .Select(f => System.IO.Path.GetExtension(f).ToLowerInvariant())
            .Where(e => e.Length > 0)
            .GroupBy(e => e, StringComparer.Ordinal)
            .OrderByDescending(g => g.Count())
            .ThenBy(g => g.Key, StringComparer.Ordinal)
            .Take(3)
            .Select(g => $"{g.Count()} {g.Key}")
            .ToList();

        return census.Count == 0 ? "it is empty" : $"found {string.Join(", ", census)}";
    }

    private static IEnumerable<ComicSource> FromArchive(string path)
    {
        if (!IsArchive(path))
        {
            return [];
        }

        var entries = Entries(path);
        var pages = entries
            .Where(e => CbzReader.IsImage(e.Name))
            .Select(e => e.Name)
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (pages.Count > 0)
        {
            var kind = System.IO.Path.GetExtension(path).Equals(".cbz", StringComparison.OrdinalIgnoreCase)
                ? ComicSourceKind.Cbz
                : ZipExtensions.Contains(System.IO.Path.GetExtension(path))
                    ? ComicSourceKind.Zip
                    : ComicSourceKind.Repack;

            return [new ComicSource(CbzName(path), kind, path, new FileInfo(path).Length, pages)];
        }

        // No pages of its own: an archive of archives, one per chapter. Each inner one is its own
        // comic, and its entries stay unlisted because reaching them means extracting it.
        return entries
            .Where(e => IsArchive(e.Name))
            .Select(e => new ComicSource(
                CbzName(e.Name), ComicSourceKind.Repack, path, e.Size, [], e.Name))
            .ToList();
    }

    private static IEnumerable<ComicSource> LooseImageFolders(string root) =>
        Directory
            .GetDirectories(root, "*", SearchOption.AllDirectories)
            .Append(root)
            .OrderBy(d => d, StringComparer.Ordinal)
            .Select(directory => new
            {
                Directory = directory,
                Files = Directory.GetFiles(directory).OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList()
            })
            // A folder holding an archive is that archive's, not a loose set: the images beside it
            // are as likely to be a cover or a sample as they are to be a chapter nobody packed.
            .Where(x => x.Files.Any(f => CbzReader.IsImage(f)) && !x.Files.Any(IsArchive))
            .Select(x => new ComicSource(
                new DirectoryInfo(x.Directory).Name + ".cbz",
                ComicSourceKind.LooseImages,
                x.Directory,
                x.Files.Where(CbzReader.IsImage).Sum(f => new FileInfo(f).Length),
                x.Files.Where(CbzReader.IsImage).Select(System.IO.Path.GetFileName).ToList()!));

    /// <summary>Entry name and uncompressed size for every file in an archive; empty if unreadable.</summary>
    internal static IReadOnlyList<(string Name, long Size)> Entries(string path)
    {
        try
        {
            if (ZipExtensions.Contains(System.IO.Path.GetExtension(path)))
            {
                using var zip = ZipFile.OpenRead(path);
                return zip.Entries
                    .Where(e => e.Name.Length > 0)
                    .Select(e => (e.FullName, e.Length))
                    .ToList();
            }

            using var archive = ArchiveFactory.OpenArchive(path);
            return archive.Entries
                .Where(e => !e.IsDirectory && e.Key is { Length: > 0 })
                .Select(e => (e.Key!, e.Size))
                .ToList();
        }
        catch
        {
            return [];
        }
    }

    private static string CbzName(string path) =>
        System.IO.Path.ChangeExtension(System.IO.Path.GetFileName(path.Replace(Backslash, '/')), ".cbz");
}
