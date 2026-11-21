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
- **Adding a setting?** Wire `since: 'next'` and New-tab markers per [docs/agents/workflow.md#adding-a-new-setting](docs/agents/workflow.md#adding-a-new-setting).

## Branches, worktrees, and PRs

- **Small work** (bug fix, tweak, copy/style change, or single-session feature touching a handful of files) commits straight to `dev`.
- **Medium or large work** (new page/subsystem, migration, backend+frontend change, multi-session, or droppable work) gets a `feature/<name>` branch, its own worktree, and a PR into `dev`. Ask if unclear.
- **Never open a PR or merge without the owner's go-ahead** (unless the initial request explicitly said to).
- For worktree setup, previewing frontend changes (alt ports / `MAKI_SHARED_DUMP_DIR`), CI checks, PR titles/templates, multi-model agent review, and merge cleanup, read [docs/agents/workflow.md](docs/agents/workflow.md).

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
