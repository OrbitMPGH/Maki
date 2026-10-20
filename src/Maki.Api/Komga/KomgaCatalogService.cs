using System.Globalization;
using Maki.Api.Services;
using Maki.Core.Entities;
using Maki.Core.Komga;
using Maki.Core.Opds;
using Maki.Core.Reading;
using Maki.Data;
using Microsoft.EntityFrameworkCore;

namespace Maki.Api.Komga;

/// <summary>Series list filters, as Komga's <c>GET series</c> takes them.</summary>
public record KomgaSeriesQuery(
    string? Search = null,
    IReadOnlyCollection<int>? LibraryIds = null,
    IReadOnlyCollection<string>? ReadStatus = null,
    IReadOnlyCollection<string>? Status = null,
    IReadOnlyCollection<string>? Genres = null,
    IReadOnlyCollection<string>? Tags = null,
    IReadOnlyCollection<string>? Publishers = null,
    IReadOnlyCollection<string>? Authors = null,
    string? Sort = null);

/// <summary>Book list filters, as Komga's <c>GET books</c> takes them.</summary>
public record KomgaBookQuery(
    string? Search = null,
    IReadOnlyCollection<int>? LibraryIds = null,
    IReadOnlyCollection<string>? ReadStatus = null,
    string? Sort = null);

/// <summary>
/// Maps Maki's library onto Komga's model: a root folder is a library, a series with at least one
/// downloaded chapter is a series, and a downloaded chapter is a book. Everything reads through the
/// request's <see cref="DataScope"/>, which <see cref="KomgaAuthFilter"/> narrows to the token's
/// owner, so root-folder grants apply exactly as they do in the app and over OPDS.
/// </summary>
public class KomgaCatalogService(MakiDbContext db, ReaderService reader, ContinueReadingService continueReading)
{
    public const string Unread = "UNREAD";
    public const string Read = "READ";
    public const string InProgress = "IN_PROGRESS";

    /// <summary>Same bounded scan the OPDS on-deck shelf uses.</summary>
    private const int RecentProgressScan = 2000;

    private static readonly string[] Roles = ["USER", "FILE_DOWNLOAD", "PAGE_STREAMING"];

    private sealed record SeriesRow(
        int Id,
        int RootFolderId,
        string Title,
        string SortTitle,
        string? OriginalTitle,
        List<LocalizedTitle> AltTitles,
        SeriesStatus Status,
        string? Type,
        string? Overview,
        List<string> Genres,
        List<string> Tags,
        string? Publisher,
        string? AuthorStory,
        string? AuthorArt,
        int? TotalChapters,
        string FolderName,
        DateTime Added,
        DateTime? LastFileAdded,
        int BooksCount);

    private sealed record ChapterRow(
        int Id,
        int SeriesId,
        decimal? Number,
        string? NumberRaw,
        int? Volume,
        string? Title,
        bool IsOneShot,
        string Language,
        DateTime? ReleaseDate,
        string RelativePath,
        long Size,
        DateTime DateAdded,
        int? MeasuredPages,
        bool SharesFile);

    /// <summary>A chapter placed in its series: Komga's 1-based <c>number</c> and its <c>numberSort</c>.</summary>
    private sealed record Placed(ChapterRow Row, int Index, double NumberSort, bool Ambiguous);

    private sealed record ProgressRow(int ChapterId, int PageIndex, bool Completed, DateTime StartedAt, DateTime UpdatedAt);

    public async Task<List<KomgaLibrary>> LibrariesAsync(CancellationToken ct)
    {
        var visible = db.Series.Where(s => s.Chapters.Any(c => c.ChapterFileId != null)).Select(s => s.RootFolderId);
        var folders = await db.RootFolders.AsNoTracking()
            .Where(r => visible.Contains(r.Id))
            .OrderBy(r => r.Id)
            .Select(r => new { r.Id, r.Path })
            .ToListAsync(ct);

        return folders.Select(f => Library(f.Id, f.Path)).ToList();
    }

    public async Task<KomgaLibrary?> LibraryAsync(int id, CancellationToken ct) =>
        (await LibrariesAsync(ct)).FirstOrDefault(l => l.Id == Id(id));

    private static KomgaLibrary Library(int id, string path)
    {
        // The folder's own name, never the path: a reading app has no business with the server's layout.
        var name = Path.GetFileName(path.TrimEnd('/', '\\'));
        return new KomgaLibrary(Id(id), string.IsNullOrEmpty(name) ? path : name, string.Empty,
            false, false, false, false, false, false, false, false, false, false, false, false, false,
            "FIRST", false);
    }

    public async Task<KomgaPage<KomgaSeries>> SeriesAsync(KomgaSeriesQuery query, int page, int? size, CancellationToken ct)
    {
        var rows = await SeriesRowsAsync(ct);
        var counts = await ProgressCountsAsync(ct);
        IEnumerable<SeriesRow> filtered = rows;

        if (query.LibraryIds is { Count: > 0 } libraries)
        {
            filtered = filtered.Where(s => libraries.Contains(s.RootFolderId));
        }

        if (query.Status is { Count: > 0 } statuses)
        {
            filtered = filtered.Where(s => statuses.Contains(StatusOf(s.Status), StringComparer.OrdinalIgnoreCase));
        }

        if (query.Genres is { Count: > 0 } genres)
        {
            filtered = filtered.Where(s => s.Genres.Any(g => genres.Contains(g, StringComparer.OrdinalIgnoreCase)));
        }

        if (query.Tags is { Count: > 0 } tags)
        {
            filtered = filtered.Where(s => s.Tags.Any(t => tags.Contains(t, StringComparer.OrdinalIgnoreCase)));
        }

        if (query.Publishers is { Count: > 0 } publishers)
        {
            filtered = filtered.Where(s => s.Publisher is { } p && publishers.Contains(p, StringComparer.OrdinalIgnoreCase));
        }

        if (query.Authors is { Count: > 0 } authors)
        {
            // Komga sends "name,role"; only the name is matched, since Maki keeps two credit strings.
            var names = authors.Select(a => a.Split(',')[0].Trim()).ToHashSet(StringComparer.OrdinalIgnoreCase);
            filtered = filtered.Where(s => AuthorsOf(s).Any(a => names.Contains(a.Name)));
        }

        if (query.Search is { Length: > 0 } search)
        {
            var needle = search.Trim();
            filtered = filtered.Where(s =>
                Contains(s.Title, needle) || Contains(s.SortTitle, needle) || Contains(s.OriginalTitle, needle) ||
                Contains(s.AuthorStory, needle) || Contains(s.AuthorArt, needle) ||
                s.AltTitles.Any(a => Contains(a.Title, needle)));
        }

        if (query.ReadStatus is { Count: > 0 } readStatus)
        {
            filtered = filtered.Where(s => readStatus.Contains(ReadStatusOf(s, counts), StringComparer.OrdinalIgnoreCase));
        }

        var sorted = SortSeries(filtered, query.Sort).ToList();
        var result = KomgaPage<SeriesRow>.Of(sorted, page, size);
        return KomgaPage<KomgaSeries>.Slice(
            result.Content.Select(s => ToSeries(s, counts)).ToList(),
            result.TotalElements, result.Number, result.Size, result.Pageable.Unpaged);
    }

    public async Task<KomgaSeries?> OneSeriesAsync(int id, CancellationToken ct)
    {
        var row = (await SeriesRowsAsync(ct, [id])).FirstOrDefault();
        return row is null ? null : ToSeries(row, await ProgressCountsAsync(ct, id));
    }

    private Task<List<SeriesRow>> SeriesRowsAsync(CancellationToken ct, IReadOnlyCollection<int>? onlyIds = null)
    {
        var source = db.Series.AsNoTracking().Where(s => s.Chapters.Any(c => c.ChapterFileId != null));
        if (onlyIds is not null)
        {
            source = source.Where(s => onlyIds.Contains(s.Id));
        }

        return source
            .Select(s => new SeriesRow(
                s.Id, s.RootFolderId, s.Title, s.SortTitle, s.OriginalTitle, s.AltTitles, s.Status, s.Type,
                s.Overview, s.Genres, s.Tags, s.Publisher, s.AuthorStory, s.AuthorArt, s.TotalChapters,
                s.FolderName, s.Added,
                s.Chapters.Where(c => c.ChapterFileId != null).Max(c => (DateTime?)c.ChapterFile!.DateAdded),
                s.Chapters.Count(c => c.ChapterFileId != null)))
            .ToListAsync(ct);
    }

    /// <summary>Read and in-progress books per series, for downloaded chapters only.</summary>
    private async Task<Dictionary<int, (int Read, int InProgress)>> ProgressCountsAsync(
        CancellationToken ct, int? onlySeries = null)
    {
        var progress = db.ChapterProgress.AsNoTracking().OwnedByScopeUser(db)
            .Where(p => db.Chapters.Any(c => c.Id == p.ChapterId && c.ChapterFileId != null));
        if (onlySeries is { } seriesId)
        {
            progress = progress.Where(p => p.SeriesId == seriesId);
        }

        return await progress
            .GroupBy(p => p.SeriesId)
            .Select(g => new
            {
                g.Key,
                Read = g.Count(p => p.Completed),
                InProgress = g.Count(p => !p.Completed && p.PageIndex > 0),
            })
            .ToDictionaryAsync(x => x.Key, x => (x.Read, x.InProgress), ct);
    }

    private static string ReadStatusOf(SeriesRow s, Dictionary<int, (int Read, int InProgress)> counts)
    {
        var (read, inProgress) = counts.GetValueOrDefault(s.Id);
        return read >= s.BooksCount ? Read : read + inProgress == 0 ? Unread : InProgress;
    }

    private static IEnumerable<SeriesRow> SortSeries(IEnumerable<SeriesRow> rows, string? sort)
    {
        var (field, descending) = ParseSort(sort);
        if (field == "random")
        {
            return rows.OrderBy(_ => Random.Shared.Next());
        }

        IOrderedEnumerable<SeriesRow> ordered = field switch
        {
            "createdDate" or "created" => descending
                ? rows.OrderByDescending(s => s.Added)
                : rows.OrderBy(s => s.Added),
            "lastModifiedDate" or "lastModified" or "fileLastModified" => descending
                ? rows.OrderByDescending(s => s.LastFileAdded)
                : rows.OrderBy(s => s.LastFileAdded),
            "booksCount" => descending
                ? rows.OrderByDescending(s => s.BooksCount)
                : rows.OrderBy(s => s.BooksCount),
            _ => descending
                ? rows.OrderByDescending(s => s.SortTitle, StringComparer.OrdinalIgnoreCase)
                : rows.OrderBy(s => s.SortTitle, StringComparer.OrdinalIgnoreCase),
        };

        return ordered.ThenBy(s => s.Id);
    }

    private static (string Field, bool Descending) ParseSort(string? sort)
    {
        if (string.IsNullOrWhiteSpace(sort))
        {
            return (string.Empty, false);
        }

        var parts = sort.Split(',', StringSplitOptions.TrimEntries);
        var field = parts[0].StartsWith("metadata.", StringComparison.Ordinal) ? parts[0]["metadata.".Length..] : parts[0];
        return (field, parts.Length > 1 && parts[1].Equals("desc", StringComparison.OrdinalIgnoreCase));
    }

    private static KomgaSeries ToSeries(SeriesRow s, Dictionary<int, (int Read, int InProgress)> counts)
    {
        var (read, inProgress) = counts.GetValueOrDefault(s.Id);
        read = Math.Min(read, s.BooksCount);
        inProgress = Math.Min(inProgress, s.BooksCount - read);
        var modified = s.LastFileAdded ?? s.Added;
        var authors = AuthorsOf(s);

        var metadata = new KomgaSeriesMetadata(
            StatusOf(s.Status), false,
            s.Title, false,
            s.SortTitle, false,
            s.Overview ?? string.Empty, false,
            ReadingDirectionOf(s.Type), false,
            s.Publisher ?? string.Empty, false,
            null, false,
            string.Empty, false,
            s.Genres, false,
            s.Tags, false,
            s.TotalChapters, false,
            [], false,
            [], false,
            s.AltTitles.Select(a => new KomgaAlternateTitle(a.Language ?? string.Empty, a.Title)).ToList(), false,
            s.Added,
            modified);

        return new KomgaSeries(
            Id(s.Id),
            Id(s.RootFolderId),
            s.Title,
            s.FolderName,
            s.Added,
            modified,
            modified,
            s.BooksCount,
            read,
            s.BooksCount - read - inProgress,
            inProgress,
            metadata,
            new KomgaBooksMetadata(authors, [], null, string.Empty, string.Empty, s.Added, modified),
            false,
            false);
    }

    private static List<KomgaAuthor> AuthorsOf(SeriesRow s)
    {
        // Each credit string stays whole: a comma inside one cannot be told apart from two people.
        var authors = new List<KomgaAuthor>();
        if (!string.IsNullOrWhiteSpace(s.AuthorStory))
        {
            authors.Add(new KomgaAuthor(s.AuthorStory, "writer"));
        }

        if (!string.IsNullOrWhiteSpace(s.AuthorArt))
        {
            authors.Add(new KomgaAuthor(s.AuthorArt, "penciller"));
        }

        return authors;
    }

    private static string StatusOf(SeriesStatus status) => status switch
    {
        SeriesStatus.Completed => "ENDED",
        SeriesStatus.Hiatus => "HIATUS",
        SeriesStatus.Cancelled => "ABANDONED",
        _ => "ONGOING",
    };

    private static string ReadingDirectionOf(string? type) => type?.ToLowerInvariant() switch
    {
        "manga" => "RIGHT_TO_LEFT",
        "manhwa" or "manhua" or "webtoon" => "WEBTOON",
        "oel" or "comic" => "LEFT_TO_RIGHT",
        _ => string.Empty,
    };

    public async Task<List<string>> GenresAsync(int? libraryId, CancellationToken ct) =>
        Distinct((await SeriesRowsAsync(ct)).Where(s => libraryId is null || s.RootFolderId == libraryId)
            .SelectMany(s => s.Genres));

    public async Task<List<string>> TagsAsync(int? libraryId, CancellationToken ct) =>
        Distinct((await SeriesRowsAsync(ct)).Where(s => libraryId is null || s.RootFolderId == libraryId)
            .SelectMany(s => s.Tags));

    public async Task<List<string>> PublishersAsync(int? libraryId, CancellationToken ct) =>
        Distinct((await SeriesRowsAsync(ct)).Where(s => libraryId is null || s.RootFolderId == libraryId)
            .Select(s => s.Publisher).OfType<string>().Where(p => p.Length > 0));

    public async Task<List<KomgaAuthor>> AuthorsAsync(CancellationToken ct) =>
        (await SeriesRowsAsync(ct))
            .SelectMany(AuthorsOf)
            .DistinctBy(a => (a.Name.ToLowerInvariant(), a.Role))
            .OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static List<string> Distinct(IEnumerable<string> values) =>
        values.Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>A series' books in reading order.</summary>
    /// <returns>Null when the series is not visible or has nothing downloaded.</returns>
    public async Task<KomgaPage<KomgaBook>?> SeriesBooksAsync(
        int seriesId, IReadOnlyCollection<string>? readStatus, string? sort, int page, int? size, CancellationToken ct)
    {
        var series = (await SeriesRowsAsync(ct, [seriesId])).FirstOrDefault();
        if (series is null)
        {
            return null;
        }

        var placed = (await PlaceAsync([seriesId], ct)).GetValueOrDefault(seriesId) ?? [];
        var progress = await ProgressAsync(placed.Select(p => p.Row.Id).ToList(), ct);
        IEnumerable<Placed> filtered = placed;
        if (readStatus is { Count: > 0 })
        {
            filtered = filtered.Where(p => readStatus.Contains(BookStatus(progress.GetValueOrDefault(p.Row.Id)),
                StringComparer.OrdinalIgnoreCase));
        }

        if (ParseSort(sort) is { Descending: true } && sort!.Contains("number", StringComparison.OrdinalIgnoreCase))
        {
            filtered = filtered.Reverse();
        }

        var cut = KomgaPage<Placed>.Of(filtered.ToList(), page, size);
        var books = await ToBooksAsync(cut.Content, new Dictionary<int, SeriesRow> { [seriesId] = series }, progress, ct);
        return KomgaPage<KomgaBook>.Slice(books, cut.TotalElements, cut.Number, cut.Size, cut.Pageable.Unpaged);
    }

    public async Task<KomgaBook?> BookAsync(int chapterId, CancellationToken ct) =>
        (await BooksByIdAsync([chapterId], ct)).FirstOrDefault();

    /// <summary>Books by chapter id, in the order given, dropping any that are not visible or downloaded.</summary>
    private async Task<List<KomgaBook>> BooksByIdAsync(IReadOnlyList<int> chapterIds, CancellationToken ct)
    {
        if (chapterIds.Count == 0)
        {
            return [];
        }

        var seriesIds = await db.Chapters.AsNoTracking()
            .Where(c => chapterIds.Contains(c.Id) && c.ChapterFileId != null)
            .Select(c => c.SeriesId)
            .Distinct()
            .ToListAsync(ct);
        var placedBySeries = await PlaceAsync(seriesIds, ct);
        var byChapter = placedBySeries.Values.SelectMany(p => p).ToDictionary(p => p.Row.Id);
        var wanted = chapterIds.Select(byChapter.GetValueOrDefault).OfType<Placed>().ToList();

        var series = (await SeriesRowsAsync(ct, seriesIds)).ToDictionary(s => s.Id);

        var progress = await ProgressAsync(wanted.Select(p => p.Row.Id).ToList(), ct);
        return await ToBooksAsync(wanted, series, progress, ct);
    }

    /// <summary>
    /// Every book in the library, newest file first unless sorted otherwise. Paged in SQL, since
    /// unlike the series list this can run to every chapter on disk.
    /// </summary>
    public async Task<KomgaPage<KomgaBook>> BooksAsync(KomgaBookQuery query, int page, int? size, CancellationToken ct)
    {
        var source = db.Chapters.AsNoTracking().Where(c => c.ChapterFileId != null);
        if (query.LibraryIds is { Count: > 0 } libraries)
        {
            source = source.Where(c => libraries.Contains(c.Series!.RootFolderId));
        }

        if (query.Search is { Length: > 0 } search)
        {
            var pattern = "%" + search.Trim().Replace("%", @"\%").Replace("_", @"\_") + "%";
            source = source.Where(c =>
                EF.Functions.Like(c.Series!.Title, pattern, @"\") || EF.Functions.Like(c.Title, pattern, @"\"));
        }

        if (query.ReadStatus is { Count: > 0 } readStatus)
        {
            var read = readStatus.Contains(Read, StringComparer.OrdinalIgnoreCase);
            var inProgress = readStatus.Contains(InProgress, StringComparer.OrdinalIgnoreCase);
            var unread = readStatus.Contains(Unread, StringComparer.OrdinalIgnoreCase);
            source = source.Where(c =>
                (read && db.ChapterProgress.Any(p => p.ChapterId == c.Id && p.Completed)) ||
                (inProgress && db.ChapterProgress.Any(p => p.ChapterId == c.Id && !p.Completed && p.PageIndex > 0)) ||
                (unread && !db.ChapterProgress.Any(p => p.ChapterId == c.Id && (p.Completed || p.PageIndex > 0))));
        }

        var (field, descending) = ParseSort(query.Sort);
        var ordered = field switch
        {
            "name" or "title" or "series" or "numberSort" or "number" => descending
                ? source.OrderByDescending(c => c.Series!.SortTitle).ThenByDescending(c => c.Number)
                : source.OrderBy(c => c.Series!.SortTitle).ThenBy(c => c.Number),
            "createdDate" or "created" or "lastModifiedDate" or "lastModified" or "fileLastModified" when !descending =>
                source.OrderBy(c => c.ChapterFile!.DateAdded),
            _ => source.OrderByDescending(c => c.ChapterFile!.DateAdded),
        };

        var total = await source.CountAsync(ct);
        var pageSize = size ?? Math.Max(total, 1);
        var number = size is null ? 0 : page;
        var ids = await ordered.ThenBy(c => c.Id).Skip(number * pageSize).Take(pageSize).Select(c => c.Id).ToListAsync(ct);
        return KomgaPage<KomgaBook>.Slice(await BooksByIdAsync(ids, ct), total, number, pageSize, size is null);
    }

    /// <summary>
    /// The next unread book of each series read recently, through <see cref="ContinueReadingService"/>
    /// like the OPDS on-deck shelf, so it never disagrees with the app's own Continue button.
    /// </summary>
    public async Task<KomgaPage<KomgaBook>> OnDeckAsync(int page, int? size, CancellationToken ct)
    {
        var recent = await db.ChapterProgress.AsNoTracking().OwnedByScopeUser(db)
            .OrderByDescending(p => p.UpdatedAt)
            .Take(RecentProgressScan)
            .Select(p => new { p.SeriesId, p.UpdatedAt })
            .ToListAsync(ct);

        var seriesOrder = recent
            .GroupBy(p => p.SeriesId)
            .OrderByDescending(g => g.Max(p => p.UpdatedAt))
            .Select(g => g.Key)
            .ToList();
        var next = await continueReading.NextForAsync(seriesOrder, ct);
        var chapterIds = seriesOrder.Select(id => next.GetValueOrDefault(id)?.ChapterId).OfType<int>().ToList();

        var cut = KomgaPage<int>.Of(chapterIds, page, size);
        return KomgaPage<KomgaBook>.Slice(await BooksByIdAsync(cut.Content, ct), cut.TotalElements, cut.Number,
            cut.Size, cut.Pageable.Unpaged);
    }

    public async Task<KomgaBook?> NeighbourAsync(int chapterId, bool next, CancellationToken ct)
    {
        var seriesId = await db.Chapters.AsNoTracking()
            .Where(c => c.Id == chapterId && c.ChapterFileId != null)
            .Select(c => (int?)c.SeriesId)
            .FirstOrDefaultAsync(ct);
        if (seriesId is null)
        {
            return null;
        }

        var placed = (await PlaceAsync([seriesId.Value], ct)).GetValueOrDefault(seriesId.Value) ?? [];
        var index = placed.FindIndex(p => p.Row.Id == chapterId);
        var neighbour = index < 0 ? -1 : next ? index + 1 : index - 1;
        return neighbour >= 0 && neighbour < placed.Count ? await BookAsync(placed[neighbour].Row.Id, ct) : null;
    }

    public async Task<List<KomgaPageInfo>?> PagesAsync(int chapterId, CancellationToken ct)
    {
        var slice = await reader.PageSliceAsync(chapterId, ct);
        if (slice is null)
        {
            return null;
        }

        return Enumerable.Range(0, slice.PageCount)
            .Select(i =>
            {
                var entry = slice.Pages[slice.StartPage + i];
                return new KomgaPageInfo(i + 1, Path.GetFileName(entry), CbzReader.ContentType(entry), null, null, null);
            })
            .ToList();
    }

    public async Task<KomgaUser> UserAsync(int userId, bool allRootFolders, CancellationToken ct)
    {
        var name = await db.Users.AsNoTracking().Where(u => u.Id == userId).Select(u => u.UserName).FirstOrDefaultAsync(ct);
        var shared = allRootFolders
            ? []
            : await db.UserRootFolders.AsNoTracking()
                .Where(g => g.UserId == userId)
                .Select(g => g.RootFolderId)
                .ToListAsync(ct);

        return new KomgaUser(Id(userId), name ?? string.Empty, Roles, allRootFolders, shared.Select(Id).ToList(), [], [], null);
    }

    /// <summary>Komga's per-series progress summary, the figure the Mihon tracker syncs on.</summary>
    public async Task<KomgaTachiyomiProgressV2?> TachiyomiProgressAsync(int seriesId, CancellationToken ct)
    {
        if (await SeriesBookStatesAsync(seriesId, ct) is not { } books)
        {
            return null;
        }

        var read = books.Count(b => b.Status == Read);
        var inProgress = books.Count(b => b.Status == InProgress);
        return new KomgaTachiyomiProgressV2(
            books.Count,
            read,
            books.Count - read - inProgress,
            inProgress,
            KomgaNumbering.LastContinuousRead(books.Select(b => (b.Placed.NumberSort, b.Status == Read)).ToList()),
            books.Count == 0 ? 0 : books.Max(b => b.Placed.NumberSort));
    }

    /// <summary>Komga v1's version of the summary: how many books in a row are read, from the start.</summary>
    public async Task<KomgaTachiyomiProgress?> TachiyomiProgressV1Async(int seriesId, CancellationToken ct)
    {
        if (await SeriesBookStatesAsync(seriesId, ct) is not { } books)
        {
            return null;
        }

        var read = books.Count(b => b.Status == Read);
        var inProgress = books.Count(b => b.Status == InProgress);
        var continuous = books.TakeWhile(b => b.Status == Read).Count();
        return new KomgaTachiyomiProgress(books.Count, read, books.Count - read - inProgress, inProgress, continuous);
    }

    /// <summary>The <c>numberSort</c> of the <paramref name="count"/>th book, for a v1 tracker update.</summary>
    public async Task<double?> NumberSortOfBookAsync(int seriesId, int count, CancellationToken ct)
    {
        var placed = (await PlaceAsync([seriesId], ct)).GetValueOrDefault(seriesId) ?? [];
        return count <= 0 || placed.Count == 0 ? null : placed[Math.Min(count, placed.Count) - 1].NumberSort;
    }

    /// <summary>
    /// The chapters a tracker update to <paramref name="upTo"/> newly marks read, grouped by
    /// <c>numberSort</c> (one group per chapter number, however many languages it is downloaded in).
    /// </summary>
    public async Task<List<List<int>>?> NewlyReadAsync(int seriesId, double upTo, CancellationToken ct)
    {
        if (await SeriesBookStatesAsync(seriesId, ct) is not { } books)
        {
            return null;
        }

        var groups = KomgaNumbering.NewlyRead(books.Select(b => (b.Placed.NumberSort, b.Status == Read)).ToList(), upTo);
        return groups
            .Select(n => books.Where(b => b.Placed.NumberSort == n).Select(b => b.Placed.Row.Id).ToList())
            .ToList();
    }

    /// <summary>Every downloaded chapter id of a visible series, or null when there is no such series.</summary>
    public async Task<List<int>?> SeriesChapterIdsAsync(int seriesId, CancellationToken ct)
    {
        var ids = await db.Chapters.AsNoTracking()
            .Where(c => c.SeriesId == seriesId && c.ChapterFileId != null)
            .Select(c => c.Id)
            .ToListAsync(ct);
        return ids.Count == 0 ? null : ids;
    }

    private async Task<List<(Placed Placed, string Status)>?> SeriesBookStatesAsync(int seriesId, CancellationToken ct)
    {
        var placed = (await PlaceAsync([seriesId], ct)).GetValueOrDefault(seriesId);
        if (placed is null)
        {
            return null;
        }

        var progress = await ProgressAsync(placed.Select(p => p.Row.Id).ToList(), ct);
        return placed.Select(p => (p, BookStatus(progress.GetValueOrDefault(p.Row.Id)))).ToList();
    }

    private static string BookStatus(ProgressRow? progress) =>
        progress is null ? Unread : progress.Completed ? Read : progress.PageIndex > 0 ? InProgress : Unread;

    /// <summary>Each series' downloaded chapters in reading order, with Komga's numbering.</summary>
    private async Task<Dictionary<int, List<Placed>>> PlaceAsync(IReadOnlyCollection<int> seriesIds, CancellationToken ct)
    {
        var rows = await db.Chapters.AsNoTracking()
            .Where(c => seriesIds.Contains(c.SeriesId) && c.ChapterFileId != null)
            .Select(c => new ChapterRow(
                c.Id, c.SeriesId, c.Number, c.NumberRaw, c.Volume, c.Title, c.IsOneShot, c.Language, c.ReleaseDate,
                c.ChapterFile!.RelativePath, c.ChapterFile.Size, c.ChapterFile.DateAdded, c.ChapterFile.PageCount,
                db.Chapters.Any(o => o.ChapterFileId == c.ChapterFileId && o.Id != c.Id)))
            .ToListAsync(ct);

        var result = new Dictionary<int, List<Placed>>();
        foreach (var group in rows.GroupBy(r => r.SeriesId))
        {
            var ordered = ChapterOrder.Sort(group, c => c.Number, c => c.Volume, c => c.Id);
            var sorts = KomgaNumbering.NumberSorts(ordered.Select(c => c.Number).ToList());
            var ambiguous = ordered
                .GroupBy(c => (c.Number, c.Volume, c.IsOneShot))
                .Where(g => g.Select(c => c.Language).Distinct().Count() > 1)
                .Select(g => g.Key)
                .ToHashSet();
            result[group.Key] = ordered
                .Select((c, i) => new Placed(c, i + 1, sorts[i], ambiguous.Contains((c.Number, c.Volume, c.IsOneShot))))
                .ToList();
        }

        return result;
    }

    private async Task<Dictionary<int, ProgressRow>> ProgressAsync(IReadOnlyCollection<int> chapterIds, CancellationToken ct) =>
        await db.ChapterProgress.AsNoTracking()
            .Where(p => chapterIds.Contains(p.ChapterId))
            .Select(p => new ProgressRow(p.ChapterId, p.PageIndex, p.Completed, p.StartedAt, p.UpdatedAt))
            .ToDictionaryAsync(p => p.ChapterId, ct);

    private async Task<List<KomgaBook>> ToBooksAsync(
        IReadOnlyList<Placed> placed,
        IReadOnlyDictionary<int, SeriesRow> series,
        IReadOnlyDictionary<int, ProgressRow> progress,
        CancellationToken ct)
    {
        // The measured page count when the file backs only this chapter, else the archive slice, the
        // same rule ReaderService.MarkReadAsync uses; a list of a long series then costs no archive reads.
        var needSlices = placed
            .Where(p => p.Row.SharesFile || p.Row.MeasuredPages is not > 0)
            .Select(p => p.Row.Id)
            .ToList();
        var slices = await reader.SlicesAsync(needSlices, ct);

        var books = new List<KomgaBook>(placed.Count);
        foreach (var p in placed)
        {
            if (!series.TryGetValue(p.Row.SeriesId, out var s))
            {
                continue;
            }

            var pages = !p.Row.SharesFile && p.Row.MeasuredPages is > 0 and var measured
                ? measured
                : slices.GetValueOrDefault(p.Row.Id)?.PageCount;
            if (pages is not > 0)
            {
                continue;
            }

            books.Add(ToBook(p, s, pages.Value, progress.GetValueOrDefault(p.Row.Id)));
        }

        return books;
    }

    private static KomgaBook ToBook(Placed p, SeriesRow s, int pages, ProgressRow? progress)
    {
        var c = p.Row;
        var label = ChapterLabel.For(c.Number, c.Volume, c.Title, c.IsOneShot);
        var title = string.IsNullOrWhiteSpace(c.Title) || c.IsOneShot || c.Number is null ? label : c.Title;
        if (p.Ambiguous)
        {
            label = $"{label} [{c.Language}]";
            title = $"{title} [{c.Language}]";
        }

        var number = c.Number?.ToString("0.###", CultureInfo.InvariantCulture)
                     ?? p.NumberSort.ToString("0.###", CultureInfo.InvariantCulture);
        var release = c.ReleaseDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        KomgaReadProgress? read = null;
        if (progress is not null && (progress.Completed || progress.PageIndex > 0))
        {
            read = new KomgaReadProgress(
                progress.Completed ? pages : Math.Clamp(progress.PageIndex + 1, 1, pages),
                progress.Completed,
                progress.UpdatedAt,
                progress.StartedAt,
                progress.UpdatedAt,
                string.Empty,
                string.Empty);
        }

        return new KomgaBook(
            Id(c.Id),
            Id(s.Id),
            s.Title,
            Id(s.RootFolderId),
            label,
            c.RelativePath,
            p.Index,
            c.DateAdded,
            c.DateAdded,
            c.DateAdded,
            c.Size,
            HumanSize(c.Size),
            new KomgaMedia("READY", OpdsXml.MimeType(c.RelativePath), pages, string.Empty, "DIVINA", false, false),
            new KomgaBookMetadata(
                title, false,
                string.Empty, false,
                number, false,
                p.NumberSort, false,
                release, false,
                AuthorsOf(s), false,
                [], false,
                string.Empty, false,
                [], false,
                c.DateAdded,
                c.DateAdded),
            read,
            false,
            string.Empty,
            c.IsOneShot);
    }

    private static string HumanSize(long bytes)
    {
        string[] units = ["B", "KiB", "MiB", "GiB"];
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return $"{value.ToString(unit == 0 ? "0" : "0.#", CultureInfo.InvariantCulture)} {units[unit]}";
    }

    private static bool Contains(string? haystack, string needle) =>
        haystack is not null && haystack.Contains(needle, StringComparison.OrdinalIgnoreCase);

    public static string Id(int id) => id.ToString(CultureInfo.InvariantCulture);
}
