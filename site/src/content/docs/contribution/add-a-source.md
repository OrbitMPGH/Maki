---
title: Add a source
description: Write an ISource implementation for a new site in Maki.Sources, register it and test it.
---

A built-in source is a C# class in `Maki.Sources`. First check whether the site fits a [custom source](/Maki/guides/sources/custom-sources/): a JSON definition (CSS selectors, regular expressions or JSON paths) that admins set up in the app with no code.

Write a built-in source when the site needs logic a definition cannot express, such as scrambled pages or signed API calls. Nothing in a definition runs as code.

## The interface

Implement `ISource` from [`src/Maki.Core/Sources/ISource.cs`](https://github.com/OrbitMPGH/Maki/blob/main/src/Maki.Core/Sources/ISource.cs):

- `Name`: a stable lowercase key such as `mangadex`. It is stored in `SourceMapping.SourceName`, so it cannot change later.
- `DisplayName` and `BaseUrl`.
- `Capabilities`: `SourceCapabilities.None` unless the site needs FlareSolverr (`NeedsFlareSolverr`) or honours a language filter (`SupportsLanguageFilter`).
- `SearchAsync`, `GetSeriesAsync`, `ListChaptersAsync` and `GetPagesAsync`.

Override these defaults only when they differ: `Kind` (aggregator), `Content` (manga), `Rating` (general), `SupportedLanguages` (English), `CoverHosts` for images served from another domain, `ResolveSeriesIdFromUrl` and `GetExternalIdsAsync`.

## Example: Asura

`src/Maki.Sources/Asura/AsuraSource.cs` is a small JSON API source. Use it as the template.

- It takes an `IHttpClientFactory` and a named client, set up in `src/Maki.Api/Program.cs` next to the other `AddHttpClient` calls with the site's base address and headers.
- It packs what it needs into the chapter id, so `GetPagesAsync` can rebuild the request from the two ids alone.
- Premium chapters that have not unlocked stay in the list. `GetPagesAsync` throws `ChapterLockedException` so the download retries later.

## Steps

1. Create `src/Maki.Sources/<Site>/<Site>Source.cs` in namespace `Maki.Sources.<Site>`.
2. Implement `ISource`. Return chapters through `SourceChapterList.Normalize`, which removes duplicates by number, volume and language.
3. Register it in `src/Maki.Api/Program.cs` with `builder.Services.AddSingleton<ISource, <Site>Source>();`, next to the other source lines. Add a named HTTP client there if the source needs one.
4. Add a row to the table in [`docs/sources.md`](https://github.com/OrbitMPGH/Maki/blob/main/docs/sources.md), and to the copy of that table in the "Show all sources" dropdown in `README.md`. Bump the built-in count in `README.md` too.
5. Add tests in `tests/Maki.Sources.Tests/<Site>SourceTests.cs`.
6. Run them with `dotnet test tests/Maki.Sources.Tests`.

## Tests

Build the source on `FakeHttpClientFactory`, which answers requests with canned responses keyed by a substring of the URL.

- Put recorded HTML or JSON in `tests/Maki.Sources.Tests/Fixtures/` and read it with `FakeHttpClientFactory.Fixture("name.html")`. Short inline JSON works too, as in Asura's tests.
- Cover search, the chapter list and the page list.
- Include a page response without the expected payload. It should throw, not return an empty list.

## Things that catch people out

- **Tag each chapter with the language the site reports.** Do not hardcode `"en"`. Language is half of a chapter's identity and goes into the file name when two languages of one chapter are downloaded.
- **Set `Kind`, `Content` and `Rating` when the defaults are wrong.** A scanlation group is `SourceKind.Scanlator`. They drive the tags the UI shows.
- **`GetPagesAsync` runs at download time.** Resolve page URLs there, and return the headers each page needs in `PageRequest.Headers`.
- **Empty list and exception differ.** An empty list means the site lists no pages. A response with no page payload throws `InvalidOperationException` naming the URL.
- **Images on another domain need `CoverHosts`.** The cover proxy only fetches hosts the source lists, and a blocked host is logged with its name.

Read [`src/Maki.Sources/CLAUDE.md`](https://github.com/OrbitMPGH/Maki/blob/main/src/Maki.Sources/CLAUDE.md) first. It records what other sites have needed, such as Cloudflare clearance, rate limits and residential IPs.
