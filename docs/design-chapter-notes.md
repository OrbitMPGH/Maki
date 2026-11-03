# Design: chapter notes and shared comments

Readers want to write things down while they read: a theory, a translation quirk, "the fight on page 14 is the best in the series". On an instance with more than one account they also want to see what the others wrote and answer it. This note settles what that feature is, how it is stored, who sees what, and how it reaches the reader and the series page.

Work lands on `feat/chapter-comments` (worktree `.claude/worktrees/chapter-comments`), branched from dev on 2026-10-25. Nothing below is built yet. The open questions for the owner are at the end; the design picks a default for each so work can start. Revised the same day after a review against the code.

## The idea in one paragraph

A **note** is text a user attaches to a chapter, optionally pinned to one page. Each note is either **private** (only its author ever sees it) or **shared** (everyone who can see the series sees it). A shared note is the start of a thread: other readers reply under it, one level deep. Notes show up in the reader (a panel beside the pages, plus a marker on pages that have one), on the end-of-chapter screen, and as a count on each row of the series page's chapter table. Replies to your notes arrive in your inbox.

One noun covers both halves of the request. Private notes are the note-taking half, shared notes plus replies are the comments half, and a user can move a note between the two without rewriting it.

## Constraints that shape everything

1. **Appearing in someone else's view is opt-in.** The leaderboard is the precedent: `ProgressController.Leaderboard` lists only users who chose to be there, because reading is per user and only the library is shared. So a new note is private unless the author says otherwise, and the composer says plainly who will see it.
2. **Login names are not public.** `UserNameVisibility.For` shows another user's display name, their login name only to an admin or to themselves, and otherwise null, which callers render as a generic label. Notes follow it (see "Who sees what").
3. **Visibility follows the library, never more.** A shared note on a series in a root folder you were not granted does not exist for you. The `Series` query filter in `MakiDbContext` (root-folder grants) decides this everywhere else, and notes go through it the same way `Chapter` and `ChapterFile` do.
4. **Private means private, admins included.** An admin can moderate shared notes but has no way to read someone's private ones. `NotesController` never calls `IgnoreQueryFilters`: on a notes query it drops the nested `Series` filter too (the comment in `StatsInsightsService` explains this), so a moderation or count query written with it would also lose root-folder visibility. The chapter-move services are the only unfiltered readers, and they only move or delete rows.
5. **Chapter rows are not stable.** `ChapterSyncService.MergeDuplicatesAsync` folds duplicate rows into a keeper, `ChapterFromFilesService.RetireVolumeEntriesAsync` hands a volume entry's reads to the numbered chapters that replace it, `SourceMappingRemovalService` deletes chapters with the mapping, and a health repair with a page-layout reset moves pages. Every one of those already carries `ChapterProgress` and `ReaderBookmark` along, and notes ride the same paths.
6. **Spoilers are the main social risk.** A shared note on chapter 80 is a spoiler to someone on chapter 20. The library is shared, so anyone can open chapter 80 anyway; the job is to avoid showing text by accident, not to enforce secrecy.
7. **Self-hosters often run over plain HTTP on the LAN.** No secure-context APIs in the new UI; use `copyText()` if a "copy link" action is added.
8. **Single-user mode hides multi-user parts.** With one enabled account, the private/shared choice, author names and reply threads are noise. Notes still work, all private. The SPA already knows the mode from `singleUser` on `/auth/me`.
9. **Every string is localised.** Frontend text through `t`/`<Trans>`, backend errors through `this.Fail(localizer, key)`, inbox rows as a `MessageKey` plus params rendered in the reader's language by `InboxRenderer`.

## Data model

One new entity in `Maki.Core/Entities/ChapterNote.cs`:

```csharp
public class ChapterNote
{
    public int Id { get; set; }
    public int SeriesId { get; set; }
    public int ChapterId { get; set; }

    /// Null once the author's account is deleted and the note stays as part of someone else's thread.
    public int? AuthorId { get; set; }

    /// Null for a thread's first note. Replies are one level deep: a reply's parent is always a root.
    public int? ParentId { get; set; }

    /// Zero-based page within the chapter's own slice, like ReaderBookmark.PageIndex. Null = the whole chapter.
    public int? PageIndex { get; set; }

    public NoteVisibility Visibility { get; set; }

    public string Body { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; }
    public DateTime? EditedAt { get; set; }

    /// Set when a shared root with other people's replies is deleted: the body is cleared and the thread stays readable.
    public DateTime? DeletedAt { get; set; }

    /// The deletion at DeletedAt was an admin's, not the author's.
    public bool DeletedByModerator { get; set; }
}

public enum NoteVisibility
{
    Private = 0,
    Shared = 1,
}
```

`NoteVisibility` is persisted as an int: append only, never renumber, same rule as `MakiPermission` and `InboxEventType`.

Not `IUserOwned`, so `MakiDbContext.StampOwner` does not fill `AuthorId`. Every insert sets it explicitly from `ICurrentUser`; a forgotten assignment leaves it null and the row reads as a deleted account's.

Configuration in `MakiDbContext`:

- FKs: `Series` cascade, `Chapter` cascade, `MakiUser` (author) **SetNull**, `ParentId` self-reference cascade. The author FK cannot cascade: a tombstoned root still carries its author, so a cascade would delete it and the self-reference would then take every other person's reply with it. Account deletion cleans up explicitly instead (see "Deleting").
- Indexes: `ChapterId`, `SeriesId`, `AuthorId`, `ParentId`. A chapter holds a handful of notes, so ordering happens in memory.
- Query filter:

  ```csharp
  e.HasQueryFilter(n => _scope.Unrestricted ||
      (Series.Any(s => s.Id == n.SeriesId) &&
       (n.AuthorId == _scope.UserId || n.Visibility == NoteVisibility.Shared)));
  ```

  The rule is "mine, or shared on a series I can see", which neither the plain user filter nor the plain series filter expresses on its own. The `Series.Any(...)` shape is the one `Chapter` and `ChapterFile` already use.

Body is plain text, at most 4,000 characters, trimmed, empty refused (`error.notes.empty`, `error.notes.tooLong`). No HTML and no Markdown library. The client renders it as text nodes, keeps line breaks, turns `http(s)://` URLs into links with `rel="noopener noreferrer nofollow"`, and treats `||text||` as an inline spoiler. There is no escape for literal pipes: `||||` is an empty spoiler. That is the whole format, which keeps XSS out by construction.

`PageIndex` must be `>= 0` (`error.notes.badPage`). It is not checked against the page count: a file can be replaced without a reset. A note whose page is past the end of the current file renders as a whole-chapter note rather than disappearing.

### Reply rules

- Only a shared root takes replies. A private note has no thread.
- A reply stores its root's visibility (so the query filter needs no join) and has no page of its own; it shows the root's.
- Replies are listed oldest first. There is no pagination: a chapter's notes on a household instance are a few kilobytes.
- **Turning a shared root private** is refused while anyone else has replied (`error.notes.hasReplies`), because it would take other people's words out of view with it. With only the author's own replies, the PATCH rewrites their stored `Visibility` with the root's.
- **Turning a private note shared** is always allowed.

### Concurrency

The check-then-write rules above race with a reply arriving at the same moment: a reply can land between the "anyone else replied?" check and the visibility change, or under a root being deleted (an FK failure, a 500). Reply POST, PATCH and DELETE take `SeriesLocks.SeriesAsync(seriesId)` around the check and the save. The lock is per series and non-reentrant; none of these paths is reached from inside another holder.

A note posted to a duplicate chapter row while `MergeDuplicatesAsync` deletes it fails on the chapter FK. That is turned into a 404 (`error.notes.chapterGone`), and the client refetches.

### Deleting

- **Your own note.** A reply is deleted outright. A private root goes with its replies (all yours). A shared root with no replies from anyone else goes with your replies. A shared root with other people's replies becomes a tombstone: `DeletedAt` set, body cleared, rendered as "Note deleted" with the replies under it. When the last reply under a tombstone goes, the tombstone goes too.
- **An admin** may delete any shared note the same way, setting `DeletedByModerator`, and the tombstone says it was removed by an admin. An admin DELETE on someone's private note is a 404 like any other invisible id.
- **Account deletion.** `UsersController.Delete` is a plain `db.Users.Remove` today. Before it, in the same transaction: delete the user's private notes, their replies, and their shared roots that have no one else's replies; tombstone their shared roots that do. The SetNull on `AuthorId` then leaves those tombstones authorless, which the DTO renders as a deleted account.

## Keeping notes attached when chapters move

Each path that moves `ReaderBookmark` rows today moves notes in the same place, with the same unfiltered load:

| Path | What happens to notes |
|---|---|
| `ChapterSyncService.MergeDuplicatesAsync` (`ReadRows.MoveAsync`) | `ChapterId` moves to the keeper. No uniqueness to worry about, so every note moves. |
| `ChapterFromFilesService.RetireVolumeEntriesAsync` | Moves to `takers[0]`, the first numbered chapter on the file (a retiring entry always has at least one taker). `PageIndex` is set to null, since a volume page index means nothing in one chapter's slice. |
| `SourceMappingRemovalService` | A chapter with a note counts as read, so a chapter the removed mapping did *not* list is kept rather than deleted. A chapter the removed mapping listed is deleted with its notes, the same as every user's progress and bookmarks on it today. |
| `HealthOperationService` page-layout reset | Notes keep their chapter. Their `PageIndex` is set to null at the point where progress has its `PageIndex` zeroed and bookmarks are deleted: unlike a bookmark, a note is still worth keeping when its page moved. |
| `ChapterController.Delete` ("Remove chapters", a wrong match) | Cascades, and stays gone even if the recycle bin later restores the file, because a restore relinks files to chapter rows and does not recreate notes. The confirm dialog adds a line when the selection holds notes ("3 notes on these chapters will be deleted"), summed client-side from `series/{id}/notes/counts`, which the page already holds. That counts only notes the deleter can see; other people's private notes go unannounced on purpose, since announcing them would reveal they exist. |
| Series delete, `LibraryImportUndoService` removing an imported series, `LibraryImportService` cleaning up a failed import | Cascade, like `ChapterProgress`. Re-adding the series does not bring them back. See open question 5. |

Those are the only paths that delete chapter rows today (every `Chapters.Remove`/`RemoveRange`/`ExecuteDelete` checked). `ChapterFileDuplicateRepairService` repoints chapters to the surviving file and deletes `ChapterFile` rows only; notes are untouched.

## Who sees what

| Viewer | Private note by someone else | Shared note, series visible | Shared note, series in an ungranted root |
|---|---|---|---|
| Any user | never | yes | never |
| Admin | never | yes, and may delete it | never (admin does not bypass root grants, `AllRootFolders` does) |

**Author names** go through `UserNameVisibility.For`, so `author.name` is the display name, the login name for an admin or the author themselves, and otherwise null. A null name renders as "Another reader". That makes a thread between two people with no display name hard to follow, so the composer prompts for a display name the first time someone without one switches a note to shared (a link to the account settings, not a blocking step). A deleted account's tombstone renders as "Deleted account". A disabled account's shared notes stay visible under the same rules; they are part of a conversation others replied to.

**Incognito series.** On a series with `IncognitoMode.Full`, a shared note tells everyone you read it. The composer on such a series opens on private and shows a one-line warning when switched to shared. Notes never write `StatsEvents`, `ReadingSession` or progress rows, so the stats side needs nothing.

## Spoilers

Two layers, both client side, since the server already lets the viewer open the chapter:

1. **Unread chapters are folded.** Outside the reader, a shared note by someone else on a chapter the viewer has not finished renders collapsed: author, chapter, time, and "Tap to show". "Finished" comes from the progress the series page already fetches (`reader/series/{id}/progress`) through `readStateOf` in `chapterTable.ts`: `read || watched`. Watched counts as seen on purpose: someone who watched the anime episode already knows what happens. Not `ReadingState.MaxChapter`: `SeriesController` stopped deriving read state from it because one stale Kavita read marks everything below it read forever. Inside the reader, on the chapter itself, nothing is folded: you are reading it.
2. **Inline `||spoiler||`** for anything that reaches beyond the chapter the note is on. Blurred until clicked, in every view.

Inbox rows, pushes and hub payloads never contain note text, only "Ana replied to your note on Ch. 12". That keeps spoilers out of the bell and off lock screens.

## API

New `NotesController`, `api/v1`. Every action needs only sign-in (the fallback policy); writes check ownership or admin in the action. All mutations take the usual antiforgery header. Each new action is added to `ControllerAuthorizationInventoryTests` with that reasoning.

| Method | Route | Purpose |
|---|---|---|
| GET | `chapters/{chapterId}/notes` | Every visible thread on the chapter: roots oldest first, replies under each. |
| GET | `series/{seriesId}/notes/counts` | Per chapter: my notes, shared notes. Feeds the chapter table. |
| POST | `chapters/{chapterId}/notes` | New root: `{ body, pageIndex?, visibility }`. |
| POST | `notes/{id}/replies` | New reply: `{ body }`. 404 for a private or invisible root. |
| PATCH | `notes/{id}` | Author only: `{ body?, pageIndex?, visibility? }`. Sets `EditedAt` when the body changes. |
| DELETE | `notes/{id}` | Author, or admin for a shared note. Tombstones per the rules above. |

Status codes: an unknown or invisible id is 404 on every action, so the API never confirms a private note exists. A visible shared note that the caller may not change (not the author, and not an admin deleting) is 403, since its existence is already known. Tombstones refuse PATCH and replies (`error.notes.deleted`).

`NoteDto`:

```ts
interface NoteDto {
  id: number
  chapterId: number
  parentId: number | null
  pageIndex: number | null
  visibility: 'private' | 'shared'
  body: string                                 // empty for a tombstone
  author: { id: number; name: string | null } | null   // null = deleted account
  isMine: boolean
  canEdit: boolean     // isMine && not deleted
  canDelete: boolean   // (isMine || (admin && shared)) && not deleted
  createdAt: string
  editedAt: string | null
  deleted: 'author' | 'moderator' | null
  replies?: NoteDto[]  // roots only
}
```

The reader manifest gains `noteCount` and `notedPages: number[]` (pages with at least one visible root), so the toolbar badge and page markers draw with the first paint instead of waiting on a second request. Both halves change: the anonymous object `ReaderController` returns, and the `ReaderManifest` interface in `frontend/src/api/reader.ts`, with every fixture typed against it (`ReaderPage.stories.tsx`, `OfflineHome.stories.tsx`, `.storybook/mocks`) or `tsc -b` fails.

There is no instance switch for sharing in v1. Single-user mode already hides it, an admin can delete any shared note, and a switch would need its own non-admin settings read plus a "read-only shared notes" state. If the owner wants one (question 3), it rides `/auth/me` beside `singleUser`, and when off refuses new shared notes, PATCH to shared, and replies alike.

## Live updates

A new `EventBroadcaster.ChapterNotesChanged(seriesId, chapterId, rootFolderId)` in `Hubs/EventsHub.cs`, sent for shared notes only, to the private `AudienceForAsync(rootFolderId)` set that `ChapterImported` uses (granted users, not disabled, not pending setup).

The payload is `{ seriesId, chapterId }` and nothing else: no author, no visibility, no text. The client invalidates `['notes', chapterId]` and `['notes', 'counts', seriesId]`, and React Query refetches through the normal filtered endpoints, so the broadcast can never leak what was written or by whom. Private notes need no broadcast: the panel refetches when it opens.

## Inbox and push

One new event type, appended: `NoteReply = 25`. Raised after a reply is saved, for the root's author and everyone else who has replied in that thread, minus the replier.

- **Audience.** `InboxAudienceKind.User`, one raise per recipient. Its resolver checks only that the account is usable (not disabled, not pending setup), not root-folder access, so the raise site first drops recipients who can no longer see the series, with the same `u.AllRootFolders || UserRootFolders.Any(...)` test `InboxAudienceResolver.AdminsAsync` uses.
- **Message.** Key `inbox.noteReply` with params `{ userName, chapter }`, where `chapter` is `ChapterLabel.For(chapter)` frozen at raise time, as `inbox.chapter.downloaded` does in `ChapterDownloadProcessor`. `InboxRenderer` resolves only `{series}` at render time. `userName` is what `UserNameVisibility.For` gives the *recipient*, or the localized "Another reader". **No param is ever called `note`:** the renderer appends a `note` param verbatim to the body (it carries an admin's request-decline reason), which would put note text in the bell.
- **Per-series mute.** `SeriesId` and `ChapterId` are set, so `FilterBySeriesModeAsync` applies for free. Under the `Reading` mode, someone who posted a note without having progress on the series gets no reply notifications; accepted.
- **No coalescing in v1.** `InboxService` inserts a row per raise, and `Raise` is fire-and-forget, so a "bump the unread row" rule would race and need the English fallback re-rendered. Three rows for three replies is fine in a household bell.
- **Defaults.** `InboxPrefsSpec.Merge` appends a new type enabled, so `NoteReply` reaches every bell without anyone opting in. It is not in the default push filter (`PushPrefs` stays `newChapterAvailable` + `chapterDownloaded`); users add it in notification settings.
- **Frontend wiring.** The `InboxEventType` union in `frontend/src/api/inbox.ts`, a place in `INBOX_CATEGORIES` (a new "Notes" category with `multiUserOnly: true`, like Requests), its entries in the label and description maps, and `inbox.noteReply.title`/`.body` in the hand-authored `locales/en/server.po` followed by `node scripts/i18n/sync-server-keys.mjs`.
- **Deep link.** `Url` is `/read/{chapterId}?notes={rootId}`. The inbox toast is suppressed on `/read/` routes by `signalr.ts`, so a reply that arrives while reading only moves the bell's count.

A second, louder type, "someone shared a note on a series you read", is left out on purpose: on an active instance it would fire on every note. If wanted later it is `NoteShared`, `DefaultsOff`, `SeriesTrackers` audience.

## Frontend

### Reader

- **Toolbar button** beside the bookmark toggle: a note icon with the count from the manifest. Opens the notes panel.
- **Notes panel**: a Mantine `Drawer` on the right on desktop, a bottom sheet on phones, at the toolbar's `OVERLAY_Z`. The reader has no Drawer today (`ShortcutSheet` is hand-built), so check it against the reader's fullscreen and overlay layering. Two filters at the top, "This page" and "Whole chapter", defaulting to "This page" when the current page has notes and "Whole chapter" otherwise. Threads list, composer at the bottom.
- **Composer**: a `Textarea`, not contenteditable. `ReaderPage`'s key listener ignores `INPUT`/`TEXTAREA`/`SELECT`, so page-turn keys stay out of the text box; a contenteditable would bypass that. A "Pin to page N" checkbox, on when opened from a page. A private/shared segmented control with a line under it naming the audience ("Only you" / "Everyone who can see this series"), hidden in single-user mode.
- **Page markers**: a small dot on pages in `notedPages`, in the `PageStrip` thumbnails and above the page's segment in `PageScrubber`. Not on the page image itself; anything floating over the art is in the way.
- **Offline**: the notes endpoints stay off the service worker's `READ_ALLOWLIST`, so offline they answer the synthetic 503 with `{ code: 'offline.unavailable' }`. The panel branches on that code (never on the status, per `pwa.md`) and shows "Notes need a connection", and the composer is disabled the way bookmarks already are (`bookmarkDisabled`). A chapter saved for offline keeps the manifest from when it was saved, so its `noteCount` and markers can be stale; accepted.
- **Progress safety**: opening the panel or typing must not page, save progress or count reading time. `useReadingClock` gates on visible, focused and not idle, and typing counts as activity, so the hook takes a new `paused` input that is true while the panel is open on a phone, where the sheet covers the page.
- **`?notes={rootId}`**: `ReaderPage` reads no search params today and stays mounted across `/read/:chapterId` changes. The param is read once on load, opens the panel scrolled to that thread, and is then stripped with `replace`, or paging to the next chapter would reopen the panel.

### End-of-chapter screen

`ChapterEnd` gets a short block under "Up next" when the chapter has shared notes: the newest two, and "See all N notes". With none, a single "Add a note" link. This is the moment people actually have something to say about a chapter, so it is the main entry point for shared notes.

### Series page

- **Chapter table**: a note icon with a count on rows that have notes (`ChapterTableRows`), split visually into mine and shared. Clicking it opens the same notes panel component for that chapter, without leaving the page. Counts come from `series/{id}/notes/counts`; spoiler folding reads the progress the table already has.
- Nothing else in v1: no new tab, no panel beside the hero. The owner has turned down extra tabs and side panels on this page before, so a series-level "recent notes" view is open question 4 rather than part of the plan.

### Components and files

- `frontend/src/api/notes.ts`: types, `useChapterNotes`, `useSeriesNoteCounts`, mutations, query keys.
- `frontend/src/components/notes/NotesPanel.tsx`, `NoteThread.tsx`, `NoteComposer.tsx`, `NoteBody.tsx` (the plain-text renderer with links and spoilers).
- Stories for each new component. The new requests from `ReaderPage`, `ChapterEnd` and the series page would fail every existing story of those pages (an unmocked request fails the story), so `.storybook/mocks/` gets handlers for the notes endpoints and the page helpers (`seriesMsw()` and the reader story handler lists) include them.

## Rule file

Add `.claude/rules/notes.md` with paths covering `src/Maki.Core/Entities/ChapterNote.cs`, `src/Maki.Api/Controllers/Notes*.cs`, `src/Maki.Api/Services/*Note*.cs`, `frontend/src/api/notes.ts`, `frontend/src/components/notes/**`. The hub change sits under `src/Maki.Api/Hubs/**`, which `auth.md` already covers. Its main lines:

- the visibility filter, why admins do not bypass it, and "no `IgnoreQueryFilters` in `NotesController`";
- `AuthorId` is set explicitly on every insert and is SetNull, with account deletion cleaning up first;
- the chapter-move paths that must carry notes;
- the reply, tombstone and series-lock rules;
- no note text in inbox rows, pushes or hub payloads, and no inbox param named `note`.

List it in `CLAUDE.md`'s rule-file line. Add a line to the merge gotcha in `downloads.md` too: its `Services/Chapter*.cs` path already loads for both `ChapterSyncService` and `ChapterFromFilesService`, which is where someone would forget the notes.

## Tests

Backend, in `Maki.Api.Tests`:

- Filter: a private note is invisible to another user and to an admin; a shared note on a series in an ungranted root is invisible; `AllRootFolders` sees it.
- Endpoints: 404 for invisible ids on every action, 403 for a visible note the caller may not change; reply to a private root refused; shared to private refused with others' replies and rewriting the author's own replies otherwise; tombstone on delete with others' replies, tombstone removed with the last reply; admin may delete a shared note and gets 404 on a private one.
- Author names: a user with no display name reads as null to a non-admin and as the login name to an admin.
- Chapter moves: merge moves notes; volume-entry retirement moves notes and drops the page; mapping removal keeps an unlisted chapter that only has a note; health reset nulls pages.
- Account deletion: other people's replies survive under an authorless tombstone; the deleted user's private notes are gone.
- Inbox: one `NoteReply` per participant, none for the replier, none for a participant who lost access to the root folder, muted series gets none, no note text anywhere in the row. An `InboxRendererTests` case for the new key, and `LocalizationCatalogTests` passing with it.
- `ControllerAuthorizationInventoryTests` updated with the new actions.

Frontend: `npx tsc -b`, `npm run lint`, `npm run check:secure-context`, stories render, then a browser pass on `.simulated` with two accounts. Reader testing writes progress, so do it in a way that does not mark chapters read on the real test account, or accept that it does. The browser pane stalls Drawer transitions while hidden, so screenshot between steps.

## Build order

1. **Private notes, with the final entity.** Entity and migration with nullable `AuthorId`, the tombstone columns and the filter; controller with the series lock and status codes from the start; reader button, panel, composer, page markers, manifest fields; account-deletion cleanup. Usable on its own as a note-taking feature, and nothing in it needs changing later.
2. **Sharing and replies.** Visibility, replies, tombstones, admin delete, author names and the display-name prompt, single-user hiding, incognito warning.
3. **Everywhere else.** Series page counts, end-of-chapter block, spoiler folding, the chapter-move paths.
4. **Notifications.** `NoteReply` and its frontend wiring, hub broadcast, `?notes=` deep link.

Step 3's chapter-move work is small but must land before this merges into dev, whatever order the UI goes in.

## Out of scope for now

Reactions or likes, @mentions, edit history, notes in OPDS, Komga or KOReader, notes on a series rather than a chapter, search across notes, export, offline composing and caching notes for offline reading. None of them change the data model above, so each can come later without migrating existing rows. Series-level notes would need `ChapterId` nullable, which is the one to decide before shipping if it is likely.

Backups need nothing: notes live in `maki.db`, which every backup already holds, and a restore rolls them back with everything else.

## Questions for the owner

Each has a default the plan above already assumes.

1. **The word.** "Notes" everywhere, with shared ones labelled "Shared" (default), or "Comments" for the shared kind and "Notes" for private ones?
2. **Default visibility of a new note.** Private (default, matches the leaderboard opt-in rule) or shared?
3. **Who may share.** Anyone with library access, no switch (default); an admin switch for the instance; or a new `MakiPermission` bit granted per user? A new bit would not be in existing users' permissions, so the feature would be invisible to every non-admin until granted.
4. **Series page.** Counts in the chapter table only (default), or also a "Recent notes" view for the whole series? If the latter, where: it would be a new tab or a new section, both of which have been declined for other content before.
5. **Series delete.** Notes go with the series (default, same as reading progress), or should they survive a delete and re-add by keying on the series identity the stats log uses? The second is noticeably more work.
6. **Spoiler folding.** Fold shared notes on unread chapters outside the reader (default), or show everything and rely on `||spoiler||` alone?
7. **Users without a display name.** Prompt for one when they first share (default), or require one before sharing?
