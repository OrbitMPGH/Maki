# CLAUDE.md

See [README.md](README.md) for overview.

Subsystem gotchas live in `.claude/rules/*.md` and load automatically when you touch matching files: `auth.md`, `series-matching.md`, `reader-progress.md`, `recommendations.md`, `opds.md`, `downloads.md`, `stats-notifications.md`, `infra.md`. `distribution/CLAUDE.md` and `src/Maki.Sources/CLAUDE.md` are separate nested files, same deal.

**Adding a file to one of those subsystems? Check its rule file's `paths` frontmatter.** The globs are prefix/suffix wildcards (e.g. `Services/*Download*.cs`), not a static list — most naturally-named new files match automatically. If a new file doesn't fit any existing pattern (unusual name, new area within the subsystem), add its path to that rule file's frontmatter so future sessions actually load the gotchas instead of silently missing them.

## User-facing text style

No em dashes, anywhere. Avoid "AI writing" tells: no "it's not just X, it's Y", no rule-of-three lists, no "leverage"/"seamless"/"robust"/"delve" filler, no over-hedged "note that"/"it's worth mentioning" throat-clearing. Write plain, direct sentences like a developer explaining something to a teammate.

## Directory ownership

- `Maki.Core`: domain, no infra deps. Entities, `ISource`/`IMetadataProvider`, parsing, CBZ pipeline. Also `Security/`, `MakiPermission` flags enum, `ICurrentUser`, so domain code can check permissions without an ASP.NET Identity reference.
- `Maki.Data`: EF Core + SQLite, migrations. **Exception to "entities live in Core":** `Identity/` holds `MakiUser`, `UserApiKey`, `UserRootFolder`, `AuthEvent` — `MakiUser : IdentityUser<int>` would drag `Microsoft.Extensions.Identity.Stores` into Core.
- `Maki.Sources`: one `ISource` impl per site.
- `Maki.Metadata`: MangaBaka provider + local dump + embeddings.
- `Maki.Api`: host, controllers, Quartz jobs, DI wiring, download workers.
- `frontend`: Vite/React/Mantine SPA.

## Build/run gotchas

- API: `:8990`. Frontend dev server: `:5173`, proxies `/api` + `/signalr` to `:8990`.
- EF migrations auto-apply on startup. To add one: `dotnet ef migrations add <Name> --project src/Maki.Data --startup-project src/Maki.Api`.
- State (SQLite, logs, covers, page cache, `config.json`, data-protection keys) resolves via `MAKI_CONFIG_DIR`, falling back to `/config` in Docker or `%APPDATA%\Maki` on Windows. In dev, set `MAKI_CONFIG_DIR=$PWD/.devconfig` to avoid touching the real library/DB. `.claude/launch.json`'s `backend` preset uses the **real** APPDATA config — use `backend-dev` for anything destructive.
- `src/Maki.Api/wwwroot/` must exist or startup throws `DirectoryNotFoundException` before any app code runs. Produced by the frontend build; a fresh clone that only ran the backend needs it created by hand.
- Frontend type-checking is `npx tsc -b`, **not** `npx tsc --noEmit`: root `tsconfig.json` is `"files": []` plus project references, so `--noEmit` checks nothing and exits 0 on a broken tree.

## Non-obvious domain facts

- **`/` is a redirect (`StartPageRedirect`), not a page.** `/discover` and `/home` both bounce back to `/` when unavailable — an unconditional `/` → either would infinite-loop.
