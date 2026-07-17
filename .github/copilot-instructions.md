# Copilot instructions for Mangarr

Mangarr is a self-hosted manga collection manager (Sonarr/Radarr-style) — .NET 10 API +
Vite/React/Mantine frontend. **Read [CLAUDE.md](../CLAUDE.md) before making changes** — it documents
directory ownership and the non-obvious domain gotchas below in full; this file is a short pointer,
not a replacement.

## Layout

- `Mangarr.Core` — domain, no infra deps.
- `Mangarr.Data` — EF Core + SQLite, migrations.
- `Mangarr.Sources` — one `ISource` per manga site.
- `Mangarr.Metadata` — MangaBaka provider + local dump + embeddings.
- `Mangarr.Api` — host, controllers, Quartz jobs, download workers.
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

- Commits follow [Conventional Commits](https://www.conventionalcommits.org/) — see
  [copilot-commit-message-instructions.md](copilot-commit-message-instructions.md) for the exact
  types/scopes used here.
- No comments explaining *what* code does — only *why*, and only when non-obvious.
- Don't add abstractions, config flags, or error handling for cases that can't happen.
