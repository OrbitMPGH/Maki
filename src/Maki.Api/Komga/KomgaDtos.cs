namespace Maki.Api.Komga;

// The wire shapes Komga clients parse. Every field is always written, nulls included: the Mihon
// extension and tracker deserialize with kotlinx.serialization, which rejects a missing key even
// where the value may be null. Field names and enum strings follow Komga 1.x.

public record KomgaSort(bool Sorted, bool Unsorted, bool Empty);

public record KomgaPageable(int PageNumber, int PageSize, KomgaSort Sort, long Offset, bool Paged, bool Unpaged);

public record KomgaPage<T>(
    IReadOnlyList<T> Content,
    KomgaPageable Pageable,
    int TotalElements,
    int TotalPages,
    bool Last,
    int Number,
    int Size,
    int NumberOfElements,
    bool First,
    bool Empty,
    KomgaSort Sort)
{
    /// <param name="size">Null for an unpaged request: one page holding everything.</param>
    public static KomgaPage<T> Of(IReadOnlyList<T> all, int page, int? size, bool sorted = true)
    {
        var pageSize = size ?? Math.Max(all.Count, 1);
        var number = size is null ? 0 : page;
        var content = all.Skip(number * pageSize).Take(pageSize).ToList();
        return Slice(content, all.Count, number, pageSize, size is null, sorted);
    }

    /// <summary>A page whose content was already cut, out of <paramref name="total"/>.</summary>
    public static KomgaPage<T> Slice(
        IReadOnlyList<T> content, int total, int number, int pageSize, bool unpaged = false, bool sorted = true)
    {
        var totalPages = pageSize == 0 ? 0 : (int)Math.Ceiling(total / (double)pageSize);
        var sort = new KomgaSort(sorted, !sorted, !sorted);
        return new KomgaPage<T>(
            content,
            new KomgaPageable(number, pageSize, sort, (long)number * pageSize, !unpaged, unpaged),
            total,
            totalPages,
            number >= totalPages - 1,
            number,
            pageSize,
            content.Count,
            number == 0,
            content.Count == 0,
            sort);
    }
}

public record KomgaLibrary(
    string Id,
    string Name,
    string Root,
    bool ImportComicInfoBook,
    bool ImportComicInfoSeries,
    bool ImportComicInfoCollection,
    bool ImportComicInfoReadList,
    bool ImportEpubBook,
    bool ImportEpubSeries,
    bool ImportMylarSeries,
    bool ImportLocalArtwork,
    bool ImportBarcodeIsbn,
    bool ScanForceModifiedTime,
    bool RepairExtensions,
    bool ConvertToCbz,
    bool EmptyTrashAfterScan,
    string SeriesCover,
    bool Unavailable);

public record KomgaAuthor(string Name, string Role);

public record KomgaAlternateTitle(string Label, string Title);

public record KomgaWebLink(string Label, string Url);

public record KomgaSeriesMetadata(
    string Status,
    bool StatusLock,
    string Title,
    bool TitleLock,
    string TitleSort,
    bool TitleSortLock,
    string Summary,
    bool SummaryLock,
    string ReadingDirection,
    bool ReadingDirectionLock,
    string Publisher,
    bool PublisherLock,
    int? AgeRating,
    bool AgeRatingLock,
    string Language,
    bool LanguageLock,
    IReadOnlyList<string> Genres,
    bool GenresLock,
    IReadOnlyList<string> Tags,
    bool TagsLock,
    int? TotalBookCount,
    bool TotalBookCountLock,
    IReadOnlyList<string> SharingLabels,
    bool SharingLabelsLock,
    IReadOnlyList<KomgaWebLink> Links,
    bool LinksLock,
    IReadOnlyList<KomgaAlternateTitle> AlternateTitles,
    bool AlternateTitlesLock,
    DateTime Created,
    DateTime LastModified);

public record KomgaBooksMetadata(
    IReadOnlyList<KomgaAuthor> Authors,
    IReadOnlyList<string> Tags,
    string? ReleaseDate,
    string Summary,
    string SummaryNumber,
    DateTime Created,
    DateTime LastModified);

public record KomgaSeries(
    string Id,
    string LibraryId,
    string Name,
    string Url,
    DateTime Created,
    DateTime LastModified,
    DateTime FileLastModified,
    int BooksCount,
    int BooksReadCount,
    int BooksUnreadCount,
    int BooksInProgressCount,
    KomgaSeriesMetadata Metadata,
    KomgaBooksMetadata BooksMetadata,
    bool Deleted,
    bool Oneshot);

public record KomgaMedia(
    string Status,
    string MediaType,
    int PagesCount,
    string Comment,
    string MediaProfile,
    bool EpubDivinaCompatible,
    bool EpubIsKepub);

public record KomgaBookMetadata(
    string Title,
    bool TitleLock,
    string Summary,
    bool SummaryLock,
    string Number,
    bool NumberLock,
    double NumberSort,
    bool NumberSortLock,
    string? ReleaseDate,
    bool ReleaseDateLock,
    IReadOnlyList<KomgaAuthor> Authors,
    bool AuthorsLock,
    IReadOnlyList<string> Tags,
    bool TagsLock,
    string Isbn,
    bool IsbnLock,
    IReadOnlyList<KomgaWebLink> Links,
    bool LinksLock,
    DateTime Created,
    DateTime LastModified);

public record KomgaReadProgress(
    int Page,
    bool Completed,
    DateTime ReadDate,
    DateTime Created,
    DateTime LastModified,
    string DeviceId,
    string DeviceName);

public record KomgaBook(
    string Id,
    string SeriesId,
    string SeriesTitle,
    string LibraryId,
    string Name,
    string Url,
    int Number,
    DateTime Created,
    DateTime LastModified,
    DateTime FileLastModified,
    long SizeBytes,
    string Size,
    KomgaMedia Media,
    KomgaBookMetadata Metadata,
    KomgaReadProgress? ReadProgress,
    bool Deleted,
    string FileHash,
    bool Oneshot);

public record KomgaPageInfo(int Number, string FileName, string MediaType, int? Width, int? Height, long? SizeBytes);

public record KomgaUser(
    string Id,
    string Email,
    IReadOnlyList<string> Roles,
    bool SharedAllLibraries,
    IReadOnlyList<string> SharedLibrariesIds,
    IReadOnlyList<string> LabelsAllow,
    IReadOnlyList<string> LabelsExclude,
    object? AgeRestriction);

public record KomgaReadProgressUpdate(int? Page, bool? Completed);

public record KomgaTachiyomiProgress(
    int BooksCount,
    int BooksReadCount,
    int BooksUnreadCount,
    int BooksInProgressCount,
    int LastReadContinuousIndex);

public record KomgaTachiyomiProgressV2(
    int BooksCount,
    int BooksReadCount,
    int BooksUnreadCount,
    int BooksInProgressCount,
    double LastReadContinuousNumberSort,
    double MaxNumberSort);

public record KomgaTachiyomiUpdate(int? LastBookRead);

public record KomgaTachiyomiUpdateV2(double? LastBookNumberSortRead);
