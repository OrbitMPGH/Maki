using Maki.Core.Configuration;
using Maki.Core.Entities;
using Maki.Core.Paths;
using Maki.Core.Reading;
using Maki.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Net.Http.Headers;

namespace Maki.Api.Services;

/// <summary>
/// Keeps <see cref="KoreaderDocument"/> rows for chapter files, so the document id KOReader's
/// progress sync sends resolves to a file. Rows are added and never rewritten: a copy on the device
/// keeps the digest of the bytes it was served, even after Maki rewrites the file.
/// </summary>
public class KoreaderDocumentIndexer(MakiDbContext db, ILogger<KoreaderDocumentIndexer> logger)
{
    public const int BatchSize = 200;

    private static readonly SemaphoreSlim Gate = new(1);

    /// <summary>
    /// Records the digests of a file an OPDS client is downloading, under the name it was served as.
    /// KOReader stores that name only when "Use server filenames" is on, but the bytes always match.
    /// </summary>
    public async Task RecordServedAsync(int chapterFileId, string path, string servedName, CancellationToken ct)
    {
        var known = await KnownAsync([chapterFileId], ct);
        var rows = Missing(chapterFileId, path, [servedName, HeaderFileName(servedName), Path.GetFileName(path)],
            known.GetValueOrDefault(chapterFileId) ?? new Known());
        await SaveAsync(rows, ct);
    }

    /// <summary>
    /// Adds digests for every file whose current bytes have none. Skipped entirely while nobody has
    /// the OPDS catalogue on, since its token is what the sync signs in with.
    /// </summary>
    /// <returns>false when a pass is already running.</returns>
    public async Task<bool> RunAsync(CancellationToken ct)
    {
        if (!await Gate.WaitAsync(0, ct))
        {
            return false;
        }

        try
        {
            var anyone = await db.UserSettings.IgnoreQueryFilters()
                .AnyAsync(s => s.Key == SettingKeys.OpdsEnabled && s.Value == "true", ct);
            if (!anyone)
            {
                return true;
            }

            var added = 0;
            var lastId = 0;
            while (true)
            {
                var batch = await db.ChapterFiles.IgnoreQueryFilters().AsNoTracking()
                    .Where(f => f.Id > lastId)
                    .OrderBy(f => f.Id)
                    .Take(BatchSize)
                    .Select(f => new
                    {
                        f.Id,
                        f.RelativePath,
                        Root = db.Series.IgnoreQueryFilters()
                            .Where(s => s.Id == f.SeriesId)
                            .Select(s => s.RootFolder!.Path)
                            .FirstOrDefault()
                    })
                    .ToListAsync(ct);
                if (batch.Count == 0)
                {
                    break;
                }

                lastId = batch[^1].Id;
                var known = await KnownAsync(batch.Select(f => f.Id).ToList(), ct);
                var rows = new List<KoreaderDocument>();
                foreach (var file in batch)
                {
                    ct.ThrowIfCancellationRequested();
                    if (string.IsNullOrEmpty(file.Root) || LibraryPaths.Resolve(file.Root, file.RelativePath) is not { } path)
                    {
                        continue;
                    }

                    rows.AddRange(Missing(file.Id, path, [Path.GetFileName(path)],
                        known.GetValueOrDefault(file.Id) ?? new Known()));
                }

                added += await SaveAsync(rows, ct);
            }

            if (added > 0)
            {
                logger.LogInformation("Indexed {Count} KOReader document id(s)", added);
            }

            return true;
        }
        finally
        {
            Gate.Release();
        }
    }

    private sealed class Known
    {
        public HashSet<(KoreaderDigestKind, string)> Digests { get; } = [];
        public HashSet<string> Versions { get; } = new(StringComparer.Ordinal);
    }

    private async Task<Dictionary<int, Known>> KnownAsync(IReadOnlyCollection<int> fileIds, CancellationToken ct)
    {
        var rows = await db.KoreaderDocuments.IgnoreQueryFilters().AsNoTracking()
            .Where(d => fileIds.Contains(d.ChapterFileId))
            .Select(d => new { d.ChapterFileId, d.Kind, d.Digest, d.FileVersion })
            .ToListAsync(ct);

        var known = new Dictionary<int, Known>();
        foreach (var row in rows)
        {
            if (!known.TryGetValue(row.ChapterFileId, out var entry))
            {
                known[row.ChapterFileId] = entry = new Known();
            }

            entry.Digests.Add((row.Kind, row.Digest));
            if (row.FileVersion is { } version)
            {
                entry.Versions.Add(version);
            }
        }

        return known;
    }

    private List<KoreaderDocument> Missing(int fileId, string path, IEnumerable<string> names, Known known)
    {
        var now = DateTime.UtcNow;
        var rows = new List<KoreaderDocument>();
        void Add(KoreaderDigestKind kind, string digest, string? version)
        {
            if (known.Digests.Add((kind, digest)))
            {
                rows.Add(new KoreaderDocument
                {
                    ChapterFileId = fileId, Kind = kind, Digest = digest, FileVersion = version, CreatedAt = now
                });
            }
        }

        try
        {
            var info = new FileInfo(path);
            if (!info.Exists)
            {
                return rows;
            }

            var version = ReaderService.ArchiveVersionOf(info.Length, info.LastWriteTimeUtc.Ticks);
            if (known.Versions.Add(version))
            {
                Add(KoreaderDigestKind.Binary, KoreaderDigest.PartialMd5(path), version);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            logger.LogDebug(e, "Could not hash chapter file {ChapterFileId} for KOReader", fileId);
        }

        foreach (var name in KoreaderDigest.FileNameVariants(names))
        {
            Add(KoreaderDigestKind.FileName, KoreaderDigest.FileName(name), null);
        }

        return rows;
    }

    /// <summary>
    /// The plain <c>filename=</c> parameter ASP.NET writes next to <c>filename*</c>, with anything
    /// outside ASCII replaced. KOReader reads that one.
    /// </summary>
    private static string HeaderFileName(string name)
    {
        var disposition = new ContentDispositionHeaderValue("attachment");
        disposition.SetHttpFileName(name);
        return HeaderUtilities.RemoveQuotes(disposition.FileName).ToString();
    }

    private async Task<int> SaveAsync(List<KoreaderDocument> rows, CancellationToken ct)
    {
        if (rows.Count == 0)
        {
            return 0;
        }

        db.KoreaderDocuments.AddRange(rows);
        try
        {
            await db.SaveChangesAsync(ct);
            return rows.Count;
        }
        catch (DbUpdateException e) when (DbErrors.IsUniqueViolation(e))
        {
            // A download and a pass recorded the same file at once; the next pass fills any gap.
            logger.LogDebug("KOReader document ids raced another writer; left for the next pass");
            return 0;
        }
        finally
        {
            foreach (var row in rows)
            {
                db.Entry(row).State = EntityState.Detached;
            }
        }
    }
}
