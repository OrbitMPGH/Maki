---
paths:
  - "src/Maki.Api/Controllers/Opds*.cs"
  - "src/Maki.Api/Services/Opds*.cs"
  - "src/Maki.Api/Services/Kavita*Push*.cs"
  - "src/Maki.Core/Opds/**"
---

# OPDS

Token scope, the 404-never-401 rule and the `OpdsAccessService.EvictUser` cache rule are in `auth.md`, which loads for the same files.

- **The OPDS token lives in the path.** `ApiKeyAuthenticationHandler` returns `NoResult` when there is no `X-Api-Key` header, so OPDS requests fall through and `OpdsController` resolves the path token itself on every action through `OpdsAccessService` (SHA-256 digest lookup on `UserApiKeys`, no stored secret, so no fixed-time compare). Serves OPDS 1.2 (Atom) + PSE for page streaming, not 2.0. `pse:count` must be the chapter's **slice** length, not the archive's.
- **Path-borne tokens land in request logs.** `HttpRequestLogPolicy` drops `/api/v1/opds` below every configured level, ahead of the 5xx tier and regardless of `HttpRequestLogging` mode; `OpdsController` logs its own redacted line (rejections only). Don't "restore" normal logging there.
- **Chapter identity is `(Number, Language)`** but `ChapterLabel` never renders the language, so `OpdsCatalogService.AmbiguousWithoutLanguage` appends `[en]`/`[es]` when a feed page would show duplicates.
- **An OPDS page fetch *is* the progress signal** (`opds.trackprogress`, default on). `OpdsProgressWriter` writes it off the response path through the native reader's `ReaderService.SaveProgressAsync`, folding concurrent fetches for the same user and chapter. Readers prefetch the last page to size their page bar, so `OpdsProgressPolicy` makes a last-page fetch with no prior progress row write nothing; a one-page chapter instead stores `Completed = false` so its next fetch can finish it.
- **Kavita push-back** (`reader.pushtokavita`, default off) is gated on `KavitaSeriesId != null`: pushing an un-adopted native row's echo would land in a different row and double-count into Rewind.
- **Feed text goes through the scoped `ILocalizer`.** Locale order: `?lang=`, then `X-Maki-Language`, then the token user's `ui.language`, then `Accept-Language`. The user's own `ui.language` deliberately outranks `Accept-Language` (whoever pasted the URL into Panels or Chunky owns the preference); `?lang=` pins a reader app that sends no `Accept-Language`. English in a feed means an untranslated `msgstr`, not a missing key.
- **Some OPDS clients cache feeds**, so a language change will not retro-fix a navigation feed already fetched. Not worth solving.
