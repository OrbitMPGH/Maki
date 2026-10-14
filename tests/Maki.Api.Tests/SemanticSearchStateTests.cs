using Maki.Api.Services;

namespace Maki.Api.Tests;

public class SemanticSearchStateTests
{
    [Fact]
    public void A_built_index_that_is_only_cold_reads_as_warming_even_mid_rebuild()
    {
        Assert.Equal(SemanticSearchState.Warming, SemanticSearchState.Classify(true, true, true, false));
        Assert.Equal(SemanticSearchState.Warming, SemanticSearchState.Classify(true, true, true, true));
    }

    [Fact]
    public void No_index_while_one_is_being_built_or_downloaded_reads_as_preparing()
    {
        Assert.Equal(SemanticSearchState.Preparing, SemanticSearchState.Classify(true, false, true, true));
        Assert.Equal(SemanticSearchState.Preparing, SemanticSearchState.Classify(false, false, true, true));
    }

    [Fact]
    public void Embeddings_switched_off_read_as_off()
    {
        Assert.Equal(SemanticSearchState.Off, SemanticSearchState.Classify(false, false, true, false));
    }

    [Fact]
    public void A_missing_index_or_a_model_that_will_not_load_reads_as_unavailable()
    {
        Assert.Equal(SemanticSearchState.Unavailable, SemanticSearchState.Classify(true, false, true, false));
        Assert.Equal(SemanticSearchState.Unavailable, SemanticSearchState.Classify(true, true, false, false));
    }
}
