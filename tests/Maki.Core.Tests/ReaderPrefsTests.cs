using Maki.Core.Reading;

namespace Maki.Core.Tests;

public class ReaderPrefsTests
{
    [Fact]
    public void Sanitized_clamps_dim_and_falls_back_on_an_unknown_filter()
    {
        var spec = new ReaderPrefsSpec(Dim: 500, Filter: "blur").Sanitized();

        Assert.Equal(80, spec.Dim);
        Assert.Equal(ReaderPrefsSpec.FilterNone, spec.Filter);
        Assert.Equal(0, (new ReaderPrefsSpec(Dim: -5)).Sanitized().Dim);
    }

    [Theory]
    [InlineData("none")]
    [InlineData("grayscale")]
    [InlineData("sepia")]
    [InlineData("invert")]
    public void Sanitized_keeps_a_known_filter(string filter)
    {
        Assert.Equal(filter, new ReaderPrefsSpec(Filter: filter).Sanitized().Filter);
    }

    [Fact]
    public void Parse_of_a_blob_from_before_the_tone_fields_uses_their_defaults()
    {
        var spec = ReaderPrefsSpec.Parse("""{"mode":"double","scale":150}""");

        Assert.Equal("double", spec.Mode);
        Assert.Equal(0, spec.Dim);
        Assert.Equal(ReaderPrefsSpec.FilterNone, spec.Filter);
        Assert.True(spec.KeepAwake);
    }

    [Fact]
    public void Serialize_round_trips_the_tone_fields()
    {
        var json = ReaderPrefsSpec.Serialize(new ReaderPrefsSpec(Dim: 30, Filter: "sepia", KeepAwake: false));
        var spec = ReaderPrefsSpec.Parse(json);

        Assert.Equal(30, spec.Dim);
        Assert.Equal("sepia", spec.Filter);
        Assert.False(spec.KeepAwake);
    }
}
