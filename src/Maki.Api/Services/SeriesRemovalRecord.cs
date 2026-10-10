using System.Globalization;
using System.Text.Json;
using Maki.Core.Entities;
using Maki.Data;
using Maki.Metadata.MangaBaka;
using Microsoft.EntityFrameworkCore;

namespace Maki.Api.Services;

/// <summary>
/// What removing a series leaves behind besides the cascade: the snapshot the stats feed keeps of it
/// and the recommendation counters of everyone it was an input for. Shared by
/// <c>SeriesController.Delete</c> and an import undo, so a series removed either way is recorded the
/// same way.
/// </summary>
internal static class SeriesRemovalRecord
{
    /// <summary>
    /// Taken before the hard delete: the event row must outlive the series (its FK is severed to NULL),
    /// so it carries the title, the genre and tag lists the aggregation needs later, and enough
    /// provider metadata for the stats feed to reopen it in Discover.
    /// </summary>
    public static async Task<string> PayloadAsync(
        Series series, MangaBakaLocalStore store, ILogger logger, CancellationToken ct)
    {
        string? coverUrl = null;
        if (series.MangaBakaId is int mangaBakaId && await store.IsAvailableAsync(ct))
        {
            try
            {
                coverUrl = (await store.GetDetailAsync(mangaBakaId, ct))?.CoverUrl;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Could not snapshot the provider cover for removed series {SeriesId}", series.Id);
            }
        }

        return JsonSerializer.Serialize(new
        {
            genres = series.Genres,
            tags = series.Tags,
            providerId = series.MangaBakaId?.ToString(CultureInfo.InvariantCulture),
            coverUrl,
            rootFolderId = series.RootFolderId
        });
    }

    /// <summary>
    /// Before the delete cascades the provenance rows away, while they can still say whose
    /// recommendation inputs this series was part of. Incremented in the database rather than on
    /// tracked entities: these are other people's counters, and another request of theirs may be
    /// advancing them at the same time.
    /// </summary>
    public static async Task BumpRecommendationOwnersAsync(MakiDbContext db, int seriesId, CancellationToken ct)
    {
        var owners = await db.UserSeriesStates.IgnoreQueryFilters()
            .Where(x => x.SeriesId == seriesId && x.AddedToLibraryAtUtc != null)
            .Select(x => x.UserId).Distinct().ToListAsync(ct);
        foreach (var owner in owners)
        {
            await RecommendationFeedbackService.BumpAsync(db, owner, feedback: false, signal: true, ct);
        }
    }
}
