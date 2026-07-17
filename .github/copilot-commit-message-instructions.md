# Commit message instructions

Use [Conventional Commits](https://www.conventionalcommits.org/): `<type>(<scope>): <summary>`.

## Type

One of:

- `feat` — new user-facing capability
- `fix` — bug fix
- `refactor` — code change that doesn't change behavior
- `docs` — README/CLAUDE.md/comments only
- `test` — test-only change
- `build` — Dockerfile, csproj/package.json deps, build scripts
- `ci` — `.github/workflows/**`
- `chore` — everything else (formatting, gitignore, tooling config)

## Scope (optional)

Lowercase, matches the directory that owns the change: `core`, `data`, `sources`, `metadata`,
`api`, `frontend`. Omit it for changes that cross several of these.

## Summary

- Imperative mood ("add", not "added"/"adds").
- No capital letter after the colon, no trailing period.
- Under ~50 characters; if it can't fit, the change is probably two commits.

## Body

Only add one if the *why* isn't obvious from the diff — a constraint, a bug that motivated it, a
tradeoff. Never restate what the diff already shows line by line.

## Examples

```
feat(sources): add Asura manhwa source
fix(api): derive series monitoring from the mode, not a stale flag
ci: drive latest tag from an explicit stability flag, not github.ref
build: add local single-arch nightly build+push script
```
