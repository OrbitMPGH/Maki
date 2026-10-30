---
title: Development
description: Build the image, run from source and find your way around the code.
---

## Building the Docker image

The multi-stage [`Dockerfile`](https://github.com/OrbitMPGH/Maki/blob/main/Dockerfile) builds the frontend (Node 24) and backend (.NET 10) into an `aspnet:10.0` runtime image that serves the SPA from `wwwroot/`.

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

- The build context must be the repo root. Both stages copy `locales/` in (the API embeds `server.po`, Vite bundles `client.po`), and the build fails if a language is missing from either stage.
- `entrypoint.sh` fixes ownership of `/config` and drops to `PUID`/`PGID` via `gosu`.
- State lives in the `/config` volume. The library is a separate mount you can share with Kavita.
- `distribution/docker/Dockerfile` is an identical copy used by CI and the `distribution/build-*.ps1` scripts. Keep the two in step.

## Running from source

```bash
# Backend (http://localhost:8990, Swagger at /swagger in the Development environment only)
dotnet run --project src/Maki.Api

# Frontend dev server (http://localhost:5173, proxies /api and /signalr)
npm --prefix frontend run dev

# Tests
dotnet test

# Release publish
dotnet publish src/Maki.Api -c Release
```

State (database, logs, covers, caches, MangaBaka dump, embeddings, recommendation data) lives in `MAKI_CONFIG_DIR`. The fallback order is in [Environment variables](/Maki/reference/environment-variables/) and the contents in [Ports, paths and files on disk](/Maki/reference/ports-and-paths/). For development, point it at a throwaway directory:

```bash
MAKI_CONFIG_DIR="$PWD/.devconfig" dotnet run --project src/Maki.Api
```

EF Core migrations apply automatically on startup. Before changing code, see the [Architecture tour](/Maki/contribution/architecture/) for what goes where.
