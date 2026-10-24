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

    // The highest chapter file id the last complete pass went through. A file past it is one no pass
    // has seen; anything at or below it that still has no ids is a file the pass could not read.
    private static int _passedThrough;

    /// <summary>
    /// Records the digests of a file an OPDS client is downloading, under the name it was served as.
    /// KOReader stores that name only when "Use server filenames" is on, but the bytes always match.
    /// </summary>
    public async Task RecordServedAsync(int chapterFileId, string path, string servedName, CancellationToken ct)
    {
        var known = await KnownAsync([chapterFileId], ct);
        var found = new Found();
        Missing(chapterFileId, path, [servedName, HeaderFileName(servedName), Path.GetFileName(path)],
            known.GetValueOrDefault(chapterFileId) ?? new Known(), found);
        await SaveAsync(found, ct);
    }

    /// <summary>
    /// Whether a pass run now would find a file no pass has seen, which is the only thing running one
    /// early can fix. A miss is usually a book from somewhere else, and a pass stats every file, so
    /// misses must not set one off on their own. Rewritten files are left to the timer.
    /// </summary>
    public Task<bool> AnyUnindexedAsync(CancellationToken ct)
    {
        var passedThrough = Volatile.Read(ref _passedThrough);
        return db.ChapterFiles.IgnoreQueryFilters()
            .AnyAsync(f => f.Id > passedThrough &&
                           !db.KoreaderDocuments.IgnoreQueryFilters().Any(d => d.ChapterFileId == f.Id), ct);
    }

    /// <summary>
    /// Adds digests for every file whose current bytes have none. Skipped entirely while nobody has
    /// the OPDS catalogue on, since its token is what the sync signs in with.
    /// </summary>
    /// <returns>How many document ids were added, or -1 when a pass is already running.</returns>
    public async Task<int> RunAsync(CancellationToken ct)
    {
        if (!await Gate.WaitAsync(0, ct))
        {
            return -1;
        }

        try
        {
            var anyone = await db.UserSettings.IgnoreQueryFilters()
                .AnyAsync(s => s.Key == SettingKeys.OpdsEnabled && s.Value == "true", ct);
            if (!anyone)
            {
                return 0;
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
                var found = new Found();
                foreach (var file in batch)
                {
                    ct.ThrowIfCancellationRequested();
                    if (string.IsNullOrEmpty(file.Root) || LibraryPaths.Resolve(file.Root, file.RelativePath) is not { } path)
                    {
                        continue;
                    }

                    Missing(file.Id, path, [Path.GetFileName(path)], known.GetValueOrDefault(file.Id) ?? new Known(),
                        found);
                }

                added += await SaveAsync(found, ct);
            }

            Volatile.Write(ref _passedThrough, lastId);
            if (added > 0)
            {
                logger.LogInformation("Indexed {Count} KOReader document id(s)", added);
            }

            return added;
        }
        finally
        {
            Gate.Release();
        }
    }

    /// <summary>A document id kept aside while its file has no row, so the row that takes the same bytes back gets it.</summary>
    public sealed record Carried(KoreaderDigestKind Kind, string Digest, string? FileVersion);

    /// <summary>
    /// The document ids a file row holds. A row's ids go with it when it is removed, so a flow that
    /// removes a row and later gives the same bytes a new one (an upgrade and its revert) keeps these.
    /// </summary>
    public static Task<List<Carried>> CarryAsync(MakiDbContext db, int fileId, CancellationToken ct) =>
        db.KoreaderDocuments.IgnoreQueryFilters().AsNoTracking()
            .Where(d => d.ChapterFileId == fileId)
            .OrderBy(d => d.Id)
            .Select(d => new Carried(d.Kind, d.Digest, d.FileVersion))
            .ToListAsync(ct);

    /// <summary>
    /// Gives carried ids to the row that now holds those bytes, in a save of their own: an index pass
    /// that got to the row first may have added some, and losing the race must not undo the caller's work.
    /// </summary>
    public static async Task RestoreAsync(MakiDbContext db, int fileId, IReadOnlyCollection<Carried> carried,
        ILogger logger, CancellationToken ct)
    {
        if (carried.Count == 0)
        {
            return;
        }

        var taken = (await db.KoreaderDocuments.IgnoreQueryFilters()
                .Where(d => d.ChapterFileId == fileId)
                .Select(d => new { d.Kind, d.Digest })
                .ToListAsync(ct))
            .Select(d => (d.Kind, d.Digest))
            .ToHashSet();
        var now = DateTime.UtcNow;
        var rows = carried
            .Where(c => taken.Add((c.Kind, c.Digest)))
            .Select(c => new KoreaderDocument
            {
                ChapterFileId = fileId, Kind = c.Kind, Digest = c.Digest, FileVersion = c.FileVersion, CreatedAt = now
            })
            .ToList();
        if (rows.Count == 0)
        {
            return;
        }

        db.KoreaderDocuments.AddRange(rows);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException e) when (DbErrors.IsUniqueViolation(e))
        {
            logger.LogDebug("KOReader document ids for chapter file {ChapterFileId} raced an index pass", fileId);
        }
        finally
        {
            foreach (var row in rows)
            {
                db.Entry(row).State = EntityState.Detached;
            }
        }
    }

    /// <summary>
    /// Moves the ids of rows about to be removed onto the row replacing them, for a replacement whose
    /// pages are the old file's own, so a copy of the old bytes on a device still lands in the right place.
    /// </summary>
    public static async Task MoveAsync(MakiDbContext db, IReadOnlyCollection<int> fromFileIds, int toFileId,
        CancellationToken ct)
    {
        var taken = (await db.KoreaderDocuments.IgnoreQueryFilters()
                .Where(d => d.ChapterFileId == toFileId)
                .Select(d => new { d.Kind, d.Digest })
                .ToListAsync(ct))
            .Select(d => (d.Kind, d.Digest))
            .ToHashSet();
        var ids = (await db.KoreaderDocuments.IgnoreQueryFilters()
                .Where(d => fromFileIds.Contains(d.ChapterFileId))
                .OrderBy(d => d.Id)
                .Select(d => new { d.Id, d.Kind, d.Digest })
                .ToListAsync(ct))
            .Where(d => taken.Add((d.Kind, d.Digest)))
            .Select(d => d.Id)
            .ToList();
        if (ids.Count > 0)
        {
            await db.KoreaderDocuments.IgnoreQueryFilters()
                .Where(d => ids.Contains(d.Id))
                .ExecuteUpdateAsync(u => u.SetProperty(d => d.ChapterFileId, toFileId), ct);
        }
    }

    private sealed class Known
    {
        /// <summary>Each digest the file has, with its row id (0 for one only just found).</summary>
        public Dictionary<(KoreaderDigestKind, string), int> Digests { get; } = [];
        public HashSet<string> Versions { get; } = new(StringComparer.Ordinal);
    }

    private sealed class Found
    {
        public List<KoreaderDocument> Rows { get; } = [];

        /// <summary>
        /// Binary rows whose digest the file's current version hashed to again (a copy to a new disk,
        /// a touched write time), stamped with that version so the next pass does not hash it again.
        /// </summary>
        public Dictionary<int, string> Restamped { get; } = [];
    }

    private async Task<Dictionary<int, Known>> KnownAsync(IReadOnlyCollection<int> fileIds, CancellationToken ct)
    {
        var rows = await db.KoreaderDocuments.IgnoreQueryFilters().AsNoTracking()
            .Where(d => fileIds.Contains(d.ChapterFileId))
            .Select(d => new { d.Id, d.ChapterFileId, d.Kind, d.Digest, d.FileVersion })
            .ToListAsync(ct);

        var known = new Dictionary<int, Known>();
        foreach (var row in rows)
        {
            if (!known.TryGetValue(row.ChapterFileId, out var entry))
            {
                known[row.ChapterFileId] = entry = new Known();
            }

            entry.Digests[(row.Kind, row.Digest)] = row.Id;
            if (row.FileVersion is { } version)
            {
                entry.Versions.Add(version);
            }
        }

        return known;
    }

    private void Missing(int fileId, string path, IEnumerable<string> names, Known known, Found found)
    {
        var now = DateTime.UtcNow;
        void Add(KoreaderDigestKind kind, string digest, string? version)
        {
            if (known.Digests.TryGetValue((kind, digest), out var id))
            {
                if (id != 0 && version is not null)
                {
                    found.Restamped[id] = version;
                }
            }
            else
            {
                known.Digests[(kind, digest)] = 0;
                found.Rows.Add(new KoreaderDocument
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
                return;
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

    private async Task<int> SaveAsync(Found found, CancellationToken ct)
    {
        foreach (var (id, version) in found.Restamped)
        {
            await db.KoreaderDocuments.IgnoreQueryFilters()
                .Where(d => d.Id == id)
                .ExecuteUpdateAsync(s => s.SetProperty(d => d.FileVersion, version), ct);
        }

        var rows = found.Rows;
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
