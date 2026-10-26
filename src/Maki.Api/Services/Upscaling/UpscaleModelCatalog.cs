using System.Diagnostics.CodeAnalysis;

namespace Maki.Api.Services.Upscaling;

public enum UpscaleAdapter
{
    Plain2x,
    Plain4xThenHalve
}

public enum UpscaleTensorType
{
    Float16,
    Float32
}

/// <summary>The id of a model Maki knows how to run. Only ids in <see cref="UpscaleModelCatalog"/> parse.</summary>
public readonly record struct UpscaleModelId
{
    public static readonly UpscaleModelId SpanS = new("span-s");
    public static readonly UpscaleModelId RealEsrganAv3 = new("realesrgan-av3");

    private UpscaleModelId(string value) => Value = value;

    public string Value { get; }

    public static bool TryParse(string? text, out UpscaleModelId id)
    {
        foreach (var known in UpscaleModelCatalog.Ids)
        {
            if (string.Equals(known.Value, text?.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                id = known;
                return true;
            }
        }

        id = default;
        return false;
    }

    public static UpscaleModelId Parse(string text) =>
        TryParse(text, out var id) ? id : throw new FormatException($"Unknown upscale model id '{text}'.");

    public override string ToString() => Value ?? string.Empty;
}

/// <param name="ModelScale">What the network itself produces (4 for Real-ESRGAN).</param>
/// <param name="OutputScale">What the reader gets after any resize (2 for both).</param>
public sealed record UpscaleModelInfo(
    UpscaleModelId Id,
    UpscaleAdapter Adapter,
    int ModelScale,
    int OutputScale,
    string InputName,
    UpscaleTensorType InputType,
    string FileName,
    string DisplayName,
    string License,
    string Author,
    string SourceUrl);

/// <summary>What Maki can run. The downloaded manifest only supplies checksums and where the files are.</summary>
public sealed class UpscaleModelCatalog
{
    private static readonly UpscaleModelInfo[] Entries =
    [
        new(UpscaleModelId.SpanS, UpscaleAdapter.Plain2x, 2, 2, "input", UpscaleTensorType.Float16,
            "span-s-2x.onnx", "SPAN-S (IllustrationJaNai V3)", "CC BY-NC 4.0",
            "the-database (IllustrationJaNai V3)", "https://github.com/the-database/MangaJaNai/releases/tag/3.0.0"),
        new(UpscaleModelId.RealEsrganAv3, UpscaleAdapter.Plain4xThenHalve, 4, 2, "input", UpscaleTensorType.Float32,
            "realesrgan-av3-4x.onnx", "Real-ESRGAN animevideov3", "BSD-3-Clause",
            "Xintao Wang et al.", "https://github.com/xinntao/Real-ESRGAN/releases/tag/v0.2.5.0")
    ];

    internal static IEnumerable<UpscaleModelId> Ids => Entries.Select(e => e.Id);

    public IReadOnlyList<UpscaleModelInfo> All => Entries;

    public UpscaleModelInfo Get(UpscaleModelId id) =>
        TryGet(id, out var info) ? info : throw new KeyNotFoundException($"Unknown upscale model '{id}'.");

    public bool TryGet(UpscaleModelId id, [NotNullWhen(true)] out UpscaleModelInfo? info)
    {
        info = Entries.FirstOrDefault(e => e.Id == id);
        return info is not null;
    }

    public bool TryGet(string? id, [NotNullWhen(true)] out UpscaleModelInfo? info)
    {
        if (UpscaleModelId.TryParse(id, out var parsed))
        {
            return TryGet(parsed, out info);
        }

        info = null;
        return false;
    }
}
