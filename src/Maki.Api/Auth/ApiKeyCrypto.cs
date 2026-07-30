using System.Security.Cryptography;
using System.Text;

namespace Maki.Api.Auth;

/// <summary>Generation and hashing for <c>UserApiKey</c> secrets.</summary>
public static class ApiKeyCrypto
{
    /// <summary>How many leading characters of the plaintext are kept for display.</summary>
    public const int PrefixLength = 8;

    /// <summary>
    /// A 256-bit random token as lowercase hex. Twice the entropy of the 128-bit instance key this
    /// replaces, and generated from <see cref="RandomNumberGenerator"/> rather than anything seeded.
    /// </summary>
    public static string Generate() => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));

    /// <summary>
    /// Lowercase hex SHA-256 of <paramref name="key"/> — what actually gets stored.
    /// <para>
    /// A single fast hash is the right choice here, not PBKDF2 or Argon2: the input is 256 bits of
    /// uniform randomness, so there is no guessable password to slow an attacker down against, and
    /// this runs on every authenticated request including every page image an OPDS reader prefetches.
    /// The hash exists so a database leak does not hand over working credentials, and so lookup is
    /// an indexed match on a digest rather than a comparison against a secret — which removes the
    /// timing side channel the old <c>string.Equals</c> key check had.
    /// </para>
    /// </summary>
    public static string Hash(string key) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(key)));

    public static string Prefix(string key) =>
        key.Length <= PrefixLength ? key : key[..PrefixLength];
}
