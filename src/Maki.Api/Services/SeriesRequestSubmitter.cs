using Maki.Api.Hubs;
using Maki.Api.Localization;
using Maki.Core.Entities;
using Maki.Core.Inbox;
using Maki.Core.Metadata;
using Maki.Core.Notifications;
using Maki.Data;
using Maki.Metadata.MangaBaka;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Maki.Api.Services;

public enum SeriesRequestSubmitError
{
    MetadataNotFound,
    SeriesAlreadyExists,
    AlreadyPending,
    ContentRatingTooHigh,
}

public record SeriesRequestSubmitResult(
    SeriesRequest? Request, SeriesRequestSubmitError? Error, int? ExistingSeriesId = null);

/// <summary>
/// Files a <see cref="SeriesRequest"/> and tells the admins about it. Shared by
/// <c>SeriesRequestsController.Create</c> and the import list run, which files new-series requests
/// on behalf of a user who cannot add series themselves.
/// </summary>
public class SeriesRequestSubmitter(
    MakiDbContext db,
    IEnumerable<IMetadataProvider> metadataProviders,
    EventBroadcaster events,
    InboxService inbox,
    NotificationService notifications,
    IUserLocaleResolver locales,
    IMessageCatalog catalog,
    ILogger<SeriesRequestSubmitter> logger)
{
    /// <summary>
    /// Fills the display snapshot of a <see cref="SeriesRequestKind.NewSeries"/> request from the
    /// provider. Resolved from the provider rather than taken from the caller: the admin reviewing it
    /// has to see the title that provider id actually resolves to.
    /// </summary>
    public async Task<SeriesRequestSubmitResult?> FillNewSeriesAsync(
        SeriesRequest request, string metadataProviderId, CancellationToken ct, string? maxContentRating = null)
    {
        var metadata = await metadataProviders.First().GetAsync(metadataProviderId, ct);
        if (metadata is null)
        {
            return new(null, SeriesRequestSubmitError.MetadataNotFound);
        }

        if (maxContentRating is not null && !ContentRating.Permits(metadata.ContentRating, maxContentRating))
        {
            return new(null, SeriesRequestSubmitError.ContentRatingTooHigh);
        }

        if (metadata.MangaBakaId is int mangaBakaId)
        {
            // IgnoreQueryFilters: the library is shared, and "already there" is true regardless of
            // whether this user has been granted its root folder.
            var existing = await db.Series
                .IgnoreQueryFilters()
                .Where(s => s.MangaBakaId == mangaBakaId)
                .Select(s => (int?)s.Id)
                .FirstOrDefaultAsync(ct);

            if (existing is not null)
            {
                return new(null, SeriesRequestSubmitError.SeriesAlreadyExists, existing);
            }
        }

        request.MetadataProviderId = metadataProviderId;
        request.Title = metadata.Title;
        request.CoverUrl = metadata.CoverUrl;
        request.Year = metadata.Year;
        return null;
    }

    /// <summary>
    /// Saves a filled request unless an identical one is already pending. "Already pending" is read
    /// through the context's query filter, as the controller always did: a requester's own rows, or
    /// every user's for an admin or a background scope.
    /// <para>
    /// The upfront check narrows the common case, but two submits landing at the same instant can
    /// both pass it before either has saved. The partial unique index on the identity columns
    /// (<c>SeriesRequestPendingUnique</c> migration) is what actually stops the second row, and a
    /// violation here is read back as the same <see cref="SeriesRequestSubmitError.AlreadyPending"/>.
    /// </para>
    /// <para>
    /// The index only covers <see cref="SeriesRequestStatus.Pending"/> rows, so this check also
    /// treats a <see cref="SeriesRequestStatus.Processing"/> one as already pending: a request an
    /// admin is mid-approval on is exactly as much "already asked for" as a plain pending one, and
    /// the index has no way to stop a fresh submit while it's claimed.
    /// </para>
    /// </summary>
    public async Task<SeriesRequestSubmitResult> SubmitAsync(
        SeriesRequest request, string userName, CancellationToken ct)
    {
        // A second identical pending (or in-flight) request is noise in the admin queue, not a
        // stronger signal.
        var duplicate = await db.SeriesRequests.AnyAsync(r =>
            (r.Status == SeriesRequestStatus.Pending || r.Status == SeriesRequestStatus.Processing) &&
            r.Kind == request.Kind &&
            r.MetadataProviderId == request.MetadataProviderId &&
            r.SeriesId == request.SeriesId &&
            r.ChapterStart == request.ChapterStart &&
            r.ChapterEnd == request.ChapterEnd, ct);

        if (duplicate)
        {
            return new(null, SeriesRequestSubmitError.AlreadyPending);
        }

        db.SeriesRequests.Add(request);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException e) when (IsUniqueViolation(e))
        {
            db.Entry(request).State = EntityState.Detached;
            return new(null, SeriesRequestSubmitError.AlreadyPending);
        }

        logger.LogInformation("{User} requested {Kind} '{Title}'", userName, request.Kind, request.Title);

        await events.SeriesRequested(request.Id, request.Title, userName);
        inbox.Raise(InboxEventType.RequestSubmitted, new InboxMessage(
                Key: "inbox.request.submitted",
                Params: InboxMessage.Args(new { user = userName, title = request.Title }),
                Url: "/requests"),
            InboxAudience.Admins);

        var locale = await locales.DefaultAsync(ct);
        notifications.Dispatch(NotificationEventType.RequestSubmitted, new NotificationMessage(
            NotificationEventType.RequestSubmitted,
            Title: catalog.GetFor(locale, "notify.request.submitted.title"),
            Body: catalog.GetFor(locale, "notify.request.submitted.body", new
            {
                user = userName,
                series = request.Title,
                range = RangeKind(request.ChapterStart, request.ChapterEnd),
                start = ChapterLabel(request.ChapterStart),
                end = ChapterLabel(request.ChapterEnd),
            }),
            SeriesTitle: request.Title,
            SeriesId: request.SeriesId,
            Url: "/requests"));

        return new(request, null);
    }

    /// <summary>Which shape a chapter range takes, as a select key for <c>notify.request.submitted.body</c>.</summary>
    private static string RangeKind(decimal? start, decimal? end) => (start, end) switch
    {
        (null, null) => "all",
        (not null, null) => "from",
        (null, not null) => "upTo",
        _ => "between",
    };

    private static string ChapterLabel(decimal? number) =>
        number?.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;

    /// <summary>
    /// Shared with the controller, whose Edit and release paths hit the same index.
    /// <para>
    /// Takes the base <see cref="Exception"/> type because the two call shapes don't wrap the same
    /// way: <c>SaveChangesAsync</c> goes through the change tracker and wraps the provider's
    /// exception in a <see cref="DbUpdateException"/>, but <c>ExecuteUpdateAsync</c> executes the
    /// statement directly and lets the provider's own exception through unwrapped.
    /// </para>
    /// </summary>
    internal static bool IsUniqueViolation(Exception e) => e switch
    {
        DbUpdateException { InnerException: SqliteException { SqliteExtendedErrorCode: 2067 or 1555 } } => true,
        SqliteException { SqliteExtendedErrorCode: 2067 or 1555 } => true,
        _ => false,
    };
}
