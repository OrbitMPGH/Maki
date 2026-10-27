---
title: Architecture tour
description: What each project owns, and where a change belongs.
---

Maki is a .NET 10 API with a React frontend. The backend is split into projects by what they own, and that split decides where new code goes. This page walks through each project.

```text
src/
├── Maki.Api/        ASP.NET Core host, REST /api/v1, SignalR, Quartz jobs, download workers
├── Maki.Core/       Domain: entities, ISource and IMetadataProvider, parsing, CBZ pipeline
├── Maki.Data/       EF Core and SQLite, migrations
├── Maki.Sources/    One ISource implementation per site
└── Maki.Metadata/   MangaBaka provider, local dump and embeddings
frontend/            Vite, React, TypeScript and Mantine SPA
```

## Maki.Core

Domain code with no infrastructure dependencies. It holds the entities, the `ISource` and `IMetadataProvider` interfaces, the parsers and the CBZ pipeline. `Security/`, the `MakiPermission` flags and `ICurrentUser` also live here, so domain code can check permissions without referencing ASP.NET Identity. If a change needs an ASP.NET or EF Core type, it probably belongs in another project.

Image work goes through `Maki.Core.Images` (`ImageCodec`, `Raster`). Do not call NetVips directly.

## Maki.Data

EF Core with SQLite. It holds the `MakiDbContext`, the migrations and the SQLite pragma setup. Entities live in Core, with one exception: `Identity/` holds `MakiUser`, `UserApiKey`, `UserRootFolder` and `AuthEvent`. `MakiUser` derives from `IdentityUser<int>`, and putting it in Core would pull ASP.NET Identity stores into a project that has no infrastructure dependencies.

Migrations apply on startup, so users never run a manual step. To add one:

```bash
dotnet ef migrations add <Name> --project src/Maki.Data --startup-project src/Maki.Api
```

## Maki.Sources

One `ISource` implementation per site, each in its own folder such as `Asura/` or `MangaFire/`. A source searches, lists series and chapters, and resolves page images. Page URLs are resolved at download time, not when the item is queued, because some of them expire. Page requests carry their own headers (Referer, cookies) through to the image fetch, so hotlink-protected CDNs still work.

The site-specific notes live in [`src/Maki.Sources/CLAUDE.md`](https://github.com/OrbitMPGH/Maki/blob/main/src/Maki.Sources/CLAUDE.md). Read them before you change a source. For adding one, see [Add a source](/Maki/contribution/add-a-source/).

## Maki.Metadata

The MangaBaka metadata provider, the local MangaBaka dump, and the ONNX embeddings that recommendations are built from.

## Maki.Api

The host. It holds the ASP.NET Core setup, the controllers under `/api/v1`, the SignalR hubs, the Quartz jobs, the download workers and the DI wiring. Sources are registered in `src/Maki.Api/Program.cs`, one `AddSingleton<ISource, ...>()` line each. Server-side localization lives in `Localization/`.

## frontend

A Vite, React, TypeScript and Mantine single-page app. Its folders are `api/` for the client code that calls the API, `pages/` and `components/` for the UI, `lib/` for shared helpers, and `pwa/` and `sw/` for the service worker. The dev server runs on port 5173 and proxies `/api` and `/signalr` to the API on port 8990.

Browser APIs that do not exist on plain HTTP need their helpers from `lib/`. See [Pull request conventions](/Maki/contribution/pull-requests/#secure-context).

## Where the detail is

The subsystem notes in `.claude/rules/` are the most detailed reference for each area, covering auth, downloads, series matching, the reader, recommendations and more. They are written for AI assistants, but they are the same facts a human contributor needs.
