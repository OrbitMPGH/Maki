# Design: installable PWA, offline reading, web push

Maki ships a manifest today and nothing else: no service worker, no offline data, one 194px PNG icon. This note settles how the app becomes a proper PWA. It covers the service worker, browsing the library offline, saving chapters to the device, queueing reading progress while offline, web push for new chapters, and an icon set.

Owner decisions (2026-10-16):

- Chapters reach the device two ways: an explicit "Save for offline" on chapters and series, and an opt-in "keep the next N unread chapters" for series in progress.
- Progress read offline is queued and replayed to the server when the connection returns.
- In scope as well: offline library and series browsing, web push, a proper icon set.
- Work lands on `feat/pwa` (worktree `.claude/worktrees/pwa`), merged into dev after the owner verifies.

## Constraints that shape everything

1. **Secure context only.** Service workers, Cache Storage, Push and `navigator.storage` do not exist on plain HTTP outside localhost. Most self-hosters reach Maki over HTTP on the LAN (issue #104). On those installs everything in this note is absent: no SW registers, the UI hides offline and push actions, and the settings cards explain that HTTPS is required. Nothing may throw there. Every new page-side API goes through a helper in `frontend/src/lib/pwa/` and is registered in `scripts/frontend/check-secure-context.mjs`.
2. **Root-hosted only.** No PathBase support exists, so the SW is `/sw.js` with scope `/`.
3. **Auth is the `Maki.Session` cookie.** Image URLs carry no secret, so Cache API keys by URL are fine. Mutations need `X-XSRF-TOKEN`, read from a cookie the SW cannot read. The page, not the SW, sends any replayed write.
4. **The cache is per origin, not per user.** Everything user-specific is keyed by user id.
5. **React Query defaults to `networkMode: 'online'`**, which pauses queries when `navigator.onLine` is false. They would never reach the SW cache.
6. **Server progress is last-write-wins** on `PageIndex` with server time, reading seconds are additive deltas clamped to 900 per report, and completion is sticky. A naive replay moves progress backwards and double-counts time.
7. **No frontend test runner.** Frontend correctness is `npx tsc -b`, `npm run lint`, `npm run check:secure-context`, and browser verification.
8. **A backend that is down usually still answers.** Behind a reverse proxy, a stopped Maki gives 502 or 504 with an HTML or empty body, and `fetch` resolves. Maki itself does send 502 (`ApiResults.BadGateway` from the cover proxy, Suwayomi, releases, connection tests), but always with a JSON body. So "unreachable" below means: a rejected fetch, or a 502 or 504 whose `Content-Type` is not `application/json`. One helper, `isUnreachable(response)`, in `src/sw/net.ts`, used everywhere. A 503 is never unreachable.

## Service worker

### Build

A hand-rolled SW in TypeScript at `frontend/src/sw/` (entry `sw.ts`), no Workbox, no vite-plugin-pwa. vite-plugin-pwa's support for Vite 8 and Rolldown is unproven, and the routing rules below are specific enough that a library adds little.

A Vite plugin in `frontend/vite/sw-plugin.ts` runs in the parent build's `closeBundle`. It reads the emitted bundle, builds the precache list, then runs a nested build that does not inherit the parent's plugins:

```ts
build({
  configFile: false,
  plugins: [],
  define: { __PRECACHE__: JSON.stringify(list), __BUILD_ID__: JSON.stringify(hash) },
  build: {
    emptyOutDir: false,
    outDir,
    rolldownOptions: {
      input: 'src/sw/sw.ts',
      output: { entryFileNames: 'sw.js', format: 'iife', inlineDynamicImports: true },
    },
  },
})
```

`precompress()` walks `outDir` in its own `closeBundle`, so it moves after the SW plugin in the plugin array and picks up `sw.js`. In `vite dev` no SW is built or registered.

`sw.js` is served by the existing static-file pipeline with `no-cache`, which is what a SW script wants. CSP needs no change: `script-src 'self'` covers a same-origin worker.

`sw.ts` stays small and imports one module per concern: `shell.ts`, `data.ts`, `chapters.ts`, `progress.ts`, `push.ts`. Shared constants and types live in `frontend/src/pwa/contract.ts`, and the IDB access both sides use lives in `frontend/src/pwa/db.ts`. Both are imported by the SW and the page.

### Contract (`frontend/src/pwa/contract.ts`, `db.ts`)

- Cache names: `maki-shell-{buildId}`, `maki-assets`, `maki-meta`, `maki-u{userId}-data`, `maki-u{userId}-covers`, `maki-u{userId}-chapters`.
- IndexedDB database `maki-pwa`, version 1, stores:
  - `session`: key `current`, value `{ userId: number | null }`.
  - `offlineChapters`: key `[userId, chapterId]`, value `{ userId, chapterId, seriesId, pageVersion, pageCount, bytes, savedAt, reason: 'manual' | 'auto', state: 'saving' | 'saved' | 'stale' | 'error' }`, index on `[userId, seriesId]`.
  - `progressQueue`: auto-increment key, value `{ userId, chapterId, seriesId, pageIndex, completed, seconds, clientTime, opId, mergeable, state: 'pending' | 'sending' }`, index on `[userId, chapterId]`.
  - `prefs`: key `[userId, name]` for per-device settings (auto-keep count, last revalidation time).
- Response headers the SW adds: `X-Maki-Offline: 1` on anything served from cache in place of the network, `X-Maki-Queued: 1` on a progress write it queued.
- The SW's synthetic failure: status 503, JSON `{ "code": "offline.unavailable" }`, `X-Maki-Offline: 1`. The page branches on that code, never on status.
- Messages page to SW (`postMessage` to the controller): `{ type: 'user', userId: number | null }`, `{ type: 'purge-browse', userId }`, `{ type: 'purge-user', userId }`. SW to page through `BroadcastChannel('maki-pwa')`: `{ type: 'queued', chapterId }`.
- The IDB wrapper is the `idb` package (about 1 KB).
- Queue merge rules live in one function in `db.ts`, used by both the SW capture and the page's offline writes, each call in a single IDB transaction.

### Who the user is

The page tells the SW. `noteSignedInUser` and `noteSignedOut` in `frontend/src/lib/pageState.ts` are the points all sign-in paths (password, 2FA, setup, SSO return) and explicit sign-out reach. They post `{ type: 'user', userId }` and `{ type: 'user', userId: null }`. The SW writes it to `session.current`. As a cross-check, a live 200 from `GET /api/v1/auth/me` passing through the SW also sets `userId`.

`postToWorker` awaits `navigator.serviceWorker.ready` and posts to `registration.active`, so a message sent while the SW is still installing (first visit, password sign-in) is not lost. `registerServiceWorker` also re-posts the current user, read from the `me` query, once `ready` resolves and on every `controllerchange`.

- `userId` null: the SW serves nothing user-specific from cache and stores nothing user-specific.
- A 401 from any `/api/v1/*` request clears `userId`. Caches stay. This is not redundant with the page: the unauthorized handler in `AuthProvider` calls neither page helper. A session that expires while the reader is away must not destroy saved chapters or queued progress.
- Explicit logout (`useLogout.onSuccess`) posts `{ type: 'purge-browse', userId }`: the SW deletes that user's `data` and `covers` caches. Saved chapters and the progress queue stay, keyed by user, and reappear when the same user signs in. The offline settings card says this and offers "Remove everything from this device" (`purge-user`).

The SW reads `session.current` from IDB lazily (it can be restarted at any time) and keeps it in memory.

### Routing

Only same-origin requests are considered. Everything not listed passes through untouched (no `respondWith`).

| Request | Strategy | Cache |
|---|---|---|
| Navigation whose path does not start with `/api/`, `/signalr`, `/swagger` | Network raced against 5 s. Rejected fetch, 502, 504 or timeout: the precached `/index.html`. | shell |
| Precached URL | Cache first, network on miss | shell |
| Other `/assets/*` (locale catalogs) | Cache first, store on miss (hashed, so safe). `activate` deletes every entry not in `__CATALOGS__`, the current build's catalog URLs | assets |
| `/source-icons/*` | Cache first, store on miss, cap 500 entries | meta |
| `/initialize.json` | Network first, cache fallback | meta |
| `/api/v1/auth/me` | Network first, offline fallback; a live 200 also sets `userId` | data |
| Allowlisted read endpoints (below) | Network first, offline fallback | data, cap 2000 entries |
| `/api/v1/mediacover/*` | Cache first by full URL (the `?v=` busts it), store on miss | covers, cap 3000 entries |
| `GET /api/v1/reader/chapter/{id}` (manifest) | Network first. A live 200 is stored in `data` and, if `chapters` already holds that URL, overwritten there too. Offline fallback: `data`, then `chapters`. | data, chapters |
| `/api/v1/reader/chapter/{id}/page/{n}` | Cache first from `chapters`, network on miss, never stored by the router | chapters |
| `/api/v1/reader/chapter/{id}/thumb/{n}` | Network. Unreachable: the full page `/page/{n}` with the same `v` from `chapters` if present. | none |
| `PUT /api/v1/reader/chapter/{id}/progress` | See "Progress while offline" | IDB |
| Any other `GET /api/v1/*` | Pass through with `respondWith`; unreachable gives the synthetic 503 | none |
| `/signalr/*`, `/api/v1/opds/*`, non-GET other than progress | No `respondWith` at all | none |

"Network first, offline fallback": try the network. A 200 whose `Content-Type` is JSON (or `image/*` for images) is stored. Never store a non-200, and never store HTML from an `/api/` path (the SPA fallback answers unknown `/api/` paths with 200 HTML). If the fetch is unreachable (rejected, 502, 504), answer from the cache with `X-Maki-Offline: 1`; with nothing cached, the synthetic 503.

Data caches are not keyed by language. They are per user, and a language switch made online refreshes entries as pages are visited. Offline the reader sees the last fetched copy.

The read allowlist is one array in `src/sw/data.ts`, covering what Home, Library, the series page and the reader need: `auth/me`, `series` (list), `series/{id}`, `series/{id}/files/summary`, `chapter?seriesId=`, `reader/series/{id}/progress`, `reader/series/{id}/continue`, `reader/chapter/{id}/bookmarks`, `home` and its sub-paths, `rootfolder`, `tags`, `library/filters`, `settings/ui`, `settings/reader`, `readingprofiles`, `inbox/unread-count`. The implementer confirms exact paths against the controllers.

Cache caps are enforced after a write by deleting the first keys `cache.keys()` returns. That is insertion order in Chromium and not promised by the spec, and a re-`put` keeps its old slot, so the cap is an approximate LRU. A re-fetched cover evicted early is expected.

### Precache and updates

The precache list: `/index.html`, `/manifest.webmanifest`, `/theme-init.js`, `/favicon.svg`, the icon PNGs, and every JS, CSS and font file under `dist/assets/` except the locale catalogs (`catalog-*`). Every route chunk is precached so the reader and series page work offline after an update even if the user never opened them on the new build. Catalogs are runtime-cached: the one in use loads at boot and is then stored.

`install` adds the list to `maki-shell-{buildId}` in chunks of 20 (Safari fails a whole `addAll` on one bad entry) and calls `skipWaiting()`. `activate` deletes other `maki-shell-*` caches and calls `clients.claim()`. This is safe because navigations are network-first: an online page always gets the current `index.html`, and an old page missing a lazy chunk hits the existing `vite:preloadError` reload, as today.

## Page side

### Registration and helpers (`frontend/src/lib/pwa/`)

- `serviceWorker.ts`: `pwaSupported()` (`isSecureContext && 'serviceWorker' in navigator && 'caches' in window`), `registerServiceWorker()` called once from `main.tsx` in production builds, `postToWorker(message)`.
- `storage.ts`: `estimate()`, `requestPersistence()` wrapping `navigator.storage`. Both tolerate the methods being absent (Safari has no `persist` on older versions).
- `push.ts`: wraps `PushManager` and `Notification.requestPermission`.
- `cache.ts`: the only page-side user of `caches`.

`check-secure-context.mjs` gains entries for `navigator.serviceWorker`, `caches.`, `PushManager`/`pushManager`, `navigator.storage`, and `Notification.`. The script's `home` check becomes a prefix match so all of `lib/pwa/` can be the home, and `frontend/src/sw/**` is excluded (the worker only exists in a secure context).

### Connectivity (`frontend/src/lib/connectivity.ts`)

A small store with `useConnectivity()`: `'online' | 'offline'`. The API client sets it offline when a response carries `X-Maki-Offline` or a fetch rejects, and online when a live API response arrives. `online`/`offline` window events feed it too. AppShell shows a slim banner while offline: "You're offline. Showing what's saved on this device." with a link to the offline settings card.

### React Query

When `pwaSupported()`: queries use `networkMode: 'offlineFirst'` so they reach the SW, and mutations use `networkMode: 'always'` so they fail straight away with the SW's 503 instead of pausing and firing later. `isFinalClientError` treats `code === 'offline.unavailable'` as final so it is not retried. That code maps to one localized toast ("Not available offline"), and the query-error toast is suppressed for it when a cached copy is on screen. On HTTP installs the defaults stay.

### Auth offline

Offline, the SW answers `/initialize.json` and `/auth/me` from cache, so the app boots signed in. Two fixes:

- A network failure or the synthetic 503 from `/auth/me` or SignalR's `checkSession()` must never count as signed out or trigger `dropAccountData`. Only a real 401 does.
- A first offline load with no cached `me` shows an "Offline, and nothing is saved on this device yet" screen instead of `LoginPage` (whose submit would only 503).

SignalR keeps retrying while offline. `checkSession()` hitting the cached `me` is harmless.

## Saving chapters to the device

### Download manager (`frontend/src/pwa/offlineChapters.ts`)

Runs in the page, not the SW. A SW can be killed mid-download, and the SPA keeps the module alive across route changes. A closed tab leaves records in `saving`, which the manager resumes on the next start.

Saving a chapter:

1. Fetch the manifest live (`GET /reader/chapter/{id}`) and store it in `maki-u{id}-chapters` under its URL.
2. Fetch every page `/reader/chapter/{id}/page/{n}?v={pageVersion}` with concurrency 3 and store each response in the same cache. Pages already present are skipped.
3. Warm the data cache for the chapter's series with plain `api()` calls: `series/{id}`, `chapter?seriesId={id}`, and the cover URL.
4. Mark the record `saved` with the byte total.

Before the first save, `requestPersistence()`. A `QuotaExceededError` stops the job, marks the record `error`, and shows one toast with the storage estimate.

Chapters on a shared volume file have different page URLs and are stored twice. Accepted.

### Revalidation

At most once an hour per device while online (on start and after the queue drains), the manager posts all saved chapter ids to a new endpoint, `POST /api/v1/reader/chapters/versions` (lane C), which returns `{ id, pageVersion }` for each chapter the caller can still read. One cheap query instead of a full manifest per chapter.

- Id missing from the response (gone, no file, or no access): delete the record and its pages.
- `pageVersion` changed: mark `stale` and re-download.

This is also how revocation reaches the device. Offline, a revoked user can keep reading what is saved until the next online check. Accepted and documented.

### Auto-keep

Per device, per user, in IDB `prefs`: off, or keep the next 1, 3, 5 or 10 unread chapters of each series in progress. When online, on start and after the queue drains, the manager takes the in-progress series (the same source as Home's continue reading, capped at 10 series), finds the next N unread chapters that have a file, saves the missing ones with `reason: 'auto'`, and deletes `auto` chapters that are now read and have no queue row. `manual` chapters are never removed automatically.

### UI

- Series page chapter rows: "Save for offline" / "Remove from this device", and a small saved indicator with progress while saving. Only rendered when `pwaSupported()`.
- Series page actions menu: "Save unread chapters for offline" and "Remove offline copies".
- Library: a "Saved on this device" filter chip, client-side from IDB, shown only when the device has saved chapters.
- Settings, Reading tab, card "Offline reading" (registry id `offline-reading`): storage used and quota, persistence status, the auto-keep select, saved chapters grouped by series with remove buttons, "Remove everything from this device", and the note that signing out keeps saved chapters. On iPhone and iPad outside the installed app, a note that Safari clears saved data after 7 days without use unless Maki is added to the home screen. On an insecure context the card only explains that offline reading and push need HTTPS, with a pointer to the reverse proxy docs.
- Reader: when the next chapter is not available offline, the end-of-chapter "next" leads to a "Not available offline" state instead of a generic error.

## Reader changes

- With the SW, the manifest fetch succeeds offline, so `ReaderPage`'s `!isFetching && !isError` gate passes.
- **Queued progress wins whenever it exists, online or offline.** When the progress queue holds a row for the chapter, the reader first waits for an in-flight drain of that chapter (bounded at 3 s). If a row is still there, the resume page is `max(manifest.resumePage, row.pageIndex)` and completion is `manifest.completed || row.completed`. When the row has `completed`, the reader seeds `finishedFor = chapterId` so its own live writes carry `completed` too. Without this, reopening a chapter right after reconnecting writes the stale server position live, and the queued completion then arrives as stale and is dropped.
- The bookmark toggle is disabled while offline (it is a toggle, so replaying it is unsafe).
- `flushProgress` currently ignores the response status. It must check `res.ok`, as `saveProgress` does.
- Every live progress write carries an `opId` (`randomUUID()` from `lib/uuid`).
- While `useConnectivity()` is `offline`, `saveProgress` and `flushProgress` write straight to the IDB queue (mergeable rows) instead of sending a PUT. The SW capture below covers the first failure before the store flips, and the `pagehide` flush.
- Prefs saves already swallow errors and need nothing.

## Progress while offline

### Queue rows

- A row created by the **SW capture** of a failed live write keeps that write's `opId` and is not mergeable. The write may have reached the server before the connection dropped, so the row must be dedupable by exactly that id.
- A row created by the **page's offline write** is mergeable and gets a fresh `opId`. Later offline writes for the same chapter merge into the newest mergeable `pending` row:
  - `pageIndex` = the latest value.
  - `completed` = OR of all values.
  - `seconds` += the new value. If the sum would pass 900, the row is closed (no longer mergeable) at its value and a new row takes the remainder, so no row exceeds the server's per-report clamp.
  - `clientTime` = the time of the latest write.
  - A merge regenerates `opId` (the row has never been sent, or its last send is known to have failed).

### Capture (in the SW)

For `PUT /api/v1/reader/chapter/{id}/progress` the fetch handler calls `respondWith` synchronously with a promise that:

1. Reads `const body = await event.request.clone().text()` before the network attempt (the attempt consumes the body).
2. Tries the network. A response that is not 502 or 504 is returned as is, including 401 and other errors, as today.
3. Unreachable (rejected, 502, 504): if `session.current.userId` is set, inserts a non-mergeable row with the write's `opId` and `clientTime = now`, answers `202` with `X-Maki-Queued: 1`, and broadcasts `{ type: 'queued' }`. Otherwise passes the failure through.

Whether a `keepalive` fetch from a page in `pagehide` reaches the SW and completes there is checked during verification. The page-side offline write above means correctness does not depend on it once the connectivity store has flipped.

### Replay (in the page, `frontend/src/pwa/progressQueue.ts`)

Triggered once `me` resolves live, on the `online` event, when the connectivity store flips to online, and when the reader asks for a chapter (above). One replay at a time: a module-level lock plus `navigator.locks` where available, so two tabs do not double-send.

For each row of the current user, oldest first:

1. Set `state: 'sending'` and persist.
2. `PUT .../progress` through `api()` with `{ pageIndex, completed: completed || undefined, seconds, final: true, clientTime, opId }`. `completed` is never sent as `false`, because the server derives completion from a last-page position only when the field is absent.
3. 2xx: delete the row. 401: set back to `pending`, stop, resume after sign-in. 404, or a 400 with `code === 'error.reader.pageOutOfRange'` (file gone, page count changed): delete the row. Any other 400 stays `pending` and is reported once to the console, so a binding bug cannot silently eat the queue. 5xx, 502, 504 or network failure: set back to `pending`, keep the same `opId`, stop and retry later with backoff.

Rows for another user id stay until that user signs in again. A row found in `sending` at startup (tab closed mid-send) goes back to `pending` with its `opId` unchanged. After a drain, invalidate the progress queries (`series`, `home`, `reader-progress`, `reader-continue`).

### Server changes (lane C)

`ReaderProgressRequest` gains two optional fields, ignored when absent:

- `clientTime` (UTC). Clamped to `[now - 30 days, now]`.
- `opId` (string, max 64).

`ChapterProgress` gains `RecentOpIds` (nullable string: the last 32 op ids for that row, space-separated). 32 covers a drain held back by a 5xx backoff while the user keeps reading the same chapter live. Behaviour in `ReaderService.SaveProgressCoreAsync`:

- `opId` already in `RecentOpIds`: return success without changing anything. Live writes now carry op ids too, so a queued copy of a write that did land is recognised even after later live writes.
- `clientTime` older than the stored `UpdatedAt`: the write is stale (read further elsewhere, or marked unread since). Apply `seconds`. Apply `completed: true` unless the row's `UnreadAt` is later than `clientTime`: completion is sticky, and the only thing the stale rule protects it from is a mark-unread made after the offline read. Do not move `PageIndex` and do not touch `UpdatedAt` (today it is stamped unconditionally; the stale path skips that).
- Otherwise apply as today, with `clientTime` (or now when absent) as the time for: `UpdatedAt`, `StartedAt` on a new row, `CompletedAt` when the write completes the chapter (`MakiDbContext` only stamps `CompletedAt` when it is null, so set it explicitly), and `StageReadingTime` (which uses `DateTime.UtcNow` today, `ReaderService.cs` around line 549).
- Chapter-read stats events from `TrackNativeAsync` and `RecordUnnumberedReadAsync` get an `at` parameter if that is a matter of passing it through. If it needs restructuring, they stay dated at replay and the rule file says so.
- Reading-session stitching (`ReadingSessionService.Stitch`): when the report carries a `clientTime`, it only joins the latest sitting if its interval `[clientTime - seconds, clientTime]` overlaps or comes within the gap of that sitting on either side. Otherwise it becomes its own row, even though it is older than the latest. A morning's offline reading must not merge into the afternoon's live sitting as one long row.

New endpoint `POST /api/v1/reader/chapters/versions`: body `{ ids: int[] }` (max 500), returns `[{ id, pageVersion }]` for chapters the caller can see that have a readable file, using the same scoping and `pageVersion` formula as the manifest. Entry in `ControllerAuthorizationInventoryTests`.

Tests in `Maki.Api.Tests`:

- An `opId` replay is a no-op, including after a later live write.
- A stale `clientTime` keeps position and `UpdatedAt` but adds seconds.
- A fresh `clientTime` dates `UpdatedAt`, `CompletedAt` and the reading-time event.
- Mark unread after the offline completion, then the replay: the chapter stays unread. Mark unread before the offline completion: the replay completes it.
- A stale completing replay after a live write at an earlier page: the chapter ends completed.
- A live write after a queued completion does not un-complete (completion is sticky).
- `Stitch` with a report older than the latest sitting by more than the gap makes a new row.
- Absent fields behave exactly as before.
- `chapters/versions` omits chapters outside the caller's scope and chapters without a file.

`.claude/rules/reader-progress.md` gets a short section on `clientTime`, `opId` and the stitching rule.

## Web push

### Server (lane C)

- Package: `Lib.Net.Http.WebPush` in `Maki.Api` (VAPID and aes128gcm). Key generation uses the built-in `ECDiffieHellman` P-256.
- VAPID keys live in `AppConfig` through `IAppSettings` (`push.vapid.publickey`, `push.vapid.privatekey`), generated on first use. `AppConfig` is in backups, so a restore keeps subscriptions working. The VAPID subject is the project's GitHub URL.
- Entity `PushSubscription` in `Maki.Core/Entities`: `Id`, `UserId`, `Endpoint` (unique), `P256dh`, `Auth`, `UserAgent`, `CreatedAt`, `LastSuccessAt`, `FailureCount`. Cascade delete with the user, user query filter like `UserNotification`. Migration `PushSubscriptions`, made after the `ChapterProgressRecentOpIds` migration in the same lane so the snapshot does not conflict.
- `PushController` at `/api/v1/push`: `GET public-key`, `POST subscription` (looks up the endpoint with `IgnoreQueryFilters()`, reassigns it to the current user if another user had it, otherwise inserts), `DELETE subscription` (by endpoint, own rows only), `POST test`. Entries in `ControllerAuthorizationInventoryTests`.
- Per-user push filter: user setting `notifications.push` (added to `UserSettingKeys.Fixed`), a list of `InboxEventType` values. Default: `NewChapterAvailable` and `ChapterDownloaded`. `GET/PUT /api/v1/push/prefs`.
- Delivery: `WebPushSender`, a singleton with a bounded `Channel` and one background consumer, so `InboxService` never waits on a push service. In `InboxService.RaiseAsync`, beside the SignalR push and inside the same try/catch, for each recipient whose push filter includes the event type (loaded in the same batch query that already reads `notifications.inbox` for all recipients, by widening its key filter), enqueue `{ userId, title, body, url, tag }` with the title and body already rendered for that recipient's locale. `tag` is `series-{id}` when the row has a series, so repeated chapter pushes collapse. The consumer works in its own scope, where `DataScope` is unrestricted, so loading and deleting any user's subscriptions needs no filter bypass.
- Send with TTL 24 h. 404 or 410: delete the subscription. Other failures: increment `FailureCount`, delete at 5, log at Debug.
- `POST test` sends a localized test message (new key in `locales/en/server.po`, then the sync and validate scripts).
- Tests: subscription upsert and reassignment, scoping (a user cannot delete another's endpoint), filter defaults, the sender drops a subscription on 410 (HTTP handler stub), the payload carries the recipient's locale.
- `.claude/rules/stats-notifications.md` paths gain the new push files.

### SW (`src/sw/push.ts`)

- `push`: parse `{ title, body, url, tag }` and `showNotification` with `icon: /icons/icon-192.png`, `badge: /icons/badge-96.png`, `tag`, `data: { url }`. A subscription is removed on explicit logout, so a push that arrives means the user's own expired session at worst, and the text is shown.
- `notificationclick`: focus an open client and navigate it to `url`, or `clients.openWindow(url)`.

### Page

- On explicit logout: unsubscribe locally and `DELETE` the subscription before the logout request.
- On sign-in, if this device has a subscription, re-`POST` it so the server ties it to the current user.
- Settings, Preferences tab, card "Push notifications" (registry id `push-notifications`) beside `notification-prefs`: enable on this device (asks for permission), current state, send a test, and checkboxes for the push filter using the existing inbox event labels. On iOS outside an installed home-screen app, a note that iOS only delivers push to installed apps. On an insecure context, the HTTPS explanation.

## Icons and manifest

- `scripts/frontend/gen-icons.mjs`, run by hand with `npx -p @resvg/resvg-js node scripts/frontend/gen-icons.mjs` so no native binary enters `npm ci` or the Docker build. It renders `frontend/public/favicon.svg` into `frontend/public/icons/`: `icon-192.png`, `icon-512.png` (transparent, the mark as is), `icon-maskable-512.png` (the mark at about 60% on `#0b0b0b`, inside the maskable safe zone), `apple-touch-icon-180.png` (on `#0b0b0b`), `badge-96.png` (white silhouette on transparent). The PNGs are committed.
- `manifest.webmanifest`: the new icons with the right `purpose`, keep the SVG `any` entry and the shortcuts. `index.html`: `apple-touch-icon` points at the 180 PNG.

## Docs and rules

- `README.md`: a short "Install as an app and read offline" section: needs HTTPS (reverse proxy); on plain HTTP the manifest still allows "Add to Home Screen" but nothing works offline; on iPhone and iPad, save for offline only in the installed app.
- New `.claude/rules/pwa.md` with `paths` covering `frontend/src/sw/**`, `frontend/src/pwa/**`, `frontend/src/lib/pwa/**`, `frontend/src/lib/connectivity.ts`, `frontend/vite/sw-plugin.ts`, `Services/*Push*.cs`, `Controllers/PushController.cs`. It records the routing table's pass-through list, the user-keying rule, the queue and replay rules, the 502/504 rule and the secure-context rule. `CLAUDE.md`'s rule-file list gains `pwa.md`.

## Implementation lanes

All on `feat/pwa`. Backend builds use `-p:SkipFrontendBuild=true` so a backend lane never runs `vite build` over a frontend lane's half-finished files. Implementers do not touch `locales/`. The lead runs `npx lingui extract` once after phase 2 and never translates.

| Phase | Lane | Model | Owns |
|---|---|---|---|
| 1 | A. Icons and manifest | Sonnet | `scripts/frontend/gen-icons.mjs`, `frontend/public/icons/*`, manifest, `index.html` |
| 1 | B. SW core and page plumbing | Opus | `frontend/src/sw/**` (with `push.ts` as a stub of two no-op listeners), `frontend/src/pwa/contract.ts`, `db.ts`, `frontend/src/lib/pwa/**`, `frontend/src/lib/connectivity.ts`, `vite.config.ts`, `frontend/vite/sw-plugin.ts`, `main.tsx`, `api/client.ts`, `lib/pageState.ts` (user messages), `api/auth.ts` (logout purge), `AuthProvider` and the offline first-load screen, AppShell banner, `check-secure-context.mjs`, and both settings registry entries plus `useSectionNodes` lines pointing at placeholder components `OfflineReadingSection.tsx` and `PushNotificationsSection.tsx` |
| 1 | C. Backend: progress semantics, `chapters/versions`, then push server | Opus | `ReaderService`, `ReaderController`, `ReadingSessionService`, `ReadingProgressService`, `ChapterProgress`, both migrations, push entity, controller, sender, `InboxService`, tests, `server.po`, `reader-progress.md`, `stats-notifications.md` |
| 2 | D. Offline chapters, queue replay, reader changes, offline UI | Opus | `frontend/src/pwa/offlineChapters.ts`, `progressQueue.ts`, `api/reader.ts`, reader files, series page, library filter, `OfflineReadingSection.tsx` |
| 2 | E. Push frontend | Sonnet | `frontend/src/sw/push.ts`, `PushNotificationsSection.tsx`, `lib/pwa/push.ts`, the push calls in the logout and sign-in hooks |
| 3 | Review | Opus, separate from implementers | the whole diff against this note |
| 3 | Verification | lead plus Sonnet | the checks below |

Phase 2 starts when B and C are committed. D and E share the worktree: their files do not overlap except E's two small additions to `api/auth.ts`, which E makes as one helper call each. E runs `tsc -b` only after D reports a clean state, or both accept transient errors from the other's files.

## Verification

On a backend from the worktree, port 8991, `MAKI_CONFIG_DIR` pointing at a copy of `.devconfig` (localhost is a secure context, so the SW runs):

1. Manifest parses, icons load, the SW controls the page after one reload, the app is installable.
2. On a fresh browser profile with no SW installed yet, sign in with a password and do not reload. Browse Library, a series page, open a chapter. Save two chapters for offline.
3. Stop the backend. Reload. The app boots signed in with the offline banner, Library and the visited series render, covers show, the saved chapters open and page through, an unsaved chapter shows "Not available offline", a mutation shows the offline toast.
4. Read several pages of a saved chapter offline, finish it, close the reader. The queue holds the rows.
5. Start the backend. Reopen the finished chapter straight away: it opens at the end and stays read. The queue drains, progress is not moved backwards when a newer position exists, and reading time appears once on the right day.
6. Sign out: browse caches go, saved chapters stay. Sign back in: they reappear.
7. Push: subscribe, send a test, trigger a new-chapter inbox event if practical. If the browser pane cannot reach a push service, rely on the server tests and say so.
8. Plain HTTP: load through the LAN IP. No SW registers, nothing throws, offline and push actions are hidden, the settings cards explain HTTPS.
9. `npx tsc -b`, `npm run lint`, `npm run check:secure-context`, `dotnet test`.

## Not in this work

- Background Fetch or Periodic Background Sync (Chromium only).
- Reading undownloaded (source-only) chapters offline. The reader cannot read them online either.
- Offline mutations other than reading progress. Bookmarks, ratings, read marks and settings stay online-only.
- Sub-path hosting.
