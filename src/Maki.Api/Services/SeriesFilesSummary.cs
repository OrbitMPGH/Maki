using System.Security.Cryptography;
using System.Text;
using Maki.Core.Entities;
using Maki.Core.Paths;
using Maki.Core.Reading;
using Maki.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace Maki.Api.Services;

/// <param name="Count">What <c>GET series/{id}/files</c> would list: every record plus untracked files on disk.</param>
/// <param name="UnlinkedOnDisk">Files on disk that list would not show as linked.</param>
public record SeriesFilesSummaryDto(int Count, int UnlinkedOnDisk);

/// <summary>
/// The series page's Files tab count and unlinked-files banner, without the full listing's
/// <c>FileInfo</c> per file, filename parsing and upgrade evaluation.
/// <para>
/// The folder walk is the slow part on a NAS, so its result is cached per series. The cache is keyed
/// on a fingerprint of the series' file records and folders rather than invalidated by each writer:
/// every import, rescan, relink, delete and chapter download changes those records, so the next
/// summary sees a new fingerprint and walks again, and nothing that writes files has to know this
/// cache exists. A file dropped into the folder by hand changes no record; the listing's lifetime
/// and <see cref="Remember"/> (the full listing refreshes it) cover that.
/// </para>
/// </summary>
public static class SeriesFilesSummary
{
    private static readonly TimeSpan ListingLifetime = TimeSpan.FromMinutes(10);

    private sealed record DiskListing(string Fingerprint, HashSet<string> Keys);

    private static object CacheKey(int seriesId) => (typeof(SeriesFilesSummary), seriesId);

    /// <summary>Null when the series is not visible or has no root folder.</summary>
    public static async Task<SeriesFilesSummaryDto?> ForAsync(
        MakiDbContext db, IMemoryCache cache, int seriesId, CancellationToken ct)
    {
        var row = await db.Series
            .AsNoTracking()
            .Where(s => s.Id == seriesId)
            .Select(s => new
            {
                Series = new Series { Id = s.Id, RootFolderId = s.RootFolderId, FolderName = s.FolderName },
                RootPath = s.RootFolder == null ? null : s.RootFolder.Path,
            })
            .FirstOrDefaultAsync(ct);
        if (row?.RootPath is null)
        {
            return null;
        }

        var records = await db.ChapterFiles
            .AsNoTracking()
            .Where(f => f.SeriesId == seriesId)
            .Select(f => new FileRecord(f.Id, f.RelativePath))
            .ToListAsync(ct);
        // Same rule as the full listing, which only counts a file as linked by a numbered chapter.
        var linked = (await db.Chapters
                .Where(c => c.SeriesId == seriesId && c.ChapterFileId != null && c.Number != null)
                .Select(c => c.ChapterFileId!.Value)
                .Distinct()
                .ToListAsync(ct))
            .ToHashSet();

        var folders = await SeriesFolders.ForAsync(db, row.Series, ct);
        var fingerprint = Fingerprint(row.RootPath, folders, records);
        HashSet<string> disk;
        if (cache.TryGetValue(CacheKey(seriesId), out DiskListing? cached) && cached!.Fingerprint == fingerprint)
        {
            disk = cached.Keys;
        }
        else
        {
            disk = List(row.RootPath, folders);
            cache.Set(CacheKey(seriesId), new DiskListing(fingerprint, disk), ListingLifetime);
        }

        var recordKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var unlinked = 0;
        foreach (var record in records)
        {
            var key = LibraryPaths.ComparisonKey(record.RelativePath);
            recordKeys.Add(key);
            if (disk.Contains(key) && !linked.Contains(record.Id))
            {
                unlinked++;
            }
        }

        var untracked = disk.Count(k => !recordKeys.Contains(k));
        return new SeriesFilesSummaryDto(records.Count + untracked, unlinked + untracked);
    }

    /// <summary>Stores a folder walk the full listing just did, so the summary agrees with what it showed.</summary>
    public static void Remember(IMemoryCache cache, int seriesId, string rootPath, IReadOnlyList<string> folders,
        IEnumerable<FileRecord> records, IEnumerable<string> diskKeys) =>
        cache.Set(
            CacheKey(seriesId),
            new DiskListing(Fingerprint(rootPath, folders, records), new HashSet<string>(diskKeys, StringComparer.OrdinalIgnoreCase)),
            ListingLifetime);

    public record FileRecord(int Id, string RelativePath);

    /// <summary>Comparison keys of every comic file under the series' folders, the way the full listing keys them.</summary>
    private static HashSet<string> List(string rootPath, IReadOnlyList<string> folders)
    {
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var folder in folders)
        {
            var seriesDir = Path.Combine(rootPath, folder);
            if (!Directory.Exists(seriesDir))
            {
                continue;
            }

            foreach (var f in Directory.EnumerateFiles(seriesDir, "*", SearchOption.AllDirectories).Where(ComicFile.IsComic))
            {
                keys.Add(LibraryPaths.ComparisonKey(Path.Combine(folder, Path.GetRelativePath(seriesDir, f))));
            }
        }

        return keys;
    }

    private static string Fingerprint(string rootPath, IReadOnlyList<string> folders, IEnumerable<FileRecord> records)
    {
        var text = new StringBuilder(rootPath);
        foreach (var folder in folders)
        {
            text.Append('\n').Append(folder);
        }

        foreach (var record in records.OrderBy(r => r.Id))
        {
            text.Append('\n').Append(record.Id).Append('\t').Append(record.RelativePath);
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())));
    }
}
