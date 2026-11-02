using Maki.Core.Configuration;

namespace Maki.Api.Services.Upscaling;

public record EnhanceServerModelDto(string Id, bool Ready);

public record EnhanceServerDto(bool Enabled, IReadOnlyList<EnhanceServerModelDto> Models);

/// <summary>What the reader manifest tells the client about server-side page enhancement.</summary>
public class EnhanceAvailability(IAppSettings settings, UpscaleModelStore store)
{
    public async Task<EnhanceServerDto> GetAsync(CancellationToken ct = default)
    {
        var options = await UpscaleOptions.LoadAsync(settings, ct);
        if (!options.Enabled)
        {
            return new EnhanceServerDto(false, []);
        }

        return new EnhanceServerDto(true, options.Models.Select(id => new EnhanceServerModelDto(id.Value, store.IsReady(id))).ToList());
    }

    /// <summary>
    /// Null when <paramref name="model"/> can be served right now, else the catalogue key of the 409:
    /// the tier is off, or the model is unknown, not enabled or not downloaded.
    /// </summary>
    public static async Task<(string? RefusalKey, UpscaleModelId Model)> CheckAsync(
        IAppSettings settings, UpscaleModelStore store, string? model, CancellationToken ct)
    {
        var options = await UpscaleOptions.LoadAsync(settings, ct);
        if (!options.Enabled)
        {
            return ("error.enhance.disabled", default);
        }

        if (!UpscaleModelId.TryParse(model, out var id) || !options.Models.Contains(id) || !store.IsReady(id))
        {
            return ("error.enhance.modelUnavailable", default);
        }

        return (null, id);
    }
}
