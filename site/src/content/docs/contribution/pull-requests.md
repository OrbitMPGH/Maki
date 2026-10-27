---
title: Pull request conventions
description: Branches, commit messages, and the rules a change has to follow before review.
---

This page covers what a pull request needs in this repo: where it branches from, how commits are written, and the rules for user-facing text and browser APIs. Run the checks in [Tests, CI and Storybook](/Maki/contribution/tests-and-ci/) before you open one.

## Branches

Work from `dev` and open pull requests against `dev`. Branch names in the history use the commit type as a prefix, such as `feat/series-archive` or `fix/...`. Hotfix branches are named `hotfix/<version>`.

## Commit messages

Commits follow [Conventional Commits](https://www.conventionalcommits.org/): `<type>(<scope>): <summary>`.

- **Type** is `feat` for a new capability, `fix` for a bug, `refactor` for a change with no behavior change, `perf`, `test`, `docs`, `build` for the Dockerfile, dependencies or build scripts, `ci` for `.github/workflows/`, or `chore` for anything else.
- **Scope** is optional. Use the lowercase project that owns the change: `core`, `data`, `sources`, `metadata`, `api` or `frontend`. Leave it out when a change spans several.
- **Summary** is in the imperative mood, with a lowercase first word, no full stop at the end, and about 50 characters or fewer.
- **Body** is only for the why, when the diff does not make it obvious: a constraint, the bug that prompted the change, or a trade-off. Do not restate the diff.

For example, `feat(discover): offer a thumbs up or down after marking a title seen`.

## User-facing text

Every string a user can see goes through a translation function. A raw English literal ships in English in all eleven languages, and the extractor never sees it.

- **Frontend:** wrap text in `t` or `<Trans>` from Lingui. Run `npx lingui extract --clean` from `frontend/` and commit the `locales/` changes with your code. Never hand-edit `locales/en/client.po`.
- **Backend:** return errors with `this.Fail(localizer, key)` through `ILocalizer`. The English text for a new key goes in `locales/en/server.po`, which you write by hand. Then run `npm run i18n:sync` from `frontend/` to copy the key to the other languages.
- **Culture:** never set `CurrentCulture` or `CurrentUICulture`, and never call `UseRequestLocalization`. Parsing code depends on invariant culture, and an ambient culture changes what `"12.5"` means.

## Secure context

Self-hosters open Maki over plain HTTP on a LAN address, where some browser APIs do not exist and throw when called. Use the helpers instead of the raw APIs:

- `randomUUID()` from `frontend/src/lib/uuid.ts`, not `crypto.randomUUID`
- `copyText()` from `frontend/src/lib/clipboard.ts`, not `navigator.clipboard`

`npm run check:secure-context` fails the build on raw use. If you add a new API that needs a secure context, add it to `scripts/frontend/check-secure-context.mjs`.

## Writing style

These rules come from the repo's `CLAUDE.md` and apply to code comments, docs and UI text alike.

- Keep comments to a minimum. Write one only where the reason is not obvious from the code.
- Write plain, direct sentences. Leave out filler words such as "leverage", "seamless", "robust" and "delve".
- No em dashes anywhere.

## Before you open it

- Run the backend and frontend checks, and the Storybook tests if you touched UI.
- Commit any catalogue changes that the checks ask for.
- Describe what changed and why, and how you checked it. The repo has no pull request template, so the description is free-form.
