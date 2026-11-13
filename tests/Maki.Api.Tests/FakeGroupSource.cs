using Maki.Core.Sources;

namespace Maki.Api.Tests;

/// <summary>A source listing several groups' uploads of a chapter, as MangaDex does.</summary>
internal sealed class FakeGroupSource : IScanlationGroupSource
{
    public List<SourceChapter> All { get; } = [];
    public string Name => "multi";
    public string DisplayName => "Multi";
    public string BaseUrl => "https://multi.test";
    public SourceCapabilities Capabilities => SourceCapabilities.None;

    public FakeGroupSource Add(string id, decimal number, string? group, int day)
    {
        All.Add(new SourceChapter(Name, "series", id, number.ToString(), number, null, null, "en",
            new DateTime(2025, 1, day, 0, 0, 0, DateTimeKind.Utc), Group: group));
        return this;
    }

    public Task<IReadOnlyList<SourceChapter>> ListUploadsAsync(string sourceSeriesId, string? languageFilter, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<SourceChapter>>([.. All]);

    public async Task<IReadOnlyList<SourceChapter>> ListChaptersAsync(string sourceSeriesId, string? languageFilter = null, CancellationToken ct = default) =>
        ScanlationGroupChoice.Pick(await ListUploadsAsync(sourceSeriesId, languageFilter, ct), ScanlationGroupPreference.None);

    public Task<IReadOnlyList<SourceSeriesResult>> SearchAsync(string title, CancellationToken ct = default) => throw new NotSupportedException();
    public Task<SourceSeriesDetail> GetSeriesAsync(string sourceSeriesId, CancellationToken ct = default) => throw new NotSupportedException();
    public Task<ChapterPages> GetPagesAsync(SourceChapter chapter, CancellationToken ct = default) => throw new NotSupportedException();
}
