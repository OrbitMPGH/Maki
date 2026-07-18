using System.IO.Compression;

namespace Maki.Core.Download;

public static class CbzPackager
{
    /// <summary>
    /// Packages ordered page files plus ComicInfo.xml into a CBZ at <paramref name="targetPath"/>.
    /// Images are stored uncompressed — they are already compressed formats.
    /// Writes to a .partial file and moves into place so a crash never leaves a torn archive.
    /// </summary>
    public static void Package(IReadOnlyList<string> pageFiles, string comicInfoXml, string targetPath)
    {
        var partialPath = targetPath + ".partial";
        Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);

        try
        {
            using (var archive = ZipFile.Open(partialPath, ZipArchiveMode.Create))
            {
                var comicInfo = archive.CreateEntry("ComicInfo.xml", CompressionLevel.Optimal);
                using (var writer = new StreamWriter(comicInfo.Open()))
                {
                    writer.Write(comicInfoXml);
                }

                for (var i = 0; i < pageFiles.Count; i++)
                {
                    var extension = Path.GetExtension(pageFiles[i]);
                    archive.CreateEntryFromFile(
                        pageFiles[i],
                        $"{i + 1:000}{extension}",
                        CompressionLevel.NoCompression);
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
    }
}
