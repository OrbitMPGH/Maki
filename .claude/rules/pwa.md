---
paths:
  - "frontend/src/sw/**"
  - "frontend/src/pwa/**"
  - "frontend/src/lib/pwa/**"
  - "frontend/src/lib/connectivity.ts"
  - "frontend/src/lib/pageScroll.ts"
  - "frontend/vite/sw-plugin.ts"
  - "frontend/tsconfig.sw.json"
  - "frontend/src/components/settings/OfflineReadingSection.tsx"
  - "frontend/src/components/settings/PushNotificationsSection.tsx"
  - "frontend/src/auth/OfflineStartPage.tsx"
  - "frontend/src/components/OfflineBanner.tsx"
  - "frontend/public/manifest.webmanifest"
  - "scripts/frontend/gen-icons.mjs"
---

# PWA: service worker, offline reading, push

Design and rationale: `docs/design-pwa.md`. Push server side is in `stats-notifications.md`, progress replay semantics in `reader-progress.md`.

## Secure context

- Service workers, Cache Storage, Push, Notification and `navigator.storage` only exist on HTTPS or localhost. Most self-hosters use plain HTTP on the LAN, where all of this is absent and nothing may throw. Page code reaches these APIs only through `lib/pwa/*`; `check:secure-context` enforces it. `src/sw/**` is exempt because the worker only runs in a secure context.
- `pwaSupported()` gates every offline or push UI entry point. On an insecure context the two settings cards show `SecureContextNote` instead.
- Dev (`vite dev`) builds and registers no SW. Verify against a built `wwwroot` on localhost.

## Service worker

- The page's bootstrap requests on a first visit go out before the worker controls it. `registerServiceWorker` re-fetches `/initialize.json` and `auth/me` on `controllerchange` so both are cached for the next offline launch; a new boot-critical request needs the same.
- Hand-rolled, built by `vite/sw-plugin.ts` as a nested build in `closeBundle` into `dist/sw.js` (IIFE, unhashed, `no-cache`). `precompress()` must stay after it in the plugin array or `sw.js.br` is missing.
- Navigations are network-first, so `skipWaiting` plus `clients.claim` is safe. Do not switch the shell to cache-first: an old `index.html` would point at deleted chunk hashes.
- Never `respondWith` for `/signalr/*`, `/api/v1/opds/*`, or non-GET requests other than the progress PUT. Never store a non-200, or HTML from an `/api/` path (the SPA fallback answers unknown `/api/` paths with 200 HTML).
- "Unreachable" is a rejected fetch or a 502/504 that is not JSON (`isUnreachable` in `sw/net.ts`). Maki's own 502s carry JSON and must not flip the app offline. A 503 is never unreachable.
- Served-from-cache responses carry `X-Maki-Offline: 1`. The synthetic failure is 503 `{ code: 'offline.unavailable' }`. Page code branches on that code, never on status.

## Per-user data

- Caches and IDB rows are keyed by user id; the cache is per origin, not per user. The SW learns the user from the page (`noteSignedInUser`/`noteSignedOut` post to it, after `serviceWorker.ready`) and cross-checks a live `/auth/me`. With no user it serves and stores nothing user-specific.
- A 401 clears the SW's user and the page's queue user (`clearQueueUser`, also on `noteSignedOut`) but keeps every cache: an expired session must not wipe saved chapters or queued progress. Explicit logout purges browse caches (data, covers) only. "Remove everything" (`purge-user`) also drops queued progress, so drain or warn first.

## Progress queue

- Two writers share the merge rules in `pwa/db.ts`: the SW captures a failed live PUT as a non-mergeable row that keeps the write's `opId` (it may have landed), and the page writes mergeable rows directly while offline. A page row never exceeds 900 s; a captured row is one row as sent (the server clamps). A page write at or past the last page is stored as `completed`, since a later merge moves the position the server would derive it from.
- Replay runs in the page (the SW cannot read the XSRF cookie), one at a time across tabs. It first checks that a live `/auth/me` (not served offline) is the rows' user, and sends nothing otherwise. Replays carry `X-Maki-Replay: 1`, which the SW passes through without capturing. It sends `completed: completed || undefined`, never `false`, and deletes a row on 2xx, 403, 404, 410, or 400 `error.reader.pageOutOfRange`. Other 4xx keep the row and bump `attempts`; the fifth such refusal drops it. The reader's settle wait skips rows already refused.
- The reader seeds its resume page and finished state from queued rows only while offline. Online it relies on the replay: the server applies a stale completion unless the chapter was marked unread after it.

## Saved chapters

- Pages are stored under exactly the URLs the reader requests, `?v={pageVersion}` included. A new `pageVersion` means a re-download, not a cache hit.
- The manifest goes into `chapters` only after every page is stored, then older versions' pages are deleted, so a stale re-download leaves the old copy readable until the new one is complete. A live manifest overwrites the saved one only at the same `pageVersion`.
- The offline manifest fallback reads only `chapters`, never `data`: an unsaved chapter gets the synthetic 503 and the reader says "Not available offline" rather than showing pages that cannot load.
- Revalidation goes through `POST reader/chapters/versions`, at most hourly. A missing id means gone or no access: delete the copy. A null `pageVersion` means the file is unreadable right now (unmounted root): keep the copy. This is the only way revocation reaches a device.
