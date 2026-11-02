using System.Globalization;
using Maki.Core.Configuration;

namespace Maki.Api.Services.Upscaling;

/// <summary>The instance-wide <c>library.upscaling.*</c> settings, read with their defaults applied.</summary>
public sealed record UpscaleOptions(
    bool Enabled,
    IReadOnlyList<UpscaleModelId> Models,
    int Threads,
    int CacheMb,
    string? ManifestUrl)
{
    public const int DefaultCacheMb = 1024;
    public const int MinCacheMb = 128;
    public const int MaxCacheMb = 65536;

    public static int DefaultThreads => Math.Clamp(Environment.ProcessorCount / 2, 1, 4);

    public static async Task<UpscaleOptions> LoadAsync(IAppSettings settings, CancellationToken ct)
    {
        var models = new List<UpscaleModelId>();
        var csv = await settings.GetAsync(SettingKeys.UpscalingModels, ct);
        foreach (var part in (csv ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (UpscaleModelId.TryParse(part, out var id) && !models.Contains(id))
            {
                models.Add(id);
            }
        }

        var manifest = (await settings.GetAsync(SettingKeys.UpscalingManifestUrl, ct))?.Trim();
        return new UpscaleOptions(
            await settings.GetAsync(SettingKeys.UpscalingEnabled, ct) == "true",
            models,
            Int(await settings.GetAsync(SettingKeys.UpscalingThreads, ct), DefaultThreads, 1, Math.Max(Environment.ProcessorCount, 1)),
            Int(await settings.GetAsync(SettingKeys.UpscalingCacheMb, ct), DefaultCacheMb, MinCacheMb, MaxCacheMb),
            string.IsNullOrEmpty(manifest) ? null : manifest);
    }

    private static int Int(string? raw, int fallback, int min, int max) =>
        int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? Math.Clamp(value, min, max)
            : fallback;
}
