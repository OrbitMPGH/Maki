using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Maki.Core.Entities;
using Maki.Core.Security;
using Maki.Data;
using Maki.Metadata.MangaBaka;
using Microsoft.EntityFrameworkCore;

namespace Maki.Api.Services;

public record PlanToReadCommand(string? Origin, Guid ClientMutationId);

/// <param name="RequestStatus"><c>pending</c> while the caller has an open request for it, else null.</param>
public record PlanToReadEntryDto(long ProviderId, string Title, string? CoverUrl, string Origin,
    DateTime AddedAtUtc, int? InLibrarySeriesId, string? RequestStatus);

/// <summary>
/// The caller's "Want to read" list. Deliberately writes no <see cref="RecommendationFeedbackEvent"/>:
/// the feedback history reads every event as a feedback state, and a save is not one.
/// </summary>
public class PlanToReadService(
    MakiDbContext db, MangaBakaLocalStore catalogue, ICurrentUser currentUser, HiddenContentService hidden)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly string[] Origins = ["taste", "trending", "manual"];

    public async Task<IReadOnlyList<PlanToReadEntryDto>> ListAsync(CancellationToken ct = default)
    {
        var allowed = ContentRating.Allowed(currentUser.MaxContentRating);
        var rows = await db.PlanToReadEntries.AsNoTracking()
            .Where(x => x.UserId == currentUser.UserId &&
                (x.ContentRating == null || allowed.Contains(x.ContentRating)))
            .OrderByDescending(x => x.AddedAtUtc).ThenByDescending(x => x.Id)
            .ToListAsync(ct);
        if (rows.Count == 0)
        {
            return [];
        }

        var isHidden = await hidden.PredicateAsync(ct);
        return await ToDtosAsync(rows.Where(x => isHidden?.Invoke(x.ProviderId) != true).ToList(), ct);
    }

    public async Task<PlanToReadEntryDto> AddAsync(long id, PlanToReadCommand command, CancellationToken ct = default)
    {
        if (id <= 0) throw new FeedbackValidationException("error.planToRead.invalidTitle");
        if (command.ClientMutationId == Guid.Empty)
            throw new FeedbackValidationException("error.feedback.mutationIdRequired");
        var origin = string.IsNullOrWhiteSpace(command.Origin) ? "manual" : command.Origin.Trim().ToLowerInvariant();
        if (!Origins.Contains(origin)) throw new FeedbackValidationException("error.planToRead.unsupportedOrigin");

        var userId = currentUser.UserId;
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"plan|{id}|{origin}")));
        if (await ReplayAsync(userId, command.ClientMutationId, hash, ct) is { } earlier)
        {
            return earlier;
        }

        // The dump is read before the transaction opens so the SQLite write lock is never held
        // across a catalogue lookup.
        var resolved = await db.PlanToReadEntries.AsNoTracking().AnyAsync(
            x => x.UserId == userId && x.Provider == "mangabaka" && x.ProviderId == id, ct)
            ? null
            : await ResolveAsync(id, origin, ct);

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        if (await ReplayAsync(userId, command.ClientMutationId, hash, ct) is { } raced)
        {
            return raced;
        }

        var entry = await db.PlanToReadEntries.FirstOrDefaultAsync(
            x => x.UserId == userId && x.Provider == "mangabaka" && x.ProviderId == id, ct);
        if (entry is null)
        {
            entry = resolved ?? throw new FeedbackConflictException("error.planToRead.changed");
            db.PlanToReadEntries.Add(entry);
            await db.SaveChangesAsync(ct);
        }

        var result = (await ToDtosAsync([entry], ct))[0];
        db.RecommendationMutationReceipts.Add(new RecommendationMutationReceipt
        {
            UserId = userId, ClientMutationId = command.ClientMutationId, Operation = "plan",
            ProviderId = id, PayloadHash = hash, ResultJson = JsonSerializer.Serialize(result, Json),
            ExpiresAtUtc = DateTime.UtcNow.AddDays(90)
        });
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return result;
    }

    public async Task RemoveAsync(long id, CancellationToken ct = default) =>
        await db.PlanToReadEntries
            .Where(x => x.UserId == currentUser.UserId && x.Provider == "mangabaka" && x.ProviderId == id)
            .ExecuteDeleteAsync(ct);

    private async Task<PlanToReadEntryDto?> ReplayAsync(int userId, Guid mutationId, string hash, CancellationToken ct)
    {
        var receipt = await db.RecommendationMutationReceipts.AsNoTracking().FirstOrDefaultAsync(
            x => x.UserId == userId && x.ClientMutationId == mutationId, ct);
        if (receipt is null)
        {
            return null;
        }

        if (receipt.Operation != "plan" || receipt.PayloadHash != hash)
            throw new FeedbackConflictException("error.feedback.mutationIdReused");
        return JsonSerializer.Deserialize<PlanToReadEntryDto>(receipt.ResultJson, Json)!;
    }

    private async Task<PlanToReadEntry> ResolveAsync(long id, string origin, CancellationToken ct)
    {
        if (!await catalogue.IsAvailableAsync(ct))
            throw new FeedbackMetadataUnavailableException("error.feedback.catalogueUnavailable");
        var detail = await catalogue.GetDetailAsync(id, ct)
            ?? throw new FeedbackNotFoundException("error.feedback.unknownTitle");
        if (detail.ProviderId != id.ToString() ||
            detail.ContentRating is not null &&
            !ContentRating.Allowed(currentUser.MaxContentRating).Contains(detail.ContentRating))
            throw new FeedbackValidationException("error.feedback.titleNotAvailable");
        var card = (await catalogue.GetByIdsAsync([id], null, ct)).FirstOrDefault();
        return new PlanToReadEntry
        {
            UserId = currentUser.UserId, ProviderId = id, Title = detail.Title, Origin = origin,
            CoverUrl = card?.ThumbUrl ?? card?.CoverUrl ?? detail.CoverUrl,
            ContentRating = detail.ContentRating, AddedAtUtc = DateTime.UtcNow,
        };
    }

    private async Task<List<PlanToReadEntryDto>> ToDtosAsync(IReadOnlyList<PlanToReadEntry> rows, CancellationToken ct)
    {
        var ids = rows.Where(x => x.ProviderId <= int.MaxValue).Select(x => (int)x.ProviderId).ToList();
        var series = (await db.Series.AsNoTracking()
                .Where(s => s.MangaBakaId != null && ids.Contains(s.MangaBakaId.Value))
                .Select(s => new { s.Id, MangaBakaId = s.MangaBakaId!.Value })
                .ToListAsync(ct))
            .GroupBy(s => (long)s.MangaBakaId).ToDictionary(g => g.Key, g => g.Min(s => s.Id));
        var wanted = rows.Select(x => x.ProviderId.ToString()).ToList();
        var requested = (await db.SeriesRequests.AsNoTracking()
                .Where(r => r.UserId == currentUser.UserId && r.Kind == SeriesRequestKind.NewSeries &&
                    (r.Status == SeriesRequestStatus.Pending || r.Status == SeriesRequestStatus.Processing) &&
                    r.MetadataProviderId != null && wanted.Contains(r.MetadataProviderId))
                .Select(r => r.MetadataProviderId!).ToListAsync(ct))
            .ToHashSet();
        return rows.Select(x => new PlanToReadEntryDto(x.ProviderId, x.Title, x.CoverUrl, x.Origin, x.AddedAtUtc,
            series.TryGetValue(x.ProviderId, out var seriesId) ? seriesId : null,
            requested.Contains(x.ProviderId.ToString()) ? "pending" : null)).ToList();
    }
}
