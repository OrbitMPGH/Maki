using System.Globalization;
using Maki.Core.Entities;
using Maki.Core.Paths;
using Maki.Core.Quality;
using Maki.Data;
using Microsoft.EntityFrameworkCore;

namespace Maki.Api.Services;

/// <summary>Why a revert could not happen; the controller maps each to a response.</summary>
public enum UpgradeRevertError
{
    None,
    NotFound,
    AlreadyReverted,
    NotLatest,
    TrashGone,
    MoveFailed
}

/// <summary>Puts an upgraded chapter's previous file back and trashes the upgraded copy in its place.</summary>
public class UpgradeRevertService(MakiDbContext db, ReaderArchiveCache archives, ILogger<UpgradeRevertService> logger)
{
    public async Task<(UpgradeHistory? Row, UpgradeRevertError Error)> RevertAsync(int historyId, int? userId, CancellationToken ct)
    {
        var history = await db.UpgradeHistory.FirstOrDefaultAsync(h => h.Id == historyId, ct);
        if (history is null)
        {
            return (null, UpgradeRevertError.NotFound);
        }

        if (history.GroupId is { } groupId)
        {
            var (_, groupError) = await RevertGroupAsync(groupId, userId, ct);
            return (history, groupError);
        }

        if (history.RevertedAtUtc is not null)
        {
            return (history, UpgradeRevertError.AlreadyReverted);
        }

        // An older upgrade's trash holds a copy from before the newer one, so putting it back would
        // skip over the newer upgrade's own before-state. Only the newest standing upgrade unwinds.
        if (await db.UpgradeHistory.AnyAsync(h => h.ChapterFileId == history.ChapterFileId && h.Id > history.Id &&
                                                  h.RevertedAtUtc == null, ct))
        {
            return (history, UpgradeRevertError.NotLatest);
        }

        var file = await db.ChapterFiles.FirstOrDefaultAsync(f => f.Id == history.ChapterFileId, ct);
        var rootPath = await db.Series.Where(s => s.Id == history.SeriesId).Select(s => s.RootFolder!.Path).FirstOrDefaultAsync(ct);
        var trashPath = history.TrashPath is { } relative && rootPath is not null ? LibraryPaths.Resolve(rootPath, relative) : null;
        var currentPath = file is not null && rootPath is not null ? LibraryPaths.Resolve(rootPath, file.RelativePath) : null;
        if (file is null || trashPath is null || currentPath is null || !File.Exists(trashPath))
        {
            return (history, UpgradeRevertError.TrashGone);
        }

        var after = QualitySnapshot.Parse(history.AfterJson);
        int? mappingId = after?.SourceName is { } afterSource
            ? await db.SourceMappings
                .Where(m => m.SeriesId == history.SeriesId && m.SourceName == afterSource)
                .Select(m => (int?)m.Id)
                .FirstOrDefaultAsync(ct)
            : null;

        var fileName = Path.GetFileName(file.RelativePath);
        var asideRelative = UpgradeTrash.NewRelativePath(rootPath!, history.SeriesId,
            $"{file.Id.ToString(CultureInfo.InvariantCulture)}-reverted", fileName);
        var asidePath = LibraryPaths.Resolve(rootPath!, asideRelative)!;
        UpgradeTrash.EnsureFolder(rootPath!, history.SeriesId);

        var hadCurrent = File.Exists(currentPath);
        if (hadCurrent)
        {
            if (!await UpgradeTrash.MoveIntoTrashAsync(currentPath, asidePath, logger, ct))
            {
                return (history, UpgradeRevertError.MoveFailed);
            }

            logger.LogDebug("Moved {Current} aside to {Aside}", currentPath, asidePath);
        }

        // Disk has started changing: nothing below may be cancelled, or the rows stop describing it.
        try
        {
            File.Move(trashPath, currentPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Could not restore {Trash} to {Path}", trashPath, currentPath);
            if (hadCurrent)
            {
                try
                {
                    File.Move(asidePath, currentPath);
                }
                catch (Exception restoreEx) when (restoreEx is IOException or UnauthorizedAccessException)
                {
                    logger.LogError(restoreEx, "Could not put {Aside} back at {Path}", asidePath, currentPath);
                }
            }

            return (history, UpgradeRevertError.MoveFailed);
        }

        archives.Invalidate(file.Id);

        var before = QualitySnapshot.Parse(history.BeforeJson) ?? new QualitySnapshot();
        var now = DateTime.UtcNow;
        file.Tier = before.ParsedTier();
        file.Group = before.Group;
        file.PageCount = before.PageCount;
        file.MedianWidth = before.MedianWidth;
        file.MedianHeight = before.MedianHeight;
        file.ImageFormat = before.ImageFormat;
        file.Size = new FileInfo(currentPath).Length;
        file.SourceName = before.SourceName ?? file.SourceName;
        file.SourceChapterId = before.SourceChapterId;
        file.ReleaseName = before.ReleaseName;
        file.ReleaseHash = before.ReleaseHash;
        // Never measured stays unmeasured, so the backfill picks it up.
        file.MeasuredAtUtc = before.PageCount is null && before.MedianWidth is null ? null : now;
        // The swap back starts the quiet period too, or the next scan replaces what the user restored.
        file.ReplacedAtUtc = now;

        history.RevertedAtUtc = now;
        history.TrashPath = hadCurrent ? asideRelative : null;
        history.TrashBytes = hadCurrent ? new FileInfo(asidePath).Length : 0;

        if (mappingId is { } id && after?.SourceChapterId is { } sourceChapterId)
        {
            await UpgradeAttempts.UpsertAsync(db, history.ChapterId, history.SeriesId, id, sourceChapterId,
                history.ProfileId, history.ProfileVersion, UpgradeReasons.RevertedByUser, probed: true,
                after.PageCount, after.MedianWidth, after.Score, CancellationToken.None);
        }

        await db.SaveChangesAsync(CancellationToken.None);
        logger.LogInformation("Reverted upgrade {Id} of chapter file {File} (user {User})", history.Id, file.Id, userId);
        return (history, UpgradeRevertError.None);
    }

    /// <summary>
    /// Undoes a torrent replacement: the volume files it imported go into the trash, every file it
    /// superseded comes back to its old path with its old row, and the chapters point at them again.
    /// Nothing moves unless every row can be reverted, and a failed move puts back what already moved.
    /// </summary>
    public async Task<(IReadOnlyList<UpgradeHistory> Rows, UpgradeRevertError Error)> RevertGroupAsync(
        Guid groupId, int? userId, CancellationToken ct)
    {
        var rows = await db.UpgradeHistory.Where(h => h.GroupId == groupId).OrderBy(h => h.Id).ToListAsync(ct);
        if (rows.Count == 0)
        {
            return (rows, UpgradeRevertError.NotFound);
        }

        if (rows.Any(r => r.RevertedAtUtc is not null))
        {
            return (rows, UpgradeRevertError.AlreadyReverted);
        }

        var seriesId = rows[0].SeriesId;
        var rootPath = await db.Series.Where(s => s.Id == seriesId).Select(s => s.RootFolder!.Path).FirstOrDefaultAsync(ct);
        var plans = new List<(UpgradeHistory Row, VolumeReplacementDetail Detail, string Trash, string Target)>();
        foreach (var row in rows)
        {
            var detail = VolumeReplacementDetail.Parse(row.DetailJson);
            var trash = row.TrashPath is { } relative && rootPath is not null ? LibraryPaths.Resolve(rootPath, relative) : null;
            var target = detail is not null && rootPath is not null ? LibraryPaths.Resolve(rootPath, detail.RelativePath) : null;
            if (detail is null || trash is null || target is null || !File.Exists(trash))
            {
                return (rows, UpgradeRevertError.TrashGone);
            }

            plans.Add((row, detail, trash, target));
        }

        var chapterIds = plans.SelectMany(p => p.Detail.ChapterIds).Distinct().ToList();
        var chapters = await db.Chapters.Where(c => chapterIds.Contains(c.Id)).ToDictionaryAsync(c => c.Id, ct);
        var volumeIds = plans
            .SelectMany(p => p.Detail.ChapterIds.Select((_, i) => p.Detail.ReplacementFor(i)))
            .Distinct()
            .ToList();
        var volumes = await db.ChapterFiles.Where(f => volumeIds.Contains(f.Id)).ToListAsync(ct);
        if (volumes.Count != volumeIds.Count ||
            plans.Any(p => p.Detail.ChapterIds.Select((id, i) => (id, i)).Any(c =>
                !chapters.TryGetValue(c.id, out var chapter) || chapter.ChapterFileId != p.Detail.ReplacementFor(c.i))))
        {
            return (rows, UpgradeRevertError.NotLatest);
        }

        // Disk starts changing here: nothing below may be cancelled, or the rows stop describing it.
        UpgradeTrash.EnsureFolder(rootPath!, seriesId);
        var asides = new Dictionary<int, (string Relative, string Path, long Bytes)>();
        var restored = new List<(string From, string To)>();
        foreach (var volume in volumes)
        {
            var current = LibraryPaths.Resolve(rootPath!, volume.RelativePath);
            if (current is null || !File.Exists(current))
            {
                continue;
            }

            var relative = UpgradeTrash.NewRelativePath(rootPath!, seriesId,
                $"{volume.Id.ToString(CultureInfo.InvariantCulture)}-reverted", Path.GetFileName(volume.RelativePath));
            var aside = LibraryPaths.Resolve(rootPath!, relative)!;
            if (!await UpgradeTrash.MoveIntoTrashAsync(current, aside, logger, CancellationToken.None))
            {
                RollBack();
                return (rows, UpgradeRevertError.MoveFailed);
            }

            asides[volume.Id] = (relative, aside, new FileInfo(aside).Length);
            restored.Add((current, aside));
        }

        foreach (var plan in plans)
        {
            try
            {
                if (File.Exists(plan.Target))
                {
                    throw new IOException($"{plan.Target} already exists");
                }

                Directory.CreateDirectory(Path.GetDirectoryName(plan.Target)!);
                File.Move(plan.Trash, plan.Target);
                restored.Add((plan.Trash, plan.Target));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                logger.LogWarning(ex, "Could not restore {Trash} to {Path}", plan.Trash, plan.Target);
                RollBack();
                return (rows, UpgradeRevertError.MoveFailed);
            }
        }

        var now = DateTime.UtcNow;
        var created = new List<(VolumeReplacementDetail Detail, ChapterFile File)>();
        foreach (var plan in plans)
        {
            var before = QualitySnapshot.Parse(plan.Row.BeforeJson) ?? new QualitySnapshot();
            var file = new ChapterFile
            {
                SeriesId = seriesId,
                RelativePath = plan.Detail.RelativePath,
                Size = new FileInfo(plan.Target).Length,
                SourceName = before.SourceName ?? string.Empty,
                SourceChapterId = before.SourceChapterId,
                DateAdded = plan.Detail.DateAdded,
                ReleaseName = before.ReleaseName,
                ReleaseHash = before.ReleaseHash,
                Tier = before.ParsedTier(),
                Group = before.Group,
                PageCount = before.PageCount,
                MedianWidth = before.MedianWidth,
                MedianHeight = before.MedianHeight,
                ImageFormat = before.ImageFormat,
                MeasuredAtUtc = before.PageCount is null && before.MedianWidth is null ? null : now,
                Trusted = plan.Detail.Trusted
            };
            db.ChapterFiles.Add(file);
            created.Add((plan.Detail, file));
        }

        await db.SaveChangesAsync(CancellationToken.None);
        foreach (var (detail, file) in created)
        {
            foreach (var chapterId in detail.ChapterIds)
            {
                chapters[chapterId].ChapterFileId = file.Id;
            }

            archives.Invalidate(file.Id);
        }

        // Chapters the volume took from a file this import did not supersede go back to that file when
        // it still exists; ones that had no file before have none again once the volume is set aside.
        var previous = plans
            .SelectMany(p => p.Detail.TakenChapters)
            .GroupBy(t => t.ChapterId)
            .ToDictionary(g => g.Key, g => g.First().PreviousFileId);
        var previousIds = previous.Values.OfType<int>().Where(id => !volumeIds.Contains(id)).Distinct().ToList();
        var stillThere = (await db.ChapterFiles
                .Where(f => f.SeriesId == seriesId && previousIds.Contains(f.Id))
                .Select(f => f.Id)
                .ToListAsync(CancellationToken.None))
            .ToHashSet();
        var orphaned = await db.Chapters
            .Where(c => c.SeriesId == seriesId && c.ChapterFileId != null && volumeIds.Contains(c.ChapterFileId.Value))
            .ToListAsync(CancellationToken.None);
        foreach (var chapter in orphaned.Where(c => !chapterIds.Contains(c.Id)))
        {
            chapter.ChapterFileId = previous.GetValueOrDefault(chapter.Id) is { } fileId && stillThere.Contains(fileId)
                ? fileId
                : null;
            if (chapter.ChapterFileId is { } relinked)
            {
                archives.Invalidate(relinked);
            }
        }

        foreach (var volume in volumes)
        {
            archives.Invalidate(volume.Id);
            db.ChapterFiles.Remove(volume);
        }

        foreach (var plan in plans)
        {
            archives.Invalidate(plan.Row.ChapterFileId);
            plan.Row.RevertedAtUtc = now;
            var aside = asides.GetValueOrDefault(plan.Detail.ReplacementFileId);
            plan.Row.TrashPath = aside.Relative;
            plan.Row.TrashBytes = aside.Relative is null ? 0 : aside.Bytes;
        }

        await db.SaveChangesAsync(CancellationToken.None);
        logger.LogInformation("Reverted torrent replacement {Group}: {Files} file(s) restored (user {User})",
            groupId, plans.Count, userId);
        return (rows, UpgradeRevertError.None);

        void RollBack()
        {
            for (var i = restored.Count - 1; i >= 0; i--)
            {
                var (from, to) = restored[i];
                try
                {
                    File.Move(to, from);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    logger.LogError(ex, "Could not put {Path} back at {Original}", to, from);
                }
            }
        }
    }
}
