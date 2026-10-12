# Suwayomi as an optional source: plan

Status: proposal, no application code changed. Researched against `dev` (f293475) and Suwayomi-Server `v2.4.2366` (latest stable, 2026-09-29), with `v2.1.1867` and `master` (d4f88a3) checked for drift.

## Goal

An admin who already runs Suwayomi (or is willing to) points Maki at it, picks which of Suwayomi's installed sources Maki should use, and those sources then behave like any built-in source: they can be searched, auto-matched, prioritised, switched off, synced, and downloaded from. Maki talks only to Suwayomi's GraphQL API and its page/thumbnail endpoints. Extensions are installed and configured in Suwayomi's own UI.

With no Suwayomi configured nothing changes: no new sources, no new background work, no new health checks, and no extra settings beyond an empty integration card.

## The shape decision: one Maki source per Suwayomi source

There are two ways to model this.

1. **One `suwayomi` source** that fans out across every Suwayomi source internally, with the Suwayomi source id folded into `SourceSeriesId`.
2. **One virtual `ISource` per Suwayomi source the admin exposes**, named `suwayomi-{sourceId}`.

Option 2 is the recommendation. Almost everything in Maki is keyed on `SourceName`, and option 1 would collapse all of Suwayomi onto one key:

- `SourceMapping` is unique on `(SeriesId, SourceName)`, so option 1 allows only one Suwayomi mapping per series. You could not keep "Bato via Suwayomi" as a fallback behind "MangaSee via Suwayomi".
- Priority order, the global on/off switch (`sources.disabled`), language ranking (`SourceLanguagePreference`), quality samples (`SourceQualitySamples`), rate-limit cooldowns and health rows are all per source name. Under option 1 one broken extension would put every Suwayomi source in cooldown.
- `ChapterFile.SourceName` records where a file came from. "suwayomi" says nothing useful.

Suwayomi source ids are the standard Mihon ids (a hash of name, language and version id), so `suwayomi-{sourceId}` is stable across Suwayomi reinstalls and across instances that have the same extension. A mapping survives a Suwayomi rebuild as long as the extension is reinstalled.

The cost of option 2 is that `SourceRegistry` stops being a fixed list built once from DI. That is the main piece of core work, covered below.

## What Suwayomi gives us

Verified in Suwayomi's code-first schema (`server/src/main/kotlin/suwayomi/tachidesk/graphql/`). Every field used below is identical in v2.1, v2.4 and master.

| Maki need | Suwayomi call |
| --- | --- |
| Version check | `query { aboutServer { name version buildType } }`, works anonymously except under basic auth |
| List installed sources | `query { sources { nodes { id name lang displayName iconUrl isNsfw contentWarning supportsLatest extension { pkgName name versionName } } } }` |
| `SearchAsync` | `mutation { fetchSourceManga(input:{source:"<id>", type:SEARCH, page:1, query:"..."}) { hasNextPage mangas { id url title thumbnailUrl author description realUrl } } }` |
| Find a manga we already know | `query { mangas(condition:{sourceId:"<id>", url:"<url>"}) { nodes { id } } }` |
| `GetSeriesAsync` + `ListChaptersAsync` | `mutation { fetchMangaAndChapters(input:{id:<mangaId>, fetchManga:true, fetchChapters:true}) { manga {...} chapters { id url name chapterNumber scanlator uploadDate sourceOrder } } }` (v2.4+; v2.1 needs `fetchManga` + `fetchChapters`, deprecated but still present) |
| `GetPagesAsync` | `mutation { fetchChapterPages(input:{chapterId:<id>}) { pages } }` returns relative URLs `/api/v1/manga/{mangaId}/chapter/{sourceOrder}/page/{n}` |
| Page bytes | `GET` those URLs with auth. Suwayomi fetches through the extension's own client (headers, Referer, Cloudflare handling) and caches the image |
| Cover | `GET /api/v1/manga/{id}/thumbnail` with auth |

Facts that shape the design:

- **Long values are strings** (`LongString` scalar). Source ids must be sent and stored as strings.
- **Manga and chapter `id`s are local autoincrement ints.** Every `fetchSourceManga` result is upserted into Suwayomi's DB keyed on `(sourceId, url)`, so ids are stable within one instance but mean nothing on another.
- **`manga.url` is the portable key.** There is no "resolve by URL" mutation: a manga not in Suwayomi's DB can only get back in by being found again through `fetchSourceManga`.
- **Page URLs depend on `sourceOrder`**, which shifts when chapters are added or removed. Resolve pages at download time (Maki already requires this for every source) and never store them.
- **Errors under login modes arrive as HTTP 200** with `errors[].message` containing "Unauthorized". REST endpoints (pages, thumbnails) answer 401.
- **`server.webUISubpath` prefixes the API** (`/suwayomi/api/graphql`), but the page and thumbnail paths Suwayomi returns are hardcoded `/api/v1/...` without it. The configured base URL must include the subpath, and returned paths get resolved against it.
- Suwayomi does no API-level rate limiting. Each extension throttles its own site.

### Authentication modes

| `server.authMode` | Headless client |
| --- | --- |
| `none` | Nothing |
| `basic_auth` | `Authorization: Basic` on every request, images included |
| `ui_login` (v2.2+) | `login(input:{username,password}) { accessToken refreshToken }`, then `Authorization: Bearer`. Access token lives 5 minutes by default, refresh token 60 days and is not rotated. Refresh with `refreshToken(input:{refreshToken})` |
| `simple_login` | `POST /login.html` form (`user`, `pass`), keep `JSESSIONID`. Session is in-memory, dies after 30 minutes or a restart |

v1 supports `none`, `basic_auth` and `ui_login`. `simple_login` is a cookie-session variant of the same retry loop and can follow if anyone asks. One refresh at a time behind a `SemaphoreSlim`, the same pattern `MangaTubeSession` uses; treat both a 401 and a 200 with "Unauthorized" as "refresh once, then retry once".

Suwayomi master (unreleased) adds multi-user accounts with permissions. When that ships, Maki should log in as a user that can read sources; installing extensions is not something Maki does, so no extra permission is needed.

## Design

### 1. Dynamic source registry (Maki.Core)

`SourceRegistry` today is a dictionary built once from `IEnumerable<ISource>`. All of its consumers (`All`, `Find`, `GetRequired`) read it at call time, and a mapping whose source is missing is already skipped with a warning (`ChapterSyncService`, `ChapterSourceResolver`), so making it dynamic is contained.

- Add `ISourceProvider` in `Maki.Core/Sources/`: `IReadOnlyList<ISource> Current { get; }` plus a change notification (an `event` or a version counter).
- `SourceRegistry` takes the static sources and the providers, and holds an immutable snapshot that it swaps atomically when a provider reports a change. Static sources win on a name clash (they cannot clash given the `suwayomi-` prefix, but the rule should be explicit).
- The list of exposed Suwayomi sources is **persisted in settings**, not read live from Suwayomi. The registry is therefore the same at startup whether Suwayomi is up or not, `SetSourcePriority`'s unknown-name check keeps passing, and a Suwayomi outage shows up as failing sources rather than vanishing ones.

New setting keys in `SettingKeys`:

| Key | Value |
| --- | --- |
| `suwayomi.url` | Base URL including any subpath, e.g. `http://suwayomi:4567` |
| `suwayomi.authmode` | `none`, `basic`, `uilogin` |
| `suwayomi.username` / `suwayomi.password` | Never returned by GET, only `passwordSet`, same as the Kitsu password. Null on PUT means unchanged |
| `suwayomi.sources` | JSON array of exposed sources: `{ id, name, displayName, lang, chapterLanguage, contentWarning }`. A snapshot so the registry can be built without calling Suwayomi |

### 2. The virtual source (Maki.Sources/Suwayomi)

New folder, matching the existing one-folder-per-source layout:

- `SuwayomiClient`: GraphQL POST over the `suwayomi` named `HttpClient`, auth handling, error mapping, version probe. One client per instance.
- `SuwayomiSource : ISource`: one instance per exposed source, constructed from the settings snapshot plus the shared client.
- `SuwayomiSourceProvider : ISourceProvider`: reads `suwayomi.sources`, rebuilds the list when the setting changes.
- `SuwayomiMangaIdCache`: `(sourceId, url) -> mangaId` and `(mangaId, chapterUrl) -> chapterId`, in memory. Cleared when `suwayomi.url` changes.

Field mapping:

| `ISource` | From Suwayomi |
| --- | --- |
| `Name` | `suwayomi-{sourceId}` |
| `DisplayName` | `{displayName}` with a "via Suwayomi" tag added by the UI, not baked into the name |
| `BaseUrl` | The configured Suwayomi URL (used as Referer and for the cover policy, see below) |
| `SupportedLanguages` | `[chapterLanguage]` |
| `Rating` | `contentWarning` NSFW → `Adult`, MIXED → `Mature`, else `General`. Fall back to `isNsfw` on v2.1, which has no `contentWarning` |
| `Kind` / `Content` | Default `Aggregator` / `Manga`. Suwayomi does not know, and guessing per extension is not worth it in v1 |
| `Capabilities` | `None`. Suwayomi handles Cloudflare itself (`server.flareSolverrEnabled`) |
| `SourceSeriesId` | `manga.url` (source-relative, portable) |
| `SourceSeriesResult.Url` / `SourceSeriesDetail.Url` | `realUrl`, so "open on site" links go to the real site |
| `SourceChapterId` | `chapter.url` |
| `SourceChapter.Number` | `chapterNumber`, with `-1` (Mihon's "unknown") mapped to null |
| `NumberRaw` / `Title` | `name` |
| `Volume` | Parsed from `name` with the shared `ChapterNumberParser` (Mihon chapter names are often `Vol.3 Ch.15`) |
| `Language` | The exposed source's `chapterLanguage` |
| `ReleaseDate` | `uploadDate` (epoch ms as a string), 0 → null |
| `Group` | `scanlator` |
| `ExternalIds` | None. Mihon's manga model carries no tracker ids, so the title pass alone decides auto-match |

Chapters go through `SourceChapterList.Normalize` like every other source.

**Chapter language.** Mihon chapters have no language field; the language is the source's. Most extensions register one source per language (`MangaDex (EN)`, `MangaDex (ES)`), so `lang` is right. A few use `all` or `multi`, where we cannot know. The source picker makes the admin choose a chapter language for those before they can be exposed, rather than defaulting to `en`. Language is half of chapter identity and lands in the CBZ's `LanguageISO`, so a wrong default would file chapters in the wrong place, the same failure `Maki.Sources/CLAUDE.md` warns about for MANGA Plus.

**Resolving a stored mapping to Suwayomi ids.** `GetSeriesAsync`, `ListChaptersAsync` and `GetPagesAsync` get a portable url and need a local id:

1. Cache hit → use it.
2. `mangas(condition:{sourceId, url})` → cache and use.
3. Miss means this Suwayomi instance has never seen the manga (wiped DB, new instance). Throw a `SourceErrorException` saying the manga is not in this Suwayomi instance and the series needs re-linking, so the mapping's `LastError` and the health page say why. (`LastError` is plain English today, see the note on `SourceMapping.LastError`.) Do not search by title to recover: `GetSeriesAsync` has no title to search with, and a fuzzy recovery that picks the wrong manga downloads the wrong series.

`GetPagesAsync` resolves the chapter id with `chapters(condition:{mangaId})` and matches on `url`. If the chapter is not there (Suwayomi's list is older than Maki's), run `fetchMangaAndChapters` once and retry before giving up with a 404 `HttpRequestException`, so the download moves to the next source as it does elsewhere.

**Search.** `fetchSourceManga` with `type: SEARCH`, page 1 only, matching what the built-in sources do. Each call upserts results into Suwayomi's DB as non-library rows. That is what Suwayomi's own browse screen does and is harmless, but it is worth one sentence in the docs.

### 3. Networking: LAN access without opening the SSRF guard

Suwayomi almost always sits on the LAN or a Docker network. Two things in Maki deliberately refuse private addresses:

- The `pages` `HttpClient` used by `PageDownloader` runs `PublicAddressGuard` at connect time.
- The cover proxy (`GET search/cover`) refuses private destinations as well as hosts outside `CoverHostPolicy`.

Neither guard should be loosened. Both exist because page and cover URLs come from third-party sites. The Suwayomi URL is different: an admin typed it, the same way they type the Kavita and FlareSolverr URLs, which already use guard-free clients.

- **New `suwayomi` named `HttpClient`** without `PublicAddressGuard`, like the Kavita client. Every request it sends is checked against the configured origin first; the client refuses anything else. Add a per-client concurrency limit (4 in flight by default) since every exposed source shares this one host.
- **Pages.** Add an optional seam to `PageDownloader`: an `IPageFetcher` interface a source may implement (`Task<Stream> FetchAsync(PageRequest, CancellationToken)`). `PageDownloader` uses it when the source implements it and falls back to its own client otherwise. `SuwayomiSource` implements it with the `suwayomi` client and the origin check. All four page consumers (`ChapterDownloadProcessor`, `SeriesPreviewService`, `SourceComparePreviewService`, `SourceProbeService`) already go through `PageDownloader`, so this one seam covers them. The alternative, filling `PageRequest.Data` inside `GetPagesAsync`, would hold a whole chapter in memory before the first page is written and skip `PageDownloader`'s retry and rate-limit handling, so it is not recommended.
- **Covers.** `SuwayomiSource` returns cover URLs pointing at a new Maki endpoint, `GET /api/v1/suwayomi/thumbnail/{sourceId}/{mangaId}`, which fetches `/api/v1/manga/{mangaId}/thumbnail` from the configured origin with auth. The caller only chooses two integers, so this is not an SSRF primitive. `SearchController.ProxiedCoverUrl` must pass a Maki-relative URL through unchanged instead of wrapping it in `search/cover`. The endpoint needs an entry in `ControllerAuthorizationInventoryTests`.

### 4. Settings and UI

**Integration card** (`frontend/src/pages/settings/IntegrationCards.tsx`, next to Kavita):

- URL, auth mode, username, password.
- "Test connection": calls `aboutServer` and `sources`, shows the server version and how many sources are installed. Below v2.1 it reports the version as unsupported. With `ui_login` the test also performs a login so a wrong password shows up here, not later in a sync.
- Empty source list: explain that extensions are installed in Suwayomi, and link to the configured Suwayomi URL.

**Source picker** (a modal from the card, structured like `ManageSourcesModal`):

- Every installed Suwayomi source with icon, display name, language and content warning. Grouped by language, filtered by the instance's enabled languages by default, since a typical Suwayomi install has hundreds of language variants.
- A checkbox per source. Nothing is exposed until the admin ticks it.
- `all`/`multi` sources require a chapter language to be chosen before they can be ticked.
- Unticking a source that has mappings warns with the count. The mappings are kept and simply stop running (existing unknown-source behaviour), so re-ticking restores them.
- Sources exposed earlier but no longer installed in Suwayomi are listed as missing rather than dropped silently.

Once exposed, the sources appear in the normal priority list with a small "Suwayomi" badge (an extra field on `GET search/sources`, e.g. `provider: "suwayomi"`). They are ordered after every listed source by default, because `OrderSources` already ranks unlisted names after listed ones. A built-in source and its Suwayomi twin (MangaDex both ways, say) coexist; the admin's priority order decides.

All new strings through `t`/`<Trans>` and `this.Fail(localizer, key)`.

**Endpoints** (admin only, in `SettingsController` per the existing per-action rule, or a small `SuwayomiController`):

| Endpoint | Purpose |
| --- | --- |
| `GET/PUT settings/suwayomi` | Connection settings |
| `POST settings/suwayomi/test` | Version + source count |
| `GET settings/suwayomi/sources` | Installed sources merged with the exposed snapshot (live + missing) |
| `PUT settings/suwayomi/sources` | Save the exposed set; triggers the provider's rebuild |
| `GET suwayomi/thumbnail/{sourceId}/{mangaId}` | Cover passthrough, any signed-in user |

### 5. Health

- `HealthMonitor` probes Suwayomi (`aboutServer`) only when at least one exposed source is enabled, the same gating the FlareSolverr probe uses with `NeedsFlareSolverr`.
- A periodic (hourly is plenty) refresh compares the exposed snapshot with `sources` and flags exposed sources whose extension was uninstalled, so their mappings report "extension missing in Suwayomi" rather than a generic failure.
- Suwayomi down means each Suwayomi source's calls throw `HttpRequestException`, and the existing per-source failure, cooldown and `LastError` handling applies unchanged.

### 6. Auto-match cost

Each exposed source is one more entry in `SourceMatchService`'s fan-out, already capped by `MaxParallelSources`, plus the `suwayomi` client's own concurrency limit. Exposing 30 sources means 30 searches per added series, each of which Suwayomi forwards to a real site. Two things keep this sane: exposure is opt-in per source, and the picker shows the number exposed with a short note that each one adds a search per added series. No extra logic is needed in the matcher.

## Out of scope for v1

- **Suwayomi's own download queue** (`enqueueChapterDownloads`). Maki fetching pages on demand through Suwayomi is simpler and keeps one download pipeline. Suwayomi caches fetched images anyway.
- **Installing or updating extensions from Maki.** Suwayomi's UI already does this, and it needs an extension store configured first (`server.extensionStores` defaults to empty).
- **Source filters and preferences** (`source.filters`, `updateSourcePreference`). Configure them in Suwayomi.
- **Pasting a site URL to link** (`ResolveSeriesIdFromUrl`). Suwayomi has no resolve-by-URL call; it would need a host match plus a title search plus a url compare. Possible later.
- **Browsing Popular/Latest in Discover.** Possible later via `fetchSourceManga` `POPULAR`/`LATEST`.
- **Several Suwayomi instances.** One URL in v1; the `suwayomi-{sourceId}` name would need an instance segment to support more.
- **WebSocket subscriptions.** Nothing in this plan needs push.

## Delivery

Each phase is shippable and leaves a non-Suwayomi install unchanged.

1. **Registry seam.** `ISourceProvider`, dynamic `SourceRegistry`, tests that a provider change is visible through `All`/`Find` and that a vanished source's mappings are skipped. No user-visible change.
2. **Client and source.** `SuwayomiClient` (all three auth modes, retry-once on Unauthorized, single-flight refresh), `SuwayomiSource`, id cache, `IPageFetcher` seam in `PageDownloader`, `suwayomi` named client. Tests on recorded GraphQL responses through `FakeHttpClientFactory`, covering v2.1 and v2.4 shapes, the 200-with-Unauthorized case, the `-1` chapter number, an `all`-language source, a manga missing from Suwayomi, and the client refusing a URL outside the configured origin.
3. **Settings and endpoints.** Setting keys, controller actions, thumbnail passthrough, `ControllerAuthorizationInventoryTests` entries, `GET settings/suwayomi` never returning the password.
4. **UI.** Integration card, source picker, Suwayomi badge on the priority list, strings extracted.
5. **Health and docs.** Probe, missing-extension refresh, a `Maki.Sources/CLAUDE.md` entry with the gotchas above (portable url ids, `sourceOrder`-based page URLs, 200-with-Unauthorized, subpath, chapter language for `all` sources), an optional `suwayomi` service in the Docker compose example, and a release-checklist line for a smoke test against a real instance.

Phase 2's new files under `src/Maki.Sources/Suwayomi/` are covered by `Maki.Sources/CLAUDE.md`. `PageDownloader` (`src/Maki.Core/Download/`) matches no rule file today, so the `IPageFetcher` seam's gotcha (the origin check is what keeps it from being a guard bypass) should go in `downloads.md` with that path added to its frontmatter. The thumbnail endpoint belongs next to the cover-proxy note in `auth.md`.

## Open questions

- **Minimum supported Suwayomi version.** v2.1 works for every call above except `fetchMangaAndChapters` (fall back to the two deprecated calls) and `ui_login` (unavailable before v2.2). Supporting v2.1 costs one code path; requiring v2.2 is simpler. Recommendation: support v2.1 for `none`/`basic`, require v2.2 for `ui_login`.
- **`simple_login`.** Leave out until someone asks?
- **`Kind`/`Content` per exposed source.** Let the admin set them in the picker so the source tags are right, or keep the defaults? Defaults are fine for v1.
