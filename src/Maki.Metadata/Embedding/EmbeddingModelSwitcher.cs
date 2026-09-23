using Maki.Core.Configuration;
using Microsoft.Extensions.Logging;

namespace Maki.Metadata.Embedding;

/// <summary>
/// Outcome of asking to switch models: whether a switch actually started, and why not.
/// <see cref="Reason"/> is a server message catalogue key, not display text; this project has no
/// <c>ILocalizer</c> (see <c>CLAUDE.md</c>'s directory ownership), so the caller in <c>Maki.Api</c>
/// renders it.
/// </summary>
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
    private volatile object? _lastErrorArgs;

    /// <summary>True while a switch is downloading in the background.</summary>
    public bool Switching => _switching;

    /// <summary>
    /// Why the last switch didn't fully complete (e.g. no prebuilt index for that model), or null. A
    /// server message catalogue key, not display text; same reason as <see cref="ModelSwitchStart"/>.
    /// </summary>
    public string? LastError => _lastError;

    /// <summary>ICU placeholder values for <see cref="LastError"/>.</summary>
    public object? LastErrorArgs => _lastErrorArgs;

    /// <summary>The model in effect right now ("off"/"base").</summary>
    public string CurrentModel => options.Enabled ? options.Model.Kind : EmbeddingModelProfile.OffKind;

    /// <summary>
    /// Kicks off a switch to <paramref name="kind"/> ("off"/"base") in the background.
    /// Returns immediately: a no-op when already on that model, refused while an indexing pass or
    /// another switch is in flight. The caller reports <see cref="Switching"/> until it clears.
    /// </summary>
    public ModelSwitchStart Start(string? kind)
    {
        var off = EmbeddingModelProfile.IsOff(kind);
        var target = off ? null : EmbeddingModelProfile.Resolve(kind);
        var targetKind = off ? EmbeddingModelProfile.OffKind : target!.Kind;

        if (string.Equals(targetKind, CurrentModel, StringComparison.Ordinal))
        {
            return new ModelSwitchStart(false, targetKind, "install.embeddingModel.alreadyOn");
        }

        if (indexStatus.Running)
        {
            return new ModelSwitchStart(false, CurrentModel, "install.embeddingModel.indexingRunning");
        }

        // Non-blocking acquire: hold the gate for the whole background run so a second request
        // can't start an overlapping switch. Released in RunAsync's finally.
        if (!_gate.Wait(0))
        {
            return new ModelSwitchStart(false, CurrentModel, "install.embeddingModel.switchInProgress");
        }

        _switching = true;
        _lastError = null;
        _lastErrorArgs = null;
        _ = Task.Run(() => RunAsync(targetKind, target));
        return new ModelSwitchStart(true, targetKind, "install.embeddingModel.switching");
    }

    private async Task RunAsync(string targetKind, EmbeddingModelProfile? target)
    {
        try
        {
            logger.LogInformation("Switching embedding model to {Model}…", targetKind);
            await settings.SetAsync(SettingKeys.RecommendationsEmbeddingModel, targetKind);

            if (target is null)
            {
                // Turn embeddings off: disable, then drop the caches so search/recs fall back to
                // lexical/genre immediately. Nothing to download.
                options.Enabled = false;
                cache.Invalidate();
                embedder.Reset();
                _lastError = null;
                logger.LogInformation("Embeddings turned off");
                return;
            }

            // Repoint first, then drop the caches that were built for the old model. A search in
            // this window sees a width mismatch and falls back to titles — degraded, never wrong.
            options.Enabled = true;
            options.Model = target;
            cache.Invalidate();
            embedder.Reset();

            await modelStore.EnsureAsync();            // downloads the target ONNX + vocab if missing
            var modelReady = await embedder.EnsureReadyAsync();

            // force: fetch the target model's index regardless of the freshness check — this is a
            // deliberate switch, not the nightly poll.
            var install = await prebuilt.InstallAsync(force: true);

            _lastError = !modelReady
                ? "install.embeddingModel.downloadFailed"
                : install.Installed
                    ? null
                    : install.Reason;
            _lastErrorArgs = !modelReady || install.Installed ? null : install.ReasonArgs;

            if (_lastError is null)
            {
                logger.LogInformation("Model switch to {Model} complete: {Reason}", targetKind, install.Reason);
            }
            else
            {
                logger.LogWarning("Model switch to {Model} finished with a caveat: {Reason}", targetKind, _lastError);
            }
        }
        catch (Exception ex)
        {
            _lastError = ex.Message;
            logger.LogError(ex, "Model switch to {Model} failed", targetKind);
        }
        finally
        {
            _switching = false;
            _gate.Release();
        }
    }
}
