# Copilot instructions for Maki

Maki is a self-hosted manga collection manager (Sonarr/Radarr-style) — .NET 10 API +
Vite/React/Mantine frontend. **Read [CLAUDE.md](../CLAUDE.md) before making changes** — it documents
directory ownership and the non-obvious domain gotchas below in full; this file is a short pointer,
not a replacement.

## Layout

- `Maki.Core` — domain, no infra deps.
- `Maki.Data` — EF Core + SQLite, migrations.
- `Maki.Sources` — one `ISource` per manga site.
- `Maki.Metadata` — MangaBaka provider + local dump + embeddings.
- `Maki.Api` — host, controllers, Quartz jobs, download workers.
- `frontend` — Vite/React/Mantine SPA.

## Before proposing a change, know these

- `Chapter.Number` is `REAL` in SQLite — never assume `ORDER BY` sorts it correctly.
- MangaBaka has no MangaDex IDs; source matching is by normalized-title search, not shared UUID.
- Series monitoring is only `Series.MonitorNewItems` — there is no separate `Monitored` flag.
- Settings secrets (qBittorrent, Kavita, Prowlarr, MAL, tracker tokens) are intentionally plaintext
  in the `AppConfig` table — don't "fix" this without reading why in CLAUDE.md.
- `ImageSharp` is pinned to 3.1.12 — do not bump to 4.x (paid license required).
- EF Core migrations auto-apply on startup; never suggest a manual migration step for users.
- Page requests carry their own `Headers` end-to-end and must be resolved at **download** time, not
  enqueue time (some source URLs are short-lived).

## Conventions

- No comments explaining *what* code does — only *why*, and only when non-obvious.
- Don't add abstractions, config flags, or error handling for cases that can't happen.

## Commit messages

[Conventional Commits](https://www.conventionalcommits.org/): `<type>(<scope>): <summary>`.

- Type: `feat` (new capability), `fix` (bug fix), `refactor` (no behavior change), `docs`, `test`,
  `build` (Dockerfile/deps/build scripts), `ci` (`.github/workflows/**`), `chore` (everything else).
- Scope (optional): lowercase directory that owns the change — `core`, `data`, `sources`,
  `metadata`, `api`, `frontend`. Omit for changes spanning several.
- Summary: imperative mood ("add", not "added"), no capital after the colon, no trailing period,
  under ~50 characters.
- Body: only when the *why* isn't obvious from the diff — a constraint, a bug that motivated it, a
  tradeoff. Never restate what the diff already shows.

Examples: `feat(sources): add Asura manhwa source`,
`fix(api): derive series monitoring from the mode, not a stale flag`,
`ci: drive latest tag from an explicit stability flag, not github.ref`.
