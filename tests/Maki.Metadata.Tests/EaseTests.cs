using Maki.Metadata.Ease;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Maki.Metadata.Tests;

public class EaseTests : IDisposable
{
    private readonly string _dir;

    public EaseTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "maki-ease-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    [Fact]
    public void Score_SumsSignedWeightsAcrossSeeds_AndScalesTheBestToOne()
    {
        var table = EaseTable.Build([(1, 10, 0.4f), (2, 10, 0.4f), (1, 11, 0.2f), (2, 12, -0.2f)]);

        var scores = EaseScorer.Score(table, [1, 2], null, EaseTuning.Default);

        Assert.Equal(1.0, scores[10], 6);
        Assert.Equal(0.25, scores[11], 6);
        Assert.Equal(-0.25, scores[12], 6);
    }

    [Fact]
    public void Score_ReadsTheTableInTheSeedsDirectionOnly()
    {
        var table = EaseTable.Build([(1, 10, 0.4f), (10, 2, 0.9f)]);

        var scores = EaseScorer.Score(table, [1], null, EaseTuning.Default);

        Assert.Equal([10L], scores.Keys);
    }

    [Fact]
    public void Score_NeverReturnsASeed_AndAppliesSeedWeights()
    {
        var table = EaseTable.Build([(1, 2, 0.5f), (1, 10, 0.1f), (2, 11, 0.1f)]);

        var scores = EaseScorer.Score(table, [1, 2], new Dictionary<long, double> { [1] = 3.0 }, EaseTuning.Default);

        Assert.DoesNotContain(2L, scores.Keys);
        Assert.Equal(1.0, scores[10], 6);
        Assert.Equal(1.0 / 3, scores[11], 6);
    }

    [Fact]
    public void Score_FloorsAtMinusOne_AndCanDropNegatives()
    {
        var table = EaseTable.Build([(1, 10, 0.1f), (1, 11, -0.5f)]);

        Assert.Equal(-1.0, EaseScorer.Score(table, [1], null, EaseTuning.Default)[11]);
        Assert.DoesNotContain(11L, EaseScorer.Score(table, [1], null, EaseTuning.Default with { KeepNegative = false }).Keys);
    }

    [Fact]
    public void Score_IsEmpty_WhenNoSeedIsInTheTable_OrNothingSumsAboveZero()
    {
        var table = EaseTable.Build([(1, 10, -0.1f)]);

        Assert.Empty(EaseScorer.Score(table, [5], null, EaseTuning.Default));
        Assert.Empty(EaseScorer.Score(table, [1], null, EaseTuning.Default));
    }

    [Fact]
    public async Task NoFile_OrAnUnreadableOne_IsNoTable()
    {
        var corrupt = Path.Combine(_dir, "corrupt.db");
        await File.WriteAllTextAsync(corrupt, "not a database");

        Assert.Null(await new EaseCache(new EaseOptions(Path.Combine(_dir, "absent.db")), NullLogger<EaseCache>.Instance).GetAsync());
        Assert.Null(await new EaseCache(new EaseOptions(corrupt), NullLogger<EaseCache>.Instance).GetAsync());
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
