using Maki.Core.Entities;

namespace Maki.Api.Dtos;

public record CreditGroupListDto(IReadOnlyList<CreditGroupDto> Groups);

/// <summary>
/// One release group in the groups view: the notices that name it (or the Site and Unnamed buckets).
/// <see cref="Key"/> is <c>group:&lt;name key&gt;</c>, <c>site</c> or <c>unnamed</c>. <see cref="NameSource"/> is the
/// strongest source among its notices: <c>confirmed</c>, <c>propagated</c>, <c>suggested</c> or <c>none</c>.
/// <see cref="Kind"/> is <c>group</c>, <c>site</c> or <c>unknown</c>.
/// </summary>
public record CreditGroupDto(
    string Key, string? Name, string NameSource, string Kind, IReadOnlyList<int> NoticeIds,
    int Notices, int Series, int Chapters, int ChaptersWithoutGroup);

public record CreditGroupDetailDto(IReadOnlyList<CreditNoticeDto> Notices, IReadOnlyList<CreditGroupSeriesDto> Series);

/// <summary>
/// A notice as the groups view shows it. <see cref="Hidden"/> is the verdict readers get (the override, else
/// <see cref="Accepted"/>, which is OCR's). <see cref="Override"/> is <c>none</c>, <c>hide</c> or <c>keep</c>;
/// <see cref="Kind"/> <c>unknown</c>, <c>group</c> or <c>site</c>; <see cref="GroupSource"/> <c>none</c>,
/// <c>propagated</c> or <c>confirmed</c>. <see cref="Share"/> is the share of the page from the top, or from the
/// bottom when <see cref="Tail"/> and not <see cref="Whole"/>.
/// </summary>
public record CreditNoticeDto(
    int Id, bool Hidden, bool Accepted, string Override, string Kind, string? GroupName, string GroupSource,
    string? SuggestedName, string? Invite, string? Excerpt, bool Whole, double Share, bool Tail, int Chapters, int Series);

public record CreditGroupSeriesDto(int SeriesId, string Title, IReadOnlyList<CreditGroupChapterDto> Chapters);

/// <summary><see cref="Group"/> is the group the chapter's file carries, null when it has none.</summary>
public record CreditGroupChapterDto(
    int ChapterId, string Label, decimal? Number, int? Volume, string? Title, bool IsOneShot, string? Group,
    IReadOnlyList<int> NoticeIds);

/// <summary>
/// Each field null leaves it as it is. <see cref="GroupName"/> confirms that name (and makes the notice a Group),
/// an empty string clears the name.
/// </summary>
public record UpdateCreditNoticeRequest(
    CreditNoticeKind? Kind = null, string? GroupName = null, CreditNoticeOverride? Override = null);

/// <summary>Confirms one name for several notices at once, such as every notice that suggests it.</summary>
public record NameCreditNoticesRequest(List<int> NoticeIds, string Name);

/// <summary>
/// The notices a detail or an apply is for. A body rather than a query string: the Site and Unnamed buckets can
/// hold hundreds of notices, more than a request line takes.
/// </summary>
public record CreditNoticeIdsRequest(List<int> NoticeIds);

/// <summary>
/// <see cref="ChaptersUpdated"/>: chapters whose file took a group. <see cref="FilesRenamed"/>: files the naming
/// format then moved. <see cref="Conflicts"/>: files left alone because two notices name different groups for
/// them. <see cref="Warnings"/>: the rename's own messages, already localized.
/// </summary>
public record ApplyCreditGroupsResult(int ChaptersUpdated, int FilesRenamed, int Conflicts, IReadOnlyList<string> Warnings);
