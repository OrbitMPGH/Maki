namespace Maki.Core.Download;

/// <summary>
/// Records which source chapter a page working directory holds. <see cref="PageDownloader"/> keeps
/// any page file already there, so a directory reused for a different mapping or source chapter id
/// (the 404 fallback, a retry that resolves elsewhere) would package pages from two releases.
/// </summary>
public static class PageCacheManifest
{
    public const string FileName = ".maki-pages";

    public static string Key(int mappingId, string sourceChapterId, int pageCount) =>
        $"{mappingId}|{sourceChapterId}|{pageCount}";

    /// <summary>
    /// Keeps the directory when its manifest matches <paramref name="key"/>, otherwise empties it,
    /// then writes the manifest. A directory with files but no manifest has unknown provenance and is
    /// emptied too. Returns true when anything was deleted.
    /// </summary>
    public static bool Prepare(string workingDir, string key)
    {
        var manifest = Path.Combine(workingDir, FileName);
        var wiped = false;
        if (Directory.Exists(workingDir))
        {
            var existing = File.Exists(manifest) ? File.ReadAllText(manifest) : null;
            if (existing == key)
            {
                return false;
            }

            if (existing is not null || Directory.EnumerateFileSystemEntries(workingDir).Any())
            {
                Directory.Delete(workingDir, recursive: true);
                wiped = true;
            }
        }

        Directory.CreateDirectory(workingDir);
        File.WriteAllText(manifest, key);
        return wiped;
    }
}
