using System.Globalization;
using System.IO.Compression;
using Mangarr.Core.Entities;
using Mangarr.Core.Parsing;

namespace Mangarr.Core.ComicInfo;

/// <summary>
/// Rewrites the ComicInfo.xml inside CBZ files Mangarr adopts (library imports,
/// completed torrents, rescans) so Kavita groups them with Mangarr's own downloads:
/// series-level fields are standardized to Mangarr's metadata (English title, summary,
/// authors, genres), file-level fields (chapter/volume number, title, dates) are kept
/// or filled from the linked chapter / parsed file name. Fields Mangarr has no opinion
/// on (publisher, translator, scan info, ...) are preserved as-is.
/// </summary>
public static class ComicInfoUpdater
{
    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".webp", ".gif", ".avif", ".bmp"
    };

    /// <summary>
    /// Standardizes the ComicInfo.xml of <paramref name="cbzPath"/> in place (atomic rewrite).
    /// Returns true when the file was rewritten, false when it was already up to date.
    /// </summary>
    public static bool UpdateFile(string cbzPath, Series series, ParsedReleaseFile parsed, Chapter? chapter)
    {
        string newXml;
        using (var source = ZipFile.OpenRead(cbzPath))
        {
            var existingEntry = FindComicInfoEntry(source);
            ComicInfo? info = null;
            string? existingXml = null;
            if (existingEntry != null)
            {
                using var reader = new StreamReader(existingEntry.Open());
                existingXml = reader.ReadToEnd();
                using var xmlStream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(existingXml));
                info = ComicInfoBuilder.Deserialize(xmlStream);
            }

            info ??= new ComicInfo();
            var pageCount = source.Entries.Count(e => ImageExtensions.Contains(Path.GetExtension(e.Name)));
            Standardize(info, series, parsed, chapter, pageCount);

            newXml = ComicInfoBuilder.Serialize(info);
            if (existingXml != null && XmlEquals(existingXml, newXml))
            {
                return false;
            }
        }

        RewriteArchive(cbzPath, newXml);
        return true;
    }

    private static void Standardize(ComicInfo info, Series series, ParsedReleaseFile parsed, Chapter? chapter, int pageCount)
    {
        // Series-level fields: always Mangarr's view, so imports and downloads agree.
        info.Series = series.Title;
        info.Summary = series.Overview;
        info.Writer = series.AuthorStory ?? info.Writer;
        info.Penciller = series.AuthorArt ?? info.Penciller;
        info.Genre = series.Genres.Count > 0 ? string.Join(", ", series.Genres) : info.Genre;
        info.Tags = series.Tags.Count > 0 ? string.Join(", ", series.Tags) : info.Tags;
        info.Web = SeriesWebLinks.Joined(series) ?? info.Web;
        info.CountSerialized = series.Status == SeriesStatus.Completed
            ? series.TotalChapters?.ToString(CultureInfo.InvariantCulture)
            : null;
        info.Manga = "YesAndRightToLeft";

        // File-level fields: prefer the linked chapter, then the parsed file name,
        // then whatever the file already declared.
        var number = chapter?.Number ?? parsed.Number;
        if (number is decimal n)
        {
            info.Number = n.ToString("0.###", CultureInfo.InvariantCulture);
        }

        var volume = chapter?.Volume ?? parsed.Volume;
        if (volume is int v)
        {
            info.VolumeSerialized = v.ToString(CultureInfo.InvariantCulture);
        }

        if (string.IsNullOrWhiteSpace(info.Title))
        {
            info.Title = !string.IsNullOrWhiteSpace(chapter?.Title)
                ? chapter.Title
                : number is decimal num
                    ? $"Chapter {num.ToString("0.###", CultureInfo.InvariantCulture)}"
                    : parsed.Volume is int vol
                        ? $"Volume {vol}"
                        : null;
        }

        if (chapter?.ReleaseDate is DateTime released)
        {
            info.Year = released.Year.ToString(CultureInfo.InvariantCulture);
            info.Month = released.Month.ToString(CultureInfo.InvariantCulture);
            info.Day = released.Day.ToString(CultureInfo.InvariantCulture);
        }

        info.LanguageISO = chapter?.Language ?? info.LanguageISO;
        info.PageCount = pageCount > 0 ? pageCount.ToString(CultureInfo.InvariantCulture) : info.PageCount;
    }

    private static ZipArchiveEntry? FindComicInfoEntry(ZipArchive archive)
        => archive.Entries
            .Where(e => string.Equals(e.Name, "ComicInfo.xml", StringComparison.OrdinalIgnoreCase))
            .OrderBy(e => e.FullName.Length) // prefer the root entry over nested copies
            .FirstOrDefault();

    /// <summary>
    /// Copies every non-ComicInfo entry into a new archive with the new ComicInfo.xml at
    /// the root, then swaps it into place — a crash never leaves a torn file behind.
    /// </summary>
    private static void RewriteArchive(string cbzPath, string comicInfoXml)
    {
        var partialPath = cbzPath + ".partial";
        try
        {
            using (var source = ZipFile.OpenRead(cbzPath))
            using (var target = ZipFile.Open(partialPath, ZipArchiveMode.Create))
            {
                var comicInfo = target.CreateEntry("ComicInfo.xml", CompressionLevel.Optimal);
                using (var writer = new StreamWriter(comicInfo.Open()))
                {
                    writer.Write(comicInfoXml);
                }

                foreach (var entry in source.Entries)
                {
                    if (entry.Name.Length == 0 || // directory entries
                        string.Equals(entry.Name, "ComicInfo.xml", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    var compression = ImageExtensions.Contains(Path.GetExtension(entry.Name))
                        ? CompressionLevel.NoCompression // already-compressed formats
                        : CompressionLevel.Optimal;
                    var copied = target.CreateEntry(entry.FullName, compression);
                    copied.LastWriteTime = entry.LastWriteTime;
                    using var input = entry.Open();
                    using var output = copied.Open();
                    input.CopyTo(output);
                }
            }

            File.Move(partialPath, cbzPath, overwrite: true);
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

    private static bool XmlEquals(string a, string b)
        => string.Equals(Normalize(a), Normalize(b), StringComparison.Ordinal);

    private static string Normalize(string xml)
        => xml.Replace("\r\n", "\n").TrimStart('﻿').Trim();
}
