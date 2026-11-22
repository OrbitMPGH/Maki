# Agent Workflow

Detailed procedures referenced from [AGENTS.md](../../AGENTS.md) for medium/large work, pull requests, and new settings.

## Medium and large work

1. **Create the branch and worktree from `dev`, by hand.**
   ```
   git checkout dev && git pull
   git worktree add .claude/worktrees/<name> -b feature/<name> dev
   cd .claude/worktrees/<name>/frontend && npm ci
   ```
   Do not use the `Agent` tool's `isolation: "worktree"`; it sometimes bases the worktree on `main`. Run a real `npm ci` in the worktree; never junction or copy `node_modules` from the main checkout. Every edit, build, and commit from here on happens inside the worktree path, not the main checkout.

2. **Do the work with the owner. This step is a loop.** Build what was asked, then stop and report back. The owner looks at it and may ask for changes, so redo or adjust and stop again. Repeat for as many rounds as it takes. Commit to the feature branch as pieces land. For UI iteration, leave changes uncommitted until the owner has looked at them.

   **Every time you report back on frontend changes, give the owner a way to see them right away**, with one of:
   - screenshots of the new or changed screens, or
   - a running instance built from the worktree, with its URL. Use the alt ports (backend `8991`, frontend `5174`); the owner's own instance is on `8990`. The backend port comes from `Port` in the config dir's `config.json`.

   **A worktree instance gets its own config dir but reads the main checkout's dump.** Copy the config without the dump (`robocopy .simulated .claude\worktrees\<name>\.simulated /E /XF mangabaka.db* mangabaka.catalogue`) and start the backend with `MAKI_SHARED_DUMP_DIR=<main checkout>\.simulated`, which saves 3.7 GB per worktree. Such an instance never downloads, refreshes, or backfills the dump. Leave the variable unset on a branch that changes what the dump install writes (its tables, `BrowseColumns`, indexes): that branch needs its own copy.

   **Stop here. Do not move on to step 3 by yourself.** Finishing the requested work is not a signal to open a PR. Wait until the owner says to go ahead with a PR. The only exception is when the owner's original request clearly said to take it all the way through to a PR.

3. **Run every check CI runs, then open the PR.** Only once the owner has said to. All of them must pass:
   - `dotnet build -c Release -warnaserror -warnnotaserror:NU1901,NU1902,NU1903,NU1904` and `dotnet test -c Release`
   - in `frontend/`: `npm run lint`, `npm run build` (includes `tsc -b`), `npm run check:secure-context`, `npm run i18n:validate`, `npm run i18n:bundle`, `npm run test-storybook`
   - if strings changed: `npx lingui extract --clean` and `npm run i18n:check` in `frontend/`, with the updated `locales/` committed
   - a `changes/` entry if a user would notice the change

   Push the branch and open the PR into `dev` as described under [Pull requests](#pull-requests).

4. **Get an agent review.** Spawn a fresh reviewer with the `Agent` tool, `model: opus`, `effort: medium`. If T3's `orchestrator_capabilities` lists them, also launch a GPT 6.2 Sol reviewer (`codex`, `reasoningEffort: medium`) and a Gemini 4 Argon reviewer (`antigravity`, `gemini-4-argon`, `thought_level: medium`) with `delegate_task`, all at the same time and with the same brief. Give each reviewer the PR number, the base branch, and the rule files that apply. Do not give them your own opinion of the change.

5. **Fix the findings, then hand over to the owner.** Fix every finding from every reviewer, commit, push. If you believe a finding is wrong, do not skip it quietly: say which one and why in the handover. The handover has the PR link, a short summary, and what you did not check. If the review fixes changed the UI, include fresh screenshots or restart the running instance, as in step 2.

6. **Merge only when the owner says so.** Then, in this order:
   1. Run `git status` in the worktree. Move research notes, plans, benchmark data, and scratch scripts worth keeping to `research/<topic>/<date>-<name>/` in the main checkout (git-ignored). Tell the owner what you moved.
   2. `gh pr merge <number> --merge`, then `git pull` on `dev` in the main checkout.
   3. `git worktree remove .claude/worktrees/<name>`, then `git branch -D feature/<name>` and `git push origin --delete feature/<name>`. The branch cannot be deleted while the worktree still has it checked out, which is why `--delete-branch` is not used in step 2.

   If the work is **dropped** instead of merged, ask the owner before removing the worktree or the branch.

On Windows, `git worktree remove` fails with "Filename too long" on deep `node_modules` paths unless `git config --global core.longpaths true` is set. Delete a half-removed folder with `rd /s /q "\\?\<full path>"`, then `git worktree prune`.

## Pull requests

**PR titles are Conventional Commits (`feat(activity): group the history by series`), never a plain sentence.** The `PR title` check fails otherwise; types and scopes are in [CONTRIBUTING.md](../../CONTRIBUTING.md#pull-request-titles).

**The body fills in [.github/pull_request_template.md](../../.github/pull_request_template.md) section by section**, written to a file and passed with `gh pr create --body-file`, since `--body` and `--fill` skip the template. Every reviewer is an agent: name exact files and types, list the rule files that apply, answer each Touches line, and say plainly what was not checked. Drop the template's HTML comments from the filled body.

## Adding a new setting

**A new setting gets `since: 'next'` on its entry in `frontend/src/pages/settings/registry.ts`**, which puts it on the Settings New tab after the upgrade.

- A new control inside an existing card gets its own `within` entry, a `setting-{id}` anchor, `useNewControlMark(id)` on the element that holds it (the New tab shows only marked parts of an older card), and a `<NewSettingPill id>` beside its label.
- A whole card gets the pill from `SettingsSection`.
- A control inside an editor or in every row of a list is `nested: true`, and the mark goes on that list.
- The release commit runs `npm run settings:stamp-since -- <version>` in `frontend/`; CI fails a PR into `main` while any `'next'` is left.
