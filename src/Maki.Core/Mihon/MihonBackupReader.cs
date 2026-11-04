using System.Globalization;
using System.IO.Compression;
using System.Text;

namespace Maki.Core.Mihon;

/// <summary>Reads a Mihon <c>.tachibk</c> / <c>.proto.gz</c> backup (gzipped or raw protobuf); fields not named here are skipped.</summary>
public static class MihonBackupReader
{
    public const long DefaultMaxBytes = 256L * 1024 * 1024;

    public const int MaxMangaRecords = 100_000;

    private const int SyncMyAnimeList = 1;
    private const int SyncAniList = 2;
    private const int SyncKitsu = 3;
    private const int SyncMangaBaka = 11;

    /// <summary>
    /// Parses straight off <paramref name="input"/>, gunzipping on the way, so only one manga
    /// message is held at a time rather than the upload and its decompressed copy. The cap counts
    /// the bytes the parser takes in, decompressed where the backup is gzipped.
    /// </summary>
    public static MihonBackup Read(Stream input) => Read(input, DefaultMaxBytes);

    internal static MihonBackup Read(Stream input, long maxBytes)
    {
        if (!input.CanSeek)
        {
            var held = new MemoryStream();
            Copy(input, held, maxBytes);
            held.Position = 0;
            input = held;
        }

        var start = input.Position;
        if (input.Length - start > maxBytes)
        {
            throw new MihonBackupException(MihonBackupFailure.TooLarge, "The backup is too large.");
        }

        Span<byte> magic = stackalloc byte[2];
        var peeked = input.ReadAtLeast(magic, magic.Length, throwOnEndOfStream: false);
        input.Position = start;
        if (peeked == 0)
        {
            throw new MihonBackupException(MihonBackupFailure.NotABackup, "The file is empty.");
        }

        var gzipped = peeked == 2 && magic[0] == 0x1F && magic[1] == 0x8B;
        using var source = gzipped ? new GZipStream(input, CompressionMode.Decompress, leaveOpen: true) : null;
        var reader = new ProtoStreamReader(source ?? input, maxBytes);
        if (LooksLikeJson(reader.Lookahead()))
        {
            throw new MihonBackupException(MihonBackupFailure.JsonBackup, "JSON backups are not supported.");
        }

        return Parse(reader);
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

    private static bool LooksLikeJson(ReadOnlySpan<byte> data)
    {
        var i = data.Length >= 3 && data[0] == 0xEF && data[1] == 0xBB && data[2] == 0xBF ? 3 : 0;
        while (i < data.Length && data[i] is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n')
        {
            i++;
        }

        return i < data.Length && data[i] == (byte)'{';
    }

    private static MihonBackup Parse(ProtoStreamReader reader)
    {
        var parsed = new List<(MihonManga Manga, List<long> CategoryOrders)>();
        var categoryNames = new Dictionary<long, string>();
        var mangaCount = 0;
        var categoryCount = 0;

        while (reader.Next(out var field, out var wire))
        {
            if (field is 1 or 2 && wire != ProtoReader.LengthDelimited)
            {
                throw new MihonBackupException(MihonBackupFailure.Corrupt, "Malformed protobuf: unexpected wire type");
            }

            switch (field)
            {
                case 1:
                    if (mangaCount >= MaxMangaRecords)
                    {
                        throw new MihonBackupException(MihonBackupFailure.TooLarge, "The backup has too many manga.");
                    }

                    if (ParseManga(reader.ReadMessage(), mangaCount) is { } manga)
                    {
                        parsed.Add(manga);
                    }

                    mangaCount++;
                    break;
                case 2:
                    categoryCount++;
                    var (name, order) = ParseCategory(reader.ReadMessage());
                    categoryNames.TryAdd(order, name);
                    break;
                default:
                    reader.Skip(wire);
                    break;
            }
        }

        if (mangaCount == 0 && categoryCount == 0)
        {
            throw new MihonBackupException(MihonBackupFailure.NotABackup, "No library data found.");
        }

        // Categories follow the manga in a backup, so names are only known once the whole file is read.
        var library = parsed
            .Select(p => p.Manga with
            {
                Categories = p.CategoryOrders
                    .Select(o => categoryNames.GetValueOrDefault(o))
                    .OfType<string>()
                    .Where(n => n.Length > 0)
                    .Distinct()
                    .ToList(),
            })
            .ToList();

        return new MihonBackup(library, mangaCount - library.Count, categoryCount);
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

    private static (MihonManga Manga, List<long> CategoryOrders)? ParseManga(ReadOnlySpan<byte> message, int index)
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

        if (!favourite || string.IsNullOrWhiteSpace(title))
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

        return (new MihonManga(
            index,
            title,
            string.IsNullOrWhiteSpace(author) ? null : author,
            [],
            aniList,
            mal,
            kitsu,
            mangaBaka,
            [.. readNumbers],
            chapterCount == 0 && lastRead is { } l ? MihonFloat.NarrowToDecimal(l) : null,
            chapterCount,
            readCount), categoryOrders);
    }

    private static void ParseChapter(ReadOnlySpan<byte> message, ref int readCount, SortedSet<decimal> readNumbers)
    {
        var read = false;
        var number = 0f;
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
