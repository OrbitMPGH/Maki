using System.Text;
using Maki.Metadata.Catalogue;
using Maki.Metadata.MangaBaka;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Maki.Metadata.Tests;

/// <summary>
/// The credit and fuzzy-term indexes reload from a snapshot beside the dump. It has to answer
/// exactly as a build would, and give way to a build the moment the dump changes.
/// </summary>
public class CatalogueSnapshotTests : IDisposable
{
    private readonly DumpDbBuilder _db = new();

    public void Dispose() => _db.Dispose();

    private CatalogueIndexCache Cache(ILogger<CatalogueIndexCache>? logger = null) => new(
        new MangaBakaDumpOptions(_db.Path, Path.GetDirectoryName(_db.Path)!),
        logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<CatalogueIndexCache>.Instance);

    private void Seed() => _db
        .AddSeries(1, "Berserk", authorsJson: """["Kentaro Miura"]""", artistsJson: """["Kentaro Miura"]""",
            publishersJson: """[{"name": "Hakusensha", "type": "Original"}]""", popularity: 4)
        .AddSeries(2, "Uzumaki", authorsJson: """["Junji Ito", "Anthology"]""", publishersJson: """["Shogakukan"]""", popularity: 9)
        .AddSeries(3, "Gyo", authorsJson: """["Itō Junji"]""", popularity: 40)
        .AddSeries(4, "Vagabond", authorsJson: """["Takehiko Inoue"]""", publishersJson: """[{"name": "Kodansha"}]""")
        .BuildSearchIndex();

    private static string Fingerprint(CatalogueIndexes indexes)
    {
        var sb = new StringBuilder();
        var credits = indexes.Credits;
        sb.Append(credits.NameCount).Append('/').Append(credits.PairCount).Append('/').Append(credits.MaxSeriesId).Append('\n');
        for (var name = 0; name < credits.NameCount; name++)
        {
            sb.Append(credits.NameAt(name)).Append(':').AppendJoin(',', credits.WorksOf(name))
                .Append(':').AppendJoin(',', credits.RoleLabelsAt(name)).Append('\n');
        }

        foreach (var query in new[] { "ito", "Junji", "miura", "kodan", "zz" })
        {
            sb.AppendJoin(',', credits.Suggest(query, CreditRole.Any, 10).Select(m => $"{m.NameId}:{m.Distance}:{m.WorkCount}")).Append('\n');
        }

        var terms = indexes.Terms;
        sb.Append(terms.TermCount).Append('|');
        foreach (var token in new[] { "berserk", "bersrek", "uzumaki", "vagabnd", "gyo" })
        {
            sb.Append(terms.DocFrequency(token)).Append(':')
                .AppendJoin(',', terms.Expand(token, FuzzyOptions.Default).Select(e => $"{e.Term}/{e.Distance}/{e.DocFrequency}")).Append('|');
        }

        return sb.ToString();
    }

    [Fact]
    public async Task A_fresh_cache_reads_the_snapshot_and_answers_as_the_build_did()
    {
        Seed();
        var first = Cache();
        var built = (await first.GetAsync())!;
        await first.SnapshotSavedForTest;
        Assert.True(File.Exists(first.SnapshotPath));

        var logger = new RecordingLogger();
        var reloaded = (await Cache(logger).GetAsync())!;

        Assert.Contains(logger.Messages, m => m.Contains("from their snapshot"));
        Assert.DoesNotContain(logger.Messages, m => m.StartsWith("Built the credit index"));
        Assert.Equal(Fingerprint(built), Fingerprint(reloaded));
    }

    [Fact]
    public async Task A_built_entry_is_swapped_for_the_mapped_snapshot()
    {
        Seed();
        var cache = Cache();
        var built = (await cache.GetAsync())!;
        await cache.SnapshotSavedForTest;

        var published = (await cache.GetAsync())!;

        Assert.False(built.Credits.IsMapped);
        Assert.True(published.Credits.IsMapped);
        Assert.True(published.Terms.IsMapped);
        Assert.Equal(Fingerprint(built), Fingerprint(published));
    }

    [Fact]
    public async Task A_changed_dump_rebuilds_instead_of_reading_the_old_snapshot()
    {
        Seed();
        var first = Cache();
        await first.GetAsync();
        await first.SnapshotSavedForTest;

        _db.AddSeries(5, "Monster", authorsJson: """["Naoki Urasawa"]""");
        File.SetLastWriteTimeUtc(_db.Path, DateTime.UtcNow.AddMinutes(1));
        var reloaded = (await Cache().GetAsync())!;

        Assert.True(reloaded.Credits.TryResolve("Naoki Urasawa", CreditRole.Author, out _));
    }

    [Fact]
    public async Task A_damaged_snapshot_is_ignored_and_rebuilt_over()
    {
        Seed();
        var first = Cache();
        var built = (await first.GetAsync())!;
        await first.SnapshotSavedForTest;
        var bytes = File.ReadAllBytes(first.SnapshotPath);
        bytes[^12] ^= 0xFF;
        // Replaced rather than written in place: the live cache has the file mapped.
        File.Delete(first.SnapshotPath);
        File.WriteAllBytes(first.SnapshotPath, bytes[..^3]);

        var cache = Cache();
        var rebuilt = (await cache.GetAsync())!;
        await cache.SnapshotSavedForTest;

        Assert.Equal(Fingerprint(built), Fingerprint(rebuilt));
        Assert.Equal(bytes.Length, new FileInfo(cache.SnapshotPath).Length);
    }

    private sealed class RecordingLogger : ILogger<CatalogueIndexCache>
    {
        public System.Collections.Concurrent.ConcurrentQueue<string> Messages { get; } = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => Messages.Enqueue(formatter(state, exception));
    }
}
