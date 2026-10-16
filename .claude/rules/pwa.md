---
paths:
  - "frontend/src/sw/**"
  - "frontend/src/pwa/**"
  - "frontend/src/lib/pwa/**"
  - "frontend/src/lib/connectivity.ts"
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

- Hand-rolled, built by `vite/sw-plugin.ts` as a nested build in `closeBundle` into `dist/sw.js` (IIFE, unhashed, `no-cache`). `precompress()` must stay after it in the plugin array or `sw.js.br` is missing.
- Navigations are network-first, so `skipWaiting` plus `clients.claim` is safe. Do not switch the shell to cache-first: an old `index.html` would point at deleted chunk hashes.
- Never `respondWith` for `/signalr/*`, `/api/v1/opds/*`, or non-GET requests other than the progress PUT. Never store a non-200, or HTML from an `/api/` path (the SPA fallback answers unknown `/api/` paths with 200 HTML).
- "Unreachable" is a rejected fetch or a 502/504 that is not JSON (`isUnreachable` in `sw/net.ts`). Maki's own 502s carry JSON and must not flip the app offline. A 503 is never unreachable.
- Served-from-cache responses carry `X-Maki-Offline: 1`. The synthetic failure is 503 `{ code: 'offline.unavailable' }`. Page code branches on that code, never on status.

## Per-user data

- Caches and IDB rows are keyed by user id; the cache is per origin, not per user. The SW learns the user from the page (`noteSignedInUser`/`noteSignedOut` post to it, after `serviceWorker.ready`) and cross-checks a live `/auth/me`. With no user it serves and stores nothing user-specific.
- A 401 clears the SW's user but keeps every cache: an expired session must not wipe saved chapters or queued progress. Explicit logout purges browse caches (data, covers) only. "Remove everything" (`purge-user`) also drops queued progress, so drain or warn first.

## Progress queue

- Two writers share the merge rules in `pwa/db.ts`: the SW captures a failed live PUT as a non-mergeable row that keeps the write's `opId` (it may have landed), and the page writes mergeable rows directly while offline. No row exceeds 900 s.
- Replay runs in the page (the SW cannot read the XSRF cookie), one at a time across tabs. It sends `completed: completed || undefined`, never `false`, and deletes a row only on 2xx, 404, or 400 `error.reader.pageOutOfRange`. Other 400s stay queued so a binding bug cannot eat the queue.
- The reader prefers a queued row over the manifest whenever one exists, online or offline, and seeds its finished state from it. Without that, reopening a chapter right after reconnecting writes the stale server position live and the queued completion arrives as stale.

## Saved chapters

- Pages are stored under exactly the URLs the reader requests, `?v={pageVersion}` included. A new `pageVersion` means a re-download, not a cache hit.
- Revalidation goes through `POST reader/chapters/versions`, at most hourly. A missing id means gone or no access: delete the copy. This is the only way revocation reaches a device.
