---
paths:
  - "src/Maki.Core/Entities/ChapterNote.cs"
  - "src/Maki.Api/Controllers/Notes*.cs"
  - "src/Maki.Api/Services/*Note*.cs"
  - "tests/Maki.Api.Tests/Notes*.cs"
  - "frontend/src/api/notes.ts"
  - "frontend/src/components/notes/**"
---

# Chapter notes

Design and the reasoning behind it: `docs/design-chapter-notes.md`.

- **Visibility is the `ChapterNote` query filter and nothing else**: mine, or shared on a series I can see (`Series.Any(...)`, the shape `Chapter` uses). Admins get no bypass, private means private. **Never call `IgnoreQueryFilters` in `NotesController`**: on a notes query it drops the nested `Series` filter too, so a count or moderation query written with it loses root-folder visibility. An id the filter hides is 404 on every action; a visible shared note the caller may not change is 403.
- **`AuthorId` is set explicitly on every insert.** The entity is not `IUserOwned`, so `StampOwner` never fills it, and a forgotten assignment reads as a deleted account's note. The FK is **SetNull**, not Cascade: a tombstoned root keeps its author, and a cascade would delete it and, through the `ParentId` self-reference, everyone else's replies. `UsersController.Delete` runs `ChapterNoteCleanup.ForgetAuthorAsync` first (unfiltered, delete or blank only).
- **The chapter-move paths carry notes the way they carry bookmarks**: `ChapterSyncService.ReadRows.MoveAsync` (merge, every note moves), `ChapterFromFilesService.RetireVolumeEntriesAsync` (to the first taker, page dropped), `SourceMappingRemovalService.KeptWithoutSupportAsync` (a note keeps an unlisted chapter), the `HealthOperationService` page-layout reset (page nulled, note kept), `ChapterSyncService.DropUnlistedLockedAsync` (`ReadRows.HasReadsAsync` counts a note, so the locked row stays). A new path that deletes or repoints chapter rows must do the same.
- **Replies are one level deep and only on shared roots.** A reply stores its root's `Visibility` so the filter needs no join; turning a root private is refused (409 `error.notes.hasReplies`) while anyone else replied, and otherwise rewrites the author's own replies. A root others answered becomes a tombstone on delete (`DeletedAt`, empty body, `DeletedByModerator` for an admin); the tombstone goes with its last reply. Reply, PATCH and DELETE take `SeriesLocks.SeriesAsync` around the check and the save. A write that loses a race with a duplicate merge is 404 `error.notes.chapterGone`.
- **No note text leaves the notes endpoints.** Inbox rows (`NoteReply`, `inbox.noteReply`), pushes and the `chapterNotesChanged` hub payload (`{ seriesId, chapterId }`, shared notes only) carry names and labels, never the body. **No inbox param may be called `note`**: `InboxRenderer` appends that one verbatim to the body.
- **The replier's name is resolved per recipient** (display name, else the login name for an admin recipient or while `instance.loginnamesvisible` is on, else `hasName = "no"`, which the catalogue renders as "Another reader"), and recipients who lost the series' root folder are dropped before raising, since a `User` audience checks only that the account is usable.
