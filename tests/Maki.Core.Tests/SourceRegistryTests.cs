using Maki.Core.Sources;

namespace Maki.Core.Tests;

public class SourceRegistryTests
{
    private sealed class StubSource(string name, string displayName = "Stub") : ISource
    {
        public string Name { get; } = name;
        public string DisplayName { get; } = displayName;
        public string BaseUrl => "https://stub.test";
        public SourceCapabilities Capabilities => SourceCapabilities.None;

        public Task<IReadOnlyList<SourceSeriesResult>> SearchAsync(string title, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<SourceSeriesDetail> GetSeriesAsync(string id, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<IReadOnlyList<SourceChapter>> ListChaptersAsync(
            string id, string? languageFilter = null, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<ChapterPages> GetPagesAsync(SourceChapter chapter, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }

    /// <summary>Serves whatever <see cref="Next"/> holds once reloaded, like a provider reading settings.</summary>
    private sealed class StubProvider : ISourceProvider
    {
        public IReadOnlyList<ISource> Next { get; set; } = [];
        public IReadOnlyList<ISource> Current { get; private set; } = [];
        public int Reloads;

        public Task ReloadAsync(CancellationToken ct = default)
        {
            Reloads++;
            Current = Next;
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task Provider_sources_appear_and_vanish_on_reload()
    {
        var provider = new StubProvider { Next = [new StubSource("suwayomi-1")] };
        var registry = new SourceRegistry([new StubSource("mangadex")], [provider]);

        Assert.Null(registry.Find("suwayomi-1"));

        await registry.ReloadAsync();
        Assert.NotNull(registry.Find("SUWAYOMI-1"));
        Assert.Equal(["mangadex", "suwayomi-1"], registry.All.Select(s => s.Name));

        provider.Next = [];
        await registry.ReloadAsync();
        Assert.Null(registry.Find("suwayomi-1"));
        Assert.Equal(["mangadex"], registry.All.Select(s => s.Name));
        Assert.Equal(2, provider.Reloads);
    }

    [Fact]
    public async Task A_registered_source_keeps_its_name_against_a_provider()
    {
        var provider = new StubProvider { Next = [new StubSource("mangadex", "Impostor"), new StubSource("suwayomi-1")] };
        var registry = new SourceRegistry([new StubSource("mangadex", "MangaDex")], [provider]);

        await registry.ReloadAsync();

        Assert.Equal("MangaDex", registry.GetRequired("mangadex").DisplayName);
        Assert.Equal(["mangadex", "suwayomi-1"], registry.All.Select(s => s.Name));
    }

    [Fact]
    public async Task A_snapshot_taken_before_a_reload_is_not_changed_by_it()
    {
        var provider = new StubProvider { Next = [new StubSource("suwayomi-1")] };
        var registry = new SourceRegistry([new StubSource("mangadex")], [provider]);
        await registry.ReloadAsync();

        var before = registry.All;
        provider.Next = [];
        await registry.ReloadAsync();

        Assert.Equal(["mangadex", "suwayomi-1"], before.Select(s => s.Name));
    }

    [Fact]
    public async Task Without_providers_a_reload_changes_nothing()
    {
        var registry = new SourceRegistry([new StubSource("a"), new StubSource("b")]);

        await registry.ReloadAsync();

        Assert.Equal(["a", "b"], registry.All.Select(s => s.Name));
    }
}
