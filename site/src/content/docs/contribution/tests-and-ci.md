---
title: Tests, CI and Storybook
description: Run the backend tests and frontend checks, use Storybook, and see what CI runs on each pull request.
---

Run the checks CI runs ([`.github/workflows/ci.yml`](https://github.com/OrbitMPGH/Maki/blob/main/.github/workflows/ci.yml)) before you open a pull request.

## Backend tests

The solution is `Maki.sln` at the repo root. Test projects under `tests/`: `Maki.Api.Tests`, `Maki.Core.Tests`, `Maki.Metadata.Tests` and `Maki.Sources.Tests`.

```bash
dotnet build -c Release
dotnet test -c Release

# One project
dotnet test tests/Maki.Sources.Tests
```

Tests that set a process-wide environment variable, such as the `MAKI_SOURCE_*_BASEURL` overrides, run in their own `base-url-override` collection, so they never run in parallel with tests that check the default hosts.

## Frontend checks

Run these from `frontend/` after `npm ci`.

- `npx tsc -b` type-checks. Do not use `npx tsc --noEmit`: the root `tsconfig.json` has `"files": []` and only project references, so it checks nothing and exits 0 on a broken tree.
- `npm run build` runs `tsc -b` and then `vite build`. CI runs this first.
- `npm run check:secure-context` fails on raw `crypto.randomUUID`, `navigator.clipboard` and similar calls. See [Pull request conventions](/Maki/contribution/pull-requests/#secure-context).
- `npm run i18n:check` and `npm run i18n:validate` check the translation catalogues. `npm run i18n:bundle` checks the built output, so run `npm run build` first.
- `npx lingui extract --clean` regenerates `locales/en/client.po` from `t` and `<Trans>` calls. Run it after changing UI text and commit the result.
- `npm run lint` runs oxlint. CI does not run it.

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

CI runs on every push to `main` and on every pull request.

**Backend** (Ubuntu, .NET 10.0.x): restore, `dotnet build -c Release`, `dotnet test --no-build -c Release`.

**Frontend** (Ubuntu, Node 24, in `frontend/`):

1. `npm ci` and `npm run build`
2. Install Chromium, then `npm run test-storybook`
3. `npm run check:secure-context`
4. `npx lingui extract --clean`, `npm run i18n:check`, and `git diff --exit-code ../locales` (a non-empty diff means uncommitted catalogues).
5. `npm run i18n:validate`
6. `npm run i18n:bundle`, which fails if a non-English language ends up in the eager bundle

Each job has a 20 minute timeout. The site under `site/` has its own workflow, covered in [Write docs](/Maki/contribution/writing-docs/).
