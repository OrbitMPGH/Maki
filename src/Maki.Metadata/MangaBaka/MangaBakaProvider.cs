using Maki.Core.Entities;
using Maki.Core.Metadata;

namespace Maki.Metadata.MangaBaka;

/// <summary>
/// Serves metadata from the local MangaBaka dump. The dump is required: the SPA stays behind a
/// gate until it is installed, so the empty answers below only reach background work that runs
/// before the first download finishes.
/// </summary>
public class MangaBakaProvider(MangaBakaLocalStore localStore) : IMetadataProvider
{
    public string Name => "mangabaka";

    public async Task<IReadOnlyList<MetadataSearchResult>> SearchAsync(
        string query, string maxContentRating, CancellationToken ct = default) =>
        await localStore.IsAvailableAsync(ct)
            ? await localStore.SearchAsync(query, maxContentRating, ct: ct)
            : [];

    public async Task<SeriesMetadata?> GetAsync(string providerId, CancellationToken ct = default) =>
        await localStore.IsAvailableAsync(ct)
            ? await localStore.GetAsync(providerId, ct)
            : null;

    public static SeriesStatus MapStatus(string? status) => status?.ToLowerInvariant() switch
    {
        "releasing" => SeriesStatus.Ongoing,
        "completed" => SeriesStatus.Completed,
        "hiatus" => SeriesStatus.Hiatus,
        "cancelled" => SeriesStatus.Cancelled,
        _ => SeriesStatus.Unknown
    };
}
