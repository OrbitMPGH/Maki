---
title: Add a source
description: Write an ISource implementation for a new site in Maki.Sources, register it and test it.
---

This page is for adding a built-in source, which is a C# class in `Maki.Sources`. Before you write one, check whether the site fits a custom source. A custom source is a JSON definition that says where the search, chapter list and page images are, using CSS selectors, regular expressions or a path into JSON. It needs no code, and admins can set one up in the app. See [custom sources](/Maki/guides/sources/custom-sources/).

Write a built-in source when the site needs logic a definition cannot express, such as pages that are scrambled or API calls that have to be signed. Nothing in a definition runs as code, so those sites still need one.

## The interface

`ISource` is in [`src/Maki.Core/Sources/ISource.cs`](https://github.com/OrbitMPGH/Maki/blob/main/src/Maki.Core/Sources/ISource.cs). You implement these:

- `Name`: a stable lowercase key such as `mangadex`. It is stored in `SourceMapping.SourceName`, so it cannot change later.
- `DisplayName` and `BaseUrl`.
- `Capabilities`: `SourceCapabilities.None` unless the site needs FlareSolverr (`NeedsFlareSolverr`) or honours a language filter (`SupportsLanguageFilter`).
- `SearchAsync`, `GetSeriesAsync`, `ListChaptersAsync` and `GetPagesAsync`.

Several members have defaults you only override when they differ: `Kind` (aggregator), `Content` (manga), `Rating` (general), `SupportedLanguages` (English), `CoverHosts` for images served from another domain, `ResolveSeriesIdFromUrl` and `GetExternalIdsAsync`.

## Example: Asura

`src/Maki.Sources/Asura/AsuraSource.cs` is a small source that reads a JSON API. Use it as the template. It takes an `IHttpClientFactory`, reads a named client, and packs what it needs into the chapter id so that `GetPagesAsync` can rebuild the request from the two ids alone. Premium chapters that have not unlocked stay in the list, and `GetPagesAsync` throws `ChapterLockedException` so the download retries later.

Its named client is set up in `src/Maki.Api/Program.cs`, next to the other `AddHttpClient` calls, with the base address and headers the site expects.

## Steps

1. Create `src/Maki.Sources/<Site>/<Site>Source.cs` in namespace `Maki.Sources.<Site>`.
2. Implement `ISource`. Return chapters through `SourceChapterList.Normalize`, which removes duplicates by number, volume and language.
3. Register it in `src/Maki.Api/Program.cs` with `builder.Services.AddSingleton<ISource, <Site>Source>();`, next to the other source lines. Add a named HTTP client there if the source needs one.
4. Add a row to the table in [`docs/sources.md`](https://github.com/OrbitMPGH/Maki/blob/main/docs/sources.md), and to the copy of that table in the "Show all sources" dropdown in `README.md`. Bump the built-in count in `README.md` too.
5. Add tests in `tests/Maki.Sources.Tests/<Site>SourceTests.cs`.
6. Run them with `dotnet test tests/Maki.Sources.Tests`.

## Tests

Tests build a source on `FakeHttpClientFactory`, which answers requests from canned responses keyed by a substring of the URL. Use it to replay recorded pages. Put recorded HTML or JSON in `tests/Maki.Sources.Tests/Fixtures/`. The project copies that folder to the output directory, and `FakeHttpClientFactory.Fixture("name.html")` reads a file from it. Some tests, like Asura's, use short inline JSON instead.

Cover search, the chapter list and the page list. Include a case where the site returns a page without the expected payload, since that should throw rather than return an empty list.

## Things that catch people out

- **Tag each chapter with the language the site says it is.** Do not hardcode `"en"` for a site that publishes other languages. The language is half of a chapter's identity, and it becomes part of the file name when two languages of one chapter are downloaded.
- **Set `Kind`, `Content` and `Rating` when the defaults are wrong.** A scanlation group should say `SourceKind.Scanlator`. These values drive the tags the UI shows for the source.
- **`GetPagesAsync` runs at download time.** Resolve page URLs there, and return the headers each page needs in `PageRequest.Headers`.
- **An empty list and an exception mean different things.** An empty list says the site lists no pages. A response with no page payload should throw `InvalidOperationException` naming the URL.
- **Images on another domain need `CoverHosts`.** The cover proxy only fetches hosts the source lists, and a blocked host is logged with its name.

Before you start, read the site notes in [`src/Maki.Sources/CLAUDE.md`](https://github.com/OrbitMPGH/Maki/blob/main/src/Maki.Sources/CLAUDE.md). They record what other sites have needed, such as Cloudflare clearance, rate limits and residential IPs.
