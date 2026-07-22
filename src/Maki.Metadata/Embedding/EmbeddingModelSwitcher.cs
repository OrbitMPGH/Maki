using Maki.Core.Configuration;
using Microsoft.Extensions.Logging;

namespace Maki.Metadata.Embedding;

/// <summary>Outcome of asking to switch models: whether a switch actually started, and why not.</summary>
public record ModelSwitchStart(bool Started, string Model, string Reason);

/// <summary>
/// Switches the embedding model live — no restart, no local re-index. Changing the model changes
/// the vector dimensionality, so the query embedder, the stored vectors and the RAM-resident index
/// all have to move together; this coordinates that in the background:
///
///   persist the setting → repoint <see cref="EmbeddingOptions.Model"/> → drop the caches built
///   for the old model → download the new model's ONNX/vocab → download the new model's prebuilt
///   index (the new-dimensional vectors).
///
/// The whole time, search degrades safely rather than wrongly: once the model is repointed the old
/// vectors are the wrong width, so <see cref="VectorIndexCache"/> drops them and search falls back
/// to the title index until the new index lands (usually under a minute). Runs fire-and-forget off
/// the request thread; the settings UI polls <see cref="Switching"/> for progress.
/// </summary>
public class EmbeddingModelSwitcher(
    EmbeddingOptions options,
    EmbeddingModelStore modelStore,
    TextEmbedder embedder,
    VectorIndexCache cache,
    PrebuiltIndexInstaller prebuilt,
    EmbeddingIndexStatus indexStatus,
    IAppSettings settings,
    ILogger<EmbeddingModelSwitcher> logger)
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private volatile bool _switching;
    private volatile string? _lastError;

    /// <summary>True while a switch is downloading in the background.</summary>
    public bool Switching => _switching;

    /// <summary>Why the last switch didn't fully complete (e.g. no prebuilt index for that model), or null.</summary>
    public string? LastError => _lastError;

    /// <summary>The model in effect right now ("base"/"large").</summary>
    public string CurrentModel => options.Model.Kind;

    /// <summary>
    /// Kicks off a switch to <paramref name="kind"/> in the background. Returns immediately: a
    /// no-op when already on that model, refused while an indexing pass or another switch is in
    /// flight. The caller reports <see cref="Switching"/> until it clears.
    /// </summary>
    public ModelSwitchStart Start(string? kind)
    {
        var target = EmbeddingModelProfile.Resolve(kind);
        if (string.Equals(target.Kind, options.Model.Kind, StringComparison.Ordinal))
        {
            return new ModelSwitchStart(false, target.Kind, "Already using this model.");
        }

        if (indexStatus.Running)
        {
            return new ModelSwitchStart(false, options.Model.Kind,
                "An indexing pass is running; try again once it finishes.");
        }

        // Non-blocking acquire: hold the gate for the whole background run so a second request
        // can't start an overlapping switch. Released in RunAsync's finally.
        if (!_gate.Wait(0))
        {
            return new ModelSwitchStart(false, options.Model.Kind, "A model switch is already in progress.");
        }

        _switching = true;
        _lastError = null;
        _ = Task.Run(() => RunAsync(target));
        return new ModelSwitchStart(true, target.Kind, "Switching…");
    }

    private async Task RunAsync(EmbeddingModelProfile target)
    {
        try
        {
            logger.LogInformation("Switching embedding model to {Model}…", target.Kind);
            await settings.SetAsync(SettingKeys.RecommendationsEmbeddingModel, target.Kind);

            // Repoint first, then drop the caches that were built for the old model. A search in
            // this window sees a width mismatch and falls back to titles — degraded, never wrong.
            options.Model = target;
            cache.Invalidate();
            embedder.Reset();

            await modelStore.EnsureAsync();            // downloads the target ONNX + vocab if missing
            var modelReady = await embedder.EnsureReadyAsync();

            // force: fetch the target model's index regardless of the freshness or enabled checks —
            // this is a deliberate switch, not the nightly poll.
            var install = await prebuilt.InstallAsync(force: true);

            _lastError = !modelReady
                ? "The embedding model failed to download."
                : install.Installed
                    ? null
                    : install.Reason;

            if (_lastError is null)
            {
                logger.LogInformation("Model switch to {Model} complete: {Reason}", target.Kind, install.Reason);
            }
            else
            {
                logger.LogWarning("Model switch to {Model} finished with a caveat: {Reason}", target.Kind, _lastError);
            }
        }
        catch (Exception ex)
        {
            _lastError = ex.Message;
            logger.LogError(ex, "Model switch to {Model} failed", target.Kind);
        }
        finally
        {
            _switching = false;
            _gate.Release();
        }
    }
}
