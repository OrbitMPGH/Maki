namespace Maki.Api.Dtos;

/// <summary><see cref="SeriesCount"/> is how many series currently carry the tag.</summary>
public record TagDto(int Id, string Label, string Color, int SeriesCount);

public record CreateTagRequest(string Label, string? Color = null);

public record UpdateTagRequest(string? Label = null, string? Color = null);

/// <summary>
/// Adds and/or removes tags across a set of series in one round trip — the Library's bulk bar
/// applies both directions at once, and doing it per series would be one request per selected card.
/// </summary>
public record BulkTagRequest(List<int> SeriesIds, List<int>? Add = null, List<int>? Remove = null);

public record SetSeriesTagsRequest(List<int> TagIds);

/// <summary>
/// The Library grid's filter state. Evaluated client-side (the whole series list is already in the
/// browser); the server only stores it verbatim as a <see cref="Maki.Core.Entities.SavedFilter"/>.
/// </summary>
public record LibraryFilterSpec(
    string? Query = null,
    string Status = "all",
    List<int>? TagIds = null,
    /// <summary>"any" (default) or "all" — whether a series must carry every listed tag.</summary>
    string TagMatch = "any",
    /// <summary>"all" | "monitored" | "unmonitored".</summary>
    string Monitored = "all",
    /// <summary>"all" | "behind" (missing chapters) | "complete".</summary>
    string Completeness = "all",
    string Sort = "added",
    List<string>? Genres = null,
    /// <summary>"any" or "all" — see <see cref="TagMatch"/>.</summary>
    string GenreMatch = "any",
    /// <summary>Provider-owned tags (<c>Series.Tags</c>), not the user's.</summary>
    List<string>? MetadataTags = null,
    string MetadataTagMatch = "any",
    /// <summary>
    /// Read-percentage window, 0–100 inclusive. Only meaningful with Kavita connected — read
    /// progress has no other source — so the UI hides it otherwise and stores the full range.
    /// </summary>
    int ReadMin = 0,
    int ReadMax = 100,
    /// <summary>
    /// Chapter-count window, null on either end for "unbounded". <see cref="ChapterMode"/> picks
    /// which count it reads.
    /// </summary>
    int? ChapterMin = null,
    int? ChapterMax = null,
    /// <summary>
    /// "downloaded" (files on disk) or "total" (the denominator the cards show: monitored
    /// chapters, falling back to every known chapter when nothing is monitored).
    /// </summary>
    string ChapterMode = "downloaded",
    /// <summary>
    /// <c>ContentRating</c> vocabulary values to include, empty for "any". Gated client-side by the
    /// signed-in user's ceiling, so a preset saved by an admin narrows rather than widens for
    /// everyone else.
    /// </summary>
    List<string>? ContentRatings = null,
    /// <summary>Source keys the series must be linked to (<see cref="Maki.Api.Dtos.SeriesDto.Sources"/>).</summary>
    List<string>? Sources = null,
    /// <summary>"any" or "all" — see <see cref="TagMatch"/>.</summary>
    string SourceMatch = "any",
    /// <summary>
    /// "all" | "none" (nothing linked) | "hasEnabled" | "noneEnabled" (linked, but nothing live) |
    /// "hasDisabled" (at least one linked source switched off, per-series or globally).
    /// </summary>
    string SourceState = "all",
    /// <summary>Source keys the series' downloaded files came from (<see cref="Maki.Api.Dtos.SeriesDto.FileSources"/>).</summary>
    List<string>? FileSources = null,
    /// <summary>"any" or "all" — see <see cref="TagMatch"/>.</summary>
    string FileSourceMatch = "any",
    /// <summary>
    /// "all", "default" (no pin of its own, so the instance default applies), or a quality profile id
    /// as a string (<see cref="Maki.Api.Dtos.SeriesDto.UpgradeProfileId"/>).
    /// </summary>
    string QualityProfile = "all",
    /// <summary><see cref="Maki.Core.Entities.SeriesTypes"/> values to include, empty for "any".</summary>
    List<string>? Types = null,
    /// <summary>User tag ids a series must not carry. Absent on presets saved before this existed.</summary>
    List<int>? ExcludeTagIds = null,
    /// <summary>Genres a series must not carry.</summary>
    List<string>? ExcludeGenres = null,
    /// <summary>Only series with none of the user's own tags.</summary>
    bool UntaggedOnly = false,
    /// <summary>"asc" or "desc" to override <see cref="Sort"/>'s natural direction; empty keeps it.</summary>
    string SortDir = "",
    /// <summary>
    /// "all" | "rated" | "unrated", on the user's own 1 to 10 rating. <see cref="RatingMin"/> and
    /// <see cref="RatingMax"/> only apply with "rated".
    /// </summary>
    string RatingState = "all",
    int RatingMin = 1,
    int RatingMax = 10,
    /// <summary>
    /// MangaBaka score window, 0–10 inclusive. The full range means "don't filter"; any narrower
    /// window drops series without a score.
    /// </summary>
    double CommunityRatingMin = 0,
    double CommunityRatingMax = 10,
    /// <summary>The caller's own <see cref="Maki.Core.Entities.ReadingStatus"/> names to include, empty for "any".</summary>
    List<string>? ReadingStatuses = null,
    /// <summary>First-year window; null on either end for "unbounded". Series without a year drop out once either is set.</summary>
    int? YearMin = null,
    int? YearMax = null,
    /// <summary>Story or art credits, any one of which the series must carry. Empty for "any".</summary>
    List<string>? Credits = null,
    /// <summary>Publishers to include, empty for "any".</summary>
    List<string>? Publishers = null,
    /// <summary>Root folder ids to include, empty for "any".</summary>
    List<int>? RootFolderIds = null,
    /// <summary>"all" | "on" (any incognito mode) | "off".</summary>
    string Incognito = "all",
    /// <summary>"all" | "muted" | "unmuted", on the caller's own notification mode.</summary>
    string Muted = "all",
    /// <summary>"all" | "has" | "none", on whether an anime adaptation is recorded.</summary>
    string Anime = "all",
    /// <summary>"all" | "clash" | "none", on whether sources disagree on chapter numbering.</summary>
    string NumberingClash = "all",
    /// <summary>"readCleanup" and/or "pageConversion": series with their own setting rather than the default.</summary>
    List<string>? Overrides = null,
    /// <summary>Only series added within this many days; null for "any time".</summary>
    int? AddedWithinDays = null);

public record SavedFilterDto(int Id, string Name, LibraryFilterSpec Spec, int SortOrder);

public record SaveFilterRequest(string Name, LibraryFilterSpec Spec);
