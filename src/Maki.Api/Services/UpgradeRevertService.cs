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

/// <summary>
/// Puts an upgraded chapter's previous file back. The upgraded copy goes into the trash in its place,
/// so the revert can itself be purged on the normal schedule, and the candidate is memoised as
/// <c>reverted_by_user</c> so the next scan does not queue the same copy again.
/// </summary>
public class UpgradeRevertService(MakiDbContext db, ReaderArchiveCache archives, ILogger<UpgradeRevertService> logger)
{
    public async Task<(UpgradeHistory? Row, UpgradeRevertError Error)> RevertAsync(int historyId, int? userId, CancellationToken ct)
    {
        var history = await db.UpgradeHistory.FirstOrDefaultAsync(h => h.Id == historyId, ct);
        if (history is null)
        {
            return (null, UpgradeRevertError.NotFound);
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
        file.MeasuredAtUtc = now;
        file.ReplacedAtUtc = null;

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
}
