using System.Globalization;
using System.IO.Compression;
using System.Text;

namespace Maki.Core.Mihon;

/// <summary>
/// Reads a Mihon <c>.tachibk</c> / <c>.proto.gz</c> backup (gzipped or raw protobuf). Forks add their own
/// fields and reuse retired numbers, so every field not named here is skipped.
/// </summary>
public static class MihonBackupReader
{
    public const long DefaultMaxBytes = 256L * 1024 * 1024;

    private const int SyncMyAnimeList = 1;
    private const int SyncAniList = 2;
    private const int SyncKitsu = 3;
    private const int SyncMangaBaka = 11;

    public static MihonBackup Read(Stream input) => Read(input, DefaultMaxBytes);

    internal static MihonBackup Read(Stream input, long maxBytes)
    {
        var raw = ReadAll(input, maxBytes);
        if (raw.Length == 0)
        {
            throw new MihonBackupException(MihonBackupFailure.NotABackup, "The file is empty.");
        }

        var data = raw.Length >= 2 && raw[0] == 0x1F && raw[1] == 0x8B ? Gunzip(raw, maxBytes) : raw;
        if (LooksLikeJson(data))
        {
            throw new MihonBackupException(MihonBackupFailure.JsonBackup, "JSON backups are not supported.");
        }

        return Parse(data);
    }

    private static byte[] ReadAll(Stream input, long maxBytes)
    {
        using var buffer = new MemoryStream();
        Copy(input, buffer, maxBytes);
        return buffer.ToArray();
    }

    private static byte[] Gunzip(byte[] compressed, long maxBytes)
    {
        using var output = new MemoryStream();
        try
        {
            using var gzip = new GZipStream(new MemoryStream(compressed), CompressionMode.Decompress);
            Copy(gzip, output, maxBytes);
        }
        catch (Exception e) when (e is InvalidDataException or EndOfStreamException)
        {
            throw new MihonBackupException(MihonBackupFailure.Corrupt, "The gzip data is damaged.", e);
        }

        return output.ToArray();
    }

    private static void Copy(Stream from, MemoryStream to, long maxBytes)
    {
        var chunk = new byte[81920];
        long total = 0;
        int read;
        while ((read = from.Read(chunk, 0, chunk.Length)) > 0)
        {
            total += read;
            if (total > maxBytes)
            {
                throw new MihonBackupException(MihonBackupFailure.TooLarge, "The backup is too large.");
            }

            to.Write(chunk, 0, read);
        }
    }

    private static bool LooksLikeJson(byte[] data)
    {
        var i = data.Length >= 3 && data[0] == 0xEF && data[1] == 0xBB && data[2] == 0xBF ? 3 : 0;
        while (i < data.Length && data[i] is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n')
        {
            i++;
        }

        return i < data.Length && data[i] == (byte)'{';
    }

    private static MihonBackup Parse(byte[] data)
    {
        var mangaRanges = new List<(int Offset, int Length)>();
        var categoryNames = new Dictionary<long, string>();
        var categoryCount = 0;

        var reader = new ProtoReader(data);
        while (reader.Next(out var field, out var wire))
        {
            if (field is 1 or 2 && wire != ProtoReader.LengthDelimited)
            {
                throw new MihonBackupException(MihonBackupFailure.Corrupt, "Malformed protobuf: unexpected wire type");
            }

            switch (field)
            {
                case 1:
                    mangaRanges.Add(reader.ReadRange());
                    break;
                case 2:
                    categoryCount++;
                    var (name, order) = ParseCategory(reader.ReadBytes());
                    categoryNames.TryAdd(order, name);
                    break;
                default:
                    reader.Skip(wire);
                    break;
            }
        }

        if (mangaRanges.Count == 0 && categoryCount == 0)
        {
            throw new MihonBackupException(MihonBackupFailure.NotABackup, "No library data found.");
        }

        var library = new List<MihonManga>();
        for (var index = 0; index < mangaRanges.Count; index++)
        {
            var (offset, length) = mangaRanges[index];
            if (ParseManga(data.AsSpan(offset, length), index, categoryNames) is { } manga)
            {
                library.Add(manga);
            }
        }

        return new MihonBackup(library, mangaRanges.Count - library.Count, categoryCount);
    }

    private static (string Name, long Order) ParseCategory(ReadOnlySpan<byte> message)
    {
        var name = "";
        long order = 0;
        var reader = new ProtoReader(message);
        while (reader.Next(out var field, out var wire))
        {
            switch (field)
            {
                case 1 when wire == ProtoReader.LengthDelimited:
                    name = Utf8(reader.ReadBytes());
                    break;
                case 2 when wire == ProtoReader.Varint:
                    order = (long)reader.ReadVarint();
                    break;
                default:
                    reader.Skip(wire);
                    break;
            }
        }

        return (name, order);
    }

    private static MihonManga? ParseManga(ReadOnlySpan<byte> message, int index, Dictionary<long, string> categoryNames)
    {
        var title = "";
        string? author = null;
        var favourite = true;
        var categoryOrders = new List<long>();
        var chapterCount = 0;
        var readCount = 0;
        var readNumbers = new SortedSet<decimal>();
        var trackers = new List<(long SyncId, long RemoteId, float LastRead)>();

        var reader = new ProtoReader(message);
        while (reader.Next(out var field, out var wire))
        {
            switch (field)
            {
                case 3 when wire == ProtoReader.LengthDelimited:
                    title = Utf8(reader.ReadBytes());
                    break;
                case 5 when wire == ProtoReader.LengthDelimited:
                    author = Utf8(reader.ReadBytes());
                    break;
                case 16 when wire == ProtoReader.LengthDelimited:
                    chapterCount++;
                    ParseChapter(reader.ReadBytes(), ref readCount, readNumbers);
                    break;
                case 17 when wire == ProtoReader.Varint:
                    categoryOrders.Add((long)reader.ReadVarint());
                    break;
                case 17 when wire == ProtoReader.LengthDelimited:
                    var packed = new ProtoReader(reader.ReadBytes());
                    while (!packed.AtEnd)
                    {
                        categoryOrders.Add((long)packed.ReadVarint());
                    }

                    break;
                case 18 when wire == ProtoReader.LengthDelimited:
                    trackers.Add(ParseTracking(reader.ReadBytes()));
                    break;
                case 100 when wire == ProtoReader.Varint:
                    favourite = reader.ReadVarint() != 0;
                    break;
                default:
                    reader.Skip(wire);
                    break;
            }
        }

        if (!favourite)
        {
            return null;
        }

        int? aniList = null, mal = null, kitsu = null;
        string? mangaBaka = null;
        float? lastRead = null;
        foreach (var (syncId, remoteId, trackerLastRead) in trackers)
        {
            switch (syncId)
            {
                case SyncMyAnimeList:
                    mal ??= ToId(remoteId);
                    break;
                case SyncAniList:
                    aniList ??= ToId(remoteId);
                    break;
                case SyncKitsu:
                    kitsu ??= ToId(remoteId);
                    break;
                case SyncMangaBaka:
                    mangaBaka ??= remoteId > 0 ? remoteId.ToString(CultureInfo.InvariantCulture) : null;
                    break;
                default:
                    continue;
            }

            if (float.IsFinite(trackerLastRead) && trackerLastRead > 0 && (lastRead is null || trackerLastRead > lastRead))
            {
                lastRead = trackerLastRead;
            }
        }

        var categories = categoryOrders
            .Select(o => categoryNames.GetValueOrDefault(o))
            .OfType<string>()
            .Where(n => n.Length > 0)
            .Distinct()
            .ToList();

        return new MihonManga(
            index,
            title,
            string.IsNullOrWhiteSpace(author) ? null : author,
            categories,
            aniList,
            mal,
            kitsu,
            mangaBaka,
            [.. readNumbers],
            chapterCount == 0 && lastRead is { } l ? MihonFloat.NarrowToDecimal(l) : null,
            chapterCount,
            readCount);
    }

    private static void ParseChapter(ReadOnlySpan<byte> message, ref int readCount, SortedSet<decimal> readNumbers)
    {
        var read = false;
        var number = -1f;
        var reader = new ProtoReader(message);
        while (reader.Next(out var field, out var wire))
        {
            switch (field)
            {
                case 4 when wire == ProtoReader.Varint:
                    read = reader.ReadVarint() != 0;
                    break;
                case 9 when wire == ProtoReader.Fixed32:
                    number = reader.ReadFloat();
                    break;
                default:
                    reader.Skip(wire);
                    break;
            }
        }

        if (!read)
        {
            return;
        }

        readCount++;
        if (number >= 0 && MihonFloat.NarrowToDecimal(number) is { } narrowed)
        {
            readNumbers.Add(narrowed);
        }
    }

    private static (long SyncId, long RemoteId, float LastRead) ParseTracking(ReadOnlySpan<byte> message)
    {
        long syncId = 0, mediaIdInt = 0, mediaId = 0;
        var lastRead = 0f;
        var reader = new ProtoReader(message);
        while (reader.Next(out var field, out var wire))
        {
            switch (field)
            {
                case 1 when wire == ProtoReader.Varint:
                    syncId = (long)reader.ReadVarint();
                    break;
                case 3 when wire == ProtoReader.Varint:
                    mediaIdInt = (long)reader.ReadVarint();
                    break;
                case 6 when wire == ProtoReader.Fixed32:
                    lastRead = reader.ReadFloat();
                    break;
                case 100 when wire == ProtoReader.Varint:
                    mediaId = (long)reader.ReadVarint();
                    break;
                default:
                    reader.Skip(wire);
                    break;
            }
        }

        return (syncId, mediaId != 0 ? mediaId : mediaIdInt, lastRead);
    }

    private static int? ToId(long id) => id is > 0 and <= int.MaxValue ? (int)id : null;

    private static string Utf8(ReadOnlySpan<byte> bytes) => Encoding.UTF8.GetString(bytes);
}
