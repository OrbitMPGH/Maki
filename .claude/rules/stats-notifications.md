---
paths:
  - "src/Maki.Api/Controllers/Stats*.cs"
  - "src/Maki.Api/Controllers/Progress*.cs"
  - "src/Maki.Api/Controllers/Notifications*.cs"
  - "src/Maki.Api/Controllers/Inbox*.cs"
  - "src/Maki.Api/Services/*Stats*.cs"
  - "src/Maki.Api/Services/LibraryComposition*.cs"
  - "src/Maki.Api/Services/*Achievement*.cs"
  - "src/Maki.Api/Services/UserMetrics*.cs"
  - "src/Maki.Api/Services/*Notification*.cs"
  - "src/Maki.Api/Services/Inbox*.cs"
  - "src/Maki.Api/Services/UserViewResolver.cs"
  - "frontend/src/pages/rewind/**"
---

# Stats, progress, notifications, inbox

Migrated out of the root CLAUDE.md so this only loads when touching stats/achievements/notifications code.

- **Activity stats are an append-only event log** (`StatsEvents` + `ReadingStates`). Reads are Kavita high-water-mark deltas; first sighting of a series is a silent baseline (no event), backwards movement ignored. Never derive reads from `ScrobbleSyncState` (double-counts or records nothing depending on tracker count). Never purge `StatsEvents` in housekeeping.
- **Progress (`/api/v1/progress`, `ProgressController`) stores unlocks only** (`UserAchievement`, `ReadingGoal`) — everything else recomputed from `StatsEvents`, which is what makes `IncognitoMode.Full` invisible to progression for free (those rows are already dropped upstream). Unlocks never revoked. `Key` is persisted — treat like a permission bit, never rename/reuse. The feature used to be called gamification; two persisted names still say so and must not be "tidied up" — the setting key `user.gamification` (`SettingKeys.UserGamification`, renaming it drops every user's stored preference) and the migration `20260807133045_Gamification` (its name is in `__EFMigrationsHistory`).
- **`LevelMath = 350 * (L-1)^(4/3)`** — the `L-1` matters, a plain `L^(4/3)` makes level 1→2 cost more than 2→3.
- **`user.timezone` is its own key**, separate from the progress blob, because streaks need stable local dates across DST/travel (a per-request UTC offset can't provide that).
- **Achievements split Reader track vs Library track** — `SeriesAdded`/`ChapterDownloaded` carry null `UserId` (facts about the instance, not whoever's signed in), so crediting them to a user would hand one person the whole back catalogue.
- **Cross-user stats reads go through `UserViewResolver` only** — shared by `ProgressController.Resolve` and `StatsController`; naming another user requires Admin, and even then incognito reading stays invisible since it never became an event. Never copy the check into a third controller.
- **`StatsController` holds two scoping models on purpose; don't merge its actions.** `stats/activity` + `stats/years` are per-user (`ActivityStatsService`, explicit `userId`, Admin-gated); `stats/library` has no user at all. One handler owning both rules is how a scoping bug gets in, and library composition is window-independent so folding it into the windowed call would recompute it on every range change.
- **`ActivityStatsService` takes an explicit `userId` and ignores query filters** (`e.UserId == null || e.UserId == userId`, keeping the null-user library branch) — the ambient scope is still the *admin's* when they view somebody else's year. The one exception is the `Series` join for covers/genres, which keeps its filter on: a series in a root folder the caller can't see must not hand back its cover.
- **`/api/v1/stats/library` (`LibraryCompositionService`) has no `userId`** — the library is shared, and root-folder visibility is already structural, so it runs with filters *on*. Cached 60s keyed by user, because two people with different root folders must not share an entry. Frontend calls it `useLibraryComposition`; `useLibraryStats` is the unrelated older hook that tallies the client's series list.
- **Two notification systems, deliberately not one.** `Notification` (+ `db.Notifications`, `/api/v1/notifications`, `NotificationsSection.tsx`, `useNotifications`, query key `['notifications']`) is an **outbound Discord/webhook connection**: instance-wide, admin-managed, no recipient, 7 event types. The per-user in-app feed is called **inbox** in backend code (`UserNotification`, `db.UserNotifications`, `InboxService`, `/api/v1/inbox`, key `['inbox']`) and **Notifications** in the UI (header bell, `/notifications`). They share raise *sites* and nothing else — pushing achievements and per-user request outcomes down the outbound pipe would drown the download reports it exists for.
- **`InboxEventType` values persist** in `UserNotifications.Type` *and* as the keys of a user's `notifications.inbox` prefs blob — append only, never renumber, same rule as `MakiPermission`. `InboxPrefsSpec.Merge` appends unknown-to-the-stored-spec types **enabled** (except `DefaultsOff`), so a release adding an event type doesn't need everyone to opt in.
- **Inbox audience is one of three rules** (`InboxAudience`), resolved once in `InboxAudienceResolver` on its own unrestricted scope with `IgnoreQueryFilters` throughout — "who else should see this" is the one question a per-user filter can never answer. `SeriesTrackers` = anyone with a `ChapterProgress`/`ReadingState`/`SeriesRequest` row for the series, intersected with root-folder visibility, **falling back to admins when nobody tracks it** (a freshly added series would otherwise notify nobody). `Series` records no creator, so "whoever added it" is not part of the rule. `InboxService` also drops admin-only types aimed at a non-admin audience rather than trusting call sites.
- **Level-ups need `progress.lastnotifiedlevel` because levels are never stored** (`LevelMath` is pure arithmetic). `AchievementService.NotifyLevelAsync` **seeds it silently on first evaluation** — announcing on that pass would hand every existing account a level-up the day the feature shipped — and only ever moves forward, so a curve retune can't announce a demotion. Achievements notify once per key at the highest new tier, not once per rung.
- **`EventBroadcaster.InboxNotification` is the only per-user hub method.** Everything else there is instance machinery or a folder-wide fact addressed to a group. The payload carries the recipient's new unread count so the bell's badge updates from the push. `ReaderController`'s inline achievement toast predates it and still rides the HTTP response.
- **Inbox retention needs both rules** (`HousekeepingJob`): read rows older than 30 days, *and* a 200-row per-user cap including unread. An age rule alone never touches somebody who never opens the bell; a cap alone keeps a year of acknowledged noise.
- **"Rewind" is the slideshow, not the data.** It reads the same `stats/activity` payload the Overview tab does (`frontend/src/pages/rewind/`). There is no `/api/v1/rewind` any more and no alias — the old route is gone. The `/rewind` *UI* route still redirects to `/stats` for bookmarks.
