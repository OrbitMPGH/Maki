using Maki.Api.Services;
using Maki.Core.Configuration;
using Maki.Metadata.Catalogue;
using Maki.Metadata.Embedding;
using Maki.Metadata.MangaBaka;
using Maki.Metadata.Tests;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Maki.Api.Tests;

public class HiddenContentServiceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "maki-hidden-" + Guid.NewGuid().ToString("N"));

    public HiddenContentServiceTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public async Task The_never_show_list_still_filters_when_embeddings_are_switched_off()
    {
        // Turning embeddings off leaves the vectors on disk, so the index can still answer which
        // rows carry a hidden genre. If it did not, every hidden title would reappear on the rails.
        using var dump = new DumpDbBuilder();
        dump.AddSeries(1, "Scary", rating: 80, genresJson: """["Horror"]""")
            .AddSeries(2, "Fine", rating: 80, genresJson: """["Comedy"]""");
        var vectorPath = Path.Combine(_dir, "embeddings.db");
        var options = new EmbeddingOptions(_dir, vectorPath, _dir, EmbeddingModelProfile.Base with { Dimensions = 4 })
        {
            Enabled = false,
        };
        var store = new EmbeddingStore(options);
        store.EnsureSchema();
        store.UpsertBatch([(1L, "h", [1f, 0f, 0f, 0f]), (2L, "h", [0f, 1f, 0f, 0f])]);
        var dumpOptions = new MangaBakaDumpOptions(dump.Path, _dir);
        var service = new HiddenContentService(
            new FixedSettings("""{"terms":[{"kind":"genre","name":"Horror"}]}"""),
            new VectorIndexCache(options, dumpOptions, NullLogger<VectorIndexCache>.Instance),
            new CatalogueIndexCache(dumpOptions, NullLogger<CatalogueIndexCache>.Instance));

        var hidden = await service.PredicateAsync();

        Assert.NotNull(hidden);
        Assert.True(hidden(1));
        Assert.False(hidden(2));
    }

    private sealed class FixedSettings(string hidden) : IUserSettings
    {
        public int UserId => 1;

        public Task<string?> GetAsync(string key, CancellationToken ct = default) =>
            Task.FromResult<string?>(key == SettingKeys.DiscoverHidden ? hidden : null);

        public Task<Dictionary<string, string>> GetManyAsync(
            IReadOnlyCollection<string> keys, CancellationToken ct = default) =>
            Task.FromResult(new Dictionary<string, string>());

        public Task SetAsync(string key, string? value, CancellationToken ct = default) => Task.CompletedTask;
    }
}
