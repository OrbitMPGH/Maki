# AGENTS.md

See [README.md](README.md) for overview. Before a release or smoke-test, follow [docs/release-checklist.md](docs/release-checklist.md).

Edit this file, never the `CLAUDE.md` stub beside it.

## Subsystem rules

Gotchas live in `.claude/rules/*.md` (`archive`, `auth`, `design-system`, `downloads`, `enhance`, `health`, `i18n`, `import-lists`, `infra`, `komga`, `native`, `notes`, `opds`, `pwa`, `reader-progress`, `recommendations`, `series-matching`, `stats-notifications`, `storybook`) and in nested `AGENTS.md` files (`distribution/AGENTS.md`, `src/Maki.Sources/AGENTS.md`). Claude Code loads them automatically; Antigravity loads them via generated wrappers in `.agents/rules/`. **Any other agent: before editing a file, read every rule file whose `paths` frontmatter matches it, plus any nested `AGENTS.md` for that folder.**

When adding a file to a subsystem, check whether its rule file's `paths` globs match it; add it if not. After editing any rule file's `paths`, or adding/removing a rule file or nested `AGENTS.md`, run `node scripts/agents/sync-antigravity-rules.mjs` (CI checks `--check`).

## User-facing text style

- **All new user-facing text goes through `t`/`<Trans>` (frontend) or `this.Fail(localizer, key)`/`ILocalizer` (backend). Never raw English literals.** The app ships in eleven languages; see `.claude/rules/i18n.md` for details.
- **No em dashes, anywhere.** Avoid AI writing tells (no "it's not just X, it's Y", no rule-of-three lists, no "leverage"/"seamless"/"robust"/"delve" filler, no "note that"/"it's worth mentioning"). Write plain, direct sentences.
- **Secondary text is opt-in.** Omit eyebrows, kickers, hints, and `description` props (`ModalHeading`, `EmptyState`, `PageHeader`) unless they add a fact the heading or control does not: a consequence, a requirement/limit, or a term's only explanation. One short sentence when kept.

## Release notes and new settings

- **User-visible changes add or edit a file in `changes/`** (see [changes/README.md](changes/README.md)). Fixes to unreleased work add nothing; edit or delete that feature's entry if its final form changed.
- **A new setting gets `since: 'next'` on its entry in `frontend/src/pages/settings/registry.ts`**, which puts it on the Settings New tab after the upgrade.
  - A new control inside an existing card gets its own `within` entry, a `setting-{id}` anchor, `useNewControlMark(id)` on the element that holds it (the New tab shows only marked parts of an older card), and a `<NewSettingPill id>` beside its label.
  - A whole card gets the pill from `SettingsSection`.
  - A control inside an editor or in every row of a list is `nested: true`, and the mark goes on that list.
  - The release commit runs `npm run settings:stamp-since -- <version>` in `frontend/`; CI fails a PR into `main` while any `'next'` is left.

## Branches, worktrees, and PRs

**Small work is committed straight to `dev`. Medium and large work gets its own `feature/<name>` branch, its own worktree, and a PR into `dev`.**

- **Small:** a bug fix, a tweak, a copy or style change, or a feature that fits in one session and touches a handful of files.
- **Medium or large:** anything else. Typical signs: a new page or subsystem, a migration, a change across backend and frontend, work that will span several sessions, or work the owner might still drop.

If you can't tell which one a request is, ask the owner before you start.

### Workflow for medium and large work

**Every step below is required, in order.** Agents most often go wrong at three points: opening a PR without the owner's go-ahead (step 2), opening it after running only the tests (step 3), and reporting back right after opening it without running the reviewers (steps 4 and 5).

1. **Create the branch and worktree from `dev`.**
   - **In T3 Code, use its worktree tool when it is available.** Call `t3_worktree_status` first, then `t3_worktree_handoff` with `branch: "feature/<name>"`, `baseRef: "dev"` and `startFromOrigin: true` (check `startedFromOrigin` in the result). It moves this thread into the new worktree and runs the project's setup script, so it must be the last call of the turn: pass a `continuationPrompt` to carry on there. To start a separate agent in its own worktree, use `t3_thread_launch` with `workspaceStrategy` instead.
   - **Otherwise, by hand:**
     ```
     git checkout dev && git pull
     git worktree add .claude/worktrees/<name> -b feature/<name> dev
     cd .claude/worktrees/<name>/frontend && npm ci
     ```

   Do not use the `Agent` tool's `isolation: "worktree"`; it sometimes bases the worktree on `main`. Run a real `npm ci` in the worktree's `frontend/` if the setup script did not; never junction or copy `node_modules` from the main checkout. Every edit, build, and commit from here on happens inside the worktree path, not the main checkout.

2. **Do the work with the owner. This step is a loop.** Build what was asked, then stop and report back. The owner looks at it and may ask for changes, so redo or adjust and stop again. Repeat for as many rounds as it takes. Commit to the feature branch as pieces land. For UI iteration, leave changes uncommitted until the owner has looked at them.

   **Every time you report back on frontend changes, give the owner a way to see them right away**, with one of:
   - screenshots of the new or changed screens, or
   - a running instance built from the worktree, with its URL. Use the alt ports (backend `8991`, frontend `5174`); the owner's own instance is on `8990`. The backend port comes from `Port` in the config dir's `config.json`.

   **A worktree instance gets its own config dir but reads the main checkout's dump.** Copy the config without the dump (`robocopy .simulated .claude\worktrees\<name>\.simulated /E /XF mangabaka.db* mangabaka.catalogue`) and start the backend with `MAKI_SHARED_DUMP_DIR=<main checkout>\.simulated`, which saves 3.7 GB per worktree. Such an instance never downloads, refreshes, or backfills the dump. Leave the variable unset on a branch that changes what the dump install writes (its tables, `BrowseColumns`, indexes): that branch needs its own copy.

   **Stop here. Do not move on to step 3 by yourself.** Finishing the requested work is not a signal to open a PR. Wait until the owner says to go ahead with a PR. The only exception is when the owner's original request clearly said to take it all the way through to a PR.

3. **Run every check CI runs, then open the PR.** Running the tests alone is not enough: the Release build treats analyzer warnings as errors. Only once the owner has said to. All of them must pass:
   - `dotnet build -c Release -warnaserror -warnnotaserror:NU1901,NU1902,NU1903,NU1904` and `dotnet test -c Release`
   - in `frontend/`: `npm run lint`, `npm run build` (includes `tsc -b`), `npm run check:secure-context`, `npm run i18n:validate`, `npm run i18n:bundle`, `npm run test-storybook`
   - if strings changed: `npx lingui extract --clean` and `npm run i18n:check` in `frontend/`, with the updated `locales/` committed
   - a `changes/` entry if a user would notice the change

   Push the branch and open the PR into `dev` as described under [Pull requests](#pull-requests). **Opening the PR does not finish the work: go straight on to steps 4 and 5 in the same turn.**

4. **Get an agent review, right after opening the PR.** Do not report back to the owner first. Spawn a fresh reviewer with the `Agent` tool, `model: opus`, `effort: medium`. If T3's `orchestrator_capabilities` lists them, also launch a GPT 6.2 Sol reviewer (`codex`, `reasoningEffort: medium`) and a Gemini 4 Neon reviewer (`antigravity`, `gemini-4-neon`, `thought_level: medium`) with `delegate_task`, all at the same time and with the same brief. Give each reviewer the PR number, the base branch, and the rule files that apply. Do not give them your own opinion of the change.

5. **Fix the findings, then hand over to the owner.** Fix every finding from every reviewer, commit, push. If you believe a finding is wrong, do not skip it quietly: say which one and why in the handover. The handover has the PR link, a short summary, and what you did not check. If the review fixes changed the UI, include fresh screenshots or restart the running instance, as in step 2.

6. **Merge only when the owner says so.** Then, in this order:
   1. Run `git status` in the worktree. Move research notes, plans, benchmark data, and scratch scripts worth keeping to `research/<topic>/<date>-<name>/` in the main checkout (git-ignored). Tell the owner what you moved.
   2. `gh pr merge <number> --merge`, then `git pull` on `dev` in the main checkout.
   3. `git worktree remove <worktree path>` (`.claude/worktrees/<name>` by hand; `t3_worktree_status` reports a T3 one), then `git branch -D feature/<name>` and `git push origin --delete feature/<name>`. The branch cannot be deleted while the worktree still has it checked out, which is why `--delete-branch` is not used in step 2.

   If the work is **dropped** instead of merged, ask the owner before removing the worktree or the branch.

On Windows, `git worktree remove` fails with "Filename too long" on deep `node_modules` paths unless `git config --global core.longpaths true` is set. Delete a half-removed folder with `rd /s /q "\\?\<full path>"`, then `git worktree prune`.

## Pull requests

**PR titles are Conventional Commits (`feat(activity): group the history by series`), never a plain sentence.** The `PR title` check fails otherwise; types and scopes are in [CONTRIBUTING.md](CONTRIBUTING.md#pull-request-titles).

**The body fills in [.github/pull_request_template.md](.github/pull_request_template.md) section by section**, written to a file and passed with `gh pr create --body-file`, since `--body` and `--fill` skip the template. Every reviewer is an agent: name exact files and types, list the rule files that apply, answer each Touches line, and say plainly what was not checked. Drop the template's HTML comments from the filled body.

## Directory ownership

- `Maki.Core`: domain, no infra deps. Entities, `ISource`/`IMetadataProvider`, parsing, CBZ pipeline, `Security/` (`MakiPermission`, `ICurrentUser`).
- `Maki.Data`: EF Core + SQLite, migrations. Exception: `Identity/` holds `MakiUser`, `UserApiKey`, `UserRootFolder`, `AuthEvent` (avoids pulling ASP.NET Identity into Core).
- `Maki.Sources`: `ISource` implementations.
- `Maki.Metadata`: MangaBaka provider, local dump, embeddings, taste/EASE/graph caches.
- `Maki.Api`: host, controllers, Quartz jobs, DI wiring, download workers.
- `frontend`: Vite/React/Mantine SPA.

## Build/run gotchas

- API: `:8990`. Frontend dev server: `:5173`, proxies `/api` + `/signalr` to `:8990`.
- EF migrations auto-apply on startup. Add one with `dotnet ef migrations add <Name> --project src/Maki.Data --startup-project src/Maki.Api`.
- State resolves via `MAKI_CONFIG_DIR` (fallback `/config` in Docker, `%APPDATA%\Maki` on Windows). In dev, set `MAKI_CONFIG_DIR=$PWD/.devconfig`. `.claude/launch.json`'s `backend` preset uses real APPDATA; use `backend-dev` for anything destructive.
- Debug builds target the host RID (`Directory.Build.props`, smaller output); Release, `-r`, and `-p:MakiAllRuntimes=true` include all runtimes. Switching Debug/Release re-runs restore once each way.
- `src/Maki.Api/wwwroot/` must exist or startup throws `DirectoryNotFoundException`. Debug builds run `vite build` into it (`BuildFrontend`, skipped in Release, without `frontend/node_modules`, or with `-p:SkipFrontendBuild=true`).
- Frontend type-checking is `npx tsc -b`, **not** `npx tsc --noEmit` (root `tsconfig.json` uses project references, so `--noEmit` checks nothing and exits 0).
- **Self-hosters use plain HTTP on LAN (not a secure context).** `crypto.randomUUID`, `navigator.clipboard`, and `crypto.subtle` throw there. Use `randomUUID()` (`frontend/src/lib/uuid.ts`) and `copyText()` (`frontend/src/lib/clipboard.ts`). `npm run check:secure-context` enforces this; add new APIs to `scripts/frontend/check-secure-context.mjs`.

## Non-obvious domain facts

- **`/` is a redirect (`StartPageRedirect`), not a page.** `/discover` and `/home` both bounce to `/` when unavailable, so an unconditional `/` redirect to either infinite-loops.

## Code guidelines

- Keep comments to a minimum. Only write comments where warranted.
