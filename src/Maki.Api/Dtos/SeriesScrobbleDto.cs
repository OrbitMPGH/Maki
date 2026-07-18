using Maki.Api.Services;

namespace Maki.Api.Dtos;

/// <summary>
/// Scrobble status for one library series, surfaced on its detail page. The series is
/// linked to its Kavita counterpart the same way the sync engine matches — by
/// punctuation-normalized title / folder name — so no extra bookkeeping is needed.
/// </summary>
public record SeriesScrobbleDto(
    /// <summary>Kavita is configured and at least one tracker is connected — scrobbling is set up.</summary>
    bool Configured,
    /// <summary>A Kavita series was found for this library series (some scrobble data exists).</summary>
    bool Matched,
    /// <summary>The matched Kavita series id, used to drive manual match / ignore actions.</summary>
    int? KavitaSeriesId,
    List<SeriesScrobbleServiceDto> Services);

/// <summary>Per-tracker scrobble state for a series.</summary>
public record SeriesScrobbleServiceDto(
    string Service,
    string Label,
    bool Connected,
    /// <summary>Resolved remote id, or null when unmatched/ignored.</summary>
    string? RemoteId,
    /// <summary>How the id was resolved: library | weblink | derived | search | manual | ignored.</summary>
    string? Method,
    /// <summary>Deep link to the remote entry, or null when there's no id.</summary>
    string? Url,
    int Chapter,
    int Volume,
    string? Status,
    DateTime? SyncedAt,
    string? Error,
    /// <summary>Set when this series needs review for this tracker (no confident match).</summary>
    string? ReviewReason,
    List<ScrobbleService.CandidateDto> ReviewCandidates);
