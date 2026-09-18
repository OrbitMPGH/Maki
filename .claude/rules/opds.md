---
paths:
  - "src/Maki.Api/Controllers/Opds*.cs"
  - "src/Maki.Api/Services/Opds*.cs"
  - "src/Maki.Api/Services/Kavita*Push*.cs"
---

# OPDS

Migrated out of the root CLAUDE.md so this only loads when touching OPDS code.

- **OPDS token lives in the path**; `ApiKeyMiddleware` carves `/api/v1/opds` out entirely and `OpdsController` re-checks the token itself (fixed-time compare) every action. Wrong/disabled token → **404 never 401**. Serves OPDS 1.2 (Atom) + PSE for page streaming, not 2.0. `pse:count` must be the chapter's **slice** length, not the archive's.
- **Path-borne tokens land in request logs** — `HttpRequestLogPolicy` drops `/api/v1/opds` below every configured level, ahead of the 5xx tier and regardless of `HttpRequestLogging` mode; `OpdsController` logs its own redacted line (rejections only). Don't "restore" normal logging there.
- **Chapter identity is `(Number, Language)`** but `ChapterLabel` renders only the number — `OpdsCatalogService.AmbiguousWithoutLanguage` appends `[en]`/`[es]` when a feed page would show duplicates.
- **An OPDS page fetch *is* the progress signal** (`opds.trackprogress`, default on) — streaming a page writes through the same `ReaderService.SaveProgressAsync` as the native reader. Deviation: fetching the **last** page with no prior progress row stores `Completed = false` explicitly (readers prefetch the last page to size their page bar; without this it'd falsely mark the chapter read).
- **Kavita push-back** (`reader.pushtokavita`, default off) gated on `KavitaSeriesId != null` — pushing an un-adopted native row's echo would land in a different row and double-count into Rewind.

- **Feed titles are still English.** The catalogue is served through the same locale resolution as everything else (`?lang=` wins, then `X-Maki-Language`, then the token user's `ui.language`, then `Accept-Language`), so the plumbing is there; the shelf names in `OpdsCatalogService` have simply not been keyed yet.
- The token user's own `ui.language` deliberately outranks `Accept-Language`: whoever pasted the feed URL into Panels or Chunky is whose preference it is. `?lang=` exists so a reader app that sends no `Accept-Language` can be pinned by editing the URL.
- **Some OPDS clients cache feeds**, so a language change will not retro-fix a navigation feed already fetched. Not worth solving.
