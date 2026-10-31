---
title: Pull request conventions
description: Branches, commit messages, and the rules a change has to follow before review.
---

Run the checks in [Tests, CI and Storybook](/Maki/contribution/tests-and-ci/) before you open a pull request.

## Branches

Branch from `dev` and open pull requests against `dev`. Name branches with the commit type as a prefix, such as `feat/series-archive` or `fix/...`. Hotfix branches are `hotfix/<version>`.

## Commit messages

Commits follow [Conventional Commits](https://www.conventionalcommits.org/): `<type>(<scope>): <summary>`.

- **Type:** `feat` (new capability), `fix`, `refactor` (no behavior change), `perf`, `test`, `docs`, `build` (Dockerfile, dependencies, build scripts), `ci` (`.github/workflows/`) or `chore` (anything else).
- **Scope** (optional): the project or feature area in lowercase, such as `series`, `auth`, `native`, `sources` or `opds`. Omit it when a change spans several.
- **Summary:** imperative, lowercase first word, no full stop, about 50 characters or fewer.
- **Body:** only the why, when the diff does not show it, such as a constraint or trade-off.

For example, `feat(discover): offer a thumbs up or down after marking a title seen`.

## User-facing text

Every user-visible string goes through a translation function. A raw English literal stays English in all eleven languages and the extractor never sees it.

- **Frontend:** wrap text in `t` or `<Trans>` from Lingui. Run `npx lingui extract --clean` from `frontend/` and commit the `locales/` changes with your code. Never hand-edit `locales/en/client.po`.
- **Backend:** return errors with `this.Fail(localizer, key)` through `ILocalizer`. The English text for a new key goes in `locales/en/server.po`, which you write by hand. Then run `npm run i18n:sync` from `frontend/` to copy the key to the other languages.
- **Culture:** never set `CurrentCulture` or `CurrentUICulture`, and never call `UseRequestLocalization`. Parsing depends on invariant culture, and an ambient culture changes what `"12.5"` means.

## Secure context

Self-hosters often open Maki over plain HTTP on a LAN address, where some browser APIs are undefined and throw. Use the helpers:

- `randomUUID()` from `frontend/src/lib/uuid.ts`, not `crypto.randomUUID`
- `copyText()` from `frontend/src/lib/clipboard.ts`, not `navigator.clipboard`

`npm run check:secure-context` fails the build on raw use. If you add a new API that needs a secure context, add it to `scripts/frontend/check-secure-context.mjs`.

## Writing style

These rules come from the repo's `CLAUDE.md` and apply to code comments, docs and UI text.

- Comment only where the reason is not obvious from the code.
- Write plain, direct sentences. No filler such as "leverage", "seamless", "robust" or "delve".
- No em dashes.

## Before you open it

- Run the backend and frontend checks, and the Storybook tests if you touched UI.
- Commit any catalogue changes the checks ask for.
- Describe what changed, why, and how you checked it. There is no pull request template.
