using Maki.Core.Opds;
using Maki.Core.Reading;
using Maki.Data;
using Microsoft.EntityFrameworkCore;

namespace Maki.Api.Services;

/// <summary>
/// Where the feed lives, so every href the catalogue emits is built one way.
/// </summary>
/// <param name="PathBase">ASP.NET's <c>Request.PathBase</c>, so an instance hosted under a
/// sub-path still emits resolvable links. Empty in the normal case.</param>
/// <param name="Token">The OPDS token, which is part of the path rather than a header because
/// most reading apps cannot be told to send one.</param>
public record OpdsContext(string PathBase, string Token)
{
    public string Base => $"{PathBase}/api/v1/opds/{Token}";

    /// <summary>
    /// Covers are served under the token, not off <c>/api/v1/mediacover</c>.
    /// <para>
    /// That route used to be anonymous so plain <c>&lt;img&gt;</c> tags in the SPA would work, and the
    /// catalogue reused it. It is authenticated now — the SPA's images ride on the session cookie —
    /// which leaves a reading app with no credential for posters. Routing them through the token both
    /// fixes that and means the whole feed, entries and artwork alike, is reachable with exactly one
    /// secret and no anonymous surface.
    /// </para>
    /// </summary>
    public string Cover(int seriesId) => $"{Base}/cover/{seriesId}";
}

/// <summary>
/// Builds the OPDS catalogue's feeds. Deliberately separate from the controller: these are plain
/// model objects, so the feed shape can be tested without an HTTP host.
/// </summary>
public class OpdsCatalogService(
    MakiDbContext db,
    ReaderService reader,
    ContinueReadingService continueReading)
{
    /// <summary>
    /// Series per page of the browse feed. OPDS readers render a feed as one scrolling list and
    /// several fetch every entry's thumbnail eagerly, so a whole library in one document is a bad
    /// experience even where it technically works.
    /// </summary>
    public const int SeriesPageSize = 50;

    /// <summary>
    /// Chapters per page. Every entry costs an archive read to get its page count, and those land
    /// in <see cref="ReaderArchiveCache"/>, which holds 256 entries for the whole process — a page
    /// of 100 would claim well over a third of the shared cache and evict what the built-in reader
    /// is using. Kept level with <see cref="SeriesPageSize"/> for that reason.
    /// </summary>
    public const int ChapterPageSize = 50;

    /// <summary>How many entries the flat "recently added" and "on deck" shelves hold.</summary>
    public const int ShelfSize = 60;

    /// <summary>
    /// Newest chapter files scanned for the recently-added shelf. Same index-ordered LIMIT the
    /// Home rail uses (see <c>HomeController.RecentFileScan</c>) — bounded, and not a time window,
    /// so a dormant library still has a shelf.
    /// </summary>
    private const int RecentFileScan = 500;

    /// <summary>Newest progress rows scanned to decide which series are "on deck".</summary>
    private const int RecentProgressScan = 2000;

    private const int OverviewLimit = 500;

    /// <summary>The catalogue root: a menu, not a shelf.</summary>
    public OpdsFeed Root(OpdsContext ctx)
    {
        var now = DateTime.UtcNow;
        OpdsEntry Nav(string slug, string title, string content) => new(
            $"urn:maki:opds:{slug}",
            title,
            now,
            content,
            Links: [new OpdsLink("subsection", $"{ctx.Base}/{slug}", OpdsXml.NavigationType)]);

        return new OpdsFeed(
            "urn:maki:opds:root",
            "Maki",
            now,
            OpdsFeedKind.Navigation,
            [
                new OpdsLink("self", ctx.Base, OpdsXml.NavigationType),
                new OpdsLink("start", ctx.Base, OpdsXml.NavigationType),
                new OpdsLink("search", $"{ctx.Base}/search.xml", OpdsXml.OpenSearchType),
            ],
            [
                Nav("series", "All series", "Everything in the library, by title."),
                new OpdsEntry(
                    "urn:maki:opds:on-deck",
                    "On deck",
                    now,
                    "The next unread chapter of what you have been reading.",
                    Links: [new OpdsLink("subsection", $"{ctx.Base}/on-deck", OpdsXml.AcquisitionType)]),
                new OpdsEntry(
                    "urn:maki:opds:recent",
                    "Recently added",
                    now,
                    "Chapters that arrived in the library most recently.",
                    Links: [new OpdsLink("subsection", $"{ctx.Base}/recent", OpdsXml.AcquisitionType)]),
            ]);
    }

    /// <summary>Every series with at least one downloaded chapter, by title.</summary>
    public Task<OpdsFeed> SeriesFeedAsync(OpdsContext ctx, int page, CancellationToken ct) =>
        SeriesListAsync(ctx, page, query: null, ct);

    public Task<OpdsFeed> SearchFeedAsync(OpdsContext ctx, string query, int page, CancellationToken ct) =>
        SeriesListAsync(ctx, page, query, ct);

    private async Task<OpdsFeed> SeriesListAsync(OpdsContext ctx, int page, string? query, CancellationToken ct)
    {
        // Series with nothing downloaded are omitted: an OPDS reader has no concept of a series
        // Maki is merely tracking, so they would render as empty folders.
        var source = db.Series.AsNoTracking().Where(s => s.Chapters.Any(c => c.ChapterFileId != null));

        if (query is { Length: > 0 })
        {
            var needle = query.Trim().ToLowerInvariant();
            source = source.Where(s =>
                s.SortTitle.Contains(needle) ||
                s.Title.ToLower().Contains(needle) ||
                (s.OriginalTitle != null && s.OriginalTitle.ToLower().Contains(needle)));
        }

        var total = await source.CountAsync(ct);
        var rows = await source
            .OrderBy(s => s.SortTitle)
            .ThenBy(s => s.Id)
            .Skip(page * SeriesPageSize)
            .Take(SeriesPageSize)
            .Select(s => new
            {
                s.Id, s.Title, s.Overview, s.AuthorStory, s.Genres, s.CoverPath, s.Added, s.Year
            })
            .ToListAsync(ct);

        var entries = rows.Select(s =>
        {
            var links = new List<OpdsLink>
            {
                new("subsection", $"{ctx.Base}/series/{s.Id}", OpdsXml.AcquisitionType),
            };

            if (s.CoverPath is not null)
            {
                links.Add(new OpdsLink(OpdsXml.ImageRel, ctx.Cover(s.Id), "image/jpeg"));
                links.Add(new OpdsLink(OpdsXml.ThumbnailRel, ctx.Cover(s.Id), "image/jpeg"));
            }

            return new OpdsEntry(
                $"urn:maki:series:{s.Id}",
                s.Year is { } year ? $"{s.Title} ({year})" : s.Title,
                s.Added,
                Truncate(s.Overview),
                s.AuthorStory,
                links,
                Categories: s.Genres);
        }).ToList();

        var self = query is { Length: > 0 }
            ? $"{ctx.Base}/search?q={Uri.EscapeDataString(query)}"
            : $"{ctx.Base}/series";

        return new OpdsFeed(
            query is { Length: > 0 } ? $"urn:maki:opds:search:{query}" : "urn:maki:opds:series",
            query is { Length: > 0 } ? $"Search: {query}" : "All series",
            DateTime.UtcNow,
            OpdsFeedKind.Navigation,
            PagingLinks(ctx, self, OpdsXml.NavigationType, page, SeriesPageSize, total),
            entries,
            total,
            SeriesPageSize,
            page * SeriesPageSize);
    }

    /// <summary>A series' downloaded chapters, in reading order.</summary>
    /// <returns>Null when the series does not exist.</returns>
    public async Task<OpdsFeed?> ChaptersFeedAsync(OpdsContext ctx, int seriesId, int page, CancellationToken ct)
    {
        var series = await db.Series
            .AsNoTracking()
            .Where(s => s.Id == seriesId)
            .Select(s => new { s.Id, s.Title, s.CoverPath })
            .FirstOrDefaultAsync(ct);

        if (series is null)
        {
            return null;
        }

        var all = await db.Chapters
            .AsNoTracking()
            .Where(c => c.SeriesId == seriesId && c.ChapterFileId != null)
            .Select(c => new ChapterRow(
                c.Id, c.SeriesId, c.Number, c.Volume, c.Title, c.IsOneShot, c.Language,
                c.ChapterFile!.DateAdded))
            .ToListAsync(ct);

        // Ordered in memory, not in SQL: Chapter.Number is a decimal stored as REAL, and one-shots
        // carry no number and must sort last rather than first on a null.
        var ordered = all
            .OrderBy(c => c.Number is null ? 1 : 0)
            .ThenBy(c => c.Number)
            .ThenBy(c => c.Volume)
            .ThenBy(c => c.Id)
            .ToList();

        var slice = ordered.Skip(page * ChapterPageSize).Take(ChapterPageSize).ToList();
        var entries = await ChapterEntriesAsync(
            ctx, slice, new Dictionary<int, string>(), includeSeriesTitle: false, ct);

        var links = PagingLinks(
            ctx, $"{ctx.Base}/series/{seriesId}", OpdsXml.AcquisitionType, page, ChapterPageSize, ordered.Count);

        if (series.CoverPath is not null)
        {
            links.Add(new OpdsLink(OpdsXml.ImageRel, ctx.Cover(series.Id), "image/jpeg"));
            links.Add(new OpdsLink(OpdsXml.ThumbnailRel, ctx.Cover(series.Id), "image/jpeg"));
        }

        return new OpdsFeed(
            $"urn:maki:series:{seriesId}",
            series.Title,
            DateTime.UtcNow,
            OpdsFeedKind.Acquisition,
            links,
            entries,
            ordered.Count,
            ChapterPageSize,
            page * ChapterPageSize);
    }

    /// <summary>Chapters whose files arrived most recently, newest first.</summary>
    public async Task<OpdsFeed> RecentFeedAsync(OpdsContext ctx, CancellationToken ct)
    {
        var recentFiles = await db.ChapterFiles
            .AsNoTracking()
            .OrderByDescending(f => f.DateAdded)
            .Take(RecentFileScan)
            .Select(f => new { f.Id, f.DateAdded })
            .ToListAsync(ct);

        var fileIds = recentFiles.Select(f => f.Id).ToList();
        var rows = await db.Chapters
            .AsNoTracking()
            .Where(c => c.ChapterFileId != null && fileIds.Contains(c.ChapterFileId!.Value))
            .Select(c => new ChapterRow(
                c.Id, c.SeriesId, c.Number, c.Volume, c.Title, c.IsOneShot, c.Language,
                c.ChapterFile!.DateAdded))
            .ToListAsync(ct);

        var ordered = rows
            .OrderByDescending(c => c.Updated)
            .ThenByDescending(c => c.Number)
            .Take(ShelfSize)
            .ToList();

        return await ShelfAsync(ctx, "recent", "Recently added", ordered, ct);
    }

    /// <summary>
    /// The next unread chapter of each series read recently. Resolved through
    /// <see cref="ContinueReadingService"/> so the feed can never disagree with what the app's own
    /// Continue button opens.
    /// </summary>
    public async Task<OpdsFeed> OnDeckFeedAsync(OpdsContext ctx, CancellationToken ct)
    {
        var recent = await db.ChapterProgress
            .AsNoTracking()
            .OrderByDescending(p => p.UpdatedAt)
            .Take(RecentProgressScan)
            .Select(p => new { p.SeriesId, p.UpdatedAt })
            .ToListAsync(ct);

        var seriesOrder = recent
            .GroupBy(p => p.SeriesId)
            .Select(g => new { SeriesId = g.Key, Last = g.Max(p => p.UpdatedAt) })
            .OrderByDescending(x => x.Last)
            .Take(ShelfSize)
            .ToList();

        var next = await continueReading.NextForAsync(seriesOrder.Select(x => x.SeriesId).ToList(), ct);
        var chapterIds = seriesOrder
            .Select(x => next.GetValueOrDefault(x.SeriesId)?.ChapterId)
            .OfType<int>()
            .ToList();

        var byId = (await db.Chapters
                .AsNoTracking()
                .Where(c => chapterIds.Contains(c.Id))
                .Select(c => new ChapterRow(
                    c.Id, c.SeriesId, c.Number, c.Volume, c.Title, c.IsOneShot, c.Language,
                    c.ChapterFile!.DateAdded))
                .ToListAsync(ct))
            .ToDictionary(c => c.Id);

        // Series order, not chapter order: the shelf is "what you were reading", most recent first.
        var ordered = chapterIds.Select(byId.GetValueOrDefault).OfType<ChapterRow>().ToList();

        return await ShelfAsync(ctx, "on-deck", "On deck", ordered, ct);
    }

    private async Task<OpdsFeed> ShelfAsync(
        OpdsContext ctx, string slug, string title, IReadOnlyList<ChapterRow> rows, CancellationToken ct)
    {
        var seriesIds = rows.Select(r => r.SeriesId).Distinct().ToList();
        var titles = await db.Series
            .AsNoTracking()
            .Where(s => seriesIds.Contains(s.Id))
            .Select(s => new { s.Id, s.Title })
            .ToDictionaryAsync(s => s.Id, s => s.Title, ct);

        var entries = await ChapterEntriesAsync(ctx, rows, titles, includeSeriesTitle: true, ct);

        return new OpdsFeed(
            $"urn:maki:opds:{slug}",
            title,
            DateTime.UtcNow,
            OpdsFeedKind.Acquisition,
            [
                new OpdsLink("self", $"{ctx.Base}/{slug}", OpdsXml.AcquisitionType),
                new OpdsLink("start", ctx.Base, OpdsXml.NavigationType),
            ],
            entries);
    }

    /// <summary>
    /// Turns chapter rows into acquisition entries: a CBZ download link, an OPDS-PSE streaming
    /// link, and a poster.
    /// <para>
    /// A chapter whose slice cannot be resolved — file gone, archive unreadable — is dropped
    /// rather than listed: PSE has no way to express an unknown page count, and an entry that
    /// 404s on page 0 is worse than an absent one.
    /// </para>
    /// </summary>
    private async Task<List<OpdsEntry>> ChapterEntriesAsync(
        OpdsContext ctx,
        IReadOnlyList<ChapterRow> rows,
        IReadOnlyDictionary<int, string> seriesTitles,
        bool includeSeriesTitle,
        CancellationToken ct)
    {
        if (rows.Count == 0)
        {
            return [];
        }

        var ids = rows.Select(r => r.Id).ToList();
        var slices = await reader.SlicesAsync(ids, ct);

        var progress = await db.ChapterProgress
            .AsNoTracking()
            .Where(p => ids.Contains(p.ChapterId))
            .Select(p => new { p.ChapterId, p.PageIndex, p.Completed, p.UpdatedAt })
            .ToDictionaryAsync(p => p.ChapterId, ct);

        var ambiguous = AmbiguousWithoutLanguage(rows);

        var entries = new List<OpdsEntry>(rows.Count);
        foreach (var row in rows)
        {
            if (!slices.TryGetValue(row.Id, out var slice))
            {
                continue;
            }

            var label = ChapterLabel.For(row.Number, row.Volume, row.Title, row.IsOneShot);
            if (ambiguous.Contains(Identity(row)))
            {
                label = $"{label} [{row.Language}]";
            }

            var name = includeSeriesTitle && seriesTitles.TryGetValue(row.SeriesId, out var seriesTitle)
                ? $"{seriesTitle} — {label}"
                : label;

            int? lastRead = null;
            DateTime? lastReadDate = null;
            if (progress.TryGetValue(row.Id, out var saved))
            {
                lastRead = saved.Completed
                    ? slice.PageCount
                    : Math.Min(saved.PageIndex + 1, slice.PageCount);
                lastReadDate = saved.UpdatedAt;
            }

            var links = new List<OpdsLink>
            {
                // open-access as well as plain acquisition: readers that only look for the
                // open-access relation (there is no purchase model here) otherwise show no
                // download button at all.
                new(OpdsXml.AcquisitionRel, $"{ctx.Base}/chapter/{row.Id}/file",
                    OpdsXml.ComicBookType, Length: slice.ArchiveSize),
                new(OpdsXml.OpenAccessRel, $"{ctx.Base}/chapter/{row.Id}/file",
                    OpdsXml.ComicBookType, Length: slice.ArchiveSize),
                new(OpdsXml.ThumbnailRel, ctx.Cover(row.SeriesId), "image/jpeg"),
            };

            entries.Add(new OpdsEntry(
                $"urn:maki:chapter:{row.Id}",
                name,
                row.Updated,
                Links: links,
                Stream: new OpdsPseStream(
                    // {pageNumber} is substituted by the client and must survive verbatim.
                    $"{ctx.Base}/chapter/{row.Id}/page/{{pageNumber}}",
                    slice.PageCount,
                    lastRead,
                    lastReadDate)));
        }

        return entries;
    }

    /// <summary>
    /// Chapter identities that appear more than once in this feed page under more than one
    /// language, and so need the language spelled out.
    /// <para>
    /// A multi-language library holds one <c>Chapter</c> row per language — identity is
    /// <c>(Number, Language)</c> — and <see cref="ChapterLabel"/> renders only the number. Left
    /// alone, a feed shows two entries both called "Ch.1" with no way to tell which is which.
    /// Decided per feed page rather than per series so it costs no extra query, and so a
    /// single-language library never sees a language tag it doesn't need.
    /// </para>
    /// </summary>
    private static HashSet<(int, decimal?, int?, bool)> AmbiguousWithoutLanguage(
        IReadOnlyList<ChapterRow> rows) =>
        rows.GroupBy(Identity)
            .Where(g => g.Select(r => r.Language).Distinct().Count() > 1)
            .Select(g => g.Key)
            .ToHashSet();

    private static (int, decimal?, int?, bool) Identity(ChapterRow row) =>
        (row.SeriesId, row.Number, row.Volume, row.IsOneShot);

    /// <summary>Self plus the next/previous pair, omitted at the ends so readers stop paging.</summary>
    private static List<OpdsLink> PagingLinks(
        OpdsContext ctx, string self, string type, int page, int pageSize, int total)
    {
        var separator = self.Contains('?') ? '&' : '?';
        var links = new List<OpdsLink>
        {
            new("self", page == 0 ? self : $"{self}{separator}page={page}", type),
            new("start", ctx.Base, OpdsXml.NavigationType),
        };

        if (page > 0)
        {
            links.Add(new OpdsLink("previous",
                page - 1 == 0 ? self : $"{self}{separator}page={page - 1}", type));
        }

        if ((page + 1) * pageSize < total)
        {
            links.Add(new OpdsLink("next", $"{self}{separator}page={page + 1}", type));
        }

        return links;
    }

    private static string? Truncate(string? text) =>
        text is { Length: > OverviewLimit } ? text[..OverviewLimit].TrimEnd() + "…" : text;

    private record ChapterRow(
        int Id, int SeriesId, decimal? Number, int? Volume, string? Title, bool IsOneShot,
        string Language, DateTime Updated);
}
