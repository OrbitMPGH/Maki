using Maki.Core.Quality;

namespace Maki.Core.Tests.Quality;

public class ScoutChaptersTests
{
    private static ScoutChapters.Candidate C(int id, int mappings) => new(id, id, mappings);

    [Fact]
    public void Picks_are_spread_across_the_chapters_every_source_lists()
    {
        var candidates = Enumerable.Range(1, 90).Select(i => C(i, i % 10 == 0 ? 2 : 3)).ToList();

        for (var seed = 0; seed < 20; seed++)
        {
            var picks = ScoutChapters.Pick(candidates, 3, new Random(seed));

            Assert.Equal(3, picks.Count);
            Assert.All(picks, id => Assert.NotEqual(0, id % 10));
            Assert.InRange(picks[0], 1, 30);
            Assert.InRange(picks[1], 31, 60);
            Assert.InRange(picks[2], 61, 90);
        }
    }

    [Fact]
    public void Too_few_shared_chapters_fall_back_to_the_most_widely_listed_without_repeats()
    {
        var candidates = new[] { C(1, 3), C(2, 2), C(3, 2), C(4, 1) };

        var picks = ScoutChapters.Pick(candidates, 3, new Random(1));

        Assert.Equal([1, 2, 3], picks.Order());
    }

    [Fact]
    public void Fewer_chapters_than_asked_returns_each_once()
    {
        Assert.Equal([7, 8], ScoutChapters.Pick([C(7, 2), C(8, 2)], 3, new Random(1)).Order());
        Assert.Empty(ScoutChapters.Pick([], 3, new Random(1)));
    }
}
