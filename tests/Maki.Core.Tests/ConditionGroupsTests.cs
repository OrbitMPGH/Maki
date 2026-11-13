using Maki.Core.Tags;

namespace Maki.Core.Tests;

public class ConditionGroupsTests
{
    private static AutoTagSubject Subject(string type, string rating = "safe", params string[] genres) =>
        new(genres, type, rating, RootFolderId: 0, AutoTagAddedVia.Manual, 3);

    [Fact]
    public void A_rule_saved_before_groups_reads_as_one_group_and_writes_the_same_text()
    {
        const string stored = """[{"field":"genre","value":"Action"},{"field":"type","value":"manga"}]""";

        var groups = ConditionGroups.Parse(stored);

        var group = Assert.Single(groups);
        Assert.Equal([new AutoTagCondition("genre", "Action"), new AutoTagCondition("type", "manga")], group);
        Assert.Equal(stored, ConditionGroups.Serialize(groups));
        Assert.Equal(AutoTagRules.Parse(stored), group);
    }

    [Fact]
    public void Several_groups_round_trip_through_storage()
    {
        IReadOnlyList<IReadOnlyList<AutoTagCondition>> groups =
        [
            [new(AutoTagFields.Type, "manhwa")],
            [new(AutoTagFields.Type, "manhua"), new(AutoTagFields.Genre, "Action")],
        ];

        var stored = ConditionGroups.Serialize(groups);

        Assert.StartsWith("[[", stored);
        Assert.Equal(groups, ConditionGroups.Parse(stored));
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{}")]
    [InlineData("[]")]
    [InlineData("[[]]")]
    public void Unreadable_or_empty_storage_matches_nothing(string stored)
    {
        var groups = ConditionGroups.Parse(stored);

        Assert.Empty(groups);
        Assert.False(ConditionGroups.Matches(groups, Subject("manga")));
    }

    [Fact]
    public void Any_group_matching_is_enough_and_a_group_needs_all_its_conditions()
    {
        // "If it's a manga or ecchi, put it in this folder."
        IReadOnlyList<IReadOnlyList<AutoTagCondition>> mangaOrEcchi =
        [
            [new(AutoTagFields.Type, "manga")],
            [new(AutoTagFields.Genre, "ecchi")],
        ];
        Assert.True(ConditionGroups.Matches(mangaOrEcchi, Subject("manga")));
        Assert.True(ConditionGroups.Matches(mangaOrEcchi, Subject("manhwa", "safe", "Ecchi")));
        Assert.False(ConditionGroups.Matches(mangaOrEcchi, Subject("manhwa", "safe", "Action")));

        IReadOnlyList<IReadOnlyList<AutoTagCondition>> pornographicManhwa =
        [
            [new(AutoTagFields.Type, "manhwa"), new(AutoTagFields.ContentRating, "pornographic")],
        ];
        Assert.True(ConditionGroups.Matches(pornographicManhwa, Subject("manhwa", "pornographic")));
        Assert.False(ConditionGroups.Matches(pornographicManhwa, Subject("manhwa", "erotica")));
    }

    [Fact]
    public void Normalizing_trims_and_drops_duplicate_conditions_and_groups()
    {
        var groups = ConditionGroups.Normalize(
            [
                [new(" type ", " manhwa "), new("type", "manhwa")],
                [new("type", "manhwa")],
                [new("genre", "Action"), new("type", "manhua")],
            ],
            AutoTagFields.All);

        Assert.NotNull(groups);
        Assert.Equal(2, groups.Count);
        Assert.Equal([new AutoTagCondition("type", "manhwa")], groups[0]);
    }

    [Fact]
    public void Normalizing_refuses_an_empty_rule_an_empty_group_and_fields_outside_the_set()
    {
        Assert.Null(ConditionGroups.Normalize([], AutoTagFields.All));
        Assert.Null(ConditionGroups.Normalize(null, AutoTagFields.All));
        Assert.Null(ConditionGroups.Normalize([[new("type", "manga")], []], AutoTagFields.All));
        Assert.Null(ConditionGroups.Normalize([[new("type", "novel")]], AutoTagFields.All));
        Assert.Null(ConditionGroups.Normalize([[new(AutoTagFields.RootFolder, "2")]], AutoTagFields.ForRootFolderRules));
        Assert.NotNull(ConditionGroups.Normalize([[new(AutoTagFields.RootFolder, "2")]], AutoTagFields.All));
    }

    [Fact]
    public void Normalizing_refuses_too_many_groups_or_conditions()
    {
        var eleven = Enumerable.Range(1, ConditionGroups.MaxGroups + 1)
            .Select(i => (IEnumerable<AutoTagCondition>)[new AutoTagCondition("addedBy", i.ToString())]);
        Assert.Null(ConditionGroups.Normalize(eleven, AutoTagFields.All));

        var wide = Enumerable.Range(1, AutoTagRules.MaxConditions + 1)
            .Select(i => new AutoTagCondition("genre", $"g{i}"))
            .Chunk(3)
            .Select(chunk => (IEnumerable<AutoTagCondition>)chunk);
        Assert.Null(ConditionGroups.Normalize(wide, AutoTagFields.All));
    }
}
