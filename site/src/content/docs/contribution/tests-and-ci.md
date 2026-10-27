---
title: Tests, CI and Storybook
description: Run the backend tests and frontend checks, use Storybook, and see what CI runs on each pull request.
---

Run the same checks CI runs before you open a pull request. This page covers the backend tests, the frontend checks, Storybook, and what [`.github/workflows/ci.yml`](https://github.com/OrbitMPGH/Maki/blob/main/.github/workflows/ci.yml) runs.

## Backend tests

The solution is `Maki.sln` at the repo root. The test projects are under `tests/`:

- `Maki.Api.Tests`
- `Maki.Core.Tests`
- `Maki.Metadata.Tests`
- `Maki.Sources.Tests`

```bash
dotnet build -c Release
dotnet test -c Release

# One project
dotnet test tests/Maki.Sources.Tests
```

Tests that set a process-wide environment variable, such as the `MAKI_SOURCE_*_BASEURL` overrides, run in their own `base-url-override` collection. They do not run in parallel with tests that check the default hosts.

## Frontend checks

Run these from `frontend/` after `npm ci`.

- `npx tsc -b` type-checks the project. Use this and not `npx tsc --noEmit`. The root `tsconfig.json` has `"files": []` and only project references, so `--noEmit` checks nothing and exits 0 even when the tree is broken.
- `npm run build` runs `tsc -b` and then `vite build`. CI runs this first.
- `npm run check:secure-context` fails on raw `crypto.randomUUID`, `navigator.clipboard` and similar calls outside their helpers. See [Pull request conventions](/Maki/contribution/pull-requests/#secure-context).
- `npm run i18n:check` and `npm run i18n:validate` check the translation catalogues. `npm run i18n:bundle` checks the built output, so run `npm run build` first.
- `npx lingui extract --clean` regenerates `locales/en/client.po` from the `t` and `<Trans>` calls. Run it after you add or change UI text, and commit the result with the code.
- `npm run lint` runs oxlint. CI does not run it.

## Storybook

Storybook shows the UI one component or page at a time.

```bash
cd frontend
npm run storybook          # UI on http://localhost:6006
npm run test-storybook     # every story as a browser test
```

`test-storybook` runs each story as a Vitest browser test in headless Chromium through Playwright. A story fails when it throws, when its `play` function fails, or when it makes an API request that no mock answers. The first run needs the browser: `npx playwright install chromium`, the same command CI uses.

The rules that catch most people:

- **A new API call needs a mock in the same change.** Mocks live in `frontend/.storybook/mocks/`, one file per area. An unmocked request fails every story that reaches it.
- **Stories run inside the app's real providers**, set up once in `StoryProviders`. If a component needs a context the app gives it, add it there, not in each story.
- **Stories use plain English strings, not Lingui macros.** Story text must not end up in the translation catalogues.
- **Put state and count assertions inside `waitFor`.** A second request can land after the first element appears. A bare assertion can pass locally and fail on every CI run.

The full set of notes is in [`.claude/rules/storybook.md`](https://github.com/OrbitMPGH/Maki/blob/main/.claude/rules/storybook.md).

## What CI runs

CI runs on every push to `main` and on every pull request.

**Backend** (Ubuntu, .NET 10.0.x): restore, `dotnet build -c Release`, `dotnet test --no-build -c Release`.

**Frontend** (Ubuntu, Node 24, in `frontend/`):

1. `npm ci` and `npm run build`
2. Install Chromium, then `npm run test-storybook`
3. `npm run check:secure-context`
4. `npx lingui extract --clean`, `npm run i18n:check`, and a `git diff --exit-code ../locales`. If the diff is not empty, the catalogues were not committed.
5. `npm run i18n:validate`
6. `npm run i18n:bundle`, which fails if a non-English language ends up in the eager bundle

Each job has a 20 minute timeout. The site under `site/` has its own workflow, covered in [Write docs](/Maki/contribution/writing-docs/).
