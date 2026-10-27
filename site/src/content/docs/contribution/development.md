---
title: Development
description: Build the image, run from source and find your way around the code.
---

## Building the Docker image

The multi-stage [`Dockerfile`](https://github.com/OrbitMPGH/Maki/blob/main/Dockerfile) builds the frontend (Node 24) and backend (.NET 10)
and packages them into an `aspnet:10.0` runtime image serving the SPA from `wwwroot/`.

```bash
docker build -t maki:local .

docker run -d --name maki \
  -p 8990:8990 \
  -v "$PWD/maki-config:/config" \
  -v "/path/to/manga-library:/library" \
  maki:local

# Multi-arch push with Buildx
docker buildx build --platform linux/amd64,linux/arm64 -t ghcr.io/<you>/maki:latest --push .
```

- The build context must be the repo root: both stages copy `locales/` in (the API embeds
  `server.po`, Vite bundles `client.po`), and the build fails if any language is missing from
  either the frontend or the backend stage.
- `entrypoint.sh` fixes ownership of `/config` and drops to `PUID`/`PGID` via `gosu`.
- State lives in the `/config` volume; the library is a separate mount you can share with Kavita.
- `distribution/docker/Dockerfile` is an identical copy used by CI and the `distribution/build-*.ps1`
  scripts. Keep the two in step.

## Running from source

```bash
# Backend (http://localhost:8990, Swagger at /swagger in the Development environment only)
dotnet run --project src/Maki.Api

# Frontend dev server (http://localhost:5173, proxies /api and /signalr)
npm --prefix frontend run dev

# Tests
dotnet test

# Release publish (what the container ships)
dotnet publish src/Maki.Api -c Release
```

State lives in `MAKI_CONFIG_DIR`. If it is unset, Maki uses `/config` when that directory exists
(the Docker convention) and otherwise a per-user application-data folder, `%APPDATA%\Maki` on
Windows. It holds the SQLite database, logs, covers, page cache, MangaBaka dump, embedding index,
and precomputed recommendation data. For local development point it at a throwaway directory:

```bash
MAKI_CONFIG_DIR="$PWD/.devconfig" dotnet run --project src/Maki.Api
```

EF Core migrations apply automatically on startup.

## Architecture

```text
src/
├── Maki.Api/        ASP.NET Core host, REST /api/v1, SignalR, Quartz jobs, download workers
├── Maki.Core/       Domain: entities, ISource/IMetadataProvider, parser, naming, CBZ pipeline
├── Maki.Data/       EF Core + SQLite
├── Maki.Sources/    One ISource implementation per site
└── Maki.Metadata/   MangaBaka provider + local dump + ONNX embeddings
frontend/            Vite + React + TypeScript + Mantine SPA
```

Sources implement a single `ISource` interface (search / series / chapters / pages) and are
registered in DI. Adding a site is one class plus one registration. Page requests carry their own
headers (Referer, cookies) through to the image fetch, so hotlink-protected CDNs work.
