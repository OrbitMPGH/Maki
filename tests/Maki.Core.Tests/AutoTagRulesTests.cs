using Maki.Core.Tags;

namespace Maki.Core.Tests;

public class AutoTagRulesTests
{
    private static AutoTagSubject Subject(string via = AutoTagAddedVia.Manual, int? by = 3) =>
        new(["Action", "Fantasy"], "manga", "safe", RootFolderId: 2, via, by);

    [Fact]
    public void Every_condition_must_hold()
    {
        List<AutoTagCondition> rule =
        [
            new(AutoTagFields.Genre, "fantasy"),
            new(AutoTagFields.Type, "manga"),
            new(AutoTagFields.RootFolder, "2"),
            new(AutoTagFields.AddedBy, "3"),
        ];

        Assert.True(AutoTagRules.Matches(rule, Subject()));
        Assert.False(AutoTagRules.Matches([.. rule, new(AutoTagFields.Genre, "horror")], Subject()));
        Assert.False(AutoTagRules.Matches(rule, Subject(by: null)));
    }

    [Fact]
    public void An_empty_rule_matches_nothing_and_is_refused()
    {
        Assert.False(AutoTagRules.Matches([], Subject()));
        Assert.Null(AutoTagRules.Normalize([]));
    }

    [Theory]
    [InlineData("colour", "red")]
    [InlineData(AutoTagFields.Type, "novel")]
    [InlineData(AutoTagFields.AddedVia, "carrier pigeon")]
    [InlineData(AutoTagFields.ContentRating, "everyone")]
    [InlineData(AutoTagFields.RootFolder, "abc")]
    [InlineData(AutoTagFields.Genre, " ")]
    public void Unknown_fields_and_values_are_refused(string field, string value) =>
        Assert.Null(AutoTagRules.Normalize([new AutoTagCondition(field, value)]));

    [Fact]
    public void Conditions_round_trip_through_storage()
    {
        var normalized = AutoTagRules.Normalize([new AutoTagCondition(" genre ", " Action "), new(AutoTagFields.AddedVia, "mihon")]);

        var stored = AutoTagRules.Serialize(normalized!);

        Assert.Equal(normalized, AutoTagRules.Parse(stored));
        Assert.Empty(AutoTagRules.Parse("not json"));
    }

    [Theory]
    [InlineData("recommendation", AutoTagAddedVia.Discover)]
    [InlineData("request", AutoTagAddedVia.Request)]
    [InlineData("importlist", AutoTagAddedVia.ImportList)]
    [InlineData("mihon", AutoTagAddedVia.Mihon)]
    [InlineData("library", AutoTagAddedVia.Manual)]
    [InlineData(null, AutoTagAddedVia.Manual)]
    public void Stored_provenance_maps_to_the_rule_vocabulary(string? addedFrom, string expected) =>
        Assert.Equal(expected, AutoTagAddedVia.FromAddedFrom(addedFrom));
}
