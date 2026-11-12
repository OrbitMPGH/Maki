using System.Globalization;
using Maki.Core.Configuration;

namespace Maki.Api.Services.Upscaling;

/// <summary>The instance-wide <c>library.upscaling.*</c> settings, read with their defaults applied.</summary>
public sealed record UpscaleOptions(
    bool Enabled,
    IReadOnlyList<UpscaleModelId> Models,
    int Threads,
    int CacheMb,
    string? ManifestUrl,
    bool JpegXl = true,
    PrepareOptions? Prepare = null)
{
    public const int DefaultCacheMb = 1024;
    public const int MinCacheMb = 128;
    public const int MaxCacheMb = 65536;

    public static int DefaultThreads => Math.Clamp(Environment.ProcessorCount / 2, 1, 4);

    public PrepareOptions PrepareAhead => Prepare ?? PrepareOptions.Off;

    public static async Task<UpscaleOptions> LoadAsync(IAppSettings settings, CancellationToken ct)
    {
        var models = new List<UpscaleModelId>();
        // Never saved: Maki 2x is ticked, so turning the tier on gives readers its default method.
        var csv = await settings.GetAsync(SettingKeys.UpscalingModels, ct) ?? UpscaleModelId.Maki2x.Value;
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
            string.IsNullOrEmpty(manifest) ? null : manifest,
            await settings.GetAsync(SettingKeys.UpscalingJpegXl, ct) != "false",
            new PrepareOptions(
                await settings.GetAsync(SettingKeys.UpscalingPrepare, ct) == "true",
                Int(await settings.GetAsync(SettingKeys.UpscalingPrepareDays, ct), PrepareOptions.DefaultDays, 1, PrepareOptions.MaxDays),
                Int(await settings.GetAsync(SettingKeys.UpscalingPrepareSeries, ct), PrepareOptions.DefaultSeries, 1, PrepareOptions.MaxSeries),
                Int(await settings.GetAsync(SettingKeys.UpscalingPrepareChapters, ct), PrepareOptions.DefaultChapters, 1, PrepareOptions.MaxChapters),
                Int(await settings.GetAsync(SettingKeys.UpscalingPrepareMb, ct), PrepareOptions.DefaultMb, PrepareOptions.MinMb, PrepareOptions.MaxMb)));
    }

    internal static int Int(string? raw, int fallback, int min, int max) =>
        int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? Math.Clamp(value, min, max)
            : fallback;
}

/// <summary>
/// "Prepare chapters ahead": upscale the next chapters of series a reader has set to Reading and read
/// within <see cref="Days"/>, at most <see cref="Series"/> of them per reader, <see cref="Chapters"/>
/// chapters each, holding at most <see cref="Mb"/> of pages apart from the cache's own cap.
/// </summary>
public sealed record PrepareOptions(bool Enabled, int Days, int Series, int Chapters, int Mb)
{
    public const int DefaultDays = 14;
    public const int MaxDays = 365;
    public const int DefaultSeries = 8;
    public const int MaxSeries = 100;
    public const int DefaultChapters = 1;
    public const int MaxChapters = 3;

    /// <summary>The most chapters one series can be set to keep ready from its own menu.</summary>
    public const int MaxSeriesChapters = 50;
    public const int DefaultMb = 4096;
    public const int MinMb = 256;
    public const int MaxMb = 262144;

    public static readonly PrepareOptions Off = new(false, DefaultDays, DefaultSeries, DefaultChapters, DefaultMb);
}
