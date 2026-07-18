using Maki.Core.Sources;

namespace Maki.Api.Tests;

/// <summary>A canned <see cref="ISource"/> — search hits and chapter lists are injected per test.</summary>
internal sealed class FakeSource : ISource
{
    public required string Name { get; init; }
    public string DisplayName => Name;
    public string BaseUrl => $"https://{Name}.test";
    public SourceCapabilities Capabilities => SourceCapabilities.None;

    public Func<string, IReadOnlyList<SourceSeriesResult>>? OnSearch { get; init; }
    public Func<string, IReadOnlyList<SourceChapter>>? OnListChapters { get; init; }

    /// <summary>When set, <see cref="ListChaptersAsync"/> throws this instead of returning.</summary>
    public Exception? ListThrows { get; init; }

    public int SearchCalls { get; private set; }
    public int ListCalls { get; private set; }

    public Task<IReadOnlyList<SourceSeriesResult>> SearchAsync(string title, CancellationToken ct = default)
    {
        SearchCalls++;
        return Task.FromResult(OnSearch?.Invoke(title) ?? []);
    }

    public Task<IReadOnlyList<SourceChapter>> ListChaptersAsync(
        string sourceSeriesId, string? languageFilter = null, CancellationToken ct = default)
    {
        ListCalls++;
        if (ListThrows is not null)
        {
            throw ListThrows;
        }

        return Task.FromResult(OnListChapters?.Invoke(sourceSeriesId) ?? []);
    }

    public Task<SourceSeriesDetail> GetSeriesAsync(string sourceSeriesId, CancellationToken ct = default) =>
        throw new NotSupportedException();

    public Task<ChapterPages> GetPagesAsync(SourceChapter chapter, CancellationToken ct = default) =>
        throw new NotSupportedException();

    /// <summary>Builds a chapter for this source with the common fields defaulted.</summary>
    public SourceChapter Chapter(
        decimal? number, int? volume = null, string? title = null,
        string language = "en", DateTime? releaseDate = null) =>
        new(
            SourceName: Name,
            SourceSeriesId: "series",
            SourceChapterId: $"{number?.ToString() ?? title}-{language}",
            NumberRaw: number?.ToString(),
            Number: number,
            Volume: volume,
            Title: title,
            Language: language,
            ReleaseDate: releaseDate);
}
