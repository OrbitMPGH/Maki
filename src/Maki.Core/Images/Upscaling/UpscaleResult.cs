namespace Maki.Core.Images.Upscaling;

public enum UpscaleOutcome
{
    Ready,
    Skipped,
    Failed
}

/// <summary>Short codes for why a page was skipped or failed. Not user-facing text.</summary>
public static class UpscaleReasons
{
    public const string Unreadable = "unreadable";
    public const string TooLarge = "tooLarge";
    public const string HighBitDepth = "highBitDepth";
    public const string UnknownModel = "unknownModel";
    public const string ModelUnavailable = "modelUnavailable";
    public const string InferenceFailed = "inferenceFailed";
    public const string EncodeFailed = "encodeFailed";
    public const string CacheWriteFailed = "cacheWriteFailed";
}

/// <param name="Extension">"webp" or "jpg", without the dot. Null unless Ready.</param>
/// <param name="Reason">One of <see cref="UpscaleReasons"/> when Skipped or Failed.</param>
public sealed record UpscaleResult(
    UpscaleOutcome Outcome,
    byte[]? Bytes,
    string? Extension,
    int Width,
    int Height,
    string? Reason)
{
    public static UpscaleResult Ready(UpscaledImage image) =>
        new(UpscaleOutcome.Ready, image.Bytes, image.Extension, image.Width, image.Height, null);

    public static UpscaleResult Skipped(string reason) => new(UpscaleOutcome.Skipped, null, null, 0, 0, reason);

    public static UpscaleResult Failed(string reason) => new(UpscaleOutcome.Failed, null, null, 0, 0, reason);
}

/// <param name="Bands">1 for a greyscale page, else 3.</param>
public sealed record UpscaledImage(byte[] Bytes, string Extension, int Width, int Height, int Bands);
