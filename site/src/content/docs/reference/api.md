---
title: API keys and the OpenAPI spec
description: How to create a per-user API key, send it with requests, and browse the OpenAPI spec from a source build.
---

Maki has a REST API under `/api/v1`, the same one the web app uses. Scripts authenticate with a per-user API key.

## Create a key

1. Open **Settings → Account → API keys**.
2. Create a key. This asks for your password. Accounts that only use single sign-on need a recent sign-in instead.
3. Copy the key when it appears. Maki stores only a SHA-256 hash, so it is never shown again.

Revoke a key from the same card. It stops working on the next request. For keys with two-factor sign-in, see [Two-factor and API keys](/Maki/guides/running/two-factor-and-api-keys/).

## Send the key

Send the key in the `X-Api-Key` header.

```bash
curl -H "X-Api-Key: <your key>" http://localhost:8990/api/v1/series
```

- The header is the only way in. A `?apikey=` query parameter is ignored, so the request is unauthenticated. Keys stay out of URLs because they end up in browser history and server logs.
- A key has the permissions of the account that created it. Only full-access keys work on `/api/v1`.
- OPDS tokens and API keys are not interchangeable.

## OpenAPI spec and Swagger UI

Swagger UI and the spec are served only in the Development environment. The Docker image runs in Production, so `/swagger` is not available there.

To browse the spec, run Maki from source. The launch profile in `src/Maki.Api/Properties/launchSettings.json` sets Development:

```bash
dotnet run --project src/Maki.Api
```

Swagger UI is at `http://localhost:8990/swagger` and the JSON spec at `/swagger/v1/swagger.json`.

:::tip
Use a separate config folder so a source build does not touch your real library or database:

```bash
MAKI_CONFIG_DIR=$PWD/.devconfig dotnet run --project src/Maki.Api
```
:::
