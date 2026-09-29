using Maki.Api.Services;
using Maki.Core.Entities;
using Maki.Core.Metadata;

namespace Maki.Api.Tests;

/// <summary>
/// A refresh answered by MangaBaka's API fallback has no alt titles, anime data or spoiler flags.
/// It must not blank out what an earlier dump-backed refresh filled in.
/// </summary>
public class SeriesMetadataRefreshPartialTests
{
    private sealed class Provider(SeriesMetadata metadata) : IMetadataProvider
    {
        public string Name => "stub";

        public Task<IReadOnlyList<MetadataSearchResult>> SearchAsync(
            string query, string maxContentRating, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<MetadataSearchResult>>([]);

        public Task<SeriesMetadata?> GetAsync(string providerId, CancellationToken ct = default) =>
            Task.FromResult<SeriesMetadata?>(metadata);
    }

    private static Series Existing() => new()
    {
        Title = "Frieren",
        MangaBakaId = 42,
        HasAnime = true,
        AltTitles = [new LocalizedTitle("Sousou no Frieren", "ja-ro")],
        Tags = ["Elf", "Journey"],
    };

    private static Task Refresh(Series series, SeriesMetadata metadata) =>
        new SeriesMetadataRefreshService([new Provider(metadata)], null!).RefreshAsync(series, includeCover: false);

    [Fact]
    public async Task A_partial_result_keeps_alt_titles_anime_and_tags()
    {
        var series = Existing();

        await Refresh(series, new SeriesMetadata
        {
            ProviderId = "42", Title = "Frieren", Tags = ["Elf", "Spoiler"], Partial = true
        });

        Assert.True(series.HasAnime);
        Assert.Single(series.AltTitles);
        Assert.Equal(["Elf", "Journey"], series.Tags);
    }

    [Fact]
    public async Task A_full_result_still_clears_them()
    {
        var series = Existing();

        await Refresh(series, new SeriesMetadata { ProviderId = "42", Title = "Frieren", Tags = ["Elf"] });

        Assert.False(series.HasAnime);
        Assert.Empty(series.AltTitles);
        Assert.Equal(["Elf"], series.Tags);
    }
}
