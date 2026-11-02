using System.Text.Json;
using Maki.Api.Controllers;
using Maki.Api.Json;
using Maki.Api.Services;
using Maki.Core.Configuration;
using Maki.Core.Entities;
using Maki.Core.Recommendations;
using Maki.Core.Security;
using Maki.Metadata.Catalogue;
using Maki.Metadata.Embedding;
using Maki.Metadata.MangaBaka;
using Maki.Metadata.Tests;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Maki.Api.Tests;

/// <summary>
/// The shortlist endpoints: saving a catalogue title for later with nothing else happening, read back
/// filtered the same way by the list and the ids.
/// </summary>
public sealed class ShortlistControllerTests : IDisposable
{
    private const int Frieren = 100;
    private const int Spicy = 101;
    private const int Unrated = 102;
    private const int Removed = 103;
    private const int Novel = 104;
    private const int MergedIntoFrieren = 105;
    private const int Horror = 106;
    private const int MergedIntoSpicy = 107;

    private readonly TestDb _db = new();
    private readonly DumpDbBuilder _dump = new();
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "maki-shortlist-" + Guid.NewGuid().ToString("N"));
    private readonly List<IDisposable> _owned = [];
    private readonly int _reader;
    private readonly int _other;

    public ShortlistControllerTests()
    {
        Directory.CreateDirectory(_dir);
        _reader = _db.SeedUser("reader");
        _other = _db.SeedUser("other");
        _dump.AddSeries(Frieren, "Frieren", status: "releasing", totalChapters: "120", rating: 90,
                genresJson: """["Fantasy"]""")
            .AddSeries(Spicy, "Spicy", contentRating: "erotica", status: "releasing")
            .AddSeries(Unrated, "Unrated", contentRating: null)
            .AddSeries(Removed, "Removed", state: "deleted")
            .AddSeries(Novel, "Novel", type: "novel")
            .AddSeries(MergedIntoFrieren, "Old Frieren", state: "merged", mergedWith: "100", contentRating: null)
            .AddSeries(Horror, "Horror", genresJson: """["Horror"]""")
            .AddSeries(MergedIntoSpicy, "Old Spicy", state: "merged", mergedWith: "101", contentRating: null);
    }

    public void Dispose()
    {
        foreach (var owned in _owned)
        {
            owned.Dispose();
        }

        SqliteConnection.ClearAllPools();
        _dump.Dispose();
        _db.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private MangaBakaDumpOptions DumpOptions => new(_dump.Path, _dir);

    private ShortlistController Controller(
        int userId, string ceiling = "erotica", string? hidden = null, bool allRootFolders = true)
    {
        var db = _db.NewContext(userId, allRootFolders);
        _owned.Add(db);
        var store = new MangaBakaLocalStore(DumpOptions, NullLogger<MangaBakaLocalStore>.Instance);
        var hiddenContent = new HiddenContentService(
            new Settings(userId, hidden),
            new VectorIndexCache(
                new EmbeddingOptions(_dir, Path.Combine(_dir, "none.db"), _dir, EmbeddingModelProfile.Base),
                DumpOptions, NullLogger<VectorIndexCache>.Instance),
            new CatalogueIndexCache(DumpOptions, NullLogger<CatalogueIndexCache>.Instance),
            store);
        return new ShortlistController(
            new ShortlistService(db, store, new Reader(userId, ceiling, allRootFolders), hiddenContent),
            new TestLocalizer());
    }

    private void Store(int userId, int mangaBakaId, Action<ShortlistEntry>? configure = null)
    {
        using var db = _db.NewContext();
        var entry = new ShortlistEntry
        {
            UserId = userId, MangaBakaId = mangaBakaId, Title = $"Stored {mangaBakaId}", SavedFrom = "other",
            CreatedAtUtc = DateTime.UtcNow, UpdatedAtUtc = DateTime.UtcNow,
        };
        configure?.Invoke(entry);
        db.ShortlistEntries.Add(entry);
        db.SaveChanges();
    }

    private List<ShortlistEntry> Rows(int userId)
    {
        using var db = _db.NewContext();
        return db.ShortlistEntries.AsNoTracking().Where(x => x.UserId == userId).OrderBy(x => x.Id).ToList();
    }

    private void UpdateDump(string sql)
    {
        using var conn = _dump.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private static T Ok<T>(IActionResult result) => Assert.IsType<T>(Assert.IsType<OkObjectResult>(result).Value);

    private static string? Code(IActionResult result) =>
        JsonSerializer.SerializeToElement(Assert.IsAssignableFrom<ObjectResult>(result).Value)
            .GetProperty("code").GetString();

    private static async Task<IReadOnlyList<ShortlistListItem>> ListOf(ShortlistController c) =>
        Ok<ShortlistList>(await c.List(default)).Entries;

    private static async Task<IReadOnlyList<int>> IdsOf(ShortlistController c) =>
        Ok<ShortlistIds>(await c.Ids(default)).Ids;

    [Fact]
    public async Task A_repeat_save_keeps_the_first_provenance_and_snapshot_and_only_changes_a_sent_note()
    {
        var first = Ok<ShortlistEntryDto>(await Controller(_reader).Save(Frieren,
            new ShortlistSaveRequest("rail:trending", null, "  later  "), default));
        Assert.Equal("Frieren", first.Title);
        Assert.Equal("later", first.Note);
        Assert.Equal(SeriesStatus.Ongoing, first.SavedStatus);
        Assert.Equal(120, first.SavedTotalChapters);

        UpdateDump($"UPDATE series SET status = 'completed', total_chapters = '140' WHERE id = {Frieren}");
        var again = Ok<ShortlistEntryDto>(await Controller(_reader).Save(Frieren,
            new ShortlistSaveRequest("search", "x"), default));
        var noted = Ok<ShortlistEntryDto>(await Controller(_reader).Save(Frieren,
            new ShortlistSaveRequest("search", null, "new note"), default));

        Assert.Equal("later", again.Note);
        Assert.Equal("new note", noted.Note);
        var row = Assert.Single(Rows(_reader));
        Assert.Equal("rail:trending", row.SavedFrom);
        Assert.Null(row.SavedFromDetail);
        Assert.Equal(SeriesStatus.Ongoing, row.SavedStatus);
        Assert.Equal(120, row.SavedTotalChapters);
        Assert.Equal(first.CreatedAtUtc, row.CreatedAtUtc);
    }

    [Theory]
    [InlineData("rail:trending", "rail:trending")]
    [InlineData("rail:popular", "rail:popular")]
    [InlineData("rail:12", "rail:12")]
    [InlineData("rail:nope", "other")]
    [InlineData("creator", "creator")]
    [InlineData("<script>", "other")]
    [InlineData(null, "other")]
    public void Provenance_outside_the_fixed_set_is_stored_as_other(string? sent, string stored) =>
        Assert.Equal(stored, ShortlistService.NormalizeSource(sent));

    [Fact]
    public async Task A_note_can_be_changed_and_cleared()
    {
        Store(_reader, Frieren, e => e.Note = "first");

        var changed = Ok<ShortlistEntryDto>(await Controller(_reader).UpdateNote(Frieren, new ShortlistNoteRequest(" second "), default));
        Assert.Equal("second", changed.Note);
        var cleared = Ok<ShortlistEntryDto>(await Controller(_reader).UpdateNote(Frieren, new ShortlistNoteRequest("   "), default));
        Assert.Null(cleared.Note);
        Assert.Null(Assert.Single(Rows(_reader)).Note);

        Assert.IsType<NotFoundResult>(await Controller(_reader).UpdateNote(Spicy, new ShortlistNoteRequest("x"), default));
    }

    [Fact]
    public async Task Delete_returns_the_entry_and_restore_puts_it_back_unchanged()
    {
        var created = new DateTime(2025, 3, 4, 5, 6, 7, DateTimeKind.Utc);
        Store(_reader, Frieren, e =>
        {
            e.Note = "for the holidays";
            e.SavedFrom = "creator";
            e.SavedFromDetail = "Kanehito Yamada";
            e.SavedStatus = SeriesStatus.Hiatus;
            e.SavedTotalChapters = 90;
            e.CreatedAtUtc = created;
        });

        var removed = Ok<ShortlistEntryDto>(await Controller(_reader).Remove(Frieren, default));
        Assert.Empty(Rows(_reader));
        Assert.IsType<NoContentResult>(await Controller(_reader).Remove(Frieren, default));

        var restored = Ok<ShortlistEntryDto>(await Controller(_reader).Restore(Frieren, removed, default));

        Assert.Equal(removed, restored);
        var row = Assert.Single(Rows(_reader));
        Assert.Equal(("for the holidays", "creator", "Kanehito Yamada"), (row.Note, row.SavedFrom, row.SavedFromDetail));
        Assert.Equal((SeriesStatus.Hiatus, 90), (row.SavedStatus!.Value, row.SavedTotalChapters!.Value));
        Assert.Equal(created, row.CreatedAtUtc);
        // The change it was carrying survives the round trip.
        Assert.NotNull(Assert.Single(await ListOf(Controller(_reader))).StatusChange);
    }

    [Fact]
    public async Task Restore_validates_every_field_like_a_save()
    {
        var body = new ShortlistEntryDto(
            MangaBakaId: 999, Title: new string('t', 400), Note: " " + new string('n', 600),
            SavedFrom: "javascript:alert(1)", SavedFromDetail: new string('d', 250),
            SavedStatus: (SeriesStatus)77, SavedTotalChapters: -3, CreatedAtUtc: DateTime.UtcNow.AddYears(5));

        var restored = Ok<ShortlistEntryDto>(await Controller(_reader).Restore(Frieren, body, default));

        Assert.Equal(Frieren, restored.MangaBakaId);
        Assert.Equal(ShortlistEntry.TitleMaxLength, restored.Title.Length);
        Assert.Equal(ShortlistEntry.NoteMaxLength, restored.Note!.Length);
        Assert.Equal("other", restored.SavedFrom);
        Assert.Equal(ShortlistEntry.SavedFromDetailMaxLength, restored.SavedFromDetail!.Length);
        Assert.Null(restored.SavedStatus);
        Assert.Null(restored.SavedTotalChapters);
        Assert.True(restored.CreatedAtUtc <= DateTime.UtcNow);
        Assert.Equal(_reader, Assert.Single(Rows(_reader)).UserId);
    }

    [Fact]
    public async Task Restore_over_an_existing_entry_returns_it_and_writes_nothing()
    {
        Store(_reader, Frieren, e => e.Note = "kept");
        var before = Assert.Single(Rows(_reader));

        var answer = Ok<ShortlistEntryDto>(await Controller(_reader).Restore(Frieren,
            new ShortlistEntryDto(Frieren, "x", "replaced", "search", null, null, null, DateTime.UtcNow), default));

        Assert.Equal("kept", answer.Note);
        var after = Assert.Single(Rows(_reader));
        Assert.Equal((before.Note, before.SavedFrom, before.UpdatedAtUtc), (after.Note, after.SavedFrom, after.UpdatedAtUtc));
    }

    [Fact]
    public async Task Restore_answers_like_a_save_for_an_id_the_reader_may_not_have()
    {
        var body = new ShortlistEntryDto(Spicy, "Spicy", null, "other", null, null, null, DateTime.UtcNow);

        Assert.Equal("error.shortlist.notInCatalogue", Code(await Controller(_reader, ceiling: "suggestive").Restore(Spicy, body, default)));
        _db.SeedSeries("Spicy", configure: s => s.MangaBakaId = Spicy);
        Assert.Equal("error.shortlist.inLibrary", Code(await Controller(_reader).Restore(Spicy, body, default)));
    }

    [Fact]
    public async Task Another_users_entries_are_invisible_and_untouchable()
    {
        Store(_other, Frieren, e => e.Note = "theirs");

        var mine = Controller(_reader);
        Assert.Empty(await IdsOf(mine));
        Assert.Empty(await ListOf(mine));
        Assert.IsType<NoContentResult>(await Controller(_reader).Remove(Frieren, default));
        Assert.IsType<NotFoundResult>(await Controller(_reader).UpdateNote(Frieren, new ShortlistNoteRequest("mine"), default));
        Assert.IsType<NotFoundResult>(await Controller(_reader).Acknowledge(Frieren, default));

        var saved = Ok<ShortlistEntryDto>(await Controller(_reader).Save(Frieren, new ShortlistSaveRequest("search"), default));
        Assert.Null(saved.Note);
        Assert.Equal("theirs", Assert.Single(Rows(_other)).Note);
    }

    [Fact]
    public async Task Saving_a_title_that_is_in_the_library_in_any_form_is_a_conflict()
    {
        var restricted = _db.SeedUser("restricted", MakiPermission.None, allRootFolders: false);
        _db.SeedSeries("Frieren", configure: s => s.MangaBakaId = Frieren);
        _db.SeedSeries("Horror", configure: s =>
        {
            s.MangaBakaId = Horror;
            s.ArchivedAt = DateTime.UtcNow;
        });

        Assert.Equal("error.shortlist.inLibrary", Code(await Controller(_reader).Save(Frieren, new("search"), default)));
        Assert.Equal("error.shortlist.inLibrary",
            Code(await Controller(restricted, allRootFolders: false).Save(Frieren, new("search"), default)));
        Assert.Equal("error.shortlist.inLibrary", Code(await Controller(_reader).Save(Horror, new("search"), default)));
        Assert.IsType<ConflictObjectResult>(await Controller(_reader).Save(Horror, new("search"), default));
        // A merged id whose canonical work is owned is owned too.
        Assert.Equal("error.shortlist.inLibrary", Code(await Controller(_reader).Save(MergedIntoFrieren, new("search"), default)));
        Assert.Empty(Rows(_reader));
    }

    [Fact]
    public async Task The_list_says_where_a_library_copy_is_and_whether_the_caller_can_see_it()
    {
        var restricted = _db.SeedUser("restricted", MakiPermission.None, allRootFolders: false);
        foreach (var user in new[] { _reader, restricted })
        {
            Store(user, Frieren);
            Store(user, Horror);
            Store(user, Unrated);
        }

        var frieren = _db.SeedSeries("Frieren", configure: s => s.MangaBakaId = Frieren);
        _db.SeedSeries("Horror", configure: s =>
        {
            s.MangaBakaId = Horror;
            s.ArchivedAt = DateTime.UtcNow;
        });

        var mine = (await ListOf(Controller(_reader, ceiling: "pornographic"))).ToDictionary(x => x.MangaBakaId);
        Assert.Equal(new ShortlistLibrary(frieren, false), mine[Frieren].Library);
        Assert.True(mine[Horror].Library!.Archived);
        Assert.NotNull(mine[Horror].Library!.SeriesId);
        Assert.Null(mine[Unrated].Library);

        var theirs = (await ListOf(Controller(restricted, ceiling: "pornographic", allRootFolders: false)))
            .ToDictionary(x => x.MangaBakaId);
        Assert.Equal(new ShortlistLibrary(null, false), theirs[Frieren].Library);
        Assert.Equal(new ShortlistLibrary(null, true), theirs[Horror].Library);
    }

    [Fact]
    public async Task The_cap_refuses_one_more()
    {
        using (var db = _db.NewContext())
        {
            db.ShortlistEntries.AddRange(Enumerable.Range(0, ShortlistEntry.MaxPerUser).Select(i => new ShortlistEntry
            {
                UserId = _reader, MangaBakaId = 1_000_000 + i, Title = "x", SavedFrom = "other",
                CreatedAtUtc = DateTime.UtcNow, UpdatedAtUtc = DateTime.UtcNow,
            }));
            db.SaveChanges();
        }

        var result = await Controller(_reader).Save(Frieren, new("search"), default);

        Assert.IsType<ConflictObjectResult>(result);
        Assert.Equal("error.shortlist.full", Code(result));
        Assert.Equal(ShortlistEntry.MaxPerUser, Rows(_reader).Count);
        // Someone else's room is their own.
        Assert.IsType<OkObjectResult>(await Controller(_other).Save(Frieren, new("search"), default));
    }

    [Theory]
    [InlineData(Spicy, "suggestive")]
    [InlineData(Unrated, "erotica")]
    [InlineData(Removed, "erotica")]
    [InlineData(Novel, "erotica")]
    [InlineData(999_999, "erotica")]
    public async Task An_id_the_reader_cannot_have_is_one_not_found(int id, string ceiling)
    {
        var result = await Controller(_reader, ceiling).Save(id, new("search"), default);

        Assert.IsType<NotFoundObjectResult>(result);
        Assert.Equal("error.shortlist.notInCatalogue", Code(result));
        Assert.Empty(Rows(_reader));
    }

    [Fact]
    public async Task A_non_positive_route_id_is_not_found()
    {
        Assert.IsType<NotFoundResult>(await Controller(_reader).Save(0, new("search"), default));
        Assert.IsType<NotFoundResult>(await Controller(_reader).Save(-4, new("search"), default));
        Assert.IsType<NotFoundResult>(await Controller(_reader).Restore(0,
            new ShortlistEntryDto(0, "x", null, "other", null, null, null, DateTime.UtcNow), default));
    }

    [Fact]
    public async Task The_ceiling_and_the_never_show_list_filter_both_reads_alike_and_delete_nothing()
    {
        foreach (var id in new[] { Frieren, Spicy, Horror, Unrated })
        {
            Store(_reader, id);
        }

        const string hidden = """{"terms":[{"kind":"genre","name":"Horror"}]}""";
        var narrow = Controller(_reader, ceiling: "suggestive", hidden: hidden);

        Assert.Equal([Frieren], (await ListOf(narrow)).Select(x => x.MangaBakaId));
        Assert.Equal([Frieren], await IdsOf(narrow));
        Assert.Equal(4, Rows(_reader).Count);

        var wide = Controller(_reader, ceiling: "pornographic");
        Assert.Equal([Frieren, Spicy, Unrated, Horror], (await IdsOf(wide)).Order());
    }

    [Fact]
    public async Task A_merged_entry_hydrates_from_its_canonical_row_without_writing()
    {
        Store(_reader, MergedIntoFrieren, e => e.SavedStatus = SeriesStatus.Ongoing);
        var before = Assert.Single(Rows(_reader));

        var controller = Controller(_reader);
        var item = Assert.Single(await ListOf(controller));
        Assert.Equal(MergedIntoFrieren, item.MangaBakaId);
        Assert.Equal(Frieren.ToString(), item.Item!.ProviderId);
        Assert.Equal("Frieren", item.Item.Title);
        Assert.Equal($"Stored {MergedIntoFrieren}", item.Title);
        var ids = Ok<ShortlistIds>(await controller.Ids(default));
        Assert.Equal([Frieren, MergedIntoFrieren], ids.Ids.Order());
        Assert.Equal(1, ids.Count);

        var after = Assert.Single(Rows(_reader));
        Assert.Equal((before.MangaBakaId, before.UpdatedAtUtc), (after.MangaBakaId, after.UpdatedAtUtc));
    }

    [Fact]
    public async Task A_removed_row_and_a_novel_hydrate_to_no_item_and_keep_their_stored_title()
    {
        Store(_reader, Removed);
        Store(_reader, Novel);
        Store(_reader, 999_999);

        var list = await ListOf(Controller(_reader));

        Assert.Equal(3, list.Count);
        Assert.All(list, x => Assert.Null(x.Item));
        Assert.All(list, x => Assert.Equal((null, false), (x.Type, x.HasAnime)));
        Assert.Equal(
            [$"Stored {Removed}", $"Stored {Novel}", "Stored 999999"],
            list.OrderBy(x => x.MangaBakaId).Select(x => x.Title));
        Assert.Equal([Removed, Novel, 999_999], (await IdsOf(Controller(_reader))).Order());
    }

    [Fact]
    public async Task Without_a_dump_the_list_says_so_and_sends_no_entries()
    {
        Store(_reader, Frieren);
        Store(_reader, Spicy);
        SqliteConnection.ClearAllPools();
        File.Delete(_dump.Path);

        var list = Ok<ShortlistList>(await Controller(_reader, ceiling: "safe").List(default));
        var ids = Ok<ShortlistIds>(await Controller(_reader, ceiling: "safe").Ids(default));

        Assert.False(list.CatalogueAvailable);
        Assert.Empty(list.Entries);
        Assert.Equal((0, 0), (ids.Ids.Count, ids.Count));
        Assert.Equal(2, Rows(_reader).Count);
    }

    [Fact]
    public async Task With_a_dump_the_list_says_the_catalogue_is_available_even_when_empty()
    {
        var list = Ok<ShortlistList>(await Controller(_reader).List(default));

        Assert.True(list.CatalogueAvailable);
        Assert.Empty(list.Entries);
    }

    [Fact]
    public async Task Type_and_the_anime_flag_come_from_the_canonical_row()
    {
        UpdateDump($"UPDATE series SET type = 'manhwa', anime_start = '1' WHERE id = {Frieren}");
        UpdateDump($"UPDATE series SET type = 'oel' WHERE id = {MergedIntoFrieren}");
        UpdateDump($"UPDATE series SET anime_start = '1' WHERE id = {MergedIntoSpicy}");
        Store(_reader, MergedIntoFrieren);
        Store(_reader, MergedIntoSpicy);
        Store(_reader, Horror);
        Store(_reader, Removed);

        var list = (await ListOf(Controller(_reader))).ToDictionary(x => x.MangaBakaId);

        Assert.Equal(("manhwa", true), (list[MergedIntoFrieren].Type, list[MergedIntoFrieren].HasAnime));
        Assert.Equal(("manga", false), (list[MergedIntoSpicy].Type, list[MergedIntoSpicy].HasAnime));
        Assert.Equal(("manga", false), (list[Horror].Type, list[Horror].HasAnime));
        Assert.Equal((null, false), (list[Removed].Type, list[Removed].HasAnime));
    }

    [Fact]
    public async Task The_count_is_one_per_visible_entry()
    {
        Store(_reader, MergedIntoFrieren);
        Store(_reader, Horror);
        Store(_reader, Spicy);
        Store(_reader, Removed);

        var ids = Ok<ShortlistIds>(await Controller(_reader, ceiling: "safe").Ids(default));

        Assert.Equal([Frieren, Removed, MergedIntoFrieren, Horror], ids.Ids.Order());
        Assert.Equal(3, ids.Count);
    }

    [Fact]
    public async Task Writes_by_the_canonical_id_reach_an_entry_stored_under_a_merged_id()
    {
        Store(_reader, MergedIntoFrieren, e =>
        {
            e.Note = "old";
            e.SavedStatus = SeriesStatus.Hiatus;
            e.SavedTotalChapters = 10;
        });

        var saved = Ok<ShortlistEntryDto>(await Controller(_reader).Save(Frieren, new("search", null, "put"), default));
        Assert.Equal(MergedIntoFrieren, saved.MangaBakaId);
        Assert.Equal("put", Assert.Single(Rows(_reader)).Note);

        Ok<ShortlistEntryDto>(await Controller(_reader).UpdateNote(Frieren, new ShortlistNoteRequest("patched"), default));
        Assert.Equal("patched", Assert.Single(Rows(_reader)).Note);

        var restored = Ok<ShortlistEntryDto>(await Controller(_reader).Restore(Frieren,
            new ShortlistEntryDto(Frieren, "x", "restored", "search", null, null, null, DateTime.UtcNow), default));
        Assert.Equal((MergedIntoFrieren, "patched"), (restored.MangaBakaId, restored.Note));

        Ok<ShortlistEntryDto>(await Controller(_reader).Acknowledge(Frieren, default));
        var acknowledged = Assert.Single(Rows(_reader));
        Assert.Equal((SeriesStatus.Ongoing, 120), (acknowledged.SavedStatus!.Value, acknowledged.SavedTotalChapters!.Value));

        var removed = Ok<ShortlistEntryDto>(await Controller(_reader).Remove(Frieren, default));
        Assert.Equal(MergedIntoFrieren, removed.MangaBakaId);
        Assert.Empty(Rows(_reader));
    }

    [Fact]
    public async Task A_merged_entry_follows_its_canonical_rows_rating_not_the_pointers()
    {
        Store(_reader, MergedIntoFrieren);
        Store(_reader, MergedIntoSpicy);

        var narrow = await ListOf(Controller(_reader, ceiling: "suggestive"));
        Assert.Equal([MergedIntoFrieren], narrow.Select(x => x.MangaBakaId));
        Assert.Equal(2, Rows(_reader).Count);

        Assert.Equal(2, (await ListOf(Controller(_reader, ceiling: "erotica"))).Count);
    }

    [Fact]
    public async Task A_status_change_is_reported_chapter_growth_is_a_count_and_got_it_resets_both()
    {
        Ok<ShortlistEntryDto>(await Controller(_reader).Save(Frieren, new("search"), default));
        UpdateDump($"UPDATE series SET total_chapters = '134' WHERE id = {Frieren}");

        var grown = Assert.Single(await ListOf(Controller(_reader)));
        Assert.Null(grown.StatusChange);
        Assert.Equal(14, grown.NewChapters);

        UpdateDump($"UPDATE series SET status = 'completed' WHERE id = {Frieren}");
        var finished = Assert.Single(await ListOf(Controller(_reader)));
        Assert.Equal(new ShortlistStatusChange(SeriesStatus.Ongoing, SeriesStatus.Completed), finished.StatusChange);
        Assert.Equal(14, finished.NewChapters);

        Ok<ShortlistEntryDto>(await Controller(_reader).Acknowledge(Frieren, default));
        var settled = Assert.Single(await ListOf(Controller(_reader)));
        Assert.Null(settled.StatusChange);
        Assert.Null(settled.NewChapters);
    }

    [Fact]
    public async Task The_list_serializes_to_the_contract_the_client_reads()
    {
        Ok<ShortlistEntryDto>(await Controller(_reader).Save(Frieren, new("rail:trending", null, "n"), default));
        UpdateDump($"UPDATE series SET status = 'completed', total_chapters = '130' WHERE id = {Frieren}");
        _db.SeedSeries("Frieren", configure: s => s.MangaBakaId = Frieren);
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        MakiJson.ApplyConverters(options);

        var body = JsonSerializer.SerializeToElement(
            Assert.IsType<OkObjectResult>(await Controller(_reader).List(default)).Value, options);
        Assert.True(body.GetProperty("catalogueAvailable").GetBoolean());
        var json = body.GetProperty("entries")[0];

        Assert.Equal(Frieren, json.GetProperty("mangaBakaId").GetInt32());
        Assert.Equal("Frieren", json.GetProperty("title").GetString());
        Assert.Equal("manga", json.GetProperty("type").GetString());
        Assert.False(json.GetProperty("hasAnime").GetBoolean());
        Assert.Equal("100", json.GetProperty("item").GetProperty("providerId").GetString());
        Assert.Equal("Completed", json.GetProperty("item").GetProperty("status").GetString());
        Assert.Equal("n", json.GetProperty("note").GetString());
        Assert.Equal("rail:trending", json.GetProperty("savedFrom").GetString());
        Assert.Equal(JsonValueKind.Null, json.GetProperty("savedFromDetail").ValueKind);
        Assert.EndsWith("Z", json.GetProperty("createdAtUtc").GetString());
        Assert.Equal("Ongoing", json.GetProperty("statusChange").GetProperty("from").GetString());
        Assert.Equal("Completed", json.GetProperty("statusChange").GetProperty("to").GetString());
        Assert.Equal(10, json.GetProperty("newChapters").GetInt32());
        Assert.True(json.GetProperty("library").GetProperty("seriesId").GetInt32() > 0);
        Assert.False(json.GetProperty("library").GetProperty("archived").GetBoolean());

        var ids = JsonSerializer.SerializeToElement(
            Assert.IsType<OkObjectResult>(await Controller(_reader).Ids(default)).Value, options);
        Assert.Equal([Frieren], ids.GetProperty("ids").EnumerateArray().Select(x => x.GetInt32()));
        Assert.Equal(1, ids.GetProperty("count").GetInt32());

        var removed = JsonSerializer.SerializeToElement(
            Assert.IsType<OkObjectResult>(await Controller(_reader).Remove(Frieren, default)).Value, options);
        Assert.Equal(
            ["createdAtUtc", "mangaBakaId", "note", "savedFrom", "savedFromDetail", "savedStatus", "savedTotalChapters", "title"],
            removed.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));
        Assert.Equal("Ongoing", removed.GetProperty("savedStatus").GetString());
    }

    [Fact]
    public async Task A_shortlist_write_moves_neither_the_seed_snapshot_nor_the_feedback_revision()
    {
        _db.SeedSeries("Horror", configure: s => s.MangaBakaId = Horror);
        var seeds = new SeedWeightService(
            new BehavioralTasteService(TasteTuning.Default), TasteTuning.Default, new FakeAppSettings());

        async Task<(string, long, long, int)> StateAsync()
        {
            using var db = _db.NewContext(_reader);
            var snapshot = await seeds.SnapshotAsync(db, new TestCurrentUser(_reader));
            var versions = await RecommendationFeedbackService.VersionsAsync(db, _reader);
            var suppressed = await RecommendationFeedbackService.SuppressedAsync(db, _reader);
            return (snapshot.Fingerprint(), versions.FeedbackRevision, versions.SignalRevision, suppressed.Count);
        }

        var before = await StateAsync();
        Ok<ShortlistEntryDto>(await Controller(_reader).Save(Frieren, new("search", null, "n"), default));
        Ok<ShortlistEntryDto>(await Controller(_reader).Acknowledge(Frieren, default));

        Assert.Equal(before, await StateAsync());
    }

    [Fact]
    public async Task The_explicit_user_insert_applies_that_users_ceiling_whatever_the_scope()
    {
        using var unrestricted = _db.NewContext();
        var store = new MangaBakaLocalStore(DumpOptions, NullLogger<MangaBakaLocalStore>.Instance);
        var service = new ShortlistService(unrestricted, store, new Reader(0, "pornographic", true), null!);

        var refused = await service.InsertAsync(_reader, ContentRating.Allowed("safe"), Spicy, "importlist", "AniList");
        var taken = await service.InsertAsync(_reader, ContentRating.Allowed("safe"), Frieren, "importlist", "AniList");
        var again = await service.InsertAsync(_reader, ContentRating.Allowed("safe"), Frieren, "importlist", "MAL");

        Assert.Equal(ShortlistInsertOutcome.NotInCatalogue, refused.Outcome);
        Assert.Equal(ShortlistInsertOutcome.Inserted, taken.Outcome);
        Assert.Equal(ShortlistInsertOutcome.Existing, again.Outcome);
        var row = Assert.Single(Rows(_reader));
        Assert.Equal(("importlist", "AniList"), (row.SavedFrom, row.SavedFromDetail));
        Assert.Empty(Rows(_other));
    }

    [Fact]
    public async Task The_explicit_user_insert_finds_an_entry_stored_under_a_merged_id()
    {
        Store(_reader, MergedIntoFrieren);
        using var unrestricted = _db.NewContext();
        var store = new MangaBakaLocalStore(DumpOptions, NullLogger<MangaBakaLocalStore>.Instance);
        var service = new ShortlistService(unrestricted, store, new Reader(0, "pornographic", true), null!);

        var resolved = await service.InsertAsync(_reader, ContentRating.All, Frieren, "importlist", "AniList");
        var given = await service.InsertAsync(_reader, ContentRating.All, Frieren, "importlist", "AniList",
            mergedKeys: new Dictionary<int, int> { [MergedIntoFrieren] = Frieren });

        Assert.Equal((ShortlistInsertOutcome.Existing, MergedIntoFrieren), (resolved.Outcome, resolved.Entry!.MangaBakaId));
        Assert.Equal((ShortlistInsertOutcome.Existing, MergedIntoFrieren), (given.Outcome, given.Entry!.MangaBakaId));
        Assert.Equal(MergedIntoFrieren, Assert.Single(Rows(_reader)).MangaBakaId);
        Assert.Equal(new Dictionary<int, int> { [MergedIntoFrieren] = Frieren },
            await service.MergedKeysAsync(_reader));
    }

    [Fact]
    public async Task One_run_map_sees_a_merged_id_inserted_earlier_in_the_run()
    {
        using var unrestricted = _db.NewContext();
        var store = new MangaBakaLocalStore(DumpOptions, NullLogger<MangaBakaLocalStore>.Instance);
        var service = new ShortlistService(unrestricted, store, new Reader(0, "pornographic", true), null!);
        var runMap = await service.MergedKeysAsync(_reader);

        var first = await service.InsertAsync(_reader, ContentRating.All, MergedIntoFrieren, "importlist", "AniList",
            mergedKeys: runMap);
        var second = await service.InsertAsync(_reader, ContentRating.All, Frieren, "importlist", "AniList",
            mergedKeys: runMap);

        Assert.Equal(ShortlistInsertOutcome.Inserted, first.Outcome);
        Assert.Equal((ShortlistInsertOutcome.Existing, MergedIntoFrieren), (second.Outcome, second.Entry!.MangaBakaId));
        Assert.Equal(MergedIntoFrieren, Assert.Single(Rows(_reader)).MangaBakaId);
    }

    [Fact]
    public async Task Inserting_a_merged_id_finds_the_canonical_entry()
    {
        Store(_reader, Frieren);
        using var unrestricted = _db.NewContext();
        var store = new MangaBakaLocalStore(DumpOptions, NullLogger<MangaBakaLocalStore>.Instance);
        var service = new ShortlistService(unrestricted, store, new Reader(0, "pornographic", true), null!);

        var result = await service.InsertAsync(_reader, ContentRating.All, MergedIntoFrieren, "importlist", "AniList");

        Assert.Equal((ShortlistInsertOutcome.Existing, Frieren), (result.Outcome, result.Entry!.MangaBakaId));
        Assert.Equal(Frieren, Assert.Single(Rows(_reader)).MangaBakaId);
    }

    private sealed class Reader(int userId, string ceiling, bool allRootFolders) : ICurrentUser
    {
        public bool IsAuthenticated => true;
        public int UserId => userId;
        public string UserName => "reader";
        public MakiPermission Permissions => MakiPermission.None;
        public bool AllRootFolders => allRootFolders;
        public IReadOnlySet<int> RootFolderIds => new HashSet<int>();
        public string MaxContentRating => ceiling;
    }

    private sealed class Settings(int userId, string? hidden) : IUserSettings
    {
        public int UserId => userId;

        public Task<string?> GetAsync(string key, CancellationToken ct = default) =>
            Task.FromResult(key == SettingKeys.DiscoverHidden ? hidden : null);

        public Task<Dictionary<string, string>> GetManyAsync(
            IReadOnlyCollection<string> keys, CancellationToken ct = default) =>
            Task.FromResult(new Dictionary<string, string>());

        public Task SetAsync(string key, string? value, CancellationToken ct = default) => Task.CompletedTask;
    }
}
