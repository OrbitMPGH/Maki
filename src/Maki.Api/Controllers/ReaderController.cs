using Maki.Api.Configuration;
using Maki.Api.Dtos;
using Maki.Api.Localization;
using Maki.Api.Services;
using Maki.Api.Services.Upscaling;
using Maki.Core.Configuration;
using Maki.Core.Entities;
using Maki.Core.Images;
using Maki.Core.Images.Upscaling;
using Maki.Core.Progress;
using Maki.Core.Quality;
using Maki.Core.Reading;
using Maki.Core.Security;
using Maki.Core.Io;
using Maki.Data;
using Maki.Data.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Net.Http.Headers;

namespace Maki.Api.Controllers;

/// <summary>
/// <paramref name="Seconds"/> is a delta of active reading time since the client's last report,
/// not a total: the reader knows whether its tab is on screen and its user awake, and the server
/// does not. Absent or zero simply records no time. <paramref name="Final"/> marks the write that
/// ends a sitting (tab hidden, reader closed, chapter changed), which flushes the chapter's banked
/// time instead of waiting for a report that is not coming.
/// <para>
/// <paramref name="ClientTime"/> (UTC) is when the write was made, sent by a write replayed from the
/// offline queue. <paramref name="OpId"/> identifies the write so a replay of one that already landed
/// changes nothing. Both are optional; see <see cref="ReaderService.SaveProgressAsync"/>.
/// </para>
/// <para>
/// <paramref name="PageOffset"/> is how far down the page the continuous strip is (0 to 1), so a tall
/// slice resumes mid-page. Absent means the top of the page.
/// </para>
/// </summary>
public record SaveProgressRequest(
    int PageIndex, bool? Completed, int? Seconds, bool? Final, DateTime? ClientTime = null, string? OpId = null,
    double? PageOffset = null);

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
/// Pages of a chapter to upscale on the server, current page first, plus the first pages of the
/// next chapter once the window runs past the last page. <c>WholeChapter</c> lifts the cap to
/// <see cref="UpscaleQueue.MaxChapterPages"/> so the rest of the chapter can queue behind the window,
/// and queues every page of <c>NextChapterId</c> after it.
/// <c>NotWanted</c> names pages of this chapter the reader shows plain (Enhance on Auto), dropped from
/// whatever an earlier request queued. <c>Session</c> names the reader tab, so two tabs on one account
/// keep separate queues. <c>Window</c> is how many leading <c>Pages</c> are the reading window (the rest
/// is the whole chapter's backlog), and <c>Visible</c> false marks a request from a hidden tab, which
/// does not make that tab active; absent means visible. See <see cref="ReaderController.Enhance"/>.
/// </summary>
public record EnhancePagesRequest(
    string? Model,
    int[]? Pages,
    int? NextChapterId = null,
    int[]? NextPages = null,
    bool WholeChapter = false,
    int[]? NotWanted = null,
    string? Session = null,
    int? Window = null,
    bool? Visible = null);

/// <summary>Chapters a device holds offline copies of. See <see cref="ReaderController.ChapterVersions"/>.</summary>
public record ChapterVersionsRequest(int[]? Ids);

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
    ILocalizer localizer,
    MakiDbContext db,
    ReaderService reader,
    ReaderThumbnailCache thumbnails,
    ContinueReadingService continueReading,
    ReadingProfileService profiles,
    KavitaReadImportService readImport,
    AchievementEvaluationQueue achievementQueue,
    AppPaths paths,
    ICurrentUser currentUser,
    KavitaUserResolver kavitaUser,
    IAppSettings appSettings) : ControllerBase
{
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
            return this.NotFoundMessage(localizer, "error.reader.profileNotFound");
        }

        state.ReadingProfileId = request.ProfileId;
        state.ReaderPrefsJson = null;
        state.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        return Ok(await profiles.ResolveAsync(seriesId, ct));
    }

    [HttpGet("chapter/{id:int}")]
    public async Task<IActionResult> Manifest(
        int id,
        [FromServices] EnhanceAvailability enhanceAvailability,
        [FromServices] CreditStore creditStore,
        [FromServices] ReadingTimeEstimateService estimates,
        CancellationToken ct)
    {
        var slice = await reader.SliceAsync(id, ct);
        if (slice is null)
        {
            return this.NotFoundMessage(localizer, "error.reader.chapterNotReadable");
        }

        var (previous, next) = await reader.NeighboursAsync(slice.Chapter, ct);
        var saved = await reader.ProgressAsync(id, ct);
        var resolved = await profiles.ResolveAsync(slice.Series.Id, ct);

        // How far through the series this chapter sits, for the reader's own read meter. Same pair
        // of numbers the series page draws, so the two can never disagree: downloaded chapters (plus
        // read ones whose file was cleared) as the denominator (not every known chapter — an undownloaded one isn't something you can
        // read next), and ReadCounts for the numerator. Both are counted at manifest time and go
        // stale within the chapter, which is exactly right: they only move when a chapter is
        // finished, and finishing one refetches this.
        var ending = await EndingAsync(slice.Series.Id, next, ct);

        // Whether finishing this chapter finishes the series, so the end screen can ask for the final
        // rating without waiting for the write that sets the flag. Only a completed series can end.
        // Finished is sticky, so a series whose status left Completed would still say so; only a completed one can end here.
        var seriesFinished = slice.Series.Status == SeriesStatus.Completed &&
            await RatingCheckpointService.FinishedAsync(db, currentUser.UserId, slice.Series.Id, ct);
        var finishesSeries = false;
        if (slice.Series.Status == SeriesStatus.Completed && slice.Chapter.Number is { } thisNumber)
        {
            var highest = await SeriesFinishRule.HighestNumberAsync(db, slice.Series.Id, ct);
            finishesSeries = SeriesFinishRule.Closes(slice.Series.Status, (double)thisNumber, highest);
        }

        // The same median the series page's time to finish uses, so the two never disagree about
        // pace. The total is that page's too: what the series is meant to hold, or what is wanted.
        var pace = await estimates.PaceAsync(
            slice.Series.Id, ReadingTimeEstimateService.ModeFor(slice.Series.Type, resolved), ct);
        var paceTotal = Math.Max(slice.Series.TotalChapters ?? 0,
            ending.WantedCount > 0 ? ending.WantedCount : ending.ChapterCount);

        var enhanceServer = await enhanceAvailability.GetAsync(ct);
        var tier = await db.ChapterFiles.AsNoTracking()
            .Where(f => f.Id == slice.ChapterFileId)
            .Select(f => f.Tier)
            .FirstOrDefaultAsync(ct);
        // Drawn with the first paint, so the toolbar badge and the page markers need no second request.
        var notePages = await db.ChapterNotes
            .Where(n => n.ChapterId == id && n.ParentId == null && n.DeletedAt == null)
            .Select(n => n.PageIndex)
            .ToListAsync(ct);
        // A stored answer rides along, so the first paint is already trimmed and nothing waits for it.
        var credits = resolved.Prefs.HideCredits && ReaderCreditsCache.Applies(slice)
            ? await creditStore.GetAsync(slice, ct)
            : null;

        return Ok(new
        {
            chapterId = slice.Chapter.Id,
            seriesId = slice.Series.Id,
            seriesTitle = slice.Series.Title,
            label = ChapterLabel.For(slice.Chapter),
            number = slice.Chapter.Number,
            pageCount = slice.PageCount,
            seriesChapterCount = ending.ChapterCount,
            seriesReadCount = ending.ReadCount,
            seriesWantedCount = ending.WantedCount,
            finishesSeries,
            seriesFinished,
            readingPace = pace is null
                ? null
                : new { chapterSeconds = (int)Math.Round(pace.MedianSeconds), style = pace.Style, totalChapters = paceTotal },
            resumePage = ReaderService.ResumePageFor(saved, slice.PageCount),
            resumeOffset = ReaderService.ResumeOffsetFor(saved, slice.PageCount),
            completed = saved?.Completed ?? false,
            previousChapterId = previous,
            nextChapterId = next,
            nextChapterLabel = ending.NextLabel,
            nextChapterNumber = ending.NextNumber,
            seriesCoverUrl = SeriesDto.CoverUrlFor(slice.Series.Id, slice.Series.CoverPath, slice.Series.LastMetadataRefresh),
            prefs = resolved.Prefs,
            prefsSource = resolved.Source.ToString(),
            profileId = resolved.ProfileId,
            pinnedProfileId = resolved.PinnedProfileId,
            autoProfileId = resolved.AutoProfileId,
            pageVersion = PageVersion(slice.ChapterFileId, slice.ArchiveVersion),
            credits = credits is null
                ? null
                : new { headPages = credits.HeadPages, headOffset = credits.HeadOffset, tailPages = credits.TailPages },
            tier = QualitySnapshot.TierName(tier),
            enhanceServer,
            noteCount = notePages.Count,
            notedPages = notePages.OfType<int>().Where(p => p < slice.PageCount).Distinct().Order().ToList()
        });
    }

    /// <summary>
    /// What the end screen shows that changes while it is open: the next chapter, the counts, and
    /// where the final rating stands. The manifest is not refetched for it, because a refetch moves
    /// the reader's position and remounts pages.
    /// </summary>
    [HttpGet("chapter/{id:int}/ending")]
    public async Task<IActionResult> Ending(int id, CancellationToken ct)
    {
        var row = await db.Chapters.AsNoTracking()
            .Where(c => c.Id == id)
            .Select(c => new { Chapter = c, Completed = c.Series!.Status == SeriesStatus.Completed })
            .FirstOrDefaultAsync(ct);
        var chapter = row?.Chapter;
        if (chapter is null)
        {
            return this.NotFoundMessage(localizer, "error.reader.chapterNotReadable");
        }

        var (_, next) = await reader.NeighboursAsync(chapter, ct);
        var ending = await EndingAsync(chapter.SeriesId, next, ct);
        var state = await db.UserSeriesStates
            .Where(s => s.SeriesId == chapter.SeriesId)
            .Select(s => new { s.Rating, s.ProvisionalRating, s.RateHintDismissedAt })
            .FirstOrDefaultAsync(ct);
        return Ok(new
        {
            nextChapterId = next,
            nextChapterLabel = ending.NextLabel,
            nextChapterNumber = ending.NextNumber,
            seriesChapterCount = ending.ChapterCount,
            seriesReadCount = ending.ReadCount,
            seriesWantedCount = ending.WantedCount,
            completed = (await reader.ProgressAsync(id, ct))?.Completed ?? false,
            finished = row!.Completed && await RatingCheckpointService.FinishedAsync(db, currentUser.UserId, chapter.SeriesId, ct),
            rating = state?.Rating,
            provisionalRating = state?.ProvisionalRating,
            rateHintDismissed = state?.RateHintDismissedAt != null,
        });
    }

    private sealed record EndingState(
        int ChapterCount, int ReadCount, int WantedCount, string? NextLabel, decimal? NextNumber);

    private async Task<EndingState> EndingAsync(int seriesId, int? next, CancellationToken ct)
    {
        var readCount = await ReadCounts.Read(db).CountAsync(p => p.SeriesId == seriesId, ct);

        // WantedCount is how long the series actually is, which the downloaded count can't say.
        // Same rule the series page's denominator uses.
        var counts = await db.Chapters
            .Where(c => c.SeriesId == seriesId)
            .GroupBy(_ => 1)
            .Select(g => new
            {
                Downloaded = g.Count(c => c.ChapterFileId != null || c.FileRemovedAt != null),
                Wanted = g.Count(c => c.Wanted || c.ChapterFileId != null || c.FileRemovedAt != null),
            })
            .FirstOrDefaultAsync(ct);

        // Named on the end-of-chapter screen, and its number is how that screen tells a straight
        // continuation from a jump over chapters that were never downloaded.
        var nextChapter = next is int nextId
            ? await db.Chapters.AsNoTracking()
                .Where(c => c.Id == nextId)
                .Select(c => new { c.Number, c.Volume, c.Title, c.IsOneShot })
                .FirstOrDefaultAsync(ct)
            : null;
        return new EndingState(
            counts?.Downloaded ?? 0,
            readCount,
            counts?.Wanted ?? 0,
            nextChapter is null
                ? null
                : ChapterLabel.For(nextChapter.Number, nextChapter.Volume, nextChapter.Title, nextChapter.IsOneShot),
            nextChapter?.Number);
    }

    [HttpGet("chapter/{id:int}/page/{page:int}")]
    public async Task<IActionResult> Page(int id, int page, CancellationToken ct)
    {
        var slice = await reader.PageSliceAsync(id, ct);
        if (slice is null || page < 0 || page >= slice.PageCount)
        {
            return NotFound();
        }

        var entry = slice.Pages[slice.StartPage + page];

        var etag = new EntityTagHeaderValue($"\"{slice.ChapterFileId}-{slice.ArchiveVersion}-{slice.StartPage + page}\"");
        if (Request.GetTypedHeaders().IfNoneMatch?.Any(t => t.Compare(etag, useStrongComparison: false)) == true)
        {
            return StatusCode(StatusCodes.Status304NotModified);
        }

        if (ComicFile.IsPdf(slice.ArchivePath))
        {
            var cached = await ReaderPageCache.GetOrRenderFullPageAsync(paths, slice, slice.StartPage + page, entry, ct);
            if (cached is null)
            {
                return NotFound();
            }

            SetPageCacheControl(slice);
            return PhysicalFile(cached, CbzReader.ContentType(entry), lastModified: null, entityTag: etag, enableRangeProcessing: false);
        }

        var stream = await reader.OpenPageAsync(slice, entry, ct);
        if (stream is null)
        {
            return NotFound();
        }

        SetPageCacheControl(slice);
        return File(stream, CbzReader.ContentType(entry), lastModified: null, entityTag: etag);
    }

    private static string PageVersion(int chapterFileId, string archiveVersion) => $"{chapterFileId}-{archiveVersion}";

    /// <summary>
    /// Page URLs are the same before and after a re-download, so a year-long immutable response is
    /// only safe when the URL carries the manifest's <c>pageVersion</c> and it still matches the
    /// file on disk. Anything else revalidates against the ETag.
    /// </summary>
    private void SetPageCacheControl(ReaderService.PageSlice slice) =>
        Response.Headers.CacheControl = Request.Query["v"] == PageVersion(slice.ChapterFileId, slice.ArchiveVersion)
            ? "private, max-age=31536000, immutable"
            : "private, no-cache";

    /// <summary>
    /// Asks for server upscales of a window of pages. Pages already cached answer <c>ready</c>; a page
    /// that just failed or was skipped answers that instead of queueing again. The rest become this
    /// reader's pending work, replacing what they had pending for another chapter or model. Each one
    /// is pushed as <c>pageEnhanced</c> when the worker starts it (<c>working</c>) and when it finishes.
    /// Pages of the next chapter queue after them in whatever room is left under the cap, so the next
    /// chapter opens on pages already upscaled: the ones named, or with <c>WholeChapter</c> all of them.
    /// None are queued when the next chapter's file tier is one the series' settings skip. They are not
    /// answered here; that chapter's own request picks them up.
    /// </summary>
    [HttpPost("chapter/{id:int}/enhance")]
    public async Task<IActionResult> Enhance(
        int id,
        [FromBody] EnhancePagesRequest request,
        [FromServices] UpscaleModelStore store,
        [FromServices] UpscaleQueue queue,
        [FromServices] UpscaleCache cache,
        CancellationToken ct)
    {
        var (refusal, model) = await EnhanceAvailability.CheckAsync(appSettings, store, request.Model, ct);
        if (refusal is not null)
        {
            return this.Conflict(localizer, refusal);
        }

        var slice = await reader.PageSliceAsync(id, ct);
        if (slice is null)
        {
            return NotFound();
        }

        var cap = request.WholeChapter ? UpscaleQueue.MaxChapterPages : UpscaleQueue.MaxPendingPerUser;
        var pages = (request.Pages ?? []).Distinct().Take(cap).ToList();
        var states = new string[pages.Count];
        var wanted = new List<(int Index, UpscaleRequestItem Item)>();
        for (var i = 0; i < pages.Count; i++)
        {
            var page = pages[i];
            if (page < 0 || page >= slice.PageCount)
            {
                states[i] = "skipped";
                continue;
            }

            var key = new UpscaleWorkKey(slice.ChapterFileId, slice.ArchiveVersion, slice.StartPage + page, model.Value);
            if (cache.TryGet(key) is not null)
            {
                states[i] = "ready";
                continue;
            }

            switch (queue.RecentOutcome(key))
            {
                case UpscaleOutcome.Failed:
                    states[i] = "failed";
                    continue;
                case UpscaleOutcome.Skipped:
                    states[i] = "skipped";
                    continue;
            }

            wanted.Add((i, new UpscaleRequestItem(key, new UpscaleTarget(id, page), slice)));
        }

        var items = wanted.Select(w => w.Item).ToList();
        var room = cap - items.Count;
        if (request.NextChapterId is { } nextId && nextId != id && room > 0
            && (request.NextPages is { Length: > 0 } || request.WholeChapter)
            && await reader.PageSliceAsync(nextId, ct) is { } next
            && !await SkipsEnhanceAsync(nextId, ct))
        {
            // Two chapters served from one whole archive share keys; the page asked for as this chapter's keeps its waiter.
            var asked = items.Select(item => item.Key).ToHashSet();
            items.AddRange((request.NextPages ?? [])
                .Concat(request.WholeChapter ? Enumerable.Range(0, next.PageCount) : [])
                .Distinct()
                .Where(page => page >= 0 && page < next.PageCount)
                .Select(page => new UpscaleRequestItem(
                    new UpscaleWorkKey(next.ChapterFileId, next.ArchiveVersion, next.StartPage + page, model.Value),
                    new UpscaleTarget(nextId, page), next))
                .Where(item => !asked.Contains(item.Key) && cache.TryGet(item.Key) is null && queue.RecentOutcome(item.Key) is null)
                .Take(room));
        }

        var plain = (request.NotWanted ?? [])
            .Where(page => page >= 0 && page < slice.PageCount && !pages.Contains(page))
            .Select(page => new UpscaleWorkKey(slice.ChapterFileId, slice.ArchiveVersion, slice.StartPage + page, model.Value))
            .ToHashSet();
        int? window = request.WholeChapter && request.Window is { } shown ? wanted.Count(w => w.Index < shown) : null;
        var queued = queue.Replace(
            currentUser.UserId, id, model.Value, items, cap, plain, EnhanceSession(request.Session), window, request.Visible ?? true);
        for (var i = 0; i < wanted.Count; i++)
        {
            states[wanted[i].Index] = queued[i] == UpscaleJobState.Working ? "working" : "queued";
        }

        return Ok(new { pages = pages.Select((page, i) => new { page, state = states[i] }).ToList() });
    }

    /// <summary>Drops this reader's pending upscales and stops a running one nobody else waits on. Sent as they leave the reader.</summary>
    [HttpDelete("enhance")]
    public IActionResult CancelEnhance([FromServices] UpscaleQueue queue, [FromQuery] string? session = null)
    {
        queue.Cancel(currentUser.UserId, EnhanceSession(session));
        return NoContent();
    }

    private static string? EnhanceSession(string? session) => session is { Length: > 0 and <= 64 } ? session : null;

    /// <summary>Whether the reader's settings for the chapter's series turn Enhance off for its file tier.</summary>
    private async Task<bool> SkipsEnhanceAsync(int chapterId, CancellationToken ct)
    {
        var row = await db.Chapters.AsNoTracking()
            .Where(c => c.Id == chapterId)
            .Select(c => new { c.SeriesId, Tier = c.ChapterFile == null ? QualityTier.Unknown : c.ChapterFile.Tier })
            .FirstOrDefaultAsync(ct);
        if (row is null)
        {
            return true;
        }

        var prefs = (await profiles.ResolveAsync(row.SeriesId, ct)).Prefs;
        return (prefs.EnhanceSkipTiers ?? ReaderPrefsSpec.DefaultEnhanceSkipTiers).Contains(QualitySnapshot.TierName(row.Tier));
    }

    /// <summary>
    /// A server upscale from the cache. 404 until it is there; this never queues one. A page cached as
    /// JPEG XL is sent as it is only when <paramref name="jxl"/> is "1" (the reader decoded a test
    /// image); otherwise it is converted to WebP on the way out, since Edge and older browsers cannot
    /// decode it. HEAD answers without converting.
    /// </summary>
    [HttpGet("chapter/{id:int}/page/{page:int}/enhanced")]
    [HttpHead("chapter/{id:int}/page/{page:int}/enhanced")]
    public async Task<IActionResult> EnhancedPage(
        int id,
        int page,
        [FromQuery] string? model,
        [FromQuery] string? jxl,
        [FromServices] UpscaleModelStore store,
        [FromServices] UpscaleCache cache,
        [FromServices] ILogger<ReaderController> logger,
        CancellationToken ct)
    {
        var (refusal, modelId) = await EnhanceAvailability.CheckAsync(appSettings, store, model, ct);
        if (refusal is not null)
        {
            return this.Conflict(localizer, refusal);
        }

        var slice = await reader.PageSliceAsync(id, ct);
        if (slice is null || page < 0 || page >= slice.PageCount)
        {
            return NotFound();
        }

        var key = new UpscaleWorkKey(slice.ChapterFileId, slice.ArchiveVersion, slice.StartPage + page, modelId.Value);
        var cached = cache.TryGet(key);
        if (cached is null)
        {
            return NotFound();
        }

        var convert = UpscaleCache.IsJpegXl(cached) && jxl != "1";
        var representation = UpscaleCache.IsJpegXl(cached) ? convert ? "-webp" : "-jxl" : "";
        var etag = new EntityTagHeaderValue(
            $"\"{key.ChapterFileId}-{key.ArchiveVersion}-{key.AbsIndex}-{key.Model}-{UpscaleCache.Generation}{representation}\"");
        SetPageCacheControl(slice);
        // A pipeline change raises the generation; a URL without the current one must not be kept for a year.
        if (Request.Query["g"] != UpscaleCache.Generation)
        {
            Response.Headers.CacheControl = "private, no-cache";
        }
        if (Request.GetTypedHeaders().IfNoneMatch?.Any(t => t.Compare(etag, useStrongComparison: false)) == true)
        {
            return StatusCode(StatusCodes.Status304NotModified);
        }

        if (convert)
        {
            // The hub-down poller only asks whether the page is there; a conversion would be thrown away.
            if (HttpMethods.IsHead(Request.Method))
            {
                Response.Headers.ETag = etag.ToString();
                Response.ContentType = "image/webp";
                return Ok();
            }

            byte[] bytes;
            try
            {
                bytes = await System.IO.File.ReadAllBytesAsync(cached, ct);
            }
            catch (IOException)
            {
                // Evicted since TryGet: the reader's next request queues it again.
                return NotFound();
            }

            UpscaledImage converted;
            try
            {
                // A conversion peaks at about 7 bytes a pixel (49 MB for a 7.5 MP page), twice what the
                // gate's pixel budget assumes.
                var (width, height) = JpegXl.ReadSize(bytes);
                converted = await ImageWorkGate.RunAsync(
                    () => Task.FromResult(ImageCodec.TranscodeUpscaledJpegXl(bytes)), 2 * ImageWorkGate.Weigh(width, height), ct);
            }
            catch (JpegXlException ex)
            {
                // Dropped, or the reader would read the 404 as an eviction, ask again, find the same file
                // cached and loop. Without it the page is made again.
                logger.LogInformation(ex, "Dropped the unreadable upscaled page {Path}", cached);
                cache.Discard(cached);
                return NotFound();
            }
            catch (NetVips.VipsException ex)
            {
                logger.LogInformation(ex, "Converting the upscaled page {Path} to WebP failed", cached);
                return StatusCode(StatusCodes.Status500InternalServerError);
            }

            PageCache.DropAfterScan(cached);
            return File(converted.Bytes, converted.Extension == "webp" ? "image/webp" : "image/jpeg",
                lastModified: null, entityTag: etag);
        }

        // The browser keeps the page as immutable, so the server rarely reads it again; without this its
        // pages stay in the container's page cache up to the upscale cache cap.
        Response.OnCompleted(() =>
        {
            PageCache.DropAfterScan(cached);
            return Task.CompletedTask;
        });
        return PhysicalFile(cached, UpscaleCache.ContentType(cached), lastModified: null, entityTag: etag, enableRangeProcessing: false);
    }

    [HttpGet("chapter/{id:int}/thumb/{page:int}")]
    public async Task<IActionResult> Thumbnail(int id, int page, CancellationToken ct)
    {
        var slice = await reader.PageSliceAsync(id, ct);
        if (slice is null || page < 0 || page >= slice.PageCount)
        {
            return NotFound();
        }

        var cached = await thumbnails.GetOrRenderAsync(slice, page, ct);
        if (cached is null)
        {
            return NotFound();
        }

        SetPageCacheControl(slice);
        return PhysicalFile(cached, "image/jpeg");
    }

    /// <summary>
    /// Where the chapter's recap of the previous chapter ends, for the reader's "Skip recap". Both
    /// fields are null when there is no previous chapter on disk or no recap was found. The search
    /// decodes about 35 pages the first time, so the reader asks ahead while the previous chapter ends.
    /// </summary>
    [HttpGet("chapter/{id:int}/recap")]
    public async Task<IActionResult> Recap(int id, [FromServices] ReaderRecapCache recaps, CancellationToken ct)
    {
        var slice = await reader.SliceAsync(id, ct);
        if (slice is null)
        {
            return this.NotFoundMessage(localizer, "error.reader.chapterNotReadable");
        }

        var (previousId, _) = await reader.NeighboursAsync(slice.Chapter, ct);
        var previous = previousId is int before ? await reader.SliceAsync(before, ct) : null;
        var end = previous is null ? null : await recaps.FindAsync(slice, previous, ct);
        return Ok(new { page = end?.Page, offset = end?.Offset });
    }

    /// <summary>
    /// The credit, recruitment and notice pages the reader leaves out of the chapter: whole pages at
    /// its start and end, and how far down the first page it shows a banner fused into that page ends.
    /// All zero when nothing repeats at the chapter's edges. The first search decodes the edges of up
    /// to seven chapters, so the reader asks ahead while the previous chapter ends. The manifest's
    /// <c>pageVersion</c> comes back too: the answer belongs to that file, and a replaced file needs its own.
    /// </summary>
    [HttpGet("chapter/{id:int}/credits")]
    public async Task<IActionResult> Credits(int id, [FromServices] ReaderCreditsCache credits,
        [FromServices] CreditStore store, [FromServices] IAppSettings settings, CancellationToken ct)
    {
        var slice = await reader.SliceAsync(id, ct);
        if (slice is null)
        {
            return this.NotFoundMessage(localizer, "error.reader.chapterNotReadable");
        }

        // Only a reader hiding credit pages asks, so this is what turns the background scan on.
        await CreditScanWorker.WantAsync(settings, ct);
        var found = CreditPages.None;
        if (ReaderCreditsCache.Applies(slice))
        {
            found = await store.GetAsync(slice, ct) ?? await SearchCreditsAsync(slice, credits, ct);
        }

        return Ok(new
        {
            headPages = found.HeadPages, headOffset = found.HeadOffset, tailPages = found.TailPages,
            pageVersion = PageVersion(slice.ChapterFileId, slice.ArchiveVersion),
        });
    }

    private async Task<CreditPages> SearchCreditsAsync(ReaderService.ChapterSlice slice, ReaderCreditsCache credits, CancellationToken ct)
    {
        var (before, after) = await reader.NearbySlicesAsync(
            slice.Chapter, CreditDetector.References, ReaderCreditsCache.Usable, ct);
        return await credits.FindAsync(slice, [.. before, .. after], ct);
    }

    /// <summary>
    /// Starts the credit page search for a chapter the reader is likely to open next (the series
    /// page's and Home's Continue), when the series' resolved settings hide credit pages and no answer
    /// is stored yet, and returns at once. The reader's own <see cref="Credits"/> request then finds
    /// the answer ready or joins the run. The settings are resolved here because the client only
    /// learns them from the manifest.
    /// </summary>
    [HttpPost("chapter/{id:int}/credits/warm")]
    public async Task<IActionResult> WarmCredits(int id, [FromServices] ReaderCreditsCache credits,
        [FromServices] CreditStore store, [FromServices] IAppSettings settings, CancellationToken ct)
    {
        var slice = await reader.SliceAsync(id, ct);
        if (slice is null || !ReaderCreditsCache.Applies(slice) || !(await profiles.ResolveAsync(slice.Series.Id, ct)).Prefs.HideCredits)
        {
            return NoContent();
        }

        await CreditScanWorker.WantAsync(settings, ct);
        if (await store.GetAsync(slice, ct) is not null)
        {
            return NoContent();
        }

        var (before, after) = await reader.NearbySlicesAsync(
            slice.Chapter, CreditDetector.References, ReaderCreditsCache.Usable, ct);
        credits.Warm(slice, [.. before, .. after]);
        return Accepted();
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

        // A completing write from a reader holding a stale manifest is clamped by the service rather than
        // refused, so the completion is not lost; only a plain position past the end is rejected.
        if (request.Completed != true && !ReaderService.IsPageInRange(request.PageIndex, slice.PageCount))
        {
            return this.Fail(localizer, "error.reader.pageOutOfRange");
        }

        if (!ReaderService.IsValidOpId(request.OpId))
        {
            return this.Fail(localizer, "error.reader.invalidOpId");
        }

        var finished = await reader.SaveProgressAsync(
            slice, request.PageIndex, request.Completed,
            new ReaderService.TimeReport(request.Seconds ?? 0, request.Final ?? false), ct,
            request.ClientTime, request.OpId, request.PageOffset);

        // After the save, so the evaluation sees the finished chapter. Whatever it unlocks reaches
        // the reader through the inbox push rather than this response.
        if (finished && db.Scope.UserId is var userId and not 0)
        {
            achievementQueue.Enqueue(userId);
        }

        return Ok(new
        {
            chapterId = id,
            pageIndex = ReaderService.ClampPage(request.PageIndex, slice.PageCount),
            completed = finished || request.Completed == true,
        });
    }

    [HttpPost("chapter/{id:int}/read")]
    public async Task<IActionResult> MarkRead(int id, CancellationToken ct)
    {
        // The same silent tick as the chapter table's select mode: no event, no reading time.
        if (await reader.MarkReadAsync([id], ct) == 0)
        {
            return NotFound();
        }

        return Ok(new { chapterId = id, completed = true });
    }

    [HttpPost("chapter/{id:int}/unread")]
    public async Task<IActionResult> MarkUnread(int id, CancellationToken ct)
    {
        await reader.ClearProgressAsync(id, ct);
        return Ok(new { chapterId = id, completed = false });
    }

    /// <summary>Largest set one call will act on. Bounds the <c>read</c> pass, which still opens
    /// the archive of any chapter whose page count was never measured.</summary>
    internal const int MaxBulkChapters = 2000;

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
            return this.Fail(localizer, "error.reader.tooManyChapters", new { max = MaxBulkChapters });
        }

        var state = (req.State ?? string.Empty).ToLowerInvariant();
        if (state is not ("read" or "watched" or "unread"))
        {
            return this.Fail(localizer, "error.reader.invalidState");
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

        if (state == "unread")
        {
            await reader.ClearProgressAsync(visible, ct);
            return Ok(new { updated = visible.Count });
        }

        return Ok(new { updated = await reader.MarkReadAsync(visible, ct) });
    }

    public record SetSeriesStateRequest(string State);

    /// <summary>
    /// Marks every chapter of a series read or unread, for the Library's bulk action. Read is the
    /// chapter table's silent tick, so only chapters with a file are marked.
    /// </summary>
    [HttpPost("series/{seriesId:int}/state")]
    public async Task<IActionResult> SetSeriesState(int seriesId, SetSeriesStateRequest req, CancellationToken ct)
    {
        var state = (req.State ?? string.Empty).ToLowerInvariant();
        if (state is not ("read" or "unread"))
        {
            return this.Fail(localizer, "error.reader.invalidState");
        }

        if (!await db.Series.AnyAsync(s => s.Id == seriesId, ct))
        {
            return NotFound();
        }

        var ids = await db.Chapters.Where(c => c.SeriesId == seriesId).Select(c => c.Id).ToListAsync(ct);
        if (state == "unread")
        {
            await reader.ClearProgressAsync(ids, ct);
            return Ok(new { updated = ids.Count });
        }

        return Ok(new { updated = await reader.MarkReadAsync(ids, ct) });
    }

    /// <summary>Largest set one <see cref="ChapterVersions"/> call answers.</summary>
    internal const int MaxVersionChapters = 500;

    /// <summary>
    /// The manifest's <c>pageVersion</c> for each requested chapter the caller can still read, so a
    /// device can check the chapters it saved offline in one call. A chapter that does not exist,
    /// has no file record, or is outside the caller's root folders is left out, and the device
    /// drops it. A chapter whose file cannot be read right now (an unmounted root) comes back with
    /// a null <c>pageVersion</c>, so a storage outage never wipes the device's copies.
    /// </summary>
    [HttpPost("chapters/versions")]
    public async Task<IActionResult> ChapterVersions(ChapterVersionsRequest req, CancellationToken ct)
    {
        var ids = (req.Ids ?? []).Distinct().ToArray();
        if (ids.Length > MaxVersionChapters)
        {
            return this.Fail(localizer, "error.reader.tooManyChapters", new { max = MaxVersionChapters });
        }

        var versions = await reader.ArchiveVersionsAsync(ids, ct);
        return Ok(versions.Select(v => new { id = v.ChapterId, pageVersion = v.ArchiveVersion is null ? null : PageVersion(v.ChapterFileId, v.ArchiveVersion) }));
    }

    /// <summary>
    /// Whether the built-in reader has ever been used, and whether Kavita is connected. The UI ORs
    /// the two to decide whether to show read progress at all: the Kavita check alone used to be
    /// that gate and would hide a reader-only user's progress. Kavita's state is answered here
    /// rather than read off <c>GET settings/kavita</c>, which is admin-only and 403'd every page
    /// load for everyone else.
    /// </summary>
    [HttpGet("used")]
    public async Task<IActionResult> Used(CancellationToken ct) =>
        Ok(new
        {
            used = await db.ChapterProgress.AnyAsync(ct),
            kavita = !string.IsNullOrWhiteSpace(await appSettings.GetAsync(SettingKeys.KavitaUrl, ct)) &&
                     !string.IsNullOrWhiteSpace(await appSettings.GetAsync(SettingKeys.KavitaApiKey, ct))
        });

    /// <summary>
    /// Imports read status from Kavita. Runs in the background — a large library is one Kavita
    /// call per series — so this returns immediately and the UI polls <c>GET import/kavita</c>.
    /// </summary>
    [HttpPost("import/kavita")]
    public async Task<IActionResult> StartKavitaImport(CancellationToken ct)
    {
        if (!await MayUseKavitaImportAsync(ct))
        {
            return this.Forbidden(localizer, "error.reader.kavitaImportForbidden");
        }

        return readImport.Start()
            ? Accepted(new { started = true })
            : this.Conflict(localizer, "error.reader.importAlreadyRunning");
    }

    [HttpGet("import/kavita")]
    public async Task<IActionResult> KavitaImportStatus(CancellationToken ct)
    {
        if (!await MayUseKavitaImportAsync(ct))
        {
            return this.Forbidden(localizer, "error.reader.kavitaImportForbidden");
        }

        // The raw text comes from outside Maki and can carry the Kavita URL, so only an admin sees
        // it. It is already logged by the import service.
        var state = readImport.State;
        string? error = state.ErrorKey is { } key
            ? localizer.Get(key)
            : state.RawError is null
                ? null
                : currentUser.Has(MakiPermission.Admin)
                    ? state.RawError
                    : localizer.Get("error.reader.kavitaImportFailed");
        return Ok(new
        {
            running = state.Running,
            finishedAt = state.FinishedAt,
            result = state.Result,
            error,
        });
    }

    /// <summary>The import writes the Kavita-bound user's progress, so only that user or an admin may run it.</summary>
    private async Task<bool> MayUseKavitaImportAsync(CancellationToken ct) =>
        currentUser.Has(MakiPermission.Admin) || await kavitaUser.ResolveAsync(ct) == currentUser.UserId;

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
        var chapter = await db.Chapters.AsNoTracking().Select(c => new { c.Id, c.SeriesId }).FirstOrDefaultAsync(c => c.Id == id, ct);
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

        var slice = await reader.SliceAsync(id, ct);
        if (slice is null)
        {
            return this.NotFoundMessage(localizer, "error.reader.chapterNotReadable");
        }

        if (!ReaderService.IsPageInRange(page, slice.PageCount))
        {
            return this.Fail(localizer, "error.reader.pageOutOfRange");
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
            .AsNoTracking()
            .Where(p => p.SeriesId == seriesId && !p.Completed && p.UnreadAt == null && p.PageIndex > 0 &&
                        db.Chapters.Any(c => c.Id == p.ChapterId && c.ChapterFileId != null))
            .OrderByDescending(p => p.UpdatedAt)
            .FirstOrDefaultAsync(ct);
        if (inProgress is not null)
        {
            return Ok(new { chapterId = inProgress.ChapterId, page = inProgress.PageIndex });
        }

        var next = await continueReading.NextForAsync(seriesId, ct);

        return next is null
            ? this.NotFoundMessage(localizer, "error.reader.nothingToRead")
            : Ok(new { chapterId = next.ChapterId, page = 0 });
    }
}
