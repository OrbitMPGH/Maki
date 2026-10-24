---
paths:
  - "src/Maki.Api/Controllers/Komga*.cs"
  - "src/Maki.Api/Komga/**"
  - "src/Maki.Core/Komga/**"
  - "tests/Maki.Api.Tests/Komga*.cs"
---

# Komga-compatible API

`opds.md` and `auth.md` cover the token this rides on; the rules there still apply.

- **It lives under `/komga/api/...`, not `/api/v1`.** Komga clients take a server address and append `/api/v1/...` themselves, which would collide with Maki's own routes. The address a user enters is `http://host:port/komga`. The catch-all `Unsupported` action answers 404 for Komga routes Maki does not serve, or the SPA fallback would hand clients `index.html` with a 200.
- **The credential is the user's OPDS token, and both `opds.enabled` and `komga.enabled` must be on.** `KomgaAuthenticator` accepts it three ways: `X-API-Key` (Komga's API keys), HTTP Basic with the token as the password (username ignored), and the `Maki.Komga` cookie it issues after either. A `Full` API key does not open it. `OpdsAccess.Komga` carries the switch, so `OpdsAccessService.EvictUser` covers it.
- **401, never OPDS's 404.** The Mihon extension only sends Basic after a 401 (OkHttp's authenticator), and a 404 reads as "no server here".
- **The cookie is load-bearing.** The Mihon Komga tracker shares the source's cookie jar and sends no credential of its own, so without it two-way sync fails with 401. It holds the token under data protection (30 days), so revocation, rotation and either switch apply to it at once. It never authenticates a POST: that is the one unsafe method another app on the same site can send without a CORS preflight.
- **Page fetches are not progress here**, unlike OPDS. Komga clients that track reading say so: `PATCH books/{id}/read-progress` (pages are 1-based), and the tracker's `PUT v2/series/{id}/read-progress/tachiyomi`. Both go through `ReaderService`, so incognito, stats, scrobbling and Kavita push-back behave as for the built-in reader.
- **A tracker update of one chapter is a read, a jump is a silent mark.** `ApplyTrackerProgressAsync` records a single newly crossed `numberSort` group through `SaveProgressAsync`, and marks several through `MarkReadAsync` (`BulkMarked`), so binding the tracker to a series read elsewhere does not land in Rewind as a day's reading. It only marks read, never unread, as Komga does.
- **`numberSort` is what Mihon uses as the chapter number and syncs on** (`KomgaNumbering`). It must never decrease along `ChapterOrder`: unnumbered chapters continue the sequence, and a volume's chapter 0 sorted after higher numbers repeats the previous value. Books sharing one (the same chapter in two languages) count as one group, read when any of them is.
- **Every DTO field is always written, nulls included.** The Mihon extension and tracker use kotlinx.serialization, which rejects a missing key even for a nullable property. Don't add `JsonIgnore(WhenWritingNull)` to anything in `KomgaDtos.cs`.
- **`?convert=jpeg|png` is honoured on page fetches**, because it is how a Komga client copes with a media type it cannot decode (the Komga web reader asks for JPEG when the browser lacks WebP, AVIF or JXL). The page's bytes decide, not its name: one already in that format goes out untouched, anything else is re-encoded at full size through `ImageCodec.Transcode` under `ImageWorkGate`. Any other value is a 400 as on Komga; a page no permitted loader reads (BMP) is a 415. The ETag carries the format, or a cached original would answer a conversion request. The book thumbnail's first-page fallback always asks for JPEG, since Komga thumbnails are JPEG.
