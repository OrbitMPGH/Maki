# Contributing to Maki

Thanks for helping out. This page covers where work goes and how pull requests are reviewed. For
the codebase layout and build notes, see [README.md](README.md) and [CLAUDE.md](CLAUDE.md).

## Branches

- **`dev` is the development branch.** All development happens from `dev`. New work starts from
  the latest `dev`, and every pull request targets `dev`.
- **`main` is the released branch.** It only receives release merges from `dev`. Each release is
  tagged `vX.Y.Z` on `main`.

Every pull request targets `dev`, including fixes for bugs in a released version.

## Making a change

1. Update your copy of `dev`:

   ```bash
   git checkout dev
   git pull origin dev
   ```

2. Create a branch from `dev`:

   ```bash
   git checkout -b fix/short-description
   ```

3. Make the change, then build and test it locally. CI runs the same checks on every pull request:

   ```bash
   dotnet build -c Release
   dotnet test -c Release
   ```

   For frontend changes, also run these from `frontend/`. The type check is `npx tsc -b`, not
   `npx tsc --noEmit`, because the root TypeScript config only lists project references.

   ```bash
   cd frontend
   npx tsc -b
   npm run check:secure-context
   ```

4. Open a pull request with **base branch `dev`**. The GitHub default base for pull requests is
   `main` on this repository, so check the base before you submit.

If `dev` moves on while your branch is open, merge or rebase `dev` into your branch so the pull
request stays up to date.

## Pull request titles

PR titles follow the same [Conventional Commits](https://www.conventionalcommits.org/) format as
commit messages, and the `PR title` check fails otherwise:

```
<type>(<scope>): <summary>
```

- **type:** `feat` (new capability), `fix` (bug fix), `perf`, `refactor` (no behaviour change),
  `docs`, `test`, `build` (Dockerfile, deps, build scripts), `ci` (`.github/workflows/**`),
  `style`, `revert`, `chore` (everything else). Add `!` before the colon for a breaking change.
- **scope:** the feature area, lowercase, the same names the commit log uses: `reader`, `series`,
  `library`, `discover`, `home`, `downloads`, `sources`, `import`, `upgrades`, `enhance`,
  `metadata`, `recommendations`, `health`, `activity`, `settings`, `auth`, `opds`, `pwa`, `ui`,
  `i18n`, `api`, `docker` and so on. Run `git log --format=%s | grep -oE '^[a-z]+\([^)]+\)'` to see
  them. Leave it out when the change spans many areas.
- **summary:** what the PR does, imperative, lowercase unless it starts with a name (`Jackett`,
  `MangaDex`), no trailing period. A PR of many commits
  gets a summary of the whole change, not of its first commit.

Examples: `feat(ui): run toasts on Sonner, keeping Maki's toast look`,
`feat(activity): group the history by series`,
`fix(upgrades): judge a release's language before it replaces Japanese files`.

Release PRs from `dev` to `main` are titled with the version alone (`v0.36.0`).

## Pull requests

The description follows [the PR template](.github/pull_request_template.md). PRs here are reviewed
by AI agents, which read the description first and then the diff, so the template asks for what a
reviewer without your context needs: the intent, where to start reading, which `.claude/rules`
files apply, what the change touches (migrations, files on disk, external API clients), and what
you did and did not verify. `gh pr create` skips the template unless you pass it, so fill a copy
and use `gh pr create --body-file`.

- Keep one change per pull request. Small, focused changes are reviewed faster.
- Describe what changed and why. Link the issue it fixes, if there is one.
- Add or update tests for behaviour you change.
- All user-facing text goes through the localisation helpers (`t` or `<Trans>` on the frontend,
  `ILocalizer` on the backend). Do not add raw English strings.
- Keep comments to a minimum. Write one only when the code cannot explain itself.

Maintainers may commit small fixes straight to `dev`. Everything else goes through a pull request.

## Releases

Releases are cut by maintainers. A release is a pull request from `dev` to `main`, titled with
the version (for example `v0.35.0`). The pre-release checklist is in
[docs/release-checklist.md](docs/release-checklist.md).

## Reporting bugs and requesting features

Use the [issue templates](.github/ISSUE_TEMPLATE). Bug reports are easier to act on when they
include the Maki version, your setup (Docker, Windows, or another platform), and the steps to
reproduce.
