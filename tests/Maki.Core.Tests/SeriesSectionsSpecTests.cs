using Maki.Core.Configuration;

namespace Maki.Core.Tests;

/// <summary>
/// The series-page rail toggles. Same storage discipline as <see cref="HomeLayoutSpec"/>: a stored
/// blob has to survive a release that adds a rail, and a broken one has to degrade to "show
/// everything" rather than throw on a page the user is trying to open.
/// </summary>
public class SeriesSectionsSpecTests
{
    [Fact]
    public void Default_shows_both_rails()
    {
        Assert.True(SeriesSectionsSpec.Default.Related);
        Assert.True(SeriesSectionsSpec.Default.Similar);
    }

    [Fact]
    public void Parse_falls_back_to_default_for_blank_and_broken_json()
    {
        foreach (var json in new[] { null, "", "   ", "{ not json", "[]" })
        {
            var spec = SeriesSectionsSpec.Parse(json);
            Assert.True(spec.Related);
            Assert.True(spec.Similar);
        }
    }

    [Fact]
    public void A_stored_blob_written_before_a_rail_existed_reads_that_rail_as_on()
    {
        // What a release adding a third rail does to everybody's stored two-field blob: the missing
        // property yields the parameter default, so nobody has to opt in to see the new one.
        var spec = SeriesSectionsSpec.Parse("""{"related":false}""");

        Assert.False(spec.Related);
        Assert.True(spec.Similar);
    }

    [Fact]
    public void Round_trips_through_serialize()
    {
        var stored = new SeriesSectionsSpec(Related: false, Similar: true);

        Assert.Equal(stored, SeriesSectionsSpec.Parse(SeriesSectionsSpec.Serialize(stored)));
    }

    [Fact]
    public void Property_names_are_camel_case_so_the_client_reads_what_the_server_wrote()
    {
        Assert.Contains("\"related\"", SeriesSectionsSpec.Serialize(SeriesSectionsSpec.Default));
        Assert.Contains("\"similar\"", SeriesSectionsSpec.Serialize(SeriesSectionsSpec.Default));
    }
}
