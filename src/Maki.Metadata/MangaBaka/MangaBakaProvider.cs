using System.Net.Http.Json;
using Maki.Core.Entities;
using Maki.Core.Metadata;
using Microsoft.Extensions.Logging;

namespace Maki.Metadata.MangaBaka;

/// <summary>
/// Serves metadata from the local MangaBaka dump when it has been downloaded
/// (no rate limits), falling back to the rate-limited HTTP API otherwise.
/// </summary>
public class MangaBakaProvider(
    IHttpClientFactory httpClientFactory,
    MangaBakaLocalStore localStore,
    ILogger<MangaBakaProvider> logger) : IMetadataProvider
{
    public const string HttpClientName = "mangabaka";

    public string Name => "mangabaka";

    public async Task<IReadOnlyList<MetadataSearchResult>> SearchAsync(string query, CancellationToken ct = default)
    {
        if (await localStore.IsAvailableAsync(ct))
        {
            try
            {
                return await localStore.SearchAsync(query, ct);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Local MangaBaka search failed for {Query}; falling back to API", query);
            }
        }

        var client = httpClientFactory.CreateClient(HttpClientName);
        var response = await client.GetFromJsonAsync<MangaBakaSearchResponse>(
            $"v1/series/search?q={Uri.EscapeDataString(query)}&limit=20", ct);

        return response?.Data
            .Where(s => s.State != "merged")
            .Select(s => new MetadataSearchResult(
                s.Id.ToString(),
                s.Title,
                s.Cover?.Raw?.Url,
                s.Year,
                MapStatus(s.Status),
                s.Description,
                s.TotalChapters))
            .ToList() ?? [];
    }

    public async Task<SeriesMetadata?> GetAsync(string providerId, CancellationToken ct = default)
    {
        if (await localStore.IsAvailableAsync(ct))
        {
            try
            {
                var local = await localStore.GetAsync(providerId, ct);
                if (local is not null)
                {
                    return local;
                }

                // Series newer than the nightly dump can only be resolved by the API.
                logger.LogDebug("MangaBaka series {Id} not in local dump; trying API", providerId);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Local MangaBaka lookup failed for {Id}; falling back to API", providerId);
            }
        }

        return await GetFromApiAsync(providerId, ct);
    }

    private async Task<SeriesMetadata?> GetFromApiAsync(string providerId, CancellationToken ct)
    {
        var client = httpClientFactory.CreateClient(HttpClientName);
        var response = await client.GetFromJsonAsync<MangaBakaGetResponse>($"v1/series/{providerId}", ct);
        var s = response?.Data;
        if (s is null)
        {
            return null;
        }

        // Merged entries redirect to their canonical series.
        if (s.State == "merged" && s.MergedWith is int canonical)
        {
            logger.LogInformation("MangaBaka series {Id} merged into {Canonical}; following", providerId, canonical);
            return await GetFromApiAsync(canonical.ToString(), ct);
        }

        return new SeriesMetadata
        {
            ProviderId = s.Id.ToString(),
            Title = s.Title,
            OriginalTitle = s.NativeTitle,
            Description = s.Description,
            CoverUrl = s.Cover?.Raw?.Url,
            Year = s.Year,
            Status = MapStatus(s.Status),
            Genres = s.Genres,
            Tags = s.Tags,
            AuthorStory = s.Authors.Count > 0 ? string.Join(", ", s.Authors) : null,
            AuthorArt = s.Artists.Count > 0 ? string.Join(", ", s.Artists) : null,
            TotalChapters = s.TotalChapters,
            TotalVolumes = s.FinalVolume,
            WebUrl = $"https://mangabaka.org/{s.Id}",
            MangaBakaId = s.Id,
            AniListId = s.Source?.AniList?.Id,
            MalId = s.Source?.MyAnimeList?.Id,
            MangaUpdatesId = s.Source?.MangaUpdates?.Id
        };
    }

    internal static SeriesStatus MapStatus(string? status) => status?.ToLowerInvariant() switch
    {
        "releasing" => SeriesStatus.Ongoing,
        "completed" => SeriesStatus.Completed,
        "hiatus" => SeriesStatus.Hiatus,
        "cancelled" => SeriesStatus.Cancelled,
        _ => SeriesStatus.Unknown
    };
}
