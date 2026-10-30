---
title: Architecture tour
description: What each project owns, and where a change belongs.
---

Maki is a .NET 10 API with a React frontend. Each backend project owns one area, which decides where new code goes.

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

Domain code with no infrastructure dependencies: entities, the `ISource` and `IMetadataProvider` interfaces, parsers and the CBZ pipeline. `Security/`, the `MakiPermission` flags and `ICurrentUser` are here too, so domain code can check permissions without ASP.NET Identity. A change that needs an ASP.NET or EF Core type belongs elsewhere.

Image work goes through `Maki.Core.Images` (`ImageCodec`, `Raster`). Do not call NetVips directly.

## Maki.Data

EF Core with SQLite: `MakiDbContext`, the migrations and the SQLite pragma setup. Entities live in Core except `Identity/` (`MakiUser`, `UserApiKey`, `UserRootFolder`, `AuthEvent`). `MakiUser` derives from `IdentityUser<int>`, which would pull ASP.NET Identity stores into Core.

Migrations apply on startup. To add one:

```bash
dotnet ef migrations add <Name> --project src/Maki.Data --startup-project src/Maki.Api
```

## Maki.Sources

One `ISource` per site, each in its own folder such as `Asura/` or `MangaFire/`. Page URLs are resolved at download time, not when queued, because some expire. Page requests carry their own headers (Referer, cookies) to the image fetch, so hotlink-protected CDNs work.

Site notes are in [`src/Maki.Sources/CLAUDE.md`](https://github.com/OrbitMPGH/Maki/blob/main/src/Maki.Sources/CLAUDE.md). To add a source, see [Add a source](/Maki/contribution/add-a-source/).

## Maki.Metadata

The MangaBaka metadata provider, the local MangaBaka dump, and the ONNX embeddings that recommendations are built from.

## Maki.Api

The host: ASP.NET Core setup, controllers under `/api/v1`, SignalR hubs, Quartz jobs, download workers and DI wiring. Sources are registered in `src/Maki.Api/Program.cs`, one `AddSingleton<ISource, ...>()` line each. Server-side localization is in `Localization/`.

## frontend

Vite, React, TypeScript and Mantine. Folders: `api/` (API client), `pages/` and `components/` (UI), `lib/` (shared helpers), `pwa/` and `sw/` (service worker). The dev server runs on 5173 and proxies `/api` and `/signalr` to the API on 8990.

Browser APIs missing on plain HTTP need the helpers from `lib/`. See [Pull request conventions](/Maki/contribution/pull-requests/#secure-context).

## Where the detail is

`.claude/rules/` has the most detailed per-subsystem notes (auth, downloads, series matching, the reader, recommendations and more). They are written for AI assistants but hold the same facts a human needs.
