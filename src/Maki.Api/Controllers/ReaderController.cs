using Maki.Api.Configuration;
using Maki.Api.Services;
using Maki.Core.Entities;
using Maki.Core.Progress;
using Maki.Core.Reading;
using Maki.Data;
using Maki.Data.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Net.Http.Headers;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Processing;

namespace Maki.Api.Controllers;

/// <summary>
/// <paramref name="Seconds"/> is a delta of active reading time since the client's last report,
/// not a total: the reader knows whether its tab is on screen and its user awake, and the server
/// does not. Absent or zero simply records no time. <paramref name="Final"/> marks the write that
/// ends a sitting (tab hidden, reader closed, chapter changed), which flushes the chapter's banked
/// time instead of waiting for a report that is not coming.
/// </summary>
public record SaveProgressRequest(int PageIndex, bool? Completed, int? Seconds, bool? Final);

/// <summary>A null spec clears the series override, falling back to whatever the series resolves to.</summary>
public record SeriesReaderPrefsRequest(ReaderPrefsSpec? Prefs);

/// <summary>A null id un-pins the series, handing it back to type-based auto-selection.</summary>
public record SeriesReadingProfileRequest(int? ProfileId);

/// <summary>
/// Bulk read-state change over a set of chapters. <paramref name="State"/> is one of
/// <c>read</c>, <c>watched</c> or <c>unread</c>.
/// <para>
/// <c>watched</c> ticks chapters off without reading them — see
/// <see cref="ReaderService.MarkWatchedAsync"/> for what that does and does not record.
/// </para>
/// </summary>
public record SetChaptersStateRequest(int[] ChapterIds, string State);

/// <summary>
/// Serves pages out of the library's CBZ files and records what has been read.
/// <para>
/// Page and thumbnail requests carry no credential in the URL. An <c>&lt;img&gt;</c> tag cannot send a
/// header, but it is same-origin, so the browser attaches the session cookie itself. These used to
/// append the instance API key as a query parameter — which put a credential into browser history and
/// into the access log of every proxy the image request passed through, for the endpoint that serves
/// the content itself rather than a thumbnail.
/// </para>
/// </summary>
[ApiController]
[Route("api/v1/reader")]
public class ReaderController(
    MakiDbContext db,
    ReaderService reader,
    ContinueReadingService continueReading,
    ReadingProfileService profiles,
    KavitaReadImportService readImport,
    UserMetricsService metrics,
    AchievementService achievements,
    AppPaths paths,
    ILogger<ReaderController> logger) : ControllerBase
{
    private const int ThumbnailWidth = 200;

    /// <summary>
    /// Loads (creating on demand) this user's state row for a series, or null when the series is not
    /// theirs to see. The existence check goes through the Series query filter, so a series in a root
    /// folder they hold no grant for is indistinguishable from one that does not exist.
    /// </summary>
    private async Task<UserSeriesState?> StateForAsync(int seriesId, CancellationToken ct)
    {
        if (!await db.Series.AnyAsync(s => s.Id == seriesId, ct))
        {
            return null;
        }

        var state = await db.UserSeriesStates.FirstOrDefaultAsync(s => s.SeriesId == seriesId, ct);
        if (state is null)
        {
            state = new UserSeriesState { SeriesId = seriesId };
            db.UserSeriesStates.Add(state);
        }

        return state;
    }

    /// <summary>
    /// Sets or clears this user's ad-hoc reader override for a series. Setting one un-pins any
    /// reading profile: two live answers would leave the picker naming a profile whose settings are
    /// not the ones on screen.
    /// </summary>
    [HttpPut("series/{seriesId:int}/prefs")]
    public async Task<IActionResult> SetSeriesPrefs(
        int seriesId, [FromBody] SeriesReaderPrefsRequest request, CancellationToken ct)
    {
        var state = await StateForAsync(seriesId, ct);
        if (state is null)
        {
            return NotFound();
        }

        state.ReaderPrefsJson = request.Prefs is null ? null : ReaderPrefsSpec.Serialize(request.Prefs);
        if (state.ReaderPrefsJson is not null)
        {
            state.ReadingProfileId = null;
        }

        state.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        return Ok(await profiles.ResolveAsync(seriesId, ct));
    }

    /// <summary>
    /// Pins a reading profile to a series, or clears the pin so the series' type picks one again.
    /// <para>
    /// Either way this drops the ad-hoc override, which is what makes the reader's picker a single
    /// control: "Auto" has to mean *nothing series-specific*, and clearing only the pin would leave
    /// a series that still ignored its type because of an override the picker was no longer showing.
    /// Going the other way is not symmetric — <see cref="SetSeriesPrefs"/> with a null spec clears
    /// only the override, falling back to the pin.
    /// </para>
    /// </summary>
    [HttpPut("series/{seriesId:int}/profile")]
    public async Task<IActionResult> SetSeriesProfile(
        int seriesId, [FromBody] SeriesReadingProfileRequest request, CancellationToken ct)
    {
        var state = await StateForAsync(seriesId, ct);
        if (state is null)
        {
            return NotFound();
        }

        // Through the profile query filter: another user's profile id resolves to nothing here
        // rather than being pinned to a series it could never be read from.
        if (request.ProfileId is int id && !await db.ReadingProfiles.AnyAsync(p => p.Id == id, ct))
        {
            return NotFound(new { error = "No such reading profile" });
        }

        state.ReadingProfileId = request.ProfileId;
        state.ReaderPrefsJson = null;
        state.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        return Ok(await profiles.ResolveAsync(seriesId, ct));
    }

    [HttpGet("chapter/{id:int}")]
    public async Task<IActionResult> Manifest(int id, CancellationToken ct)
    {
        var slice = await reader.SliceAsync(id, ct);
        if (slice is null)
        {
            return NotFound(new { error = "Chapter has no readable file" });
        }

        var (previous, next) = await reader.NeighboursAsync(slice.Chapter, ct);
        var saved = await reader.ProgressAsync(id, ct);
        var resolved = await profiles.ResolveAsync(slice.Series.Id, ct);

        // How far through the series this chapter sits, for the reader's own read meter. Same pair
        // of numbers the series page draws, so the two can never disagree: downloaded chapters as
        // the denominator (not every known chapter — an undownloaded one isn't something you can
        // read next), and ReadCounts for the numerator. Both are counted at manifest time and go
        // stale within the chapter, which is exactly right: they only move when a chapter is
        // finished, and finishing one refetches this.
        var seriesChapterCount = await db.Chapters
            .CountAsync(c => c.SeriesId == slice.Series.Id && c.ChapterFileId != null, ct);
        var seriesReadCount = await ReadCounts.Read(db)
            .CountAsync(p => p.SeriesId == slice.Series.Id, ct);

        // How long the series actually is, which the two counts above deliberately can't say. Same
        // rule the series page's denominator uses. The toolbar shows it as a trailing hint so
        // someone reading a series that downloads in batches can tell there is more coming.
        var seriesWantedCount = await db.Chapters
            .CountAsync(c => c.SeriesId == slice.Series.Id && (c.Wanted || c.ChapterFileId != null), ct);

        return Ok(new
        {
            chapterId = slice.Chapter.Id,
            seriesId = slice.Series.Id,
            seriesTitle = slice.Series.Title,
            label = ChapterLabel.For(slice.Chapter),
            number = slice.Chapter.Number,
            volume = slice.Chapter.Volume,
            language = slice.Chapter.Language,
            pageCount = slice.PageCount,
            seriesChapterCount,
            seriesReadCount,
            seriesWantedCount,
            resumePage = saved?.Completed == true ? 0 : saved?.PageIndex ?? 0,
            completed = saved?.Completed ?? false,
            previousChapterId = previous,
            nextChapterId = next,
            prefs = resolved.Prefs,
            prefsSource = resolved.Source.ToString(),
            profileId = resolved.ProfileId,
            profileName = resolved.ProfileName,
            pinnedProfileId = resolved.PinnedProfileId,
            autoProfileId = resolved.AutoProfileId,
            seriesType = slice.Series.Type
        });
    }

    [HttpGet("chapter/{id:int}/page/{page:int}")]
    public async Task<IActionResult> Page(int id, int page, CancellationToken ct)
    {
        var slice = await reader.SliceAsync(id, ct);
        if (slice is null || page < 0 || page >= slice.PageCount)
        {
            return NotFound();
        }

        var entry = slice.Pages[slice.StartPage + page];

        // The archive is immutable in practice, and the size guards against a re-import
        // reusing the id, so the response can be cached hard.
        var etag = new EntityTagHeaderValue($"\"{slice.ChapterFileId}-{slice.ArchiveSize}-{slice.StartPage + page}\"");
        if (Request.GetTypedHeaders().IfNoneMatch?.Any(t => t.Compare(etag, useStrongComparison: false)) == true)
        {
            return StatusCode(StatusCodes.Status304NotModified);
        }

        Response.Headers.CacheControl = "private, max-age=31536000, immutable";

        var stream = CbzReader.OpenPage(slice.ArchivePath, entry);
        if (stream is null)
        {
            return NotFound();
        }

        // Range processing stays off (the default): the zip entry stream is forward-only.
        return File(stream, CbzReader.ContentType(entry), lastModified: null, entityTag: etag);
    }

    [HttpGet("chapter/{id:int}/thumb/{page:int}")]
    public async Task<IActionResult> Thumbnail(int id, int page, CancellationToken ct)
    {
        var slice = await reader.SliceAsync(id, ct);
        if (slice is null || page < 0 || page >= slice.PageCount)
        {
            return NotFound();
        }

        var absoluteIndex = slice.StartPage + page;
        var dir = Path.Combine(paths.ReaderCacheDir, slice.ChapterFileId.ToString());
        var cached = Path.Combine(dir, $"{slice.ArchiveSize}-{absoluteIndex}.jpg");
        Response.Headers.CacheControl = "private, max-age=31536000, immutable";

        if (!System.IO.File.Exists(cached))
        {
            var entry = slice.Pages[absoluteIndex];
            try
            {
                await using var source = CbzReader.OpenPage(slice.ArchivePath, entry);
                if (source is null)
                {
                    return NotFound();
                }

                using var image = await Image.LoadAsync(source, ct);
                image.Mutate(x => x.Resize(new ResizeOptions
                {
                    Size = new Size(ThumbnailWidth, 0),
                    Mode = ResizeMode.Max
                }));

                Directory.CreateDirectory(dir);
                await image.SaveAsJpegAsync(cached, new JpegEncoder { Quality = 80 }, ct);
            }
            catch (Exception e)
            {
                // AVIF in particular cannot be decoded by the pinned ImageSharp build.
                logger.LogDebug(e, "Thumbnail failed for chapter {ChapterId} page {Page}", id, page);
                return NotFound();
            }
        }

        return PhysicalFile(cached, "image/jpeg");
    }

    [HttpPut("chapter/{id:int}/progress")]
    public async Task<IActionResult> SaveProgress(int id, [FromBody] SaveProgressRequest request,
        CancellationToken ct)
    {
        var slice = await reader.SliceAsync(id, ct);
        if (slice is null)
        {
            return NotFound();
        }

        var finished = await reader.SaveProgressAsync(
            slice, request.PageIndex, request.Completed,
            new ReaderService.TimeReport(request.Seconds ?? 0, request.Final ?? false), ct);

        return Ok(new
        {
            chapterId = id,
            pageIndex = request.PageIndex,
            completed = finished || request.Completed == true,
            unlocked = finished ? await UnlockedAsync(ct) : [],
        });
    }

    /// <summary>
    /// Evaluates achievements after a chapter completes and hands back whatever it earned, so the
    /// reader can show a toast on the same round trip.
    /// <para>
    /// Carried on the response rather than pushed over SignalR: the hub addresses admins and
    /// root-folder audiences and has no per-user method, and adding the first one to deliver a toast
    /// the client is already waiting on would be pure ceremony. Reads that arrive any other way (the
    /// Kavita pass, OPDS) are caught by the lazy evaluation on the progress endpoints instead.
    /// </para>
    /// <para>
    /// Never fails the write. The progress is already committed by the time this runs, and a badge
    /// that shows up on the next page load is not worth turning a successful read into a 500.
    /// </para>
    /// </summary>
    private async Task<object[]> UnlockedAsync(CancellationToken ct)
    {
        var userId = db.Scope.UserId;
        if (userId == 0)
        {
            return [];
        }

        try
        {
            metrics.Invalidate(userId);
            var unlocked = await achievements.EvaluateAsync(userId, ct);

            // One toast per achievement, not per tier. Crossing several rungs in one go is normal
            // and the reader experiences it as a single thing happening; acknowledging the top tier
            // marks the rest seen too (see AchievementService.MarkSeenAsync).
            return [.. unlocked
                .GroupBy(u => u.Key, StringComparer.Ordinal)
                .Select(g => g.OrderByDescending(u => u.Tier).First())
                .Select(u => new
            {
                id = u.Id,
                key = u.Key,
                tier = u.Tier,
                name = AchievementCatalog.Find(u.Key)?.Name ?? u.Key,
                tierName = AchievementCatalog.Find(u.Key) is { } d
                    ? AchievementCatalog.TierName(d, u.Tier)
                    : null,
            })];
        }
        catch (Exception e)
        {
            logger.LogWarning(e, "Achievement evaluation failed for user {UserId}", userId);
            return [];
        }
    }

    [HttpPost("chapter/{id:int}/read")]
    public async Task<IActionResult> MarkRead(int id, CancellationToken ct)
    {
        var slice = await reader.SliceAsync(id, ct);
        if (slice is null)
        {
            return NotFound();
        }

        // No time: ticking a chapter off from the chapter table is not a sitting with it.
        await reader.SaveProgressAsync(
            slice, slice.PageCount - 1, completed: true, ReaderService.TimeReport.None, ct);
        return Ok(new { chapterId = id, completed = true });
    }

    [HttpPost("chapter/{id:int}/unread")]
    public async Task<IActionResult> MarkUnread(int id, CancellationToken ct)
    {
        await reader.ClearProgressAsync(id, ct);
        return Ok(new { chapterId = id, completed = false });
    }

    /// <summary>Largest set one call will act on. Bounds the per-chapter <c>read</c> pass, which
    /// has to open each chapter's archive to learn its page count.</summary>
    private const int MaxBulkChapters = 2000;

    /// <summary>
    /// Bulk read-state change, for the chapter table's select mode and for ticking a whole anime
    /// season off at once.
    /// <para>
    /// The ids are narrowed against <c>db.Chapters</c> first rather than trusted: that query rides
    /// the series-derived global filter, so an id outside the caller's root folders silently drops
    /// out instead of letting a hand-written body reach another user's library.
    /// </para>
    /// </summary>
    [HttpPost("chapters/state")]
    public async Task<IActionResult> SetChaptersState(SetChaptersStateRequest req, CancellationToken ct)
    {
        var ids = (req.ChapterIds ?? []).Distinct().ToArray();
        if (ids.Length > MaxBulkChapters)
        {
            return BadRequest(new { error = $"At most {MaxBulkChapters} chapters per request" });
        }

        var state = (req.State ?? string.Empty).ToLowerInvariant();
        if (state is not ("read" or "watched" or "unread"))
        {
            return BadRequest(new { error = "State must be one of read, watched, unread" });
        }

        var visible = await db.Chapters
            .Where(c => ids.Contains(c.Id))
            .Select(c => c.Id)
            .ToListAsync(ct);
        if (visible.Count == 0)
        {
            return Ok(new { updated = 0 });
        }

        if (state == "watched")
        {
            return Ok(new { updated = await reader.MarkWatchedAsync(visible, ct) });
        }

        var updated = 0;
        foreach (var chapterId in visible)
        {
            if (state == "unread")
            {
                await reader.ClearProgressAsync(chapterId, ct);
                updated++;
                continue;
            }

            // Same path as MarkRead: a real read needs the slice to know where the last page is.
            // No time — ticking chapters off a table is not a sitting with them.
            var slice = await reader.SliceAsync(chapterId, ct);
            if (slice is null)
            {
                continue;
            }

            await reader.SaveProgressAsync(
                slice, slice.PageCount - 1, completed: true, ReaderService.TimeReport.None, ct);
            updated++;
        }

        return Ok(new { updated });
    }

    /// <summary>
    /// Whether the built-in reader has ever been used. The UI ORs this with "Kavita is
    /// configured" to decide whether to show read progress at all — the Kavita check alone used
    /// to be that gate, and on its own it would hide a reader-only user's progress.
    /// </summary>
    [HttpGet("used")]
    public async Task<IActionResult> Used(CancellationToken ct) =>
        Ok(new { used = await db.ChapterProgress.AnyAsync(ct) });

    /// <summary>
    /// Imports read status from Kavita. Runs in the background — a large library is one Kavita
    /// call per series — so this returns immediately and the UI polls <c>GET import/kavita</c>.
    /// </summary>
    [HttpPost("import/kavita")]
    public IActionResult StartKavitaImport() =>
        readImport.Start()
            ? Accepted(new { started = true })
            : Conflict(new { error = "An import is already running" });

    [HttpGet("import/kavita")]
    public IActionResult KavitaImportStatus() => Ok(new
    {
        running = readImport.State.Running,
        finishedAt = readImport.State.FinishedAt,
        result = readImport.State.Result,
        error = readImport.State.Error,
    });

    [HttpGet("chapter/{id:int}/bookmarks")]
    public async Task<IActionResult> Bookmarks(int id, CancellationToken ct) =>
        Ok(await db.ReaderBookmarks
            .Where(b => b.ChapterId == id)
            .OrderBy(b => b.PageIndex)
            .Select(b => new { b.Id, b.ChapterId, b.PageIndex, b.CreatedAt })
            .ToListAsync(ct));

    /// <summary>
    /// Adds or removes a bookmark on a page. Idempotent per (chapter, page), so a double-tap of
    /// the toolbar button toggles rather than stacking duplicates.
    /// </summary>
    [HttpPut("chapter/{id:int}/bookmark/{page:int}")]
    public async Task<IActionResult> ToggleBookmark(int id, int page, CancellationToken ct)
    {
        var chapter = await db.Chapters.FirstOrDefaultAsync(c => c.Id == id, ct);
        if (chapter is null)
        {
            return NotFound();
        }

        var existing = await db.ReaderBookmarks
            .FirstOrDefaultAsync(b => b.ChapterId == id && b.PageIndex == page, ct);
        if (existing is not null)
        {
            db.ReaderBookmarks.Remove(existing);
            await db.SaveChangesAsync(ct);
            return Ok(new { chapterId = id, page, bookmarked = false });
        }

        db.ReaderBookmarks.Add(new ReaderBookmark
        {
            SeriesId = chapter.SeriesId,
            ChapterId = id,
            PageIndex = page,
            CreatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync(ct);
        return Ok(new { chapterId = id, page, bookmarked = true });
    }

    /// <summary>
    /// Per-chapter read state for a series, for the chapter table. These rows are the whole story:
    /// read state is never inferred from <c>ReadingState.MaxChapter</c>, which is forward-only and
    /// therefore reports chapters read that never were.
    /// <para>
    /// <c>External</c> rides along so the table can distinguish a chapter read here from one Kavita
    /// reported, and <c>UnreadAt</c> so a tombstone (explicitly marked unread, kept to stop the
    /// Kavita tick re-marking it) reads as unread rather than as an unfinished chapter.
    /// <c>Watched</c> is completed-but-not-read, and the table labels it as such: it is still read
    /// for the purpose of every count, but it was ticked off rather than opened.
    /// </para>
    /// </summary>
    [HttpGet("series/{seriesId:int}/progress")]
    public async Task<IActionResult> SeriesProgress(int seriesId, CancellationToken ct)
    {
        var rows = await db.ChapterProgress
            .Where(p => p.SeriesId == seriesId)
            .Select(p => new
            {
                p.ChapterId, p.PageIndex, p.PageCount, p.Completed, p.External, p.Watched,
                p.UnreadAt, p.UpdatedAt
            })
            .ToListAsync(ct);
        return Ok(rows);
    }

    /// <summary>
    /// Where to resume: the most recently touched unfinished chapter, else the first
    /// downloaded chapter that has not been read.
    /// </summary>
    [HttpGet("series/{seriesId:int}/continue")]
    public async Task<IActionResult> Continue(int seriesId, CancellationToken ct)
    {
        // Tombstones excluded: a chapter the user just marked unread is the most recently touched
        // incomplete row, and resuming into it would hijack "Continue reading". It is still unread,
        // so the ordered fallback below picks it up in its proper place.
        var inProgress = await db.ChapterProgress
            .Where(p => p.SeriesId == seriesId && !p.Completed && p.UnreadAt == null && p.PageIndex > 0)
            .OrderByDescending(p => p.UpdatedAt)
            .FirstOrDefaultAsync(ct);
        if (inProgress is not null)
        {
            return Ok(new { chapterId = inProgress.ChapterId, page = inProgress.PageIndex });
        }

        var next = await continueReading.NextForAsync(seriesId, ct);

        return next is null
            ? NotFound(new { error = "Nothing left to read" })
            : Ok(new { chapterId = next.ChapterId, page = 0 });
    }
}
