using Maki.Metadata.Embedding;

namespace Maki.Api.Services;

/// <summary>
/// Why a smart search was answered from the title index, so the search box can say what happens
/// next instead of always telling the reader to build an index. Most fallbacks are an index that
/// exists but was unloaded while idle, which loads by itself within seconds.
/// </summary>
public class SemanticSearchState(
    EmbeddingOptions options,
    SemanticSearcher searcher,
    EmbeddingIndexStatus indexStatus,
    PrebuiltIndexInstaller prebuilt,
    EmbeddingModelSwitcher modelSwitcher)
{
    /// <summary>Index and model are there and loading in the background. Retrying soon gets meaning search.</summary>
    public const string Warming = "warming";

    /// <summary>The index is being built, downloaded, or switched to a new model.</summary>
    public const string Preparing = "preparing";

    /// <summary>An admin switched embeddings off.</summary>
    public const string Off = "off";

    /// <summary>No usable index and nothing is producing one.</summary>
    public const string Unavailable = "unavailable";

    public string FallbackReason() => Classify(
        options.Enabled,
        searcher.IsAvailable(),
        searcher.CanEmbed(),
        indexStatus.Running || prebuilt.Installing || modelSwitcher.Switching);

    public static string Classify(bool enabled, bool indexed, bool canEmbed, bool preparing)
    {
        if (indexed && canEmbed)
        {
            return Warming;
        }

        if (preparing)
        {
            return Preparing;
        }

        return enabled ? Unavailable : Off;
    }
}
