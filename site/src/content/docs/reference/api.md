---
title: API keys and the OpenAPI spec
description: How to create a per-user API key, send it with requests, and browse the OpenAPI spec from a source build.
---

Maki has a REST API under `/api/v1`. The web app uses it too. Scripts and other tools authenticate with a per-user API key.

## Create a key

1. Open **Settings → Account → API keys**.
2. Create a key. Creating one asks for your password. Accounts that sign in only through single sign-on need a recent sign-in instead.
3. Copy the key when it appears. Maki stores only a SHA-256 hash of it, so the key is never shown again.

Revoke a key from the same card. A revoked key stops working on the next request.

For how keys work alongside two-factor sign-in, see [Two-factor and API keys](/Maki/guides/running/two-factor-and-api-keys/).

## Send the key

Send the key in the `X-Api-Key` header:

```bash
curl -H "X-Api-Key: <your key>" http://localhost:8990/api/v1/series
```

Maki reads the key from the header only. A `?apikey=` query parameter is not accepted, because URLs end up in browser history and server logs.

A key has the same permissions as the account that created it. Only full-access keys work on `/api/v1`. An OPDS token from the reading settings does not work here, and an API key does not work in an OPDS feed URL.

## OpenAPI spec and Swagger UI

Swagger UI and the OpenAPI spec are served only in the Development environment. The Docker image runs in Production, so `/swagger` is not served there.

To browse the spec, run Maki from source. The project's launch profile in `src/Maki.Api/Properties/launchSettings.json` sets the Development environment, so this is enough:

```bash
dotnet run --project src/Maki.Api
```

Swagger UI is then at `http://localhost:8990/swagger`, and the JSON spec is at `/swagger/v1/swagger.json`.

:::tip
Point a source build at a separate config folder so it does not touch your real library or database:

```bash
MAKI_CONFIG_DIR=$PWD/.devconfig dotnet run --project src/Maki.Api
```
:::
