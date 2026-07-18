namespace Maki.Core.Sources;

/// <summary>
/// Optional source capability: maps chapter numbers to volume numbers for a series,
/// including chapters the source itself cannot serve (delisted/licensed titles).
/// Used to link volume CBZs to chapter rows that carry no volume info of their own.
/// </summary>
public interface IChapterVolumeSource
{
    /// <summary>Chapter number → volume number. Empty when the source has no volume data.</summary>
    Task<IReadOnlyDictionary<decimal, int>> GetChapterVolumesAsync(string sourceSeriesId, CancellationToken ct = default);
}
