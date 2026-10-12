# Suwayomi as an optional source: plan

Status: proposal, no application code changed. Researched against `dev` (f293475) and Suwayomi-Server `v2.4.2366` (latest stable, 2026-09-29), with `v2.1.1867` and `master` (d4f88a3) checked for drift. Revision 2 after review.

## Goal

An admin who already runs Suwayomi (or is willing to) points Maki at it, picks which of Suwayomi's installed sources Maki should use, and those sources then behave like any built-in source: they can be searched, auto-matched, prioritised, switched off, synced, and downloaded from. Maki talks only to Suwayomi's GraphQL API and its page/thumbnail endpoints. Extensions are installed and configured in Suwayomi's own UI.

With no Suwayomi configured nothing changes: no new sources, no new background work, no new health checks, and no extra settings beyond an empty integration card.

## The shape decision: one Maki source per Suwayomi source

There are two ways to model this.

1. **One `suwayomi` source** that fans out across every Suwayomi source internally, with the Suwayomi source id folded into `SourceSeriesId`.
2. **One virtual `ISource` per Suwayomi source the admin exposes**, named `suwayomi-{sourceId}`.

Option 2 is the recommendation. Almost everything in Maki is keyed on `SourceName`, and option 1 would collapse all of Suwayomi onto one key:

- `SourceMapping` is unique on `(SeriesId, SourceName)`, so option 1 allows only one Suwayomi mapping per series. You could not keep "Bato via Suwayomi" as a fallback behind "MangaSee via Suwayomi".
- Priority order, the global on/off switch (`sources.disabled`), language ranking (`SourceLanguagePreference`), quality samples (`SourceQualitySamples`), rate-limit cooldowns (`DownloadQueueService`) and health rows are all per source name. Under option 1 one broken extension would put every Suwayomi source in cooldown.
- `ChapterFile.SourceName` records where a file came from. "suwayomi" says nothing useful.

Suwayomi source ids are the standard Mihon ids (a hash of name, language and version id, always a non-negative long), so `suwayomi-{sourceId}` is path-safe and stable across Suwayomi reinstalls and across instances that have the same extension. A mapping survives a Suwayomi rebuild as long as the extension is reinstalled.

The cost of option 2 is that `SourceRegistry` stops being a fixed list built once from DI. That is contained: every consumer reads `All`/`Find` at call time, no other singleton captures `IEnumerable<ISource>`, no Quartz job holds an `ISource`, every cache is keyed by source name string, and mappings to a missing source are already skipped (`ChapterSyncService`, `ChapterSourceResolver`, `UpgradeScanService`, `SourceScoutService`).

## What Suwayomi gives us

Verified in Suwayomi's code-first schema (`server/src/main/kotlin/suwayomi/tachidesk/graphql/`). Every field used below is identical in v2.1, v2.4 and master unless noted.

| Maki need | Suwayomi call |
| --- | --- |
| Version check | `query AboutServer { aboutServer { name version buildType } }`, works anonymously except under basic auth |
| List installed sources | `query Sources { sources { nodes { id name lang displayName isNsfw contentWarning supportsLatest extension { pkgName name versionName } } } }` (`contentWarning` is v2.4+; v2.1 has `isNsfw` only) |
| `SearchAsync` | `mutation Search { fetchSourceManga(input:{source:"<id>", type:SEARCH, page:1, query:"..."}) { hasNextPage mangas { id url title thumbnailUrl author description realUrl } } }` |
| Find a manga we already know | `query MangaByUrl { mangas(condition:{sourceId:"<id>", url:"<url>"}) { nodes { id } } }` |
| `GetSeriesAsync` + `ListChaptersAsync` | `mutation Refresh { fetchMangaAndChapters(input:{id:<mangaId>, fetchManga:true, fetchChapters:true}) { manga {...} chapters { id url name chapterNumber scanlator uploadDate sourceOrder } } }` (v2.4+; v2.1 needs `fetchManga` + `fetchChapters`, deprecated but present) |
| Find a chapter we already know | `query ChapterByUrl { chapters(condition:{mangaId:<id>, url:"<url>"}) { nodes { id } } }` |
| `GetPagesAsync` | `mutation Pages { fetchChapterPages(input:{chapterId:<id>}) { pages } }` returns relative URLs `/api/v1/manga/{mangaId}/chapter/{sourceOrder}/page/{n}` |
| Page bytes | `GET` those URLs with auth. Suwayomi fetches through the extension's own client (headers, Referer, Cloudflare handling) and caches the image |
| Cover | `GET /api/v1/manga/{id}/thumbnail` with auth |

Every query is sent with an operation name (as above), which is what the test stubs match on.

Facts that shape the design:

- **Long values use the `LongString` scalar** and arrive as strings. Parse both string and number defensively; always send source ids as strings.
- **Manga and chapter `id`s are local autoincrement ints.** Every `fetchSourceManga` result is upserted into Suwayomi's DB keyed on `(sourceId, url)`, so ids are stable within one instance but mean nothing on another.
- **`manga.url` is the portable key.** It is an extension-defined string, sometimes a path, sometimes an absolute URL. Nothing in Maki puts `SourceSeriesId`/`SourceChapterId` into a file path, and the columns are unbounded `TEXT`, so either shape is safe. There is no "resolve by URL" mutation: a manga missing from Suwayomi's DB only gets back in by being found again through `fetchSourceManga`.
- **`realUrl` and `thumbnailUrl` are nullable.**
- **Page URLs depend on `sourceOrder`**, which shifts when chapters are added or removed. Resolve pages at download time (Maki already requires this for every source) and never store them.
- **Errors under login modes arrive as HTTP 200** with `errors[].message` containing "Unauthorized". REST endpoints (pages, thumbnails) answer 401.
- **`server.webUISubpath` prefixes the API** (`/suwayomi/api/graphql`), but the page and thumbnail paths Suwayomi returns are hardcoded `/api/v1/...` without it. The configured base URL must include the subpath, and returned paths get resolved against it into absolute URLs before they leave the source.
- Suwayomi does no API-level rate limiting. Each extension throttles its own site.

### Authentication modes

| `server.authMode` | Headless client |
| --- | --- |
| `none` | Nothing |
| `basic_auth` | `Authorization: Basic` on every request, images included |
| `ui_login` (v2.2+) | `login(input:{username,password}) { accessToken refreshToken }`, then `Authorization: Bearer`. Lifetimes are Suwayomi settings (`jwtTokenExpiry`, `jwtRefreshExpiry`), so Maki never assumes one. Refresh with `refreshToken(input:{refreshToken})`; when the refresh token is refused too, log in again |
| `simple_login` | `POST /login.html` form (`user`, `pass`), keep `JSESSIONID`. Session is in-memory, dies after ~30 minutes or a restart |

v1 supports `none`, `basic_auth` and `ui_login`. `simple_login` is a cookie-session variant of the same retry loop and can follow if anyone asks.

Auth lives in one `SuwayomiAuthHandler` (a `DelegatingHandler` on the `suwayomi` named client), not in each call site. It attaches the credential to every request, GraphQL and image alike, and on a 401 or a GraphQL 200 whose errors say "Unauthorized" it refreshes once and retries once. Refresh is single-flight behind a `SemaphoreSlim`, the same pattern `MangaTubeSession` uses, and is skipped when another caller already swapped in a newer token. Because the handler sits on the client, page fetches get auth and refresh for free, and a JWT that expires halfway through a 200-page chapter is handled without the source baking a token into `PageRequest.Headers`. Suwayomi also accepts `?token=` on REST; Maki never uses it, for the reason `auth.md` bans `?apikey=`.

Suwayomi master (unreleased) adds multi-user accounts with permissions. When that ships, Maki should log in as a user that can read sources; installing extensions is not something Maki does, so no extra permission is needed.

## Design

### 1. Dynamic source registry (Maki.Core)

- Add `ISourceProvider` in `Maki.Core/Sources/`: `IReadOnlyList<ISource> Current { get; }` and an explicit `Task ReloadAsync(CancellationToken)`.
- `SourceRegistry` takes the static sources plus the providers and holds an immutable snapshot that it swaps atomically after a provider reloads. Static sources win on a name clash (they cannot clash given the `suwayomi-` prefix, but the rule should be explicit). **Keep the existing single-argument constructor as an overload**: 51 tests build `new SourceRegistry([...])`.
- `IAppSettings` has no change notification, so reloads are explicit: `PUT settings/suwayomi/sources` and `PUT settings/suwayomi` call `ReloadAsync` after saving. A URL change also clears `SuwayomiMangaIdCache`. Startup loads once.
- The list of exposed Suwayomi sources is **persisted in settings**, not read live from Suwayomi. The registry is therefore the same at startup whether Suwayomi is up or not, `SetSourcePriority`'s unknown-name check keeps passing, and a Suwayomi outage shows up as failing sources rather than vanishing ones.

New setting keys in `SettingKeys`:

| Key | Value |
| --- | --- |
| `suwayomi.url` | Base URL including any subpath, e.g. `http://suwayomi:4567` |
| `suwayomi.authmode` | `none`, `basic`, `uilogin` |
| `suwayomi.username` / `suwayomi.password` | Never returned by GET, only `passwordSet`, mirroring the Kitsu password in `GET/PUT settings/scrobble` (null on PUT means unchanged, empty clears). Stored plaintext in `AppConfig` like the Prowlarr, Kavita and qBittorrent secrets, so backups carry it the same way |
| `suwayomi.sources` | JSON array of exposed sources: `{ id, name, displayName, lang, chapterLanguage, contentWarning }`. A snapshot so the registry can be built without calling Suwayomi |

### 2. The virtual source (Maki.Sources/Suwayomi)

New folder, matching the existing one-folder-per-source layout:

- `SuwayomiClient`: GraphQL POST over the `suwayomi` named `HttpClient`, error mapping, version probe. One per instance.
- `SuwayomiAuthHandler`: see Authentication above.
- `SuwayomiSource : ISource, IPageClientSource`: one instance per exposed source, constructed from the settings snapshot plus the shared client.
- `SuwayomiSourceProvider : ISourceProvider`: reads `suwayomi.sources` on `ReloadAsync`.
- `SuwayomiMangaIdCache`: `(sourceId, url) -> mangaId` and `(mangaId, chapterUrl) -> chapterId`, in memory.

Field mapping:

| `ISource` | From Suwayomi |
| --- | --- |
| `Name` | `suwayomi-{sourceId}` |
| `DisplayName` | `displayName`; the UI adds a "Suwayomi" badge rather than baking it into the name |
| `BaseUrl` | The configured Suwayomi URL (Referer, cover policy). The UI hides the host line for these sources, see §4 |
| `SupportedLanguages` | `[chapterLanguage]` |
| `Rating` | `contentWarning` NSFW → `Adult`, MIXED → `Mature`, else `General`. On v2.1, `isNsfw` → `Adult` |
| `Kind` / `Content` | Default `Aggregator` / `Manga`. Suwayomi does not know, and guessing per extension is not worth it in v1 |
| `Capabilities` | `None`. Suwayomi handles Cloudflare itself (`server.flareSolverrEnabled`) |
| `SourceSeriesId` | `manga.url` |
| `SourceSeriesResult.Url` / `SourceSeriesDetail.Url` | `realUrl`, falling back to Suwayomi's own manga page `{base}/manga/{id}` when null, since both fields are non-nullable and render as links |
| `CoverUrl` | The Maki thumbnail endpoint (§3), or null when `thumbnailUrl` is null |
| `SourceChapterId` | `chapter.url` |
| `SourceChapter.Number` | `chapterNumber`, with negative values (Mihon's `-1` "unknown") mapped to null |
| `NumberRaw` / `Title` | `name` |
| `Volume` | Parsed from `name` with the shared `ChapterNumberParser` (Mihon chapter names are often `Vol.3 Ch.15`) |
| `Language` | The exposed source's `chapterLanguage` |
| `ReleaseDate` | `uploadDate` (epoch ms), 0 → null |
| `Group` | `scanlator` |
| `ExternalIds` | None. Mihon's manga model carries no tracker ids, so the title pass alone decides auto-match |

Chapters go through `SourceChapterList.Normalize` with an explicit `preferred`: newest `uploadDate`, then lowest `sourceOrder`. Extensions like MangaDex list one entry per scanlator for the same chapter, and relying on list order would make the winner depend on the extension's sort.

**Chapter language.** Mihon chapters have no language field; the language is the source's. Most extensions register one source per language (`MangaDex (EN)`, `MangaDex (ES)`), so `lang` is right. A few use `all` or `multi`, where we cannot know. The source picker makes the admin choose a chapter language for those before they can be exposed, rather than defaulting to `en`. Language is half of chapter identity and lands in the CBZ's `LanguageISO`, so a wrong default would file chapters in the wrong place, the same failure `Maki.Sources/CLAUDE.md` warns about for MANGA Plus.

**Resolving a stored mapping to Suwayomi ids.** `GetSeriesAsync`, `ListChaptersAsync` and `GetPagesAsync` get a portable url and need a local id. `ChapterDownloadProcessor` rebuilds the `SourceChapter` it hands to `GetPagesAsync` from `(SourceName, SourceSeriesId, SourceChapterId, ...)` only, so the url ids are the only handles there, and this design matches that.

1. Cache hit → use it.
2. `MangaByUrl` → cache and use.
3. Miss means this Suwayomi instance has never seen the manga. That is routine (a container recreated without its volume). Throw a `SourceErrorException` whose text gives the fix: search the title once in Suwayomi, which re-adds it, and the next sync picks it up by url. Do not fuzzy-recover by title: `GetSeriesAsync` has no title to search with, and a recovery that picks the wrong manga downloads the wrong series. (`LastError` is plain English today, see the note on `SourceMapping.LastError`, so this message is too.)

`GetPagesAsync` resolves the chapter id with `ChapterByUrl`. If the chapter is not there (Suwayomi's list is older than Maki's), run `fetchMangaAndChapters` once and retry before giving up with a 404 `HttpRequestException`, so the download fails over to the next source as it does elsewhere (`ChapterDownloadProcessor`). The returned page URLs are made absolute against the configured base: `PageDownloader.ExtensionFor` calls `new Uri(url)` and throws on a relative one.

If the exposed source's id is no longer in Suwayomi's live source list (extension uninstalled), calls throw a `SourceErrorException` saying so, which lands in `LastError` through the normal sync path.

**Search.** `fetchSourceManga` with `type: SEARCH`, page 1 only, matching the built-in sources. Each call upserts results into Suwayomi's DB as non-library rows. That is what Suwayomi's own browse screen does and is harmless, but it gets one sentence in the docs.

### 3. Networking: LAN access without opening the SSRF guard

Suwayomi almost always sits on the LAN or a Docker network. Two things in Maki deliberately refuse private addresses:

- The `pages` `HttpClient` used by `PageDownloader` runs `PublicAddressGuard` at connect time.
- The cover proxy (`GET search/cover`) refuses private destinations as well as hosts outside `CoverHostPolicy`.

Neither guard is loosened. Both exist because page and cover URLs come from third-party sites. The Suwayomi URL is different: an admin typed it, the same way they type the Kavita and FlareSolverr URLs, which already use guard-free clients.

**The `suwayomi` named client.** No `PublicAddressGuard`, like the Kavita client. `AllowAutoRedirect = false` and redirects refused outright (nothing in Suwayomi's API needs one, and following one would make the origin check below a first-hop-only check). Handlers: an origin check that refuses any request whose scheme, host and port differ from `suwayomi.url`, then `SuwayomiAuthHandler`, `TransientRetryHandler` and `RateLimitDetectingHandler` as the `pages` client has, plus a concurrency limit of 4 in flight, since every exposed source shares this one host.

**Pages.** `PageDownloader.DownloadPageAsync` does, in order: per-source cooldown wait, `PublicAddressGuard.EnsureAllowed`, send headers-read, 429/503 → `RateLimitException`, `EnsureSuccessStatusCode` (the 404 that drives failover), stall-timeout copy, XOR, atomic move. All of that stays. The seam replaces only the client and the guard:

```csharp
// Maki.Core/Sources: optional, for a source whose pages live at an admin-configured origin.
public interface IPageClientSource
{
    string PageHttpClientName { get; }
    bool IsPageUrlAllowed(Uri url);
}
```

`PageDownloader` resolves the source by name (it already receives `sourceName`). When the source implements `IPageClientSource`, it creates that named client instead of `pages` and calls `IsPageUrlAllowed` in place of `PublicAddressGuard.EnsureAllowed`, throwing the same `BlockedDestinationException` on a refusal. Everything else in the method is untouched. `SuwayomiSource` answers `suwayomi` and "same origin as `suwayomi.url`", which is also what the client's own origin handler enforces, so the check holds twice. All four page consumers (`ChapterDownloadProcessor`, `SeriesPreviewService`, `SourceComparePreviewService`, `SourceProbeService`) go through `PageDownloader`, so this one seam covers them.

Filling `PageRequest.Data` inside `GetPagesAsync` instead was considered and rejected: it holds a whole chapter in memory before the first page is written and skips the cooldown, failover and stall handling above.

**Covers.** `SuwayomiSource` returns cover URLs pointing at a new Maki endpoint, `GET /api/v1/suwayomi/thumbnail/{sourceId}/{mangaId}`, which fetches `/api/v1/manga/{mangaId}/thumbnail` from the configured origin through the `suwayomi` client.

- `sourceId` must be an exposed source, and `mangaId` must belong to it (id cache, else one `manga(id)` lookup comparing `sourceId`). Otherwise any signed-in reader could pull thumbnails of unexposed extensions through Maki.
- The response copies the cover proxy's hygiene (`SearchController`): sniff with `ImageValidator.SniffMediaType` and refuse non-rasters, cap the size, `Content-Security-Policy: default-src 'none'; sandbox`, `Cache-Control: private`. These are third-party bytes served from Maki's origin under the session cookie.
- The caller only chooses two integers, so this is not an SSRF primitive.
- `SearchController.ProxiedCoverUrl` passes a Maki-relative URL through unchanged instead of wrapping it in `search/cover`. That method (search and series-lookup responses) is the only place source covers reach the client; `CoverService` and series creation fetch metadata-provider covers, not source covers.
- New entry in `ControllerAuthorizationInventoryTests`.

### 4. Settings and UI

**Integration card** (`frontend/src/pages/settings/IntegrationCards.tsx`, next to Kavita):

- URL, auth mode, username, password.
- "Test connection": calls `aboutServer` and `sources`, shows the server version and how many sources are installed, reports an unsupported version (below v2.1, or below v2.2 with `ui_login`). With `ui_login` the test also logs in so a wrong password shows up here, not later in a sync.
- Empty source list: explain that extensions are installed in Suwayomi, and link to the configured Suwayomi URL.

**Source picker** (a modal from the card, structured like `ManageSourcesModal`):

- Every installed Suwayomi source with display name, language and content warning. No icons in v1: Suwayomi's `iconUrl` needs auth and an icon passthrough is not worth the endpoint yet, so `SourceIcon`'s monogram fallback applies.
- Grouped by language, filtered by the instance's enabled languages by default, since a typical Suwayomi install has hundreds of language variants.
- A checkbox per source. Nothing is exposed until the admin ticks it. The picker says plainly that a ticked source whose language is English or the instance language is **on immediately** and searched for every added series (`SourceAvailability.DefaultsOn`); there is no second opt-in. It also shows the number exposed.
- `all`/`multi` sources require a chapter language to be chosen before they can be ticked.
- Unticking a source that has mappings warns with the count. The mappings are kept and simply stop running, so re-ticking restores them.
- Sources exposed earlier but no longer installed in Suwayomi are listed as missing rather than dropped silently.

Once exposed, the sources appear in the normal priority list. `GET search/sources` gains `provider: "suwayomi"` (null for built-ins); `ManageSourcesModal` shows a "Suwayomi" badge in place of the `sourceHost(baseUrl)` line, which would otherwise print the Suwayomi hostname for every one of them. They rank after every listed source by default, because `OrderSources` puts unlisted names last. A built-in source and its Suwayomi twin (MangaDex both ways, say) coexist; the admin's priority order decides.

All new strings through `t`/`<Trans>` and `this.Fail(localizer, key)`.

**Endpoints:**

| Endpoint | Purpose | Access |
| --- | --- | --- |
| `GET/PUT settings/suwayomi` | Connection settings; PUT reloads the provider and clears the id cache on a URL change | Admin, in `SettingsController` |
| `POST settings/suwayomi/test` | Version + source count | Admin |
| `GET settings/suwayomi/sources` | Installed sources merged with the exposed snapshot (live + missing) | Admin |
| `PUT settings/suwayomi/sources` | Save the exposed set; reloads the provider | Admin |
| `GET suwayomi/thumbnail/{sourceId}/{mangaId}` | Cover passthrough, `SuwayomiController` | Any signed-in user |

### 5. Health

All in `HealthMonitor.RefreshAsync`, which already runs periodically. No new job.

- A Suwayomi probe (`aboutServer`) only when at least one exposed source is enabled, the same gating the FlareSolverr probe uses. Its settings link is `/settings?tab=integrations&s=suwayomi`; `Probe` hardcodes `tab=downloads` for everything but Kavita today, so it needs a third case.
- A snapshot-vs-live comparison of `sources`, adding a `suwayomi:missing:{id}` row for each exposed source whose extension is gone. The monitor does not write `SourceMapping.LastError`; sync owns that, and the source's own `SourceErrorException` (§2) fills it.
- Suwayomi down means each Suwayomi source's calls throw `HttpRequestException`, and the existing per-source failure, cooldown and `LastError` handling applies unchanged.

### 6. Auto-match cost

Each exposed source is one more entry in `SourceMatchService`'s fan-out, which is gated by `MaxParallelSources = 6` across all sources. Exposed Suwayomi sources rank after the built-ins by default and the fan-out starts tasks in priority order, so the built-ins take the gate's slots first and Suwayomi adds latency at the tail of a match rather than in front of it. If an admin ranks Suwayomi sources above the built-ins, Suwayomi searches go first and built-ins wait; that is what the ranking asks for. A per-provider concurrency group in the fan-out is not worth it for v1. The picker's exposed-count note is the guard against exposing 50 sources by accident.

## Out of scope for v1

- **Suwayomi's own download queue** (`enqueueChapterDownloads`). Maki fetching pages on demand through Suwayomi keeps one download pipeline. Suwayomi caches fetched images anyway.
- **Installing or updating extensions from Maki.** Suwayomi's UI already does this, and it needs an extension store configured first (`server.extensionStores` defaults to empty).
- **Source filters and preferences** (`source.filters`, `updateSourcePreference`). Configure them in Suwayomi.
- **Source icons** in the picker and priority list.
- **Pasting a site URL to link** (`ResolveSeriesIdFromUrl`). Suwayomi has no resolve-by-URL call; it would need a host match plus a title search plus a url compare.
- **Browsing Popular/Latest in Discover.** Possible later via `fetchSourceManga` `POPULAR`/`LATEST`.
- **Several Suwayomi instances.** One URL in v1; the `suwayomi-{sourceId}` name would need an instance segment to support more.
- **WebSocket subscriptions.** Nothing here needs push.

## Delivery

Each phase leaves a non-Suwayomi install unchanged.

1. **Registry and page seam.** `ISourceProvider`, dynamic `SourceRegistry` (single-argument constructor kept), `IPageClientSource` in `PageDownloader`. Tests: a provider reload is visible through `All`/`Find`; a vanished source's mappings are skipped; fetcher-path cases in the existing `PageDownloaderCooldownTests` and `PageDownloaderRateLimitTests` (cooldown honoured, 429/503 → `RateLimitException`, 404 → `HttpRequestException`, a disallowed URL → `BlockedDestinationException`). No user-visible change.
2. **Client, source and settings keys.** Setting keys, `suwayomi` named client with its handlers, `SuwayomiAuthHandler`, `SuwayomiClient`, `SuwayomiSource`, provider, id cache. Testable without a UI by writing the keys directly. Tests use a stub `HttpMessageHandler` that matches GraphQL requests on `operationName` in the body (`FakeHttpClientFactory` matches on URL substring, and every GraphQL call is the same URL), either by extending `FakeHttpClientFactory` with a body matcher or a small dedicated stub in the Suwayomi tests. Cases: v2.1 and v2.4 response shapes; 200-with-Unauthorized and 401 both refresh once and retry once; concurrent callers refresh once; `-1` chapter number; an `all`-language source; a manga missing from Suwayomi; an uninstalled extension; a relative page URL made absolute under a subpath; the client refusing an off-origin URL and a redirect.
3. **Endpoints.** Controller actions, thumbnail passthrough with its sniff/CSP/ownership checks, `ControllerAuthorizationInventoryTests` entries, `GET settings/suwayomi` never returning the password.
4. **UI.** Integration card, source picker, `provider` field and badge on the priority list, strings extracted.
5. **Health and docs.** Probe and missing-extension rows in `HealthMonitor`; a `Maki.Sources/CLAUDE.md` entry with the gotchas above (portable url ids, `sourceOrder`-based page URLs, 200-with-Unauthorized, subpath, chapter language for `all` sources, upsert-on-search, the "search it once in Suwayomi" fix); an optional `suwayomi` service in the Docker compose example; a release-checklist line for a smoke test against a real instance.

Rule-file frontmatter: new files under `src/Maki.Sources/Suwayomi/` are covered by `Maki.Sources/CLAUDE.md`. `PageDownloader` (`src/Maki.Core/Download/`) matches no rule file today, so the `IPageClientSource` gotcha (the origin check is what keeps it from being a guard bypass) goes in `downloads.md` with that path added to its `paths`. The thumbnail endpoint gets a line next to the cover-proxy note in `auth.md`, with `src/Maki.Api/Controllers/Suwayomi*.cs` added to its `paths`.

## Settled decisions

- **Minimum Suwayomi version:** v2.1 for `none`/`basic` (with the `fetchManga` + `fetchChapters` fallback and `isNsfw` for rating), v2.2 for `ui_login`.
- **`simple_login`:** not in v1.
- **`Kind`/`Content`:** defaults in v1, no per-source override in the picker.
