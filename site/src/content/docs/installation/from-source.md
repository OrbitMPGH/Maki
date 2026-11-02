---
title: Build and run from source
description: Prerequisites and the commands to run Maki from a source checkout. The full development setup is on the contribution page.
---

For a normal install, use [Docker Compose](/Maki/installation/docker-compose/),
[Unraid](/Maki/installation/unraid/), the [Windows app](/Maki/installation/windows/) or the
[Linux tarball](/Maki/installation/linux/). Run from source to change Maki or test a branch.

## Prerequisites

- .NET 10 SDK, for the backend in `src/`
- Node 24, for the frontend in `frontend/`

## Run it

Start the backend. It serves the API on port `8990`:

```bash
dotnet run --project src/Maki.Api
```

In a second terminal, start the frontend dev server on port `5173`. It proxies `/api` and `/signalr`
to the backend:

```bash
npm --prefix frontend run dev
```

Open `http://localhost:5173` for the app with hot reload, or `http://localhost:8990` for the backend
with the frontend built into it.

Point `MAKI_CONFIG_DIR` at a throwaway folder so you do not touch a real library or database:

```bash
MAKI_CONFIG_DIR="$PWD/.devconfig" dotnet run --project src/Maki.Api
```

Migrations apply on startup, as in the container.

:::tip
If startup fails with a `DirectoryNotFoundException` for `wwwroot`, the folder is missing. A Debug build
of `Maki.Api` builds the frontend into it when `frontend/node_modules` exists, so run
`npm --prefix frontend ci` once. Otherwise create the folder with `mkdir -p src/Maki.Api/wwwroot`.
:::

## Tests and a release build

```bash
dotnet test
dotnet publish src/Maki.Api -c Release
```

To build the Docker image yourself, run `docker build -t maki:local .` from the repository root.

## Next

The [contribution guide](/Maki/contribution/development/) covers the full setup and project layout.
