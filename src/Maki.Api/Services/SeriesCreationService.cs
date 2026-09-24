using Maki.Api.Localization;
using Maki.Core.Configuration;
using Maki.Core.Entities;
using Maki.Core.Metadata;
using Maki.Core.Naming;
using Maki.Core.Notifications;
using Maki.Data;
using Maki.Data.Identity;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;

namespace Maki.Api.Services;

/// <summary>
/// Why adding a series failed, when it failed before the row was written. Once the row exists the
/// remaining steps degrade into <see cref="SeriesCreationResult.Warnings"/> instead — a series with
/// no folder on disk looks fine until a download lands, so it has to be said out loud, but it is not
/// worth throwing away a committed row over.
/// </summary>
public enum SeriesCreationError
{
    RootFolderNotFound,
    MetadataNotFound,
    AlreadyInLibrary,

    /// <summary>
    /// This mutation id belongs to an add that committed and whose series has since been removed.
    /// <para>
    /// Distinct from <see cref="AlreadyInLibrary"/> because it is not: replaying the id cannot hand
    /// back a row that no longer exists, and answering "already in library" sends the caller looking
    /// for a series nobody can find. Creating a fresh one instead is also wrong — the id is the only
    /// thing standing between a retried request and a second copy, and honouring it once the first
    /// result is gone means honouring it for a delete that raced the retry too. Adding the title
    /// again is a new decision, so it takes a new id.
    /// </para>
    /// </summary>
    OperationResultGone,

    /// <summary>This mutation id was already used for a different add.</summary>
    MutationIdReused,
}

public record SeriesCreationResult(Series? Series, SeriesCreationError? Error, List<string> Warnings,
    bool Replayed = false)
{
    public static SeriesCreationResult Failed(SeriesCreationError error) => new(null, error, []);

    /// <summary>
    /// The mutation id an approved request's add operates under.
    /// <para>
    /// Derived from the request rather than supplied by the approving client, because there is no
    /// client-side operation to derive one from: approval is one admin action and its retry is
    /// another admin pressing the same button. A stable id gives that path the same receipt and the
    /// same creation transaction an ordinary add gets, so a crash between committing the series and
    /// resolving the request cannot end with two series or two credited adds.
    /// </para>
    /// </summary>
    public static Guid MutationIdFor(SeriesRequest request) =>
        new(SHA256.HashData(Encoding.UTF8.GetBytes($"series-request:{request.Id}")).AsSpan(0, 16));
}

/// <summary>
/// Creates a library series from a metadata provider id: the row, its folder, its cover, its source
/// mappings and the first chapter sync.
/// <para>
/// Extracted from <c>SeriesController.Add</c> because approving a
/// <see cref="SeriesRequestKind.NewSeries"/> request has to do exactly the same thing. Two copies
/// would drift, and the half that drifted would be the one nobody with an admin account exercises.
/// </para>
/// </summary>
public class SeriesCreationService(
    MakiDbContext db,
    IEnumerable<IMetadataProvider> metadataProviders,
    CoverService coverService,
    SourceMatchService sourceMatchService,
    ChapterSyncService chapterSyncService,
    SourceMatchQueue sourceMatchQueue,
    StatsEventService stats,
    SeriesIdentityService identity,
    IAppSettings appSettings,
    NamingService naming,
    NotificationService notifications,
    IUserLocaleResolver locales,
    IMessageCatalog catalog,
    ILogger<SeriesCreationService> logger)
{
    /// <param name="deferSourceMatching">
    /// Hand auto-matching to <see cref="SourceMatchWorkerHostedService"/> instead of awaiting it.
    /// Searching every source plus the first chapter sync is tens of seconds, which is the whole
    /// cost of the Add button, so the interactive path defers and the series page renders the wait.
    /// Callers that need the chapter list to exist by the time this returns — approving a series
    /// request queues a chapter range straight afterwards — must leave it false.
    /// </param>
    /// <param name="incognito">
    /// An explicit <see cref="IncognitoMode"/> name from the caller, which always wins. Null means
    /// "decide from the content rating" — see <see cref="IncognitoRatingRules"/>, which is also what
    /// an API client that never heard of the field gets.
    /// </param>
    public async Task<SeriesCreationResult> CreateAsync(
        string metadataProviderId,
        int rootFolderId,
        bool monitored,
        string monitorNewItems,
        CancellationToken ct,
        bool deferSourceMatching = false,
        string? incognito = null,
        int? attributedUserId = null,
        string? addedFrom = null,
        Guid? clientMutationId = null,
        SeriesRequest? originatingRequest = null)
    {
        var payloadHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            $"{metadataProviderId}|{rootFolderId}|{monitored}|{monitorNewItems}|{incognito}|{addedFrom}")));
        if (clientMutationId is { } priorId && attributedUserId is > 0)
        {
            var prior = await db.RecommendationMutationReceipts.IgnoreQueryFilters()
                .FirstOrDefaultAsync(x => x.UserId == attributedUserId && x.ClientMutationId == priorId, ct);
            if (prior is not null)
            {
                if (prior.Operation != "add" || prior.PayloadHash != payloadHash)
                    return SeriesCreationResult.Failed(SeriesCreationError.MutationIdReused);
                if (int.TryParse(prior.ResultJson, out var priorSeriesId))
                {
                    // Scoped, not IgnoreQueryFilters. The receipt proves this caller made the add; it
                    // does not prove they can still reach where it landed, and replaying a series
                    // out of a root folder their access was since revoked would hand it back anyway.
                    var priorSeries = await db.Series.FirstOrDefaultAsync(x => x.Id == priorSeriesId, ct);
                    if (priorSeries is not null) return new SeriesCreationResult(priorSeries, null, [], Replayed: true);
                }
                return SeriesCreationResult.Failed(SeriesCreationError.OperationResultGone);
            }
        }
        var rootFolder = await db.RootFolders.FindAsync([rootFolderId], ct);
        if (rootFolder is null)
        {
            return SeriesCreationResult.Failed(SeriesCreationError.RootFolderNotFound);
        }

        var provider = metadataProviders.First();
        var metadata = await provider.GetAsync(metadataProviderId, ct);
        if (metadata is null)
        {
            return SeriesCreationResult.Failed(SeriesCreationError.MetadataNotFound);
        }

        if (metadata.MangaBakaId is int existingId &&
            await db.Series.AnyAsync(s => s.MangaBakaId == existingId, ct))
        {
            return SeriesCreationResult.Failed(SeriesCreationError.AlreadyInLibrary);
        }

        var series = SeriesMetadataMapper.NewFromMetadata(metadata);
        // Monitoring is only the mode now, so an unmonitored add is simply mode None —
        // there's no separate flag left for it to contradict.
        series.MonitorNewItems = await DefaultedMonitorMode(
            !monitored
                ? NewChapterMonitorMode.None
                : Enum.TryParse<NewChapterMonitorMode>(monitorNewItems, true, out var mode)
                    ? mode
                    : NewChapterMonitorMode.All, ct);
        // An explicit choice from the add form wins, including an explicit "Off" over a rule that
        // would have hidden it. Only an absent value consults the per-rating rules.
        series.Incognito = Enum.TryParse<IncognitoMode>(incognito, true, out var explicitMode)
            ? explicitMode
            : IncognitoRatingRules.Resolve(
                IncognitoRatingRules.Parse(
                    await appSettings.GetAsync(SettingKeys.LibraryIncognitoByRating, ct)),
                series.ContentRating);
        series.RootFolderId = rootFolder.Id;
        series.FolderName = await naming.BuildSeriesFolderNameAsync(series, ct);
        series.SourceMatchPending = deferSourceMatching;

        await using var creationTransaction = clientMutationId is not null && attributedUserId is > 0
            ? await db.Database.BeginTransactionAsync(ct) : null;
        int? bumpSignalFor = null;
        db.Series.Add(series);
        if (originatingRequest is not null)
        {
            originatingRequest.Series = series;
            originatingRequest.Title = series.Title;
        }
        if (attributedUserId is > 0 && series.Incognito != IncognitoMode.Full && series.MangaBakaId is not null)
        {
            var now = DateTime.UtcNow;
            db.UserSeriesStates.Add(new UserSeriesState
            {
                UserId = attributedUserId.Value, Series = series,
                AddedToLibraryAtUtc = now,
                AddedFrom = addedFrom is "recommendation" or "request" or ImportListService.AddedFrom ? addedFrom : "library",
                UpdatedAt = now
            });
            bumpSignalFor = attributedUserId.Value;
        }
        await db.SaveChangesAsync(ct);
        // After the save, so the provenance row the revision announces is already there. Inside the
        // creation transaction when there is one, so a retry cannot see one without the other.
        if (bumpSignalFor is { } provenanceOwner)
        {
            await RecommendationFeedbackService.BumpAsync(
                db, provenanceOwner, feedback: false, signal: true, ct);
        }
        if (creationTransaction is not null && clientMutationId is { } mutationId)
        {
            db.RecommendationMutationReceipts.Add(new RecommendationMutationReceipt
            {
                UserId = attributedUserId!.Value, ClientMutationId = mutationId, Operation = "add",
                ProviderId = series.MangaBakaId ?? 0, PayloadHash = payloadHash,
                ResultJson = series.Id.ToString(), ExpiresAtUtc = DateTime.UtcNow.AddDays(90)
            });
            await db.SaveChangesAsync(ct);
            await creationTransaction.CommitAsync(ct);
        }

        await NotifyAddedAsync(series, originatingRequest, ct);

        // The series row is already committed, so these steps can't fail the request — but they
        // can't be swallowed either. Collect what went wrong and hand it back with the result.
        var warnings = new List<string>();

        try
        {
            // Before the add event, so a series removed and put back reads as one continuous history.
            await identity.AdoptOrphansAsync(series, ct);
            await stats.RecordAsync(StatsEventType.SeriesAdded, series.Id, series.Title, ct: ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Post-creation history setup failed for {Title}", series.Title);
            warnings.Add($"Could not finish library history setup: {ex.Message}");
        }

        var seriesFolder = Path.Combine(rootFolder.Path, series.FolderName);
        try
        {
            Directory.CreateDirectory(seriesFolder);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not create series folder for {Title}", series.Title);
            warnings.Add($"Could not create the series folder ({seriesFolder}): {ex.Message}");
        }

        if (metadata.CoverUrl != null)
        {
            try
            {
                var coverPath = await coverService.DownloadCoverAsync(series.Id, metadata.CoverUrl, ct);
                if (coverPath != null)
                {
                    series.CoverPath = coverPath;
                    await db.SaveChangesAsync(ct);
                    await coverService.WriteLibraryCoverAsync(series.Id, seriesFolder, ct);
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Cover setup failed for {Title}", series.Title);
                warnings.Add($"Could not finish cover setup: {ex.Message}");
            }
        }

        // Link site sources by title match, then pull the initial chapter list. Enqueued rather
        // than awaited when the caller can live without the chapter list being there on return:
        // the flag is already committed on the row above, so the worker picks it up even if it
        // only starts running after a restart.
        if (deferSourceMatching)
        {
            try { sourceMatchQueue.Enqueue(series.Id); }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Could not schedule source matching for {Title}", series.Title);
                warnings.Add($"Could not schedule source matching: {ex.Message}");
            }
        }
        else
        {
            try
            {
                var mapped = await sourceMatchService.AutoMatchAsync(series, ct);
                if (mapped.Count > 0)
                {
                    await chapterSyncService.SyncSeriesAsync(series.Id, ct);
                }
                else
                {
                    await SourceMatchWorkerHostedService.NotifyManualMatchNeededAsync(
                        notifications, locales, catalog, series, logger, ct);
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Auto source matching failed for {Title}", series.Title);
                warnings.Add($"Could not match sources automatically: {ex.Message}. Link a source manually from the series page.");
            }
        }

        return new SeriesCreationResult(series, null, warnings);
    }

    private async Task NotifyAddedAsync(Series series, SeriesRequest? request, CancellationToken ct)
    {
        try
        {
            var requester = request is null
                ? null
                : await db.Users.Where(u => u.Id == request.UserId)
                    .Select(u => u.DisplayName ?? u.UserName).FirstOrDefaultAsync(ct);
            var locale = await locales.DefaultAsync(ct);
            notifications.Dispatch(NotificationEventType.SeriesAdded, new NotificationMessage(
                NotificationEventType.SeriesAdded,
                Title: catalog.GetFor(locale, "notify.series.added.title"),
                Body: catalog.GetFor(locale, "notify.series.added.body", new
                {
                    series = series.Title,
                    hasRequester = requester is null ? "no" : "yes",
                    user = requester ?? string.Empty,
                }),
                SeriesTitle: series.Title,
                SeriesId: series.Id,
                Url: $"/series/{series.Id}"));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Could not send the series added notification for {Title}", series.Title);
        }
    }

    private async Task<NewChapterMonitorMode> DefaultedMonitorMode(NewChapterMonitorMode requested, CancellationToken ct) =>
        requested == NewChapterMonitorMode.All &&
        await appSettings.GetAsync(SettingKeys.MonitoringUnmonitorSpecials, ct) == "true"
            ? NewChapterMonitorMode.MainOnly
            : requested;
}
