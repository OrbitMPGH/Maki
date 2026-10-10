namespace Maki.Core.Entities;

/// <summary>
/// The metadata fields a user can set by hand. Stored as a flags column on
/// <see cref="Series.LockedFields"/>: a set flag means the value was chosen by a user and a metadata
/// refresh must leave it alone. Persisted, so append only: never renumber or reuse a bit.
/// </summary>
[Flags]
public enum SeriesMetadataField
{
    None = 0,
    Title = 1,
    Overview = 2,
    Status = 4,
    TotalChapters = 8,
    TotalVolumes = 16,
    Genres = 32,
    Cover = 64,
}
