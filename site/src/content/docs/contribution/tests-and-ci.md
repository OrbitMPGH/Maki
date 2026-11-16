---
title: Tests, CI and Storybook
description: Run the backend tests and frontend checks, use Storybook, and see what CI runs on each pull request.
---

Run the checks CI runs ([`.github/workflows/ci.yml`](https://github.com/OrbitMPGH/Maki/blob/main/.github/workflows/ci.yml)) before you open a pull request.

## Backend tests

The solution is `Maki.sln` at the repo root. Test projects under `tests/`: `Maki.Api.Tests`, `Maki.Core.Tests`, `Maki.Metadata.Tests` and `Maki.Sources.Tests`.

```bash
dotnet build -c Release -warnaserror -warnnotaserror:NU1901,NU1902,NU1903,NU1904
dotnet test -c Release

# One project
dotnet test tests/Maki.Sources.Tests
```

CI fails on any compiler or analyzer warning, which is why the build line carries `-warnaserror`. Only NuGet vulnerability advisories stay warnings.

The JPEG XL tests skip when libjxl is not beside the test assemblies. CI provides it and sets `MAKI_REQUIRE_LIBJXL=1`, so a missing library fails them there. A Windows build fetches the libjxl DLLs on its own. On Linux, build them with `distribution/libjxl/build.sh` if you want those tests to run locally.

Tests that set a process-wide environment variable, such as the `MAKI_SOURCE_*_BASEURL` overrides, run in their own `base-url-override` collection, so they never run in parallel with tests that check the default hosts.

## Frontend checks

Run these from `frontend/` after `npm ci`.

- `npx tsc -b` type-checks. Do not use `npx tsc --noEmit`: the root `tsconfig.json` has `"files": []` and only project references, so it checks nothing and exits 0 on a broken tree.
- `npm run build` runs `tsc -b` and then `vite build`. CI runs this first.
- `npm run check:secure-context` fails on raw `crypto.randomUUID`, `navigator.clipboard` and similar calls. See [Pull request conventions](/Maki/contribution/pull-requests/#secure-context).
- `npm run i18n:check` and `npm run i18n:validate` check the translation catalogues. `npm run i18n:bundle` checks the built output, so run `npm run build` first.
- `npx lingui extract --clean` regenerates `locales/en/client.po` from `t` and `<Trans>` calls. Run it after changing UI text and commit the result.
- `npm run lint` runs oxlint. Errors fail CI; warnings are only reported.

## Storybook

```bash
cd frontend
npm run storybook          # UI on http://localhost:6006
npm run test-storybook     # every story as a browser test
```

`test-storybook` runs each story as a Vitest browser test in headless Chromium (Playwright). A story fails if it throws, its `play` function fails, or it makes an API request no mock answers. The first run needs `npx playwright install chromium`, as in CI.

The rules that catch most people:

- **A new API call needs a mock in the same change.** Mocks are in `frontend/.storybook/mocks/`, one file per area. An unmocked request fails every story that reaches it.
- **Stories run inside the app's real providers**, set up once in `StoryProviders`. Add missing contexts there, not per story.
- **Use plain English strings, not Lingui macros**, so story text stays out of the catalogues.
- **Put state and count assertions inside `waitFor`.** A second request can land after the first element appears, so a bare assertion can pass locally and fail on every CI run.

Full notes: [`.claude/rules/storybook.md`](https://github.com/OrbitMPGH/Maki/blob/main/.claude/rules/storybook.md).

## What CI runs

CI runs on every push to `main` and on every pull request. On a pull request, the backend jobs only run when `src/`, `tests/`, `locales/`, `scripts/ci/`, `distribution/libjxl/` or the build files changed, and the frontend jobs only when `frontend/`, `locales/`, `scripts/frontend/` or `scripts/i18n/` changed. A new push to a pull request cancels the run for the previous one.

**Backend** (Ubuntu, .NET 10.0.x), split across parallel jobs:

- `backend`: restore and `dotnet build -c Release -warnaserror` of the whole solution, restores libjxl from cache or builds it, then runs every test project except `Maki.Api.Tests` with `MAKI_REQUIRE_LIBJXL=1`.
- `api-tests (1/4)` to `(4/4)`: each builds `Maki.Api.Tests` and runs a quarter of its test classes through `scripts/ci/test-shard.sh`. To run one shard locally after a Release build: `bash scripts/ci/test-shard.sh tests/Maki.Api.Tests 2 4`.

**Frontend** (Ubuntu, Node 24, in `frontend/`):

- `frontend`:
  1. `npm ci`, `npm run lint` and `npm run build`
  2. `npm run check:secure-context`
  3. On a pull request into `main` only: `npm run settings:stamp-since -- --check`, which fails while any settings entry still says `since: 'next'`
  4. `npx lingui extract --clean`, `npm run i18n:check`, and `git diff --exit-code ../locales` (a non-empty diff means uncommitted catalogues).
  5. `npm run i18n:validate`
  6. `npm run i18n:bundle`, which fails if a non-English language ends up in the eager bundle
- `storybook (1/3)` to `(3/3)`: install Chromium, then `npm run test-storybook -- --shard=N/3`.

**Other checks on a pull request:**

- `PR title` fails when the title is not a Conventional Commits title. See [Pull request conventions](/Maki/contribution/pull-requests/#pull-request-titles).
- `release-notes-entry`, on pull requests into `dev`, warns when the change touches `src/` or `frontend/src/` and adds nothing under `changes/`. It never fails.
- CodeQL scans pull requests into `dev` and `main`.

The site under `site/` has its own workflow, covered in [Write docs](/Maki/contribution/writing-docs/).
