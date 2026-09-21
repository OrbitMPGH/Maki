using Maki.Api.Services;

namespace Maki.Api.Tests;

/// <summary>
/// The mining behind "these are the twelve things you actually read". Tested on synthetic facet
/// sets, because every question here is about the selection arithmetic: does a specific habit beat
/// a broad one, do two facets that only ever appear together get named as a pair, and does the same
/// library mine to the same groups twice.
/// </summary>
public class TasteGroupMiningTests
{
    /// <summary>
    /// Specificity is an IDF, so a facet on <paramref name="share"/> of the catalogue is worth
    /// log(1/share). 0.5 is near-worthless, 0.001 is rare.
    /// </summary>
    private static TasteGroupMining.Facet Facet(int key, double share) =>
        new(key, Math.Log(1 / share));

    private const int Romance = 1;
    private const int Comedy = 2;
    private const int FakeRelationship = 3;
    private const int Monsters = 4;
    private const int Skills = 5;
    private const int Classmates = 6;

    private static IReadOnlyList<TasteGroupMining.Facet> Work(params (int Key, double Share)[] facets) =>
        [.. facets.Select(f => Facet(f.Key, f.Share))];

    /// <summary>Twelve romances, five of which are fake-relationship stories.</summary>
    private static List<IReadOnlyList<TasteGroupMining.Facet>> RomanceLibrary()
    {
        var works = new List<IReadOnlyList<TasteGroupMining.Facet>>();
        for (var i = 0; i < 5; i++)
        {
            works.Add(Work((Romance, 0.3), (FakeRelationship, 0.004)));
        }

        for (var i = 0; i < 7; i++)
        {
            works.Add(Work((Romance, 0.3)));
        }

        return works;
    }

    [Fact]
    public void Prefers_the_specific_habit_over_the_broad_one()
    {
        var groups = TasteGroupMining.Mine(RomanceLibrary());

        // Romance is on every book here and says nothing; the five that share a rare theme are the
        // group worth a card. This is the whole reason the old k-means partition was replaced.
        Assert.Equal([FakeRelationship], groups[0].Keys);
        Assert.Equal(5, groups[0].Members.Count);
    }

    [Fact]
    public void A_facet_on_most_of_the_library_cannot_be_a_group_on_its_own()
    {
        var groups = TasteGroupMining.Mine(RomanceLibrary());

        // "Romance" over a romance library is the library again under a narrower name.
        Assert.DoesNotContain(groups, g => g.Keys.Count == 1 && g.Keys[0] == Romance);
    }

    /// <summary>Works carrying one rare facet each, so they pad a library without forming a group.</summary>
    private static IEnumerable<IReadOnlyList<TasteGroupMining.Facet>> Filler(int count) =>
        Enumerable.Range(0, count).Select(i => Work((900 + i, 0.001)));

    [Fact]
    public void Names_a_pair_of_broad_facets_that_travel_together()
    {
        var works = new List<IReadOnlyList<TasteGroupMining.Facet>>();
        for (var i = 0; i < 6; i++)
        {
            works.Add(Work((Romance, 0.3), (Comedy, 0.3)));
        }

        for (var i = 0; i < 6; i++)
        {
            works.Add(Work((Monsters, 0.2), (Skills, 0.2)));
        }

        works.AddRange(Filler(4));

        var groups = TasteGroupMining.Mine(works);

        // Neither half of a romcom is interesting alone and both are over the single-facet ceiling
        // at this size; the pair is the group. Same for the dungeon half of the shelf.
        Assert.Contains(groups, g => g.Keys.SequenceEqual([Romance, Comedy]));
        Assert.Contains(groups, g => g.Keys.SequenceEqual([Monsters, Skills]));
    }

    [Fact]
    public void Groups_overlap_rather_than_partition()
    {
        var works = new List<IReadOnlyList<TasteGroupMining.Facet>>();
        for (var i = 0; i < 4; i++)
        {
            works.Add(Work((Romance, 0.3), (Comedy, 0.3), (FakeRelationship, 0.004)));
        }

        for (var i = 0; i < 5; i++)
        {
            works.Add(Work((Romance, 0.3), (Comedy, 0.3), (Monsters, 0.02)));
        }

        for (var i = 0; i < 6; i++)
        {
            works.Add(Work((Monsters, 0.02), (Skills, 0.02)));
        }

        works.AddRange(Filler(6));

        var groups = TasteGroupMining.Mine(works);

        var fake = groups.Single(g => g.Keys.SequenceEqual([FakeRelationship]));
        var romcom = groups.Single(g => g.Keys.SequenceEqual([Romance, Comedy]));

        // The four fake-relationship books are romcoms too, and both cards are true of them. A
        // partition would have had to pick one of the two things to call them.
        Assert.Equal(4, fake.Members.Count);
        Assert.Equal(9, romcom.Members.Count);
        Assert.All(fake.Members, m => Assert.Contains(m, romcom.Members));
    }

    [Fact]
    public void A_pair_that_narrows_nothing_loses_to_the_facet_it_is_made_of()
    {
        var groups = TasteGroupMining.Mine(RomanceLibrary());

        // Every fake-relationship story here is a romance, so "Romance + Fake Relationship" names
        // exactly the books "Fake Relationship" does, with a longer label and no extra truth.
        Assert.DoesNotContain(groups, g => g.Keys.Count == 2);
    }

    [Fact]
    public void A_group_cannot_be_most_of_the_library()
    {
        var works = Enumerable.Range(0, 10)
            .Select(_ => Work((Monsters, 0.02), (Skills, 0.02)))
            .ToList();
        works.AddRange(Enumerable.Range(0, 3).Select(_ => Work((Romance, 0.3), (FakeRelationship, 0.004))));

        var groups = TasteGroupMining.Mine(works);

        // Monsters, Skills and the pair of them are all on ten of thirteen books. Rare in the
        // catalogue, but as a card it is this reader's shelf with a label on it.
        Assert.DoesNotContain(groups, g => g.Keys.Contains(Monsters) || g.Keys.Contains(Skills));
    }

    [Fact]
    public void A_facet_on_two_books_is_a_coincidence_rather_than_a_taste()
    {
        var works = new List<IReadOnlyList<TasteGroupMining.Facet>>();
        for (var i = 0; i < 2; i++)
        {
            works.Add(Work((Monsters, 0.001)));
        }

        for (var i = 0; i < 4; i++)
        {
            works.Add(Work((Romance, 0.3), (FakeRelationship, 0.004)));
        }

        for (var i = 0; i < 4; i++)
        {
            works.Add(Work((Romance, 0.3), (Comedy, 0.3)));
        }

        var groups = TasteGroupMining.Mine(works);

        // Rare enough to outscore everything if it were allowed to count at all.
        Assert.Contains(groups, g => g.Keys.SequenceEqual([FakeRelationship]));
        Assert.DoesNotContain(groups, g => g.Keys.Contains(Monsters));
    }

    [Fact]
    public void Too_few_series_is_no_answer_rather_than_a_bad_one()
    {
        var works = Enumerable.Range(0, 5)
            .Select(_ => Work((Romance, 0.3), (FakeRelationship, 0.004)))
            .ToList();

        Assert.Empty(TasteGroupMining.Mine(works));
    }

    [Fact]
    public void Does_not_name_the_same_set_of_books_twice()
    {
        // Two facets that are always on the same five books: "Monsters", "Skills" and the pair of
        // them all describe one shelf, and three cards of it would be three copies of one group.
        var works = new List<IReadOnlyList<TasteGroupMining.Facet>>();
        for (var i = 0; i < 5; i++)
        {
            works.Add(Work((Monsters, 0.01), (Skills, 0.01)));
        }

        for (var i = 0; i < 6; i++)
        {
            works.Add(Work((Romance, 0.3), (FakeRelationship, 0.004)));
        }

        works.AddRange(Filler(3));

        var groups = TasteGroupMining.Mine(works);

        Assert.Single(groups, g => g.Keys.Contains(Monsters) || g.Keys.Contains(Skills));
    }

    [Fact]
    public void One_facet_cannot_name_the_whole_page()
    {
        // Student-Student Relationship paired with everything on a school-romance shelf. Each
        // partner also appears without it, so every pair genuinely narrows and holds a set no other
        // group does: the member-overlap test passes all five, and the page still reads as the same
        // words over and over.
        var works = new List<IReadOnlyList<TasteGroupMining.Facet>>();
        var partners = new[] { Romance, Comedy, FakeRelationship, Monsters, Skills };
        foreach (var partner in partners)
        {
            for (var i = 0; i < 3; i++)
            {
                works.Add(Work((Classmates, 0.02), (partner, 0.02)));
                works.Add(Work((partner, 0.02)));
            }
        }

        var groups = TasteGroupMining.Mine(works);

        Assert.Equal(2, groups.Count(g => g.Keys.Contains(Classmates)));
        Assert.All(partners, p => Assert.Contains(groups, g => g.Keys.SequenceEqual([p])));
    }

    [Fact]
    public void Extreme_rarity_stops_buying_rank()
    {
        // A tag on 0.02% of the catalogue against one on 2%, four books against eight. Unclamped the
        // rarer one wins on half the evidence, which is how "Comedic Facial Expressions" over four
        // series outranked real habits on the simulated shelf.
        var works = new List<IReadOnlyList<TasteGroupMining.Facet>>();
        for (var i = 0; i < 4; i++)
        {
            works.Add(Work((Monsters, 0.0002)));
        }

        for (var i = 0; i < 8; i++)
        {
            works.Add(Work((FakeRelationship, 0.02)));
        }

        works.AddRange(Filler(8));

        var groups = TasteGroupMining.Mine(works);

        Assert.Equal([FakeRelationship], groups[0].Keys);
        Assert.Contains(groups, g => g.Keys.SequenceEqual([Monsters]));
    }

    [Fact]
    public void A_pair_of_two_facets_that_already_have_their_own_groups_is_not_a_third()
    {
        // Love Confession and Student-Student Relationship each name a row, so the pair of them is
        // the intersection of two rows already on the page: a subset of both, filtered by the AND of
        // two filters the reader can already see the results of.
        var works = new List<IReadOnlyList<TasteGroupMining.Facet>>();
        for (var i = 0; i < 3; i++)
        {
            works.Add(Work((Romance, 0.02), (Comedy, 0.02)));
        }

        for (var i = 0; i < 3; i++)
        {
            works.Add(Work((Romance, 0.02)));
        }

        for (var i = 0; i < 3; i++)
        {
            works.Add(Work((Comedy, 0.02)));
        }

        works.AddRange(Filler(6));

        var groups = TasteGroupMining.Mine(works);

        Assert.Contains(groups, g => g.Keys.SequenceEqual([Romance]));
        Assert.Contains(groups, g => g.Keys.SequenceEqual([Comedy]));
        Assert.DoesNotContain(groups, g => g.Keys.Count > 1);
    }

    [Fact]
    public void Is_deterministic_across_runs()
    {
        var works = RomanceLibrary();
        works.AddRange(Enumerable.Range(0, 4).Select(_ => Work((Monsters, 0.02), (Skills, 0.02))));

        var first = TasteGroupMining.Mine(works);
        var second = TasteGroupMining.Mine(works);

        // A reader whose groups reshuffle between visits would reasonably conclude this is invented.
        Assert.Equal(
            first.Select(g => string.Join(",", g.Keys)),
            second.Select(g => string.Join(",", g.Keys)));
    }

    [Fact]
    public void Honours_the_group_cap()
    {
        // Twenty distinct rare facets, three books each, none overlapping: every one of them is a
        // legitimate group and the page still only has room for so many.
        var works = new List<IReadOnlyList<TasteGroupMining.Facet>>();
        for (var facet = 100; facet < 120; facet++)
        {
            for (var i = 0; i < 3; i++)
            {
                works.Add(Work((facet, 0.005)));
            }
        }

        Assert.Equal(4, TasteGroupMining.Mine(works, maxGroups: 4).Count);
        Assert.Equal(TasteGroupMining.MaxGroups, TasteGroupMining.Mine(works).Count);
    }
}
