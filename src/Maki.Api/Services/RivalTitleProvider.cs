using Maki.Core.Entities;
using Maki.Core.Parsing;
using Maki.Data;
using Maki.Metadata.MangaBaka;
using Microsoft.EntityFrameworkCore;

namespace Maki.Api.Services;

/// <summary>
/// The titles a search result or release for a series could be naming instead of it: the works one
/// MangaBaka relation away and every other library series, kept only when shaped like an extension
/// of one of the series' own titles (<see cref="ReleaseTitleMatcher.Rivals"/>).
/// </summary>
/// <remarks>
/// Singleton so the library's titles are read once across scopes: the source match worker opens a
/// scope per series, and an import would otherwise reread the whole library for each one. The
/// snapshot is dropped when a series is added or removed, and after <see cref="MaxAge"/> so renames
/// and new alt titles show up.
/// </remarks>
public class RivalTitleProvider(MangaBakaLocalStore store, TimeProvider time, ILogger<RivalTitleProvider> logger)
{
    private static readonly TimeSpan MaxAge = TimeSpan.FromMinutes(5);

    private sealed record LibraryEntry(int SeriesId, int? MangaBakaId, IReadOnlyList<string> Titles);

    private sealed record Snapshot(int Count, int MaxId, DateTimeOffset ReadAt, IReadOnlyList<LibraryEntry> Series);

    private volatile Snapshot? _library;

    public async Task<IReadOnlyList<string>> ForAsync(
        MakiDbContext db, Series series, IReadOnlyList<string> ourTitles, CancellationToken ct)
    {
        var related = await RelatedAsync(series, ct);
        var library = await LibraryAsync(db, ct);
        var others = library.Series
            .Where(s => s.SeriesId != series.Id && (series.MangaBakaId is null || s.MangaBakaId != series.MangaBakaId))
            .SelectMany(s => s.Titles);

        return ReleaseTitleMatcher.Rivals(ourTitles, related.Concat(others));
    }

    private async Task<IReadOnlyList<string>> RelatedAsync(Series series, CancellationToken ct)
    {
        if (series.MangaBakaId is not { } id)
        {
            return [];
        }

        try
        {
            return await store.GetRelatedTitlesAsync(id, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Could not read MangaBaka relations for {MangaBakaId}, judging titles without them", id);
            return [];
        }
    }

    // Unfiltered on purpose: the titles are compared, never shown, and a restricted user should reach
    // the verdict an admin does.
    private async Task<Snapshot> LibraryAsync(MakiDbContext db, CancellationToken ct)
    {
        var all = db.Series.AsNoTracking().IgnoreQueryFilters();
        var count = await all.CountAsync(ct);
        var maxId = await all.MaxAsync(s => (int?)s.Id, ct) ?? 0;
        var now = time.GetUtcNow();

        var cached = _library;
        if (cached is not null && cached.Count == count && cached.MaxId == maxId && now - cached.ReadAt < MaxAge)
        {
            return cached;
        }

        var rows = await all
            .Select(s => new { s.Id, s.MangaBakaId, s.Title, s.OriginalTitle, s.AltTitles })
            .ToListAsync(ct);
        var snapshot = new Snapshot(count, maxId, now,
            [.. rows.Select(s => new LibraryEntry(s.Id, s.MangaBakaId,
                ReleaseTitleMatcher.Titles(s.Title, s.OriginalTitle, s.AltTitles)))]);
        _library = snapshot;
        return snapshot;
    }
}
