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

    /// <summary>
    /// Search that can take its time — for tests that need one source's search still open while
    /// another one runs. Wins over <see cref="OnSearch"/> when both are set.
    /// </summary>
    public Func<string, CancellationToken, Task<IReadOnlyList<SourceSeriesResult>>>? OnSearchAsync { get; init; }
    public Func<string, IReadOnlyList<SourceChapter>>? OnListChapters { get; init; }

    /// <summary>Cross-site ids per source series id, as a source that publishes them would answer.</summary>
    public Func<string, IReadOnlyDictionary<string, string>?>? OnExternalIds { get; init; }

    /// <summary>Series detail by source series id; unset means the id resolves to nothing.</summary>
    public Func<string, SourceSeriesDetail>? OnGetSeries { get; init; }

    /// <summary>When set, <see cref="ListChaptersAsync"/> throws this instead of returning.</summary>
    public Exception? ListThrows { get; init; }

    // Interlocked, not ++: SourceMatchService searches the sources in parallel, so these are
    // incremented from tasks that can be running on different threads at the same time.
    private int _searchCalls;
    private int _listCalls;
    private int _externalIdCalls;
    private int _getSeriesCalls;

    public int SearchCalls => Volatile.Read(ref _searchCalls);
    public int ListCalls => Volatile.Read(ref _listCalls);
    public int ExternalIdCalls => Volatile.Read(ref _externalIdCalls);
    public int GetSeriesCalls => Volatile.Read(ref _getSeriesCalls);

    public Task<IReadOnlyList<SourceSeriesResult>> SearchAsync(string title, CancellationToken ct = default)
    {
        Interlocked.Increment(ref _searchCalls);
        return OnSearchAsync is not null
            ? OnSearchAsync(title, ct)
            : Task.FromResult(OnSearch?.Invoke(title) ?? []);
    }

    public Task<IReadOnlyList<SourceChapter>> ListChaptersAsync(
        string sourceSeriesId, string? languageFilter = null, CancellationToken ct = default)
    {
        Interlocked.Increment(ref _listCalls);
        if (ListThrows is not null)
        {
            throw ListThrows;
        }

        return Task.FromResult(OnListChapters?.Invoke(sourceSeriesId) ?? []);
    }

    public Task<IReadOnlyDictionary<string, string>?> GetExternalIdsAsync(
        string sourceSeriesId, CancellationToken ct = default)
    {
        Interlocked.Increment(ref _externalIdCalls);
        if (OnExternalIds is null)
        {
            return Task.FromResult<IReadOnlyDictionary<string, string>?>(null);
        }

        return Task.FromResult(OnExternalIds(sourceSeriesId));
    }

    public Task<SourceSeriesDetail> GetSeriesAsync(string sourceSeriesId, CancellationToken ct = default)
    {
        Interlocked.Increment(ref _getSeriesCalls);
        if (OnGetSeries is null)
        {
            throw new NotSupportedException();
        }

        return Task.FromResult(OnGetSeries(sourceSeriesId));
    }

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
