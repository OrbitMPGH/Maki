using Maki.Api.Controllers;
using Maki.Api.Hubs;
using Maki.Api.Services;
using Maki.Core.Entities;
using Maki.Core.Inbox;
using Maki.Core.Security;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Maki.Api.Tests;

/// <summary>Who sees which note, who may change it, and what replies and deletes leave behind.</summary>
public sealed class NotesControllerTests : IDisposable
{
    private readonly TestDb _db = new TestDb().WithDistinctSeriesIds();
    private readonly RecordingInbox _inbox = new();
    private readonly NotesBroadcaster _events = new();
    private readonly FakeAppSettings _settings = new();
    private readonly int _alice;
    private readonly int _bob;
    private readonly int _carol;
    private readonly int _admin;
    private readonly int _seriesId;
    private readonly int _chapterId;

    public NotesControllerTests()
    {
        _alice = _db.SeedUser("alice", MakiPermission.None);
        _bob = _db.SeedUser("bob", MakiPermission.None, configure: u => u.DisplayName = "Bobby");
        _carol = _db.SeedUser("carol", MakiPermission.None);
        _admin = _db.SeedUser("admin", MakiPermission.Admin);
        _seriesId = _db.SeedSeries();
        using var db = _db.NewContext();
        var chapter = new Chapter { SeriesId = _seriesId, Number = 12, Language = "en" };
        db.Chapters.Add(chapter);
        db.SaveChanges();
        _chapterId = chapter.Id;
    }

    public void Dispose() => _db.Dispose();

    private NotesController As(int userId, MakiPermission permissions = MakiPermission.None, bool allRootFolders = true)
    {
        var name = userId == _admin ? "admin" : userId == _alice ? "alice" : userId == _bob ? "bob" : "carol";
        return new NotesController(
            new TestLocalizer(),
            _db.NewContext(userId, allRootFolders),
            new TestCurrentUser(userId, name, permissions, allRootFolders: allRootFolders),
            _inbox,
            _events,
            _settings,
            NullLogger<NotesController>.Instance);
    }

    private NotesController Admin() => As(_admin, MakiPermission.Admin);

    private static int? Status(IActionResult result) => (result as IStatusCodeActionResult)?.StatusCode;

    private static T Body<T>(IActionResult result) => (T)((ObjectResult)result).Value!;

    private async Task<NoteDto> CreateAsync(int userId, string visibility, int? page = null, string body = "a theory") =>
        Body<NoteDto>(await As(userId).Create(_chapterId, new CreateNoteRequest(body, page, visibility), default));

    private async Task<NoteDto> ReplyAsync(int userId, int rootId, string body = "an answer") =>
        Body<NoteDto>(await As(userId).Reply(rootId, new ReplyNoteRequest(body), default));

    private async Task<List<NoteDto>> ListAsync(NotesController controller) =>
        Body<List<NoteDto>>(await controller.List(_chapterId, default));

    [Fact]
    public async Task A_private_note_does_not_exist_for_anyone_else_admins_included()
    {
        var note = await CreateAsync(_alice, "private");

        Assert.Single(await ListAsync(As(_alice)));
        Assert.Empty(await ListAsync(As(_bob)));
        Assert.Empty(await ListAsync(Admin()));
        Assert.Equal(404, Status(await As(_bob).Update(note.Id, new UpdateNoteRequest("mine now", null, null, null), default)));
        Assert.Equal(404, Status(await As(_bob).Reply(note.Id, new ReplyNoteRequest("hi"), default)));
        Assert.Equal(404, Status(await As(_bob).Delete(note.Id, default)));
        Assert.Equal(404, Status(await Admin().Delete(note.Id, default)));
        Assert.Empty(_events.Changed);
    }

    [Fact]
    public async Task A_shared_note_on_an_ungranted_root_folder_is_invisible()
    {
        await CreateAsync(_alice, "shared");
        var restricted = _db.SeedUser("restricted", MakiPermission.None, allRootFolders: false);

        Assert.Equal(404, Status(await As(restricted, allRootFolders: false).List(_chapterId, default)));
        Assert.Empty(Body<List<NoteCountDto>>(await As(restricted, allRootFolders: false).Counts(_seriesId, default)));
        using (var db = _db.NewContext(restricted, allRootFolders: false))
        {
            Assert.Empty(db.ChapterNotes);
        }

        Assert.Single(await ListAsync(As(_bob)));
        Assert.Single(_events.Changed);
    }

    [Fact]
    public async Task A_visible_shared_note_someone_else_may_not_change_is_forbidden()
    {
        var note = await CreateAsync(_alice, "shared");

        Assert.Equal(403, Status(await As(_bob).Update(note.Id, new UpdateNoteRequest("edited", null, null, null), default)));
        Assert.Equal(403, Status(await As(_bob).Delete(note.Id, default)));
        var seen = Assert.Single(await ListAsync(As(_bob)));
        Assert.False(seen.CanEdit);
        Assert.False(seen.CanDelete);
        Assert.True(Assert.Single(await ListAsync(Admin())).CanDelete);
    }

    [Fact]
    public async Task Bodies_and_pages_are_validated()
    {
        Assert.Equal(400, Status(await As(_alice).Create(_chapterId, new CreateNoteRequest("   ", null, null), default)));
        Assert.Equal(400, Status(await As(_alice).Create(_chapterId, new CreateNoteRequest(new string('x', ChapterNote.MaxBodyLength + 1), null, null), default)));
        Assert.Equal(400, Status(await As(_alice).Create(_chapterId, new CreateNoteRequest("ok", -1, null), default)));
        Assert.Equal(400, Status(await As(_alice).Create(_chapterId, new CreateNoteRequest("ok", null, "public"), default)));
        Assert.Equal(404, Status(await As(_alice).Create(_chapterId + 999, new CreateNoteRequest("ok", null, null), default)));

        var note = await CreateAsync(_alice, null!, page: 4, body: "  trimmed  ");
        Assert.Equal("trimmed", note.Body);
        Assert.Equal("private", note.Visibility);
        Assert.Equal(4, note.PageIndex);
    }

    [Fact]
    public async Task Editing_sets_edited_at_and_clear_page_unpins()
    {
        var note = await CreateAsync(_alice, "private", page: 2);

        var edited = Body<NoteDto>(await As(_alice).Update(note.Id, new UpdateNoteRequest("second thoughts", null, null, true), default));

        Assert.Equal("second thoughts", edited.Body);
        Assert.NotNull(edited.EditedAt);
        Assert.Null(edited.PageIndex);
    }

    [Fact]
    public async Task A_private_root_takes_no_replies_even_from_its_author()
    {
        var note = await CreateAsync(_alice, "private");

        Assert.Equal(404, Status(await As(_alice).Reply(note.Id, new ReplyNoteRequest("talking to myself"), default)));
    }

    [Fact]
    public async Task A_shared_root_others_answered_cannot_turn_private()
    {
        var note = await CreateAsync(_alice, "shared");
        await ReplyAsync(_bob, note.Id);

        Assert.Equal(409, Status(await As(_alice).Update(note.Id, new UpdateNoteRequest(null, null, "private", null), default)));
    }

    [Fact]
    public async Task Turning_a_root_private_takes_the_authors_own_replies_with_it()
    {
        var note = await CreateAsync(_alice, "shared");
        await ReplyAsync(_alice, note.Id);

        var updated = Body<NoteDto>(await As(_alice).Update(note.Id, new UpdateNoteRequest(null, null, "private", null), default));

        Assert.Equal("private", updated.Visibility);
        Assert.All(updated.Replies!, r => Assert.Equal("private", r.Visibility));
        Assert.Empty(await ListAsync(As(_bob)));
    }

    [Fact]
    public async Task Deleting_an_answered_root_leaves_a_tombstone_that_goes_with_its_last_reply()
    {
        var note = await CreateAsync(_alice, "shared");
        var reply = await ReplyAsync(_bob, note.Id);

        Assert.Equal(204, Status(await As(_alice).Delete(note.Id, default)));
        var tombstone = Assert.Single(await ListAsync(As(_bob)));
        Assert.Equal("author", tombstone.Deleted);
        Assert.Equal(string.Empty, tombstone.Body);
        Assert.False(tombstone.CanDelete);
        Assert.Single(tombstone.Replies!);
        Assert.Equal(400, Status(await As(_carol).Reply(note.Id, new ReplyNoteRequest("late"), default)));

        Assert.Equal(204, Status(await As(_bob).Delete(reply.Id, default)));
        Assert.Empty(await ListAsync(As(_bob)));
        using var db = _db.NewContext();
        Assert.Empty(db.ChapterNotes);
    }

    [Fact]
    public async Task Deleting_a_root_only_its_author_answered_removes_the_thread()
    {
        var note = await CreateAsync(_alice, "shared");
        await ReplyAsync(_alice, note.Id);

        Assert.Equal(204, Status(await As(_alice).Delete(note.Id, default)));

        using var db = _db.NewContext();
        Assert.Empty(db.ChapterNotes);
    }

    [Fact]
    public async Task An_admin_removes_a_shared_note_as_a_moderator()
    {
        var note = await CreateAsync(_alice, "shared");
        await ReplyAsync(_bob, note.Id);

        Assert.Equal(204, Status(await Admin().Delete(note.Id, default)));

        Assert.Equal("moderator", Assert.Single(await ListAsync(As(_alice))).Deleted);
    }

    [Fact]
    public async Task Author_names_follow_name_visibility()
    {
        await CreateAsync(_alice, "shared");
        var bobs = await CreateAsync(_bob, "shared");

        var asCarol = await ListAsync(As(_carol));
        Assert.Null(asCarol.Single(n => n.Author!.Id == _alice).Author!.Name);
        Assert.Equal("Bobby", asCarol.Single(n => n.Id == bobs.Id).Author!.Name);
        Assert.Equal("alice", (await ListAsync(Admin())).Single(n => n.Author!.Id == _alice).Author!.Name);
        Assert.Equal("alice", (await ListAsync(As(_alice))).Single(n => n.Author!.Id == _alice).Author!.Name);
    }

    [Fact]
    public async Task An_instance_that_shows_login_names_shows_them_on_notes_and_in_the_bell()
    {
        await _settings.SetAsync(Maki.Core.Configuration.SettingKeys.LoginNamesVisible, "true");
        var note = await CreateAsync(_alice, "shared");

        Assert.Equal("alice", Assert.Single(await ListAsync(As(_carol))).Author!.Name);
        await ReplyAsync(_carol, note.Id);
        var raised = Assert.Single(_inbox.Raised);
        Assert.Equal("carol", raised.Message.Params!["userName"]);
        Assert.Equal("yes", raised.Message.Params["hasName"]);
    }

    [Fact]
    public async Task Counts_split_my_notes_from_shared_ones()
    {
        await CreateAsync(_alice, "private");
        await CreateAsync(_alice, "shared");
        await CreateAsync(_bob, "shared");

        var mine = Assert.Single(Body<List<NoteCountDto>>(await As(_alice).Counts(_seriesId, default)));
        Assert.Equal((2, 2, 3), (mine.Mine, mine.Shared, mine.Total));
        var theirs = Assert.Single(Body<List<NoteCountDto>>(await As(_carol).Counts(_seriesId, default)));
        Assert.Equal((0, 2, 2), (theirs.Mine, theirs.Shared, theirs.Total));
    }

    [Fact]
    public async Task A_reply_notifies_everyone_in_the_thread_but_the_replier_and_never_carries_text()
    {
        var note = await CreateAsync(_alice, "shared");
        await ReplyAsync(_bob, note.Id, "secret plot twist");
        var alicesFirst = Assert.Single(_inbox.Raised);
        Assert.Equal(InboxAudience.User(_alice), alicesFirst.Audience);
        Assert.Equal("Bobby", alicesFirst.Message.Params!["userName"]);

        _inbox.Raised.Clear();
        await ReplyAsync(_carol, note.Id, "another secret");

        Assert.Equal([_alice, _bob], _inbox.Raised.Select(r => r.Audience.UserId).Order());
        foreach (var (type, message, _) in _inbox.Raised)
        {
            Assert.Equal(InboxEventType.NoteReply, type);
            Assert.Equal("inbox.noteReply", message.Key);
            Assert.Equal(_seriesId, message.SeriesId);
            Assert.Equal($"/read/{_chapterId}?notes={note.Id}", message.Url);
            Assert.DoesNotContain("note", message.Params!.Keys);
            Assert.DoesNotContain(message.Params.Values, v => v is string s && s.Contains("secret"));
            Assert.Equal("no", message.Params["hasName"]);
        }
    }

    [Fact]
    public async Task A_participant_who_lost_the_root_folder_is_not_notified()
    {
        var note = await CreateAsync(_alice, "shared");
        await ReplyAsync(_bob, note.Id);
        using (var db = _db.NewContext())
        {
            db.Users.Single(u => u.Id == _alice).AllRootFolders = false;
            db.SaveChanges();
        }

        _inbox.Raised.Clear();
        await ReplyAsync(_carol, note.Id);

        Assert.Equal(_bob, Assert.Single(_inbox.Raised).Audience.UserId);
    }

    [Fact]
    public async Task An_admin_reads_the_login_name_of_an_unnamed_replier()
    {
        var note = await CreateAsync(_admin, "shared");

        await ReplyAsync(_carol, note.Id);

        var raised = Assert.Single(_inbox.Raised);
        Assert.Equal("carol", raised.Message.Params!["userName"]);
        Assert.Equal("yes", raised.Message.Params["hasName"]);
    }

    [Fact]
    public async Task Deleting_an_account_keeps_other_peoples_replies_under_an_authorless_tombstone()
    {
        var answered = await CreateAsync(_alice, "shared");
        var bobsReply = await ReplyAsync(_bob, answered.Id);
        await ReplyAsync(_alice, answered.Id);
        await CreateAsync(_alice, "private");
        await CreateAsync(_alice, "shared");
        var bobs = await CreateAsync(_bob, "shared");
        await ReplyAsync(_alice, bobs.Id);
        await As(_bob).Delete(bobs.Id, default);

        using (var db = _db.NewContext())
        {
            await ChapterNoteCleanup.ForgetAuthorAsync(db, _alice, default);
            db.Users.Remove(db.Users.Single(u => u.Id == _alice));
            await db.SaveChangesAsync();
        }

        using var check = _db.NewContext();
        var left = check.ChapterNotes.OrderBy(n => n.Id).ToList();
        Assert.Equal([answered.Id, bobsReply.Id], left.Select(n => n.Id));
        Assert.Null(left[0].AuthorId);
        Assert.NotNull(left[0].DeletedAt);
        Assert.Equal(string.Empty, left[0].Body);
        Assert.Null(Assert.Single(await ListAsync(As(_carol))).Author);
    }

    [Fact]
    public async Task Only_shared_notes_are_broadcast()
    {
        var note = await CreateAsync(_alice, "private");
        await As(_alice).Update(note.Id, new UpdateNoteRequest("still mine", null, null, null), default);
        Assert.Empty(_events.Changed);

        await As(_alice).Update(note.Id, new UpdateNoteRequest(null, null, "shared", null), default);

        Assert.Equal((_seriesId, _chapterId), Assert.Single(_events.Changed));
    }

    private sealed class NotesBroadcaster() : EventBroadcaster(null!, null!)
    {
        public List<(int SeriesId, int ChapterId)> Changed { get; } = [];

        public override Task ChapterNotesChanged(int seriesId, int chapterId, int rootFolderId)
        {
            Changed.Add((seriesId, chapterId));
            return Task.CompletedTask;
        }
    }
}
