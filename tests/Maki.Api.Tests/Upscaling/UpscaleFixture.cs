using System.Security.Cryptography;

namespace Maki.Api.Tests.Upscaling;

internal static class UpscaleFixture
{
    public static string IdentityModelPath =>
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "upscale", "identity-2x.onnx");

    public static string Sha256Of(string path) => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));
}
