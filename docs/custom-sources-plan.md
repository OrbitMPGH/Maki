# Custom sources: plan

Status: phase 1 built on `feat/custom-sources` (2026-10-27), phases 2 to 4 not started. See [Phase 1 as built](#phase-1-as-built) for where the code differs from the design below. Revision 3: the owner's answers to the first draft are folded in (see Decisions), and so is one review round against the code (an Opus 5.5 reviewer, 2026-10-27, three high and thirteen medium findings, all addressed below). Researched against `dev` (06e983ac) and silentquill.net on 2026-10-27.

## Goal

An admin can add a small site as a source without a Maki release and without Suwayomi. The motivating case: *Thus, the Seasons Learn of Blue* has chapter 1 on Atsumaru and chapter 2 only on SilentQuill, a scanlator with about 190 series. That is too small to build in, but the owner wants to follow the series there.

A custom source is a **JSON definition, never code**. Maki runs every definition through one generic engine (URL templates plus extractors). A wizard writes the definition by guessing from a series URL and a chapter URL and showing what it found before anything is saved. Definitions export and import as files, and an admin can add a repository URL that lists definitions and, if they opt in, keeps them up to date.

Once saved, a custom source behaves like a built-in one: search, auto-match, priority, the on/off switch, sync, downloads, health. It lives in Manage sources next to the others, marked with a "Custom" pill.

## Decisions

Answered by the owner on 2026-10-27:

1. **Repository updates are opt-in automatic.** Each repository has a "Keep up to date" switch, off when the repository is added. With it on, Maki applies new versions itself, within the limits in [Repositories](#repositories).
2. **Only admins create, edit, import or delete custom sources.**
3. **Sites with no search are allowed.** Auto-match never finds series on them, so every series is linked by hand by pasting its URL. The wizard and the source's row say so.
4. **Everything lives in the Manage sources modal.** No Settings card and no separate tab. Custom sources are ordinary rows in the catalogue with a "Custom" pill, the way Suwayomi sources carry theirs, and creating, importing and managing repositories start from a button in the modal.

## Non-goals

- **Running third-party code.** No assemblies, no JavaScript, no Jint. A definition is data that Maki's own engine interprets. This rules out sites that need a descrambler, protobuf, signed API calls or a solved challenge beyond what FlareSolverr does. Those stay built-in sources.
- **A headless browser mode in v1.** `MangaFireBrowser` shows what one costs (memory, idle shutdown, flakiness). SilentQuill also shows it would not help as much as it seems: the live reader DOM holds 4 of the chapter's 40 `img` elements because of lazy loading, while the raw HTML holds all 40 page URLs.
- **An official Maki repository of definitions.** Maki supports adding a repository by URL. It does not ship or host one. Listing scanlation sites under the Maki name is a different exposure from admins adding sites themselves.
- **Multi-language sites.** One definition publishes one language in v1.
- **POST search or cookie-gated search.** `HtmlFetchRequest` could carry both, but no site seen so far needs them; a definition can't express them in schema 1.

## What SilentQuill teaches about the schema

Checked on 2026-10-27 with a plain client:

| | What the site does |
| --- | --- |
| Stack | Custom Next.js App Router site, not a WordPress theme. Cloudflare is passive: curl with a browser UA gets 200 on every page |
| Search | `GET /search/?q=seasons`. Hits are `a.block[href^="/series/"]`, title in `h3[title]`, cover in the inner `img` (a same-domain `/img/<base64>?w=320` proxy path) |
| Series | `GET /series/{slug}/`. Title in `h1`, cover in `meta[property="og:image"]`, synopsis in `meta[name="description"]` |
| Chapter list | Server-rendered in the series page, inside `div.columns-1`: `<a href="/thus-the-seasons-learn-of-blue-chapter-2/"><span class="truncate"><span>Ch. 2</span></span><span class="text-right">1 month</span></a>`. Links are **root-relative chapter slugs, not under `/series/{slug}/`**, and chapter 1's slug is the romaji title (`kakushite-kisetsu-wa-ao-wo-shiru-chapter-1`), so nothing about a chapter URL contains the series slug |
| Chapter page | Both `/{chapterSlug}/` and `/series/{slug}/{chapterSlug}/` answer 200 with the full reader, and both declare `<link rel="canonical" href="https://silentquill.net/{chapterSlug}/">`. The root form is the one the site links, so it is the one the definition uses |
| Dates | Relative only ("1 month"), so dates are not reliable |
| Pages | **Not in the markup.** The reader renders them client-side from the RSC payload (`self.__next_f.push(...)`), which carries `{"pages":[{"url":"/img/p/99763-eY7SByNctzaU8baW",...}]}`. A regex over the raw HTML for `/img/p/\d+-[A-Za-z0-9_-]+` finds all 40, in order |
| Images | `/img/p/...` answers `image/webp` with a browser User-Agent and **403 without one** |
| Extras | `/rss.xml` lists the latest chapters site-wide; `<language>en</language>` |

Three consequences:

1. **CSS selectors alone are not enough.** Many modern sites embed their data in a script blob (`__NEXT_DATA__`, the RSC stream, Inertia's `data-page`, MangaThemesia's `ts_reader.run({...})`). Every extractor needs a `regex` and a `json` form next to `css`.
2. **Chapter links can't be found by "links under the series path".** The wizard has to anchor on a chapter URL the admin gives it and generalise from that element's position in the page.
3. **Default headers matter.** Every `PageRequest` carries the browser User-Agent (`Common/BrowserUserAgent.cs`, which overrides the `pages` client's own `Maki/1.0`) and a `Referer` of the base URL unless the definition overrides them. HTML fetches already go out with the browser UA; see [Fetching](#fetching) for Referer and extra headers there.

## Definition schema

Stored as one JSON array under a new settings key `sources.custom` (`SettingKeys.CustomSources`), the same way `suwayomi.sources` holds its snapshot. Settings live in `AppConfig` inside `maki.db`, so backups carry it. Repositories are a second key, `sources.customrepositories` (see [Repositories](#repositories)). Both are written only through `CustomSourceStore` (see [The engine](#the-engine)).

SilentQuill, written out in full:

```json
{
  "schema": 1,
  "id": "silentquill",
  "name": "SilentQuill",
  "baseUrl": "https://silentquill.net",
  "language": "en",
  "kind": "scanlator",
  "content": ["manga"],
  "rating": "general",
  "flareSolverr": false,
  "minIntervalMs": 2000,
  "headers": {},
  "coverHosts": [],

  "search": {
    "url": "/search/?q={query}",
    "item": { "css": "a.block[href^='/series/']" },
    "id": { "attr": "href", "regex": "^/series/([^/]+)/$" },
    "title": { "css": "h3", "attr": "title" },
    "cover": { "css": "img", "attr": "src" }
  },

  "series": {
    "url": "/series/{seriesId}/",
    "title": { "css": "h1" },
    "cover": { "css": "meta[property='og:image']", "attr": "content" },
    "description": { "css": "meta[name='description']", "attr": "content" }
  },

  "chapters": {
    "item": { "css": "div.columns-1 a[href]" },
    "id": { "attr": "href", "regex": "^/([^/]+)/$" },
    "url": "/{chapterId}/",
    "number": { "css": "span.truncate" },
    "date": { "css": "span.text-right", "format": "relative" }
  },

  "pages": {
    "list": { "regex": "/img/p/\\d+-[A-Za-z0-9_-]+" }
  },

  "sample": {
    "seriesUrl": "https://silentquill.net/series/thus-the-seasons-learn-of-blue/",
    "chapterUrl": "https://silentquill.net/thus-the-seasons-learn-of-blue-chapter-2/",
    "query": "Thus, the Seasons Learn of Blue"
  },

  "origin": null
}
```

### Top-level fields

| Field | Meaning |
| --- | --- |
| `schema` | Definition format version. The engine refuses a number it does not know rather than half-reading it (see [Invalid definitions](#invalid-definitions)) |
| `id` | Lowercase slug, `^[a-z0-9][a-z0-9-]{1,39}$`, set at creation and **read-only afterwards**. The source's `Name` is `custom-{id}`, which is what `SourceMapping.SourceName`, priority, cooldowns and `ChapterFile.SourceName` store. The `custom-` prefix keeps it clear of every built-in name and of `suwayomi-*`; `SourceRegistry` would let a built-in win a clash anyway (`TryAdd`), but the validator refuses one up front. Renaming happens through `name` |
| `name` | `DisplayName` |
| `baseUrl` | `https://` or `http://` origin, not an IP literal and not `localhost`. Relative URLs in every template resolve against it, and every HTML fetch is held to its host (see [Security](#security)). The validator refuses a host that a built-in or another custom source already serves, since `SearchController.ResolveSource` takes the first source in registry order that claims a URL and two sources on one host would also share one rate limiter |
| `language` | One language for every chapter, run through `SourceLanguages.Canonical`. Prefilled from `<html lang>` or an RSS `<language>` |
| `kind`, `content`, `rating` | Map onto `SourceKind`, `SourceContent`, `SourceRating`. The wizard defaults to `scanlator`, `manga`, `general` |
| `flareSolverr` | Declares `SourceCapabilities.NeedsFlareSolverr`. That flag only drives the badge and the FlareSolverr health warning; whether a fetch actually goes through FlareSolverr is decided per request (see [Fetching](#fetching)) |
| `minIntervalMs` | Spacing between this source's requests to its own host, HTML and page images alike (see [Fetching](#fetching)). Default 1000, between 250 and 60000. A small site's operator notices a library-wide sync far sooner than MangaDex does |
| `headers` | Extra request headers. `Host`, `Cookie`, `Content-Length` and hop-by-hop headers are refused. User-Agent and `Referer: {baseUrl}/` are sent unless overridden here |
| `coverHosts` | Extra domains covers are served from, for a site whose covers sit on a CDN. Becomes `ISource.CoverHosts`. The `covers` client already holds connections to public addresses (`PublicAddressGuard`), so this widens nothing that matters |
| `engine` | Optional preset name (`"madara"`), with `engineOptions` (see [Engine presets](#engine-presets)). With an engine, `search`/`series`/`chapters`/`pages` are absent |
| `sample` | The URLs the wizard was built from: a series URL, one of its chapter URLs, and the search query when there is search. Used for the edit preview, the health check and the identity diff of an update. Required, and the admin can replace it (a taken-down sample series must not make the source look broken forever) |
| `origin` | `null` for a hand-made definition, or `{ "repository": "<index URL>", "version": 3, "sha256": "..." }` for one installed from a repository. Keyed by URL, not name, because two repositories can share a name |
| `previous` | Set by an update: the definition it replaced, kept for one-step revert. Never exported |

### Sections

- **`search`** (optional). Instead of `search`, a definition may give **`catalogue`**: `{ "url", "item", "id", "title", "cover", "nextPage"? }`, a list page Maki reads in full (following `nextPage` up to 50 pages) and filters by title through `SourceCatalog`, like TCB Scans and Flame Comics. A 190-series site often has one. A definition with neither has no search (see [No search](#no-search)).
- **`series`**: `url` template plus title, cover, description, optional `status`. `status` text maps through a fixed word list (`ongoing`, `completed`/`complete`/`finished`, `hiatus`, `cancelled`/`dropped`, case-insensitive, as Toonily maps them); anything else is left null. `ResolveSeriesIdFromUrl` inverts the `url` template (`/series/{seriesId}/` becomes `^/series/([^/]+)/?$`), so linking by URL works without extra configuration.
- **`chapters`**: `listUrl` defaults to the series URL. `item` selects one element per chapter, `id` gives the chapter id, and `number`, `title`, `volume`, `date` are per-item fields. `nextPage` follows a pagination link up to 50 pages.
  - **`url` is required.** It is a template over `{seriesId}` and `{chapterId}`. The download path, `SourceScoutService` and `UpgradeScanService` all call `GetPagesAsync` with a `SourceChapter` rebuilt from the mapping and the stored `SourceChapterId`, without the listed `Url` (`ChapterDownloadProcessor.cs:190`), so the page URL has to be rebuildable from the two ids alone.
  - The `test` endpoint checks, for every listed chapter, that `url` expanded with its ids resolves to the item's own `href`, and fails the definition when one does not.
  - `id` regexes run against the **resolved path** of an `href`, not the raw attribute, since sites mix absolute and relative links.
- **`pages`**: `url` defaults to the chapter URL (the expanded `chapters.url`). `list` yields page image URLs in order, de-duplicated by first occurrence.

### Extractors

A **list extractor** (`item`, `pages.list`) is exactly one of:

- `{ "css": "selector" }`: elements in document order, via AngleSharp.
- `{ "regex": "pattern" }`: every match over the raw response, group 1 if the pattern has one, else the whole match. This is what reads SilentQuill's RSC payload.
- `{ "json": { "path": "...", "css"?, "attr"?, "regex"? } }`: parse a JSON blob, then walk a path. With no `css` or `regex` the blob is the response body (run through `Common/PreUnwrap` first, since FlareSolverr hands JSON back `<pre>`-wrapped). With `css` it is the first matching element's text, or its `attr` (`{ "css": "script#__NEXT_DATA__" }`, `{ "css": "div#app", "attr": "data-page" }`). With `regex` it is group 1 of the first match (`ts_reader\\.run\\((\\{.*?\\})\\);`). The path is dot segments plus `[*]` and `[n]` (`props.pageProps.chapters[*]`). No full JSONPath: filters and recursive descent are where JSONPath libraries disagree, and nothing seen so far needs them.

A **field extractor** (`id`, `title`, `number`, ...) runs against one item and is `{ "css"?, "attr"?, "regex"?, "json"? }`:

- `css` picks a descendant (omitted means the item itself).
- `attr` reads an attribute (`href`, `src`, `content`, `data-src`). Omitted means trimmed text. `attr: "src"` on an `img` uses `MadaraParser.ImageUrl`'s order, lifted into `Common/` with it: the lazy-load attributes (`data-src`, `data-lazy-src`, `data-lzl-src`, `data-cfsrc`, `data-manga-src`) win over `src`, then the largest `srcset` candidate, then `src`.
- `regex` then narrows the string, group 1.
- On a JSON item, `json` is a path relative to the item instead of `css`.
- `{ "value": "1" }` is a literal and excludes the other keys.

Field post-processing is fixed, not configurable:

- **Number**: `ChapterNumberParser.Parse(text, volumeText).OrSlugNumber(href)`. "Ch. 2" parses as 2 with no pattern, and the slug fallback catches lists that only show a title.
- **Date**: `format` is `"relative"`, `"iso"`, or a .NET format string plus `"culture"`. `"relative"` reads English "2 days ago" **and the bare "1 month" SilentQuill prints**: Toonily's private `RelativeDatePattern` requires "ago", so it moves into `Common/` with "ago" made optional. `"relative"` makes `ReleaseDatesAreReliable` false. Unparseable gives null, never a throw.
- **URLs** resolve against the response URL, then the base URL.
- Chapters go through `SourceChapterList.Normalize` like every source.

### Failure behaviour

Same rules the built-in sources follow, with the exception types the download pipeline actually classifies:

- A `chapters.item` or `pages.list` that matches **nothing** on a page that was fetched fine throws **`SourceErrorException`** naming the definition and the extractor. A layout change must read as a failing source, not as "no chapters", or a monitored series silently stalls. It has to be `SourceErrorException` and not a bare `InvalidOperationException`: `DownloadFailureReason.Classify` files the latter as `unexpected`, which `SourceFailover` does not count, so a broken custom source would keep failing its downloads instead of moving them to the next source.
- An empty search result is legitimate and returns empty.
- 404, 410 and other non-2xx statuses outside 403/429/503 surface as `HttpRequestException` with the status, so a 404 page fails the download over.
- **429** becomes the source's rate-limit cooldown through `RateLimitDetectingHandler`, as today.
- **403 and 503** on the direct path are read as a challenge by `ChallengeAwareFetcher`. With a FlareSolverr URL set they go to FlareSolverr (subject to the public-address check below). Without one they throw `ChallengeNotSolvedException`. A site that answers 503 because it is overloaded therefore does not reach the cooldown. That is the behaviour every `IHtmlFetcher` source already has; it is stated here so nobody expects otherwise.

## The engine

| Piece | Where |
| --- | --- |
| `CustomSourceDefinition` and nested records, `CustomSourceEnums` | `Maki.Core/Sources/Custom/` (pure domain, no AngleSharp) |
| `CustomSourceValidator` (schema rules, ids, hosts, templates, refused headers, and that every selector, regex and JSON path compiles) | `Maki.Sources/Custom/`, since checking a CSS selector needs AngleSharp. Host uniqueness needs the registry and lives in `CustomSourceService` |
| `CustomSource : ISource`, `CustomExtractor` (css / regex / json), `CustomTemplate` (`{query}`, `{seriesId}`, `{chapterId}` expansion and inversion) | `Maki.Sources/Custom/` |
| `CustomSourceStore` (singleton) | `Maki.Sources/Custom/`. The only reader and writer of `sources.custom` (and later `sources.customrepositories`): one `SemaphoreSlim` around read-modify-write, entries kept verbatim. It can't reload the registry itself (the provider depends on it, so that would be a DI cycle) |
| `CustomSourceService` (scoped) | `Maki.Api/Services/`. Validation with host uniqueness, the identity diff, save and delete through the store, then `SourceRegistry.ReloadAsync` and `SourceChapterListCache.EvictSource` |
| `CustomSourceProvider : ISourceProvider` | `Maki.Sources/Custom/`. Reads `sources.custom`, builds one `CustomSource` per entry (a broken one becomes a stub, below). Registered next to `SuwayomiSourceProvider` in `Program.cs` |
| Wizard guessing (`EngineDetector`, `ChapterListGuesser`, `PageListGuesser`, `SearchGuesser`) | `Maki.Sources/Custom/Inference/` |
| `CustomSourceHealthJob` (Quartz, hourly tick) | `Maki.Api/Jobs/`. See [Health](#health) |
| `CustomSourceRepositoryJob` (Quartz, daily) | `Maki.Api/Jobs/`. Refreshes repository indexes and applies updates for repositories that keep up to date |
| `CustomSourcesController` | `Maki.Api/Controllers/`, every action `Policies.Admin`, pinned by `CustomSourcesControllerTests` (see [Security](#security)) |

Page images are returned as `PageRequest`s carrying their headers, resolved at download time like every other source.

### Fetching

Custom sources get **their own named client, `custom-sources`**, not the shared `challenge-fetcher` one. The shared client has no public-address guard and follows redirects on its own, which is fine for code-reviewed scrapers with fixed hosts and not fine for admin-typed URLs:

- Primary handler `PublicAddressGuard.CreateManualRedirectHandler`, plus `ProxiedTargetGuardHandler(followRedirects: true)`, the way the `pages` client is built (`Program.cs:350-358`). Connections to loopback, private, link-local and CGNAT addresses are refused at connect time, which also covers redirects and DNS rebinding.
- No per-host limiter on the client. Spacing is per source instead: `CustomSourceThrottle` keeps one `SpacingGate` per source name, which every HTML fetch and every page image of that source waits on. It has no timer (nothing keeps running for a deleted source) and an edit changes its interval in place. Sources can't share a host (the validator refuses it), so per source and per host come to the same thing for HTML.
- `RateLimitDetectingHandler(treat503AsRateLimit: false)`, as on the shared client, so 503 still reaches the challenge check.

`ChallengeAwareFetcher` is parameterised by client name (today it reads `HttpClientName` as a constant), and `CustomSource` takes an instance built over `custom-sources`. Two more changes to the fetcher:

- **`HtmlFetchRequest` gains `Headers`.** They are sent on the direct request. FlareSolverr accepts no custom headers, so they are dropped on that path, and the doc comment says so.
- **A result that says which path answered** (`Direct` or `FlareSolverr`), so the wizard's `probe` can report it and set `flareSolverr: true`. Existing callers ignore it.

Before any FlareSolverr handoff for a custom source, the URL's host goes through `PublicAddressGuard.IsPublicHostAsync`. FlareSolverr fetches from its own network, usually the LAN, where Maki's connect-time guard cannot follow it.

Every HTML fetch is held to the definition's `baseUrl` host (`www.` tolerated either way). That covers search, series, chapters, `nextPage`, catalogue, page lists and absolute links taken from site content. A URL on any other host fails the extraction with `SourceErrorException`. Only page images and covers may leave the host. Page images go through the guarded `pages` client, and covers go through the guarded `covers` client.

**Page images share the source's rate limit.** The `pages` client has no per-host limiter and `PageDownloader` runs four pages of a chapter at once, per worker, so a `minIntervalMs` that covered HTML alone would leave a small site's heaviest traffic unthrottled. `CustomSource` returns its pages through the `PageRequest.Fetch` hatch, which waits on the source's `SpacingGate` and reads the image through the guarded `custom-source-pages` client. When the site was challenged, `ChallengeAwareFetcher.SessionHeadersFor` supplies the clearance cookies and UA for the request, the way the MangaKakalot-style sources already do.

### Availability and language

- `SourceAvailability.DefaultsOn` decides on language today (`SourceAvailability.cs:85-90`), so a non-English custom source on an English instance would start off. It gains a branch: a `CustomSource` defaults on, since an admin created it on purpose.
- Being on is not enough for auto-match. `SourceLanguagePreference` skips a source whose language the instance has not enabled. Step 5 of the wizard warns when the definition's language is not in the instance's enabled languages.

### Invalid definitions

`CustomSourceProvider` never skips an entry it can't use. Sync and resolve skip mappings of an unknown source with only a log line (`ChapterSyncService.cs:82-87`, `ChapterSourceResolver.cs:111-115`), so an unknown `schema` after a downgrade or a restore from a newer build, or a validator rule tightened in a release, would make every linked series stall with nothing visible. Instead, an invalid entry registers a **stub** `CustomSource` with the stored `id` and `name`. Every call throws `SourceErrorException("definition invalid: ...")`. The row shows "Not working" with the validation message, and Edit opens it.

### Deleting and re-importing

- Deleting a definition does not delete `SourceMapping` rows. Mappings to a missing source are already skipped by sync, the resolver, upgrade scans and the scout, and re-importing the same `id` brings them back. The delete confirmation says how many series are linked.
- Delete clears `LastError` on the source's mappings. Otherwise `HealthCheckService` keeps reporting them for good, since a deleted source is not in the disabled list (`HealthCheckService.cs:55-75`).
- `SourceComparePreviewService` calls `SourceRegistry.GetRequired` (`SourceComparePreviewService.cs:396`), but inside a `try` whose `catch` fails that panel with an error, so a source deleted mid-compare already reads as a failed panel. No change needed.
- Importing a definition whose `id` already exists is an **update** of the existing one, and goes through the [identity diff](#identity-diff). Importing an `id` that has orphaned mappings from an earlier delete, with a **different host** from the one those mappings point at, needs an explicit confirmation naming the host change and the number of series, since it reattaches them to another site.

### Engine presets

Most small scanlators run a handful of WordPress themes. `"engine": "madara"` replaces the four sections with a built-in parser. Base URL and metadata are not enough on their own, because Madara sites differ in two ways the Toonily code assumes away (`ToonilySource.cs:180-206`):

- `engineOptions.seriesPath`: `"manga"` (the common default) or the site's own segment (`"serie"` for Toonily).
- `engineOptions.chapterList`: `"inline"` (rendered into the series page, as on Toonily) or `"ajax"` (the `admin-ajax.php` `manga_get_chapters` POST, or the newer `{series}/ajax/chapters/` POST, which `IHtmlFetcher.FetchAsync` can send).

`MadaraParser` moves from `Toonily/` to `Common/` (it is already written Toonily-constant-free). The wizard's step 1 fills both options from what it finds on the sample pages. Madara ships with the wizard in phase 2. MangaThemesia, HeanCMS and Keyoapp follow in phase 3.

### No search

A definition with neither `search` nor `catalogue` is valid. What that means in practice:

- `SearchAsync` returns an empty list without a request.
- `SourceMatchService` leaves the source out of its fan-out (`SourceMatchService.cs:593-597`), so the auto-match progress table doesn't show it as "Searching" and then "No match".
- Each series has to be linked by hand by pasting its series URL in the "Link a source" modal, which `ResolveSeriesIdFromUrl` handles from the `series.url` template.
- Once linked, sync, monitoring, downloads, `SourceScoutService` probes and upgrade scans work like any other source. Search only matters for finding the series the first time.
- The wizard's search step says this before saving, and the source's row in Manage sources shows a "Link by URL" tag so nobody wonders why auto-match never picks it.

## Where it lives in the UI

Everything is inside the **Manage sources** modal (`ManageSourcesModal.tsx`). There is no Settings card and no new tab.

**Rows.** A custom source is an ordinary catalogue row: icon, name, tags, on/off switch, and a place in the Priority rail. Next to the name it carries a **"Custom" pill**, built like `SuwayomiBadge` (`span.source-provider` with `data-provider="custom"` and an icon). Unlike Suwayomi's badge, it sits beside the host rather than replacing it, since a custom source's host is a real public site and is the most useful thing to show. Extra tags in `SourceTags` where they apply:

- "Link by URL" for a source with no search.
- The repository name for one installed from a repository, "Update available" when one is waiting, and "Edited locally" for a detached one.
- "Not working" (the `adult` tone, as "No longer in Suwayomi" uses) when the last health check failed or the definition is invalid.

Custom rows also get a "..." menu with **Edit**, **Export** and **Delete**. Built-in and Suwayomi rows have no menu.

**Data.** `GET search/sources` reports `provider: "custom"` for a `CustomSource` (today the field is `"suwayomi"` or null, set in `SearchController.ListSources`) plus a `custom` object with what the row needs: `id`, `hasSearch`, `origin` (repository URL, name and version, or null), `updateAvailable`, `detached`, `invalid`, and the last health result. Unlike Suwayomi sources, the real `baseUrl` is sent, since it is a public site rather than a LAN server.

**Filter.** The Provider group in the Filters popover shows whenever a custom or Suwayomi source exists, with a "Custom" chip next to "Built in" and "Suwayomi". `ProviderFilter` gains `'custom'`, and the `builtin` filter changes from "not Suwayomi" (`ManageSourcesModal.tsx:604`) to `provider == null`, or custom sources would count as built in.

**Adding.** The tools row (search box, state filter, Filters) gets an **"Add source"** button with a menu:

- **Set up a site** opens the wizard.
- **Import definition** takes a JSON file or pasted JSON, validates it, and opens the wizard on its last step so it is tested before saving.
- **Repositories** opens the repository panel (phase 4).

The button and the row menus render only for admins. The modal is reached from the admin-only Sources card, so in practice everyone who sees it is an admin, but the UI checks anyway.

**The wizard opens as a modal stacked over Manage sources**, the same way the "Discard changes?" confirmation already stacks. Saving a definition writes it right away through `CustomSourcesController`. It is not part of Manage sources' own "Save changes", because it has to be tested against the live site and the registry reload has to happen before anything can use it. That interacts with the modal's local state:

- Manage sources seeds its rail once per opening (`seeded.current`) and keeps unsaved toggles and order locally. A newly created source is appended to **both** the local `rail` and `initialRail`, switched on. Adding it to `rail` alone would make the modal dirty (`dirty` counts any rail name missing from `initialRail`, `ManageSourcesModal.tsx:636-641`), so closing would ask "Discard changes?" when nothing needs discarding. The server already put it on and at the bottom of its priority list, which matches "Newly enabled sources join at the bottom". The admin's other unsaved changes stay as they were.
- A deleted source is removed from `rail`, `initialRail` and, optimistically, from the sources query cache. `submit` filters by the sources query (`ManageSourcesModal.tsx:703-707`), so until a refetch lands a deleted name could still be sent and refused as unknown.
- Editing a source invalidates the sources query but leaves the rail alone.

**Settings search.** No new registry entry. The existing `sources` entry in `pages/settings/registry.ts` gains search words like "custom source, add site, scanlator, website, definition, repository", so the command palette lands on the Sources card, whose button opens the modal.

**Deep links.** Settings deep links support only `tab` and `s` today (`SettingsPage.tsx:158-170`). A third parameter, `sources=manage`, opens Manage sources, and `sources=edit:custom-{id}` opens it with that source's wizard on step 5. Health rows and the update notification link there.

**Series page entry point.** The paste-URL path is the "Search or paste a series URL" field inside the series page's "Link a source" modal (`SourceMappingsSection.tsx:875-915`). When no source resolves a pasted URL:

- `SearchController.ResolveSource` answers with a structured error key, `error.search.noSourceForUrl`, so the UI can tell this case from a fetch failure.
- **Admins only** (the modal itself is open to `ManageSources` users; `resolvesource` is `ManageSources` at `SearchController.cs:64-65`) see "Set up this site as a source". It opens Manage sources with the wizard already on top and the URL filled in as the series URL. That stacks three modals, which is acceptable for a rare path, and closing the wizard returns to the Link a source modal.
- When the URL's host already belongs to a custom source (its `series.url` template simply didn't match), the offer is "Edit {name}" instead, so the admin fixes the template rather than creating a duplicate.
- After saving, the wizard invalidates the failed `['resolve-source', url]` query (`api/hooks.ts:3046-3053`), so the Link a source modal resolves the URL again and offers to link the series.
- The "Link a source" button is disabled when every enabled source is already mapped (`SourceMappingsSection.tsx:377`). That stays as it is: a new custom source is enabled on save, so the admin would reach it through Manage sources in that case.

All copy goes through `t`/`<Trans>`, and backend errors go through `this.Fail(localizer, key)`.

## Wizard flow

Five steps (site, chapters, pages, search, details and test). Every step shows what the current draft actually extracts from the live site, and every guess can be overridden by editing the extractor in a small "Advanced" field that re-runs on change.

The server side of each step is `POST custom-sources/probe` (fetch a URL on the draft's host through the guarded client, return the guesses and which path answered) and `POST custom-sources/test` (run an unsaved draft against the sample URLs and return what each section produced). Neither saves anything.

**1. Site.** The admin pastes a series URL and a chapter URL from the site. The chapter URL is optional but the hint text says it makes the guess far better. Maki fetches both and reports how: directly, through FlareSolverr (which sets `flareSolverr: true`), or not at all (shown as an error with the status). The series page's title becomes the search query for step 4. `EngineDetector` then looks for known themes (Madara: `body.wp-manga` or `manga_get_chapters`, plus which chapter-list mode and series path the pages show). On a match, it says "This site runs Madara" and jumps to step 5 with the preset filled in.

**2. Chapters.** `ChapterListGuesser` works in this order:

1. When a chapter URL was given, find the element on the series page whose `href` resolves to it, or to the chapter page's `<link rel="canonical">` (SilentQuill links the root form while a pasted URL may be the `/series/...` form). Walk up to the nearest ancestor whose siblings share its tag and class signature. That repeated element is the `item`, and the selector is built from the closest ancestor with an id or a distinctive class (`div.columns-1 a[href]` for SilentQuill).
2. Otherwise, group every link by its DOM path signature and keep the groups where most entries yield a chapter number from `ChapterNumberParser`.
3. If no markup group works, look for a JSON blob (`__NEXT_DATA__`, `data-page`, RSC strings) holding an array of objects with a number-like field, and offer a `json` extractor.

It then proposes `id` and the `url` template together, preferring the shortest id that round-trips (`url` expanded with the id resolves to the item's `href` for every item). The step shows the best candidate as a table (number, title, date, link) with the count, plus up to two alternatives as a choice. That matters because a page often also carries a "latest updates" list of other series' chapters. Numbers that came out null are highlighted.

**3. Pages.** `PageListGuesser` loads the chapter page:

1. Group `img` elements by parent, drop the ones inside `header`, `nav` or `footer` and the ones whose `src` is also on the series page (logos, covers). Take the largest group with a common URL prefix.
2. If that finds fewer than three, scan the raw HTML for URLs with image extensions or a repeated same-prefix path, group by prefix, take the largest group, and offer a `regex`. This is the path SilentQuill takes.

The step shows the page count and thumbnails of the first three pages and the last one. It then fetches one image server-side. On a 403 it retries with `Referer` set to the chapter URL, then the base URL, and records whichever worked in `headers`. "Image did not load" is shown here, not discovered on the first download. A cover that loads from another domain prompts to add that domain to `coverHosts`.

**4. Search.** Using the series title from step 1 as the query, `SearchGuesser` tries the site's own search form first (a GET `form` with a `type=search` or `name=s|q|keyword` input on the home page), then the common shapes (`/?s=`, `/search?q=`, `/search/?q=`, `/search?keyword=`). It accepts the first one whose results contain the sample series' id. When none does, the step offers:

- a catalogue page, if one is found (`/series/`, `/manga/`, `/comics/` listing at least the sample series), or
- **No search**, with the consequence spelled out: "Maki won't find series on this site by itself. You'll link each series by pasting its URL on the series page."

**5. Details and test.** Name, id (derived from the name, editable here on creation only, read-only on every later edit), language, kind, content, rating, request spacing. "Test" runs the full draft against the sample: the search query (when there is one), the sample series' chapter list with the `url` round-trip check, the pages of `sample.chapterUrl` (not the newest chapter, which may be locked or not up yet), and one image.

- **On creation**, every check must pass before Save is enabled.
- **On edit**, a failing check shows "Save anyway". Otherwise an admin couldn't rename a source, change its spacing or save a partial fix while the site is down or half broken.
- Saving an edit of a source with linked series first shows the [identity diff](#identity-diff) when it applies.

Save writes the definition and reloads the registry. When the wizard was opened from a series page, it returns to the Link a source modal.

Editing an existing definition opens the same wizard on step 5 with the stored sample, so the admin sees at once whether it still works and can step back to whichever section broke.

## Identity diff

A chapter's identity is its number and language. A change to `language`, to `chapters.item`, or to how `number` is extracted makes the next sync create new `Chapter` rows for every linked series, which are wanted and get downloaded again (a non-English language also adds the ` [xx]` file suffix). `monitoring.bulkholdthreshold` softens that only on monitored refreshes. So before any change to a definition with linked series takes effect, whether a hand edit, an import over an existing id, a manual update or an automatic one, `CustomSourceStore` compares old and new:

1. `language` changed.
2. The sample series' chapter list under the new definition has different numbers, a different count, or different chapter ids from the list under the old one. Different ids matter because queued downloads and file records hold `SourceChapterId`.
3. The set of hosts the sample chapter's page images come from changed.
4. The `baseUrl` host changed.

A hand edit or a manual apply shows what changed (for example "Chapter numbers change for 3 of 12 chapters on the sample series; 41 linked series could get new chapter rows") and asks to confirm. An automatic update with any of the four differences is not applied; it waits as "Update available" for the admin.

## Health

`HealthMonitor` reports sources that fail during sync and downloads, but it does not probe them. Its own refresh runs every 15 minutes with 10 s probe timeouts (`Program.cs:1125-1131`), which a chapter list at 2 s spacing or a FlareSolverr solve would not fit, so custom sources get their own job.

- **`CustomSourceHealthJob`** ticks hourly and checks each enabled custom source whose last check is more than six hours old. The last check time and result are stored as `HealthChecks` rows (`custom:source:{name}`), so a restart doesn't re-probe everything.
- **The check** lists the sample series' chapters. If that fails, it tries one other linked mapping of the source before reporting, so a taken-down sample doesn't mark a working source broken. An unknown series on both counts as "sample needs replacing", not "Not working".
- **Rows exist only while the source is enabled**, and are removed on disable or delete, following the Suwayomi row pattern (`CheckSuwayomiAsync`). A disabled source isn't probed.
- **A failure** marks the source "The site's layout may have changed. Edit the definition to check.", linking to `sources=edit:custom-{id}`. The row in Manage sources shows "Not working" from the same result, via `GET search/sources`.

This is the main maintenance cost of definitions, so it has to be visible rather than discovered as a stalled series.

## Repositories

A repository is an `https://` URL serving an index:

```json
{
  "name": "Someone's sources",
  "definitions": [
    { "id": "silentquill", "name": "SilentQuill", "language": "en", "schema": 1,
      "version": 3, "url": "silentquill.json", "sha256": "..." }
  ]
}
```

Plain `http://` repositories can be added and installed from, but can't turn on "Keep up to date". The `sha256` comes from the same server as the file, so it only catches corruption and partial uploads, not tampering. Over plain HTTP anyone on the path could change both.

Stored in `sources.customrepositories` as `[{ "url", "name", "autoUpdate", "lastCheckedUtc", "lastError", "index" }]`, where `index` is the last fetched index. "Update available" and the diff are computed from that stored index plus the definition file fetched on demand.

**Panel.** "Add source > Repositories" swaps the catalogue pane for a repository panel inside the modal (the same pane swap the narrow layout uses for catalogue and priority), with a back button. It lists the repositories, each with its "Keep up to date" switch, last check and last error. It lists the selected repository's definitions with:

- **Install**, for one not installed.
- **Installed** and the installed version, for one installed from this repository.
- **Edited locally**, for one installed from it and then detached. That one offers "Replace with repository version", which goes through the identity diff.
- **"Needs a newer Maki"**, for an entry whose `schema` this build doesn't know. It is skipped quietly in the daily job instead of failing validation every day.

Installing copies a definition into `sources.custom` with `origin`, runs the step 5 test against its sample, and saves only if the test passes.

**Updates.** `CustomSourceRepositoryJob` refreshes each index once a day, and the panel has a "Check now" button. For an installed, not detached definition whose repository `version` is higher:

- **With "Keep up to date" off** (the default for a newly added repository), the row in Manage sources shows "Update available". Edit shows a field-by-field diff and an Apply button, and Apply goes through the identity diff.
- **With it on**, Maki applies the update itself, but only when all of these hold:
  1. The file's hash matches the index's `sha256`, and it validates.
  2. The step 5 test passes against the new definition's sample.
  3. The [identity diff](#identity-diff) finds none of its four differences: same language, same `baseUrl` host, same sample chapter numbers, count and ids, and the same image hosts.

  Anything else waits as "Update available", with the reason in the repository's `lastError`, and the source keeps running on the installed version.

Applying an update (either way) keeps the replaced definition in `previous`, so Edit offers "Revert to version N" for one step back. An automatically applied update is logged and raises an admin inbox notification naming the source and both versions. That needs a new append-only `InboxEventType` (`CustomSourceUpdated`) listed in `IsAdminOnly` (`InboxEventType.cs:61-67`), with its preference and renderer keys.

**Races with a running sync.** An update swaps the registry snapshot atomically, so a sync or download already holding the old `CustomSource` finishes on the old definition, and the next one uses the new. `CustomSourceStore` evicts the source's `SourceChapterListCache` entries on every change (a new `EvictSource(name)`, since the cache is keyed by source name and has no such call today, `SourceChapterListCache.cs:91-92`), so resolves don't run on a listing made by the old definition for up to five minutes. A queued download whose `SourceChapterId` no longer exists under the new definition can't happen through an automatic update, since changed ids block it. Through a confirmed manual change, it fails as "not listed" and fails over like any chapter a site removed.

**Local edits.** Editing a definition installed from a repository detaches it: `origin` is cleared, `detached` is set, and the row's tag becomes "Edited locally". Otherwise the next automatic update would silently overwrite the admin's fix. The Edit screen says so before the first change is saved.

**Removing a repository** detaches every definition installed from it. The sources keep working as hand-made ones, and the confirmation lists them.

**Conflicts.** An id that is already taken by a hand-made definition, or by one from another repository, is refused at install, never overwritten. A detached definition from the same repository is the "Replace" case above.

Export writes exactly the stored definition minus `origin`, `previous` and `detached`, so a definition made in the wizard can be put in a repository unchanged.

## Security

- **Admin only, and pinned.** Every `CustomSourcesController` action is `Policies.Admin`. `ControllerAuthorizationInventoryTests` checks policies by name only for `SettingsController`, and for other controllers only requires a mutating action to have some policy (`ControllerAuthorizationInventoryTests.cs:70-98`). That leaves the list and export GETs uncovered, so a `CustomSourcesControllerTests` asserts `Policies.Admin` on every action, as `SuwayomiControllerTests` does. The "Add source" button, the row menus and the series-page offer render only for admins.
- **Fetching admin-typed URLs.** All custom source HTML, repository indexes and definition files go through the guarded `custom-sources` client described in [Fetching](#fetching). Connections to non-public addresses are refused at connect time, including after redirects and DNS changes, and every HTML fetch is held to the definition's host. The FlareSolverr handoff checks the host first. `probe` returns page bodies to the admin, so without these rules it would read the LAN. The validator's refusal of IP literals and `localhost` in `baseUrl` is only an early, readable error; the connect-time guard is what actually enforces it.
- **Covers** go through the existing `covers` client, which is already held to public addresses with redirects re-checked per hop (`Program.cs:122-140`). `CoverHostPolicy` plus the definition's `coverHosts` decide which domains the proxy will fetch at all.
- **Page images** go through the guarded `pages` handler chain.
- **Regex** is compiled with `RegexOptions.NonBacktracking` where the pattern allows it, and always with a 250 ms match timeout. A timeout is a failed extraction, not a hung sync.
- **Limits**: a definition is at most 32 KB, a repository index at most 256 KB, a fetched document is read up to 8 MB, a chapter list is capped at 5,000 items and 50 pages, a catalogue at 50 pages, a page list at 1,000. Unknown JSON fields are rejected on save and import, so a typo can't silently do nothing.
- **Nothing executes.** No script evaluation, no `eval` on extracted text, no headless browser.

## Rule files

- `src/Maki.Sources/Custom/**` is covered by `src/Maki.Sources/CLAUDE.md` automatically; the engine gotchas go there.
- The fetching and SSRF rules belong in `.claude/rules/auth.md`, which already covers `CoverHostPolicy` and Suwayomi. Its `paths` frontmatter gains `src/Maki.Sources/Custom/**`, `src/Maki.Api/Controllers/CustomSources*.cs` and `src/Maki.Core/Sources/Custom/**`.
- `src/Maki.Api/Jobs/CustomSource*.cs` and the frontend wizard files need a home too. Check whether an existing rule file's globs already match them, and add them if not.
- `docs/sources.md` gets a "Custom sources" section. The README's built-in source count doesn't change.

## Phase 1 as built

On `feat/custom-sources`, 2026-10-27. What exists:

- **Backend.** The definition model, validator, engine (`CustomSource`, `CustomExtractor`, `CustomTemplate`), `InvalidCustomSource` stubs, `CustomSourceStore`, `CustomSourceProvider`, `CustomSourceFetcher` over the guarded `custom-sources` client, the `custom-source-pages` client, `CustomSourceThrottle`, `CustomSourceService` with the identity diff, `CustomSourceTester`, and `CustomSourcesController`.
- **Endpoints**, all `Policies.Admin`, under `api/v1/custom-sources`:
  - `GET` lists stored entries with their raw JSON, validity and linked series count.
  - `GET {id}` exports a definition without `origin`.
  - `POST test` runs an unsaved draft.
  - `PUT {id}` creates or replaces. It answers 409 `error.customSource.identityChange` with `reasons` and `changes` until it is sent with `confirm=true`.
  - `DELETE {id}` removes the definition, keeps the mappings and clears their `LastError`.
- **Other changes.** `GET search/sources` reports `provider: "custom"` and `custom: { id, hasSearch, invalid, problem }`. `SourceAvailability.DefaultsOn` turns custom sources on. `SourceMatchService` skips search-less ones. `HtmlFetchRequest.Headers`, `ChallengeAwareFetcher.FetchDetailedAsync`, its overridable client name and pre-FlareSolverr hook, `SourceChapterListCache.EvictSource`, and `Common/RelativeDate` (Toonily now uses it). `ApiError.payload` on the client carries the problem list and the identity findings.
- **Manage sources.**
  - The Custom pill beside the host, the "Link by URL" and "Not working" tags, and a "Custom" provider chip.
  - A "..." menu on custom rows with Edit, Export and Delete.
  - "Add source" with one item, Import definition.
  - `CustomSourceEditor`: a JSON editor with Open file, Test (per-step results) and Save. A new source needs a passing test before Save is enabled; an edit can save without one. The id can't change on edit.
- **Tests.**
  - `CustomSourceTests`: 30 tests against recorded SilentQuill pages, with the definition in `tests/Maki.Sources.Tests/Fixtures/silentquill-definition.json`.
  - `CustomSourcesControllerTests`: admin pinning, save, delete, host clash, problems and export.
  - Storybook stories for the pill, the filter, the import flow and the reader view.
  - A live run of `CustomSourceTester` against silentquill.net through the real guarded clients passed every step: search found the series, 2 chapters, 40 pages for chapter 2, and a 289 KB WebP first page. That run was a throwaway test and is not committed.

Not in phase 1, as planned: the wizard and its guessers, `probe`, the health job, deep links, the series-page entry point, engine presets, repositories (`origin` is accepted and kept, nothing reads it yet), and the language-ranking warning.

## Phases

1. **Engine.**
   - Schema, validator, `CustomSource`, extractors, `CustomSourceStore`, provider with stubs, settings key.
   - The guarded `custom-sources` client, the `ChallengeAwareFetcher` changes (client name, headers, answered path), per-source `SpacingGate`s, rate-limited page fetches.
   - `GET`/`PUT`/`DELETE` plus import and export, with the identity diff on edits.
   - In Manage sources: the Custom pill, the Provider chip, the row menu, and "Add source > Import definition" with a plain JSON editor and the test, but no guessing yet.
   - SilentQuill (recorded fixtures) is the first test case, and *Thus, the Seasons Learn of Blue* gets chapter 2.
2. **Wizard.**
   - `probe` and `test` endpoints, the guessers, the five steps, the Madara preset, the series-page entry point, deep links, the health job.
   - Test corpus: the HTML-scraped built-in sources' recorded fixtures in `tests/Maki.Sources.Tests/Fixtures`, where the series page and chapter page are both HTML. Many fixtures are JSON APIs or separate chapter endpoints and don't apply, so the usable set is smaller than the fixture count. For each that does, the guesser should produce a chapter list and page list that agree with what the hand-written source parses from the same file.
3. **More presets.** MangaThemesia, HeanCMS, Keyoapp.
4. **Repositories.** Index format, the panel, the daily job, opt-in automatic updates behind the identity diff, revert, detach on edit and on repository removal, the admin inbox event.
