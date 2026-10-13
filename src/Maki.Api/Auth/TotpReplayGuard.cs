using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using Maki.Data.Identity;
using Microsoft.AspNetCore.Identity;

namespace Maki.Api.Auth;

/// <summary>
/// Refuses an authenticator code whose time step is at or before the last one accepted for the user.
/// <para>
/// Identity's authenticator provider accepts the current 30 second step and two either side, and
/// remembers nothing, so a code read off a shoulder or phished in real time works again for a couple
/// of minutes. Identity does not say which step matched, so it is recomputed here with RFC 6238 over
/// the same window, after Identity has already accepted the code. This class never accepts anything
/// itself: it can only turn an accepted code into a refusal.
/// </para>
/// </summary>
public static class TotpReplayGuard
{
    private const string TokenProvider = "Maki";
    private const string TokenName = "TotpLastStep";
    private const int StepSeconds = 30;
    private const int Window = 2;

    /// <summary>True when <paramref name="code"/> matches a step that was already used.</summary>
    public static async Task<bool> IsReplayAsync(UserManager<MakiUser> users, MakiUser user, string code)
    {
        var step = await StepOfAsync(users, user, code);
        return step is { } s && await LastStepAsync(users, user) is { } last && s <= last;
    }

    /// <summary>Remembers the step of an accepted code so it cannot be used again.</summary>
    public static async Task RecordAsync(UserManager<MakiUser> users, MakiUser user, string code)
    {
        if (await StepOfAsync(users, user, code) is { } step
            && (await LastStepAsync(users, user) is not { } last || step > last))
        {
            await users.SetAuthenticationTokenAsync(
                user, TokenProvider, TokenName, step.ToString(CultureInfo.InvariantCulture));
        }
    }

    /// <summary>Forgets the last step. Called wherever the authenticator key is reset, since steps of the old key mean nothing.</summary>
    public static Task ClearAsync(UserManager<MakiUser> users, MakiUser user) =>
        users.RemoveAuthenticationTokenAsync(user, TokenProvider, TokenName);

    private static async Task<long?> LastStepAsync(UserManager<MakiUser> users, MakiUser user) =>
        long.TryParse(await users.GetAuthenticationTokenAsync(user, TokenProvider, TokenName),
            NumberStyles.None, CultureInfo.InvariantCulture, out var last) ? last : null;

    private static async Task<long?> StepOfAsync(UserManager<MakiUser> users, MakiUser user, string code)
    {
        var key = await users.GetAuthenticatorKeyAsync(user);
        return string.IsNullOrEmpty(key) ? null : MatchStep(key, code, DateTimeOffset.UtcNow);
    }

    /// <summary>The newest step within the accepted window that produces <paramref name="code"/>, or null.</summary>
    public static long? MatchStep(string base32Key, string code, DateTimeOffset now)
    {
        if (code.Length != 6 || !code.All(char.IsAsciiDigit) || Base32Decode(base32Key) is not { } key)
        {
            return null;
        }

        var current = now.ToUnixTimeSeconds() / StepSeconds;
        for (var offset = Window; offset >= -Window; offset--)
        {
            if (CodeFor(key, current + offset) == code)
            {
                return current + offset;
            }
        }

        return null;
    }

    public static string CodeFor(byte[] key, long step)
    {
        Span<byte> counter = stackalloc byte[8];
        BinaryPrimitives.WriteInt64BigEndian(counter, step);
        Span<byte> hash = stackalloc byte[20];
        HMACSHA1.HashData(key, counter, hash);

        var offset = hash[^1] & 0x0F;
        var binary = BinaryPrimitives.ReadInt32BigEndian(hash.Slice(offset, 4)) & 0x7FFFFFFF;
        return (binary % 1_000_000).ToString("D6", CultureInfo.InvariantCulture);
    }

    public static byte[]? Base32Decode(string value)
    {
        var bytes = new List<byte>();
        var buffer = 0;
        var bits = 0;
        foreach (var c in value.TrimEnd('=').ToUpperInvariant())
        {
            var digit = c switch
            {
                >= 'A' and <= 'Z' => c - 'A',
                >= '2' and <= '7' => c - '2' + 26,
                _ => -1
            };
            if (digit < 0)
            {
                return null;
            }

            buffer = (buffer << 5) | digit;
            bits += 5;
            if (bits >= 8)
            {
                bits -= 8;
                bytes.Add((byte)((buffer >> bits) & 0xFF));
            }
        }

        return bytes.ToArray();
    }
}
