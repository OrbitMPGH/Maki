using Maki.Api.Services;

namespace Maki.Api.Dtos;

/// <summary>
/// Scrobble status for one library series, surfaced on its detail page. Kavita-driven pushes are
/// linked to the series the same way the sync engine matches (punctuation-normalized title or
/// folder name); the built-in reader's pushes are keyed by the series itself.
/// </summary>
public record SeriesScrobbleDto(
    /// <summary>At least one tracker is connected, so scrobbling is set up.</summary>
    bool Configured,
    /// <summary>Some scrobble data exists: a Kavita series matched, or the built-in reader pushed it.</summary>
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
