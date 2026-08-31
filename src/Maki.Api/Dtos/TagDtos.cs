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
    string FileSourceMatch = "any");

public record SavedFilterDto(int Id, string Name, LibraryFilterSpec Spec, int SortOrder);

public record SaveFilterRequest(string Name, LibraryFilterSpec Spec);
