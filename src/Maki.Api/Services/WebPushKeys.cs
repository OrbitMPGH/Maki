using System.Buffers.Text;
using System.Security.Cryptography;
using Maki.Core.Configuration;

namespace Maki.Api.Services;

/// <summary>
/// The instance's VAPID key pair, read from <c>AppConfig</c> and generated there the first time
/// anything asks. Browsers bind a subscription to the public key, so it must never change on its own:
/// a new pair silently orphans every device that subscribed under the old one.
/// </summary>
public sealed class WebPushKeys(IAppSettings settings)
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private (string PublicKey, string PrivateKey)? _keys;

    public async Task<(string PublicKey, string PrivateKey)> GetAsync(CancellationToken ct)
    {
        if (_keys is { } known)
        {
            return known;
        }

        await _gate.WaitAsync(ct);
        try
        {
            if (_keys is { } raced)
            {
                return raced;
            }

            var publicKey = await settings.GetAsync(SettingKeys.PushVapidPublicKey, ct);
            var privateKey = await settings.GetAsync(SettingKeys.PushVapidPrivateKey, ct);
            if (string.IsNullOrWhiteSpace(publicKey) || string.IsNullOrWhiteSpace(privateKey))
            {
                (publicKey, privateKey) = Generate();
                await settings.SetAsync(SettingKeys.PushVapidPrivateKey, privateKey, ct);
                await settings.SetAsync(SettingKeys.PushVapidPublicKey, publicKey, ct);
            }

            _keys = (publicKey, privateKey);
            return _keys.Value;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>A P-256 pair as base64url: the uncompressed public point (65 bytes) and the private scalar (32 bytes).</summary>
    internal static (string PublicKey, string PrivateKey) Generate()
    {
        using var ecdh = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var parameters = ecdh.ExportParameters(includePrivateParameters: true);
        var point = new byte[65];
        point[0] = 0x04;
        parameters.Q.X!.CopyTo(point, 1);
        parameters.Q.Y!.CopyTo(point, 33);
        return (Base64Url.EncodeToString(point), Base64Url.EncodeToString(parameters.D!));
    }
}
