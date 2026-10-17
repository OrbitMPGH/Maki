using System.IO.Compression;
using System.Text;
using Maki.Core.Mihon;

namespace Maki.Core.Tests.Mihon;

public class MihonBackupReaderTests
{
    private static ProtoWriter Manga(string title, Action<ProtoWriter>? more = null)
    {
        var m = new ProtoWriter().Varint(1, 1234).String(2, "/series/x").String(3, title);
        more?.Invoke(m);
        return m;
    }

    private static ProtoWriter Chapter(float number, bool read) =>
        new ProtoWriter().String(1, "/c").String(2, "Ch").Bool(4, read).Float(9, number);

    private static ProtoWriter Tracker(int syncId, long mediaId, float lastRead = 0, long? mediaIdInt = null)
    {
        var t = new ProtoWriter().Varint(1, syncId);
        if (mediaIdInt is { } legacy)
        {
            t.Varint(3, legacy);
        }

        if (lastRead != 0)
        {
            t.Float(6, lastRead);
        }

        if (mediaId != 0)
        {
            t.Varint(100, mediaId);
        }

        return t;
    }

    private static byte[] Backup(params ProtoWriter[] manga)
    {
        var b = new ProtoWriter();
        foreach (var m in manga)
        {
            b.Message(1, m);
        }

        return b.ToArray();
    }

    private static MihonBackup Read(byte[] data, long? cap = null)
    {
        using var stream = new MemoryStream(data);
        return cap is { } c ? MihonBackupReader.Read(stream, c) : MihonBackupReader.Read(stream);
    }

    private static byte[] Gzip(byte[] data)
    {
        using var output = new MemoryStream();
        using (var gz = new GZipStream(output, CompressionLevel.Optimal, leaveOpen: true))
        {
            gz.Write(data);
        }

        return output.ToArray();
    }

    private static MihonBackupFailure Fails(byte[] data, long? cap = null) =>
        Assert.Throws<MihonBackupException>(() => Read(data, cap)).Reason;

    [Fact]
    public void Reads_a_raw_protobuf_backup()
    {
        var result = Read(Backup(Manga("Berserk", m => m.String(5, "Miura"))));

        var manga = Assert.Single(result.Manga);
        Assert.Equal("Berserk", manga.Title);
        Assert.Equal("Miura", manga.Author);
        Assert.Equal(0, manga.Index);
    }

    [Fact]
    public void Reads_a_gzipped_backup()
    {
        var result = Read(Gzip(Backup(Manga("Berserk"))));

        Assert.Equal("Berserk", Assert.Single(result.Manga).Title);
    }

    [Fact]
    public void Rejects_a_json_backup_raw_and_gzipped()
    {
        var json = Encoding.UTF8.GetBytes("﻿ \n {\"backupManga\":[]}");

        Assert.Equal(MihonBackupFailure.JsonBackup, Fails(json));
        Assert.Equal(MihonBackupFailure.JsonBackup, Fails(Gzip(json)));
    }

    [Fact]
    public void Empty_input_is_not_a_backup()
    {
        Assert.Equal(MihonBackupFailure.NotABackup, Fails([]));
    }

    [Fact]
    public void Text_that_is_not_protobuf_is_rejected()
    {
        var reason = Fails(Encoding.UTF8.GetBytes("this is not a backup file at all"));

        Assert.Contains(reason, new[] { MihonBackupFailure.NotABackup, MihonBackupFailure.Corrupt });
    }

    [Fact]
    public void A_backup_without_library_fields_is_not_a_backup()
    {
        Assert.Equal(MihonBackupFailure.NotABackup, Fails(new ProtoWriter().String(3, "sources").ToArray()));
    }

    [Fact]
    public void A_truncated_message_is_corrupt()
    {
        var data = Backup(Manga("Berserk", m => m.Message(16, Chapter(1, true))));

        Assert.Equal(MihonBackupFailure.Corrupt, Fails(data[..^3]));
    }

    [Fact]
    public void A_truncated_gzip_stream_is_corrupt()
    {
        var random = new Random(7);
        var noise = new byte[5000];
        random.NextBytes(noise);
        var gz = Gzip(Backup(Manga("Berserk", m => m.Bytes(5, noise))));

        Assert.Equal(MihonBackupFailure.Corrupt, Fails(gz[..(gz.Length / 2)]));
    }

    [Fact]
    public void A_varint_that_never_ends_is_corrupt()
    {
        Assert.Equal(MihonBackupFailure.Corrupt, Fails([0x08, 0xFF, 0xFF, 0xFF]));
    }

    [Fact]
    public void Group_wire_types_are_corrupt()
    {
        Assert.Equal(MihonBackupFailure.Corrupt, Fails([0x0B, 0x0C]));
    }

    [Fact]
    public void Decompression_is_capped()
    {
        var data = Backup(Manga("Berserk", m => m.String(5, new string('x', 20_000))));
        var gz = Gzip(data);
        Assert.True(gz.Length < 1000);

        Assert.Equal(MihonBackupFailure.TooLarge, Fails(gz, cap: 10_000));
        Assert.Equal("Berserk", Assert.Single(Read(gz, cap: 100_000).Manga).Title);
    }

    [Fact]
    public void Categories_map_by_order_not_position()
    {
        var backup = new ProtoWriter()
            .Message(1, Manga("A", m => m.Varint(17, 5).Varint(17, 9).Varint(17, 77)))
            .Message(2, new ProtoWriter().String(1, "Reading").Varint(2, 5))
            .Message(2, new ProtoWriter().String(1, "Plan").Varint(2, 9))
            .ToArray();

        var result = Read(backup);

        Assert.Equal(2, result.CategoryCount);
        Assert.Equal(["Reading", "Plan"], Assert.Single(result.Manga).Categories);
    }

    [Fact]
    public void Packed_categories_and_duplicates_are_handled()
    {
        var backup = new ProtoWriter()
            .Message(1, Manga("A", m => m.Packed(17, 5, 9, 5)))
            .Message(1, Manga("B"))
            .Message(2, new ProtoWriter().String(1, "Reading").Varint(2, 5))
            .Message(2, new ProtoWriter().String(1, "Plan").Varint(2, 9))
            .ToArray();

        var result = Read(backup);

        Assert.Equal(["Reading", "Plan"], result.Manga[0].Categories);
        Assert.Empty(result.Manga[1].Categories);
    }

    [Fact]
    public void Favourite_defaults_to_true_and_non_library_manga_are_counted()
    {
        var result = Read(Backup(
            Manga("Implicit"),
            Manga("Explicit", m => m.Bool(100, true)),
            Manga("Dropped", m => m.Bool(100, false))));

        Assert.Equal(["Implicit", "Explicit"], result.Manga.Select(m => m.Title));
        Assert.Equal(1, result.SkippedNonLibrary);
    }

    [Fact]
    public void Index_counts_every_manga_in_the_backup()
    {
        var result = Read(Backup(Manga("Dropped", m => m.Bool(100, false)), Manga("Kept")));

        Assert.Equal(1, Assert.Single(result.Manga).Index);
    }

    [Fact]
    public void Media_id_falls_back_to_the_deprecated_field()
    {
        var result = Read(Backup(Manga("A", m => m
            .Message(18, Tracker(2, 0, mediaIdInt: 321))
            .Message(18, Tracker(1, 555, mediaIdInt: 1)))));

        var manga = Assert.Single(result.Manga);
        Assert.Equal(321, manga.AniListId);
        Assert.Equal(555, manga.MalId);
    }

    [Fact]
    public void Tracker_sync_ids_map_to_services_and_unknown_ones_are_ignored()
    {
        var result = Read(Backup(Manga("A", m => m
            .Message(18, Tracker(1, 10))
            .Message(18, Tracker(2, 20))
            .Message(18, Tracker(3, 30))
            .Message(18, Tracker(11, 40))
            .Message(18, Tracker(7, 99, lastRead: 500)))));

        var manga = Assert.Single(result.Manga);
        Assert.Equal(10, manga.MalId);
        Assert.Equal(20, manga.AniListId);
        Assert.Equal(30, manga.KitsuId);
        Assert.Equal("40", manga.MangaBakaId);
        Assert.Null(manga.TrackerLastRead);
    }

    [Fact]
    public void Read_numbers_are_narrowed_distinct_sorted_and_skip_unknown()
    {
        var result = Read(Backup(Manga("A", m => m
            .Message(16, Chapter(10.1f, true))
            .Message(16, Chapter(1.5f, true))
            .Message(16, Chapter(0, true))
            .Message(16, Chapter(1.5f, true))
            .Message(16, Chapter(-1, true))
            .Message(16, Chapter(7, false)))));

        var manga = Assert.Single(result.Manga);
        Assert.Equal([0m, 1.5m, 10.1m], manga.ReadNumbers);
        Assert.Equal(6, manga.ChapterCount);
        Assert.Equal(5, manga.ReadCount);
    }

    [Fact]
    public void A_read_chapter_without_a_number_field_is_chapter_zero()
    {
        var unnumbered = new ProtoWriter().String(1, "/c").String(2, "Prologue").Bool(4, true);

        var manga = Assert.Single(Read(Backup(Manga("A", m => m
            .Message(16, unnumbered)
            .Message(16, Chapter(-1, true))))).Manga);

        Assert.Equal([0m], manga.ReadNumbers);
        Assert.Equal(2, manga.ReadCount);
    }

    [Fact]
    public void A_backup_in_a_seekable_memory_stream_reads_from_its_position()
    {
        var data = Backup(Manga("Berserk"));
        using var stream = new MemoryStream();
        stream.Write([0x00, 0x00]);
        stream.Write(data);
        stream.Position = 2;

        Assert.Equal("Berserk", Assert.Single(MihonBackupReader.Read(stream).Manga).Title);
    }

    [Fact]
    public void Tracker_progress_is_used_only_when_the_manga_has_no_chapter_rows()
    {
        var result = Read(Backup(
            Manga("Bare", m => m
                .Message(18, Tracker(1, 10, lastRead: 12.5f))
                .Message(18, Tracker(2, 20, lastRead: 40))),
            Manga("WithChapters", m => m
                .Message(16, Chapter(1, false))
                .Message(18, Tracker(2, 20, lastRead: 40)))));

        Assert.Equal(40m, result.Manga[0].TrackerLastRead);
        Assert.Null(result.Manga[1].TrackerLastRead);
    }

    [Fact]
    public void Fork_fields_and_retired_numbers_are_skipped()
    {
        var chapter = Chapter(3, true)
            .Varint(11, 7)
            .String(12, "retired");
        var backup = new ProtoWriter()
            .Message(1, Manga("A", m => m
                .Varint(106, 5)
                .String(109, "retired")
                .Varint(600, 1)
                .String(800, "fork")
                .Fixed64(900, 123)
                .Float(901, 2.5f)
                .Message(16, chapter)
                .Message(18, Tracker(2, 20))))
            .Varint(3, 99)
            .String(100, "preferences")
            .ToArray();

        var manga = Assert.Single(Read(backup).Manga);

        Assert.Equal("A", manga.Title);
        Assert.Equal([3m], manga.ReadNumbers);
        Assert.Equal(20, manga.AniListId);
    }

    [Fact]
    public void Negative_int64_varints_decode_as_ten_bytes()
    {
        var backup = new ProtoWriter()
            .Message(1, Manga("A", m => m.Varint(1, -5).Varint(17, -3)))
            .Message(2, new ProtoWriter().String(1, "Neg").Varint(2, -3))
            .ToArray();

        Assert.Equal(["Neg"], Assert.Single(Read(backup).Manga).Categories);
    }
}
