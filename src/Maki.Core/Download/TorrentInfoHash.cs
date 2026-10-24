using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace Maki.Core.Download;

/// <summary>
/// The infohash qBittorrent reports for a torrent (lowercase hex, 40 characters), read from a magnet
/// link or computed from a .torrent file. Knowing it at grab time is what lets the completed-download
/// job find the torrent by hash instead of guessing from the time it was added.
/// </summary>
public static partial class TorrentInfoHash
{
    private const int MaxDepth = 64;

    [GeneratedRegex(@"xt=urn:btih:([0-9a-fA-F]{40}|[a-zA-Z2-7]{32})(?![0-9a-zA-Z])")]
    private static partial Regex MagnetHash();

    public static string? FromMagnet(string magnet)
    {
        if (MagnetHash().Match(magnet) is not { Success: true } match)
        {
            return null;
        }

        var value = match.Groups[1].Value;
        return value.Length == 40 ? value.ToLowerInvariant() : Convert.ToHexStringLower(FromBase32(value));
    }

    /// <summary>
    /// SHA-1 of the bencoded <c>info</c> dictionary, or for a v2-only torrent (no <c>pieces</c>) its
    /// SHA-256 cut to 20 bytes, which is what qBittorrent lists as <c>hash</c>. Null when the bytes are
    /// not a torrent file.
    /// </summary>
    public static string? FromTorrentFile(ReadOnlySpan<byte> torrent)
    {
        if (torrent.IsEmpty || torrent[0] != (byte)'d')
        {
            return null;
        }

        var pos = 1;
        while (pos < torrent.Length && torrent[pos] != (byte)'e')
        {
            if (ReadString(torrent, ref pos) is not { } key)
            {
                return null;
            }

            var start = pos;
            if (!Skip(torrent, ref pos, 0))
            {
                return null;
            }

            if (key.AsSpan().SequenceEqual("info"u8))
            {
                var info = torrent[start..pos];
                return IsV2Only(info)
                    ? Convert.ToHexStringLower(SHA256.HashData(info)[..20])
                    : Convert.ToHexStringLower(SHA1.HashData(info));
            }
        }

        return null;
    }

    private static bool IsV2Only(ReadOnlySpan<byte> info)
    {
        var pos = 1;
        var hasPieces = false;
        var metaVersion2 = false;
        while (pos < info.Length && info[pos] != (byte)'e')
        {
            var key = ReadString(info, ref pos);
            var start = pos;
            if (key is null || !Skip(info, ref pos, 0))
            {
                return false;
            }

            hasPieces |= key.AsSpan().SequenceEqual("pieces"u8);
            metaVersion2 |= key.AsSpan().SequenceEqual("meta version"u8) && info[start..pos].SequenceEqual("i2e"u8);
        }

        return metaVersion2 && !hasPieces;
    }

    private static byte[]? ReadString(ReadOnlySpan<byte> data, ref int pos)
    {
        var colon = data[pos..].IndexOf((byte)':');
        if (colon <= 0 || !int.TryParse(data.Slice(pos, colon), out var length) || length < 0)
        {
            return null;
        }

        var start = pos + colon + 1;
        if (length > data.Length - start)
        {
            return null;
        }

        pos = start + length;
        return data.Slice(start, length).ToArray();
    }

    private static bool Skip(ReadOnlySpan<byte> data, ref int pos, int depth)
    {
        if (pos >= data.Length || depth > MaxDepth)
        {
            return false;
        }

        switch (data[pos])
        {
            case (byte)'i':
                var end = data[pos..].IndexOf((byte)'e');
                if (end < 0)
                {
                    return false;
                }

                pos += end + 1;
                return true;
            case (byte)'l':
            case (byte)'d':
                var isDict = data[pos] == (byte)'d';
                pos++;
                while (pos < data.Length && data[pos] != (byte)'e')
                {
                    if (isDict && ReadString(data, ref pos) is null)
                    {
                        return false;
                    }

                    if (!Skip(data, ref pos, depth + 1))
                    {
                        return false;
                    }
                }

                if (pos >= data.Length)
                {
                    return false;
                }

                pos++;
                return true;
            default:
                return ReadString(data, ref pos) is not null;
        }
    }

    private static byte[] FromBase32(string value)
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        var bytes = new byte[value.Length * 5 / 8];
        int buffer = 0, bits = 0, index = 0;
        foreach (var c in value.ToUpperInvariant())
        {
            buffer = (buffer << 5) | alphabet.IndexOf(c);
            bits += 5;
            if (bits >= 8)
            {
                bits -= 8;
                bytes[index++] = (byte)(buffer >> bits);
            }
        }

        return bytes;
    }
}
