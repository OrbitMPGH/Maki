using Maki.Api.Hubs;
using Maki.Api.Localization;
using Maki.Core.Entities;
using Maki.Core.Inbox;
using Maki.Core.Metadata;
using Maki.Core.Notifications;
using Maki.Data;
using Microsoft.EntityFrameworkCore;

namespace Maki.Api.Services;

public enum SeriesRequestSubmitError
{
    MetadataNotFound,
    SeriesAlreadyExists,
    AlreadyPending,
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
        SeriesRequest request, string metadataProviderId, CancellationToken ct)
    {
        var metadata = await metadataProviders.First().GetAsync(metadataProviderId, ct);
        if (metadata is null)
        {
            return new(null, SeriesRequestSubmitError.MetadataNotFound);
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
    /// </summary>
    public async Task<SeriesRequestSubmitResult> SubmitAsync(
        SeriesRequest request, string userName, CancellationToken ct)
    {
        // A second identical pending request is noise in the admin queue, not a stronger signal.
        var duplicate = await db.SeriesRequests.AnyAsync(r =>
            r.Status == SeriesRequestStatus.Pending &&
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
        await db.SaveChangesAsync(ct);

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
}
