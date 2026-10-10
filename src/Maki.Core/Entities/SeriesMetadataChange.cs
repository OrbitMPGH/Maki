namespace Maki.Core.Entities;

/// <summary>Persisted as an int: append only.</summary>
public enum MetadataChangeSource
{
    Refresh = 0,
    User = 1,
}

/// <summary>
/// One metadata value that changed, from a provider refresh or a user edit. Values are stored as
/// invariant strings (a status by its enum name) so the history reads the same in every language
/// and the client words them.
/// </summary>
public class SeriesMetadataChange
{
    public int Id { get; set; }
    public int SeriesId { get; set; }

    /// <summary>Exactly one flag.</summary>
    public SeriesMetadataField Field { get; set; }

    /// <summary>Null when the field had no value, and always null for the synopsis and the cover.</summary>
    public string? OldValue { get; set; }

    public string? NewValue { get; set; }
    public MetadataChangeSource Source { get; set; }

    /// <summary>Who made a <see cref="MetadataChangeSource.User"/> change; null for a refresh or a deleted account.</summary>
    public int? UserId { get; set; }

    public DateTime ChangedAtUtc { get; set; }
}
