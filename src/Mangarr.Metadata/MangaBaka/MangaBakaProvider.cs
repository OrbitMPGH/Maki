using System.Net.Http.Json;
using Mangarr.Core.Entities;
using Mangarr.Core.Metadata;
using Microsoft.Extensions.Logging;

namespace Mangarr.Metadata.MangaBaka;

public class MangaBakaProvider(IHttpClientFactory httpClientFactory, ILogger<MangaBakaProvider> logger) : IMetadataProvider
{
    public const string HttpClientName = "mangabaka";

    public string Name => "mangabaka";

    public async Task<IReadOnlyList<MetadataSearchResult>> SearchAsync(string query, CancellationToken ct = default)
    {
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
            return await GetAsync(canonical.ToString(), ct);
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

    private static SeriesStatus MapStatus(string? status) => status?.ToLowerInvariant() switch
    {
        "releasing" => SeriesStatus.Ongoing,
        "completed" => SeriesStatus.Completed,
        "hiatus" => SeriesStatus.Hiatus,
        "cancelled" => SeriesStatus.Cancelled,
        _ => SeriesStatus.Unknown
    };
}
