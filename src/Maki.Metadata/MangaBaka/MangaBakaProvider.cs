using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
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

    public async Task<IReadOnlyList<MetadataSearchResult>> SearchAsync(
        string query, string maxContentRating, CancellationToken ct = default)
    {
        if (await localStore.IsAvailableAsync(ct))
        {
            try
            {
                return await localStore.SearchAsync(query, maxContentRating, ct: ct);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Local MangaBaka search failed for {Query}; falling back to API", query);
            }
        }

        var allowed = ContentRating.Allowed(maxContentRating);
        var client = httpClientFactory.CreateClient(HttpClientName);
        var response = await client.GetFromJsonAsync<MangaBakaSearchResponse>(
            $"v1/series/search?q={Uri.EscapeDataString(query)}&limit=20", ct);

        return response?.Data
            .Where(s => s.State != "merged" && s.Type != "novel" && allowed.Contains(s.ContentRating))
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

    /// <summary>Same cap as the local store's merge walk; two rows merged into each other would loop.</summary>
    internal const int MaxMergeHops = 5;

    private async Task<SeriesMetadata?> GetFromApiAsync(string providerId, CancellationToken ct)
    {
        // Spliced into the path, so anything but a plain id could change which endpoint is asked.
        if (!long.TryParse(providerId, NumberStyles.None, CultureInfo.InvariantCulture, out var id))
        {
            return null;
        }

        var client = httpClientFactory.CreateClient(HttpClientName);
        for (var hop = 0; ; hop++)
        {
            using var response = await client.GetAsync($"v1/series/{id.ToString(CultureInfo.InvariantCulture)}", ct);
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return null;
            }

            response.EnsureSuccessStatusCode();
            var s = (await response.Content.ReadFromJsonAsync<MangaBakaGetResponse>(ct))?.Data;
            if (s is null)
            {
                return null;
            }

            // Merged entries redirect to their canonical series.
            if (s.State == "merged" && s.MergedWith is int canonical)
            {
                if (hop >= MaxMergeHops)
                {
                    logger.LogWarning("MangaBaka series {Id} is still merged after {Hops} hops; giving up", providerId, hop);
                    return null;
                }

                logger.LogInformation("MangaBaka series {Id} merged into {Canonical}; following", id, canonical);
                id = canonical;
                continue;
            }

            return ToMetadata(s);
        }
    }

    private static SeriesMetadata? ToMetadata(MangaBakaSeries s)
    {
        if (s.Type == "novel")
        {
            return null;
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
            Type = SeriesTypes.Normalize(s.Type),
            Genres = s.Genres,
            Tags = s.Tags,
            ContentRating = s.ContentRating,
            AuthorStory = s.Authors.Count > 0 ? string.Join(", ", s.Authors) : null,
            AuthorArt = s.Artists.Count > 0 ? string.Join(", ", s.Artists) : null,
            Publisher = ParsePublisherNames(s.Publishers) is { Count: > 0 } publishers
                ? string.Join(", ", publishers)
                : null,
            TotalChapters = s.TotalChapters,
            TotalVolumes = s.FinalVolume,
            WebUrl = $"https://mangabaka.org/{s.Id}",
            MangaBakaId = s.Id,
            AniListId = s.Source?.AniList?.Id,
            MalId = s.Source?.MyAnimeList?.Id,
            KitsuId = s.Source?.Kitsu?.Id,
            MangaUpdatesId = s.Source?.MangaUpdates?.Id,
            Partial = true
        };
    }

    public static SeriesStatus MapStatus(string? status) => status?.ToLowerInvariant() switch
    {
        "releasing" => SeriesStatus.Ongoing,
        "completed" => SeriesStatus.Completed,
        "hiatus" => SeriesStatus.Hiatus,
        "cancelled" => SeriesStatus.Cancelled,
        _ => SeriesStatus.Unknown
    };

    /// <summary>Publisher entries are objects (<c>{"name","note","type"}</c>), occasionally bare strings.</summary>
    private static List<string> ParsePublisherNames(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var names = new List<string>();
        foreach (var item in element.EnumerateArray())
        {
            var name = item.ValueKind == JsonValueKind.Object &&
                       item.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String
                ? n.GetString()?.Trim()
                : item.ValueKind == JsonValueKind.String ? item.GetString()?.Trim() : null;
            if (!string.IsNullOrWhiteSpace(name) && !names.Contains(name, StringComparer.OrdinalIgnoreCase))
            {
                names.Add(name);
            }
        }

        return names;
    }
}
