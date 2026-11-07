# Unreleased changes

One file per user-visible change since the last release. The `release-notes` skill turns this
folder into the GitHub release notes, and the release commit empties it again (this README stays).

## When to add, edit or delete an entry

- **A change someone on the last release would notice:** add a file. New feature, behaviour change,
  fix for a bug that shipped, security hardening, anything they have to do before upgrading.
- **A fix or tweak to something that hasn't shipped yet:** add nothing. If it changes what the
  unreleased feature does or looks like, edit that feature's entry so it describes the final form.
- **Reworking something unreleased:** rewrite its entry. Removing it: delete the entry.
- **Refactors, tests, CI, docs, catalogue churn, perf work nobody can feel:** add nothing.

The folder should always read as "what changes for someone upgrading from the last tag to HEAD".

## Format

`changes/<short-kebab-slug>.md`, for example `changes/delisted-chapters.md`:

```markdown
---
type: fix
section: Downloads
issue: 163
---
Fixed Smart Download picking a source before it had measured it.
```

- `type` (required): `feature`, `improvement`, `fix`, `security` or `breaking`. `breaking` goes
  under "Before you upgrade": anything an upgrader has to know or do (changed exit codes, renamed
  permissions, settings that move).
- `section` (optional): the themed heading it belongs under, such as `Reader`, `Library`,
  `Discover`, `Downloads`, `Sources`, `Settings`. Reuse a name another entry already uses.
- `issue` (optional): GitHub issue or PR number, rendered as `(#163)`.
- `by` (optional): GitHub login of an outside contributor, for the thanks section.

The body is what goes in the notes: plain sentences describing what the user sees, no class names,
endpoints or migrations. A fix starts with "Fixed". A big feature can be a short paragraph or a few
bullets. Follow the text style in the root `CLAUDE.md` (no em dashes, no filler).

## Hotfixes

A fix that also goes out in a patch release keeps the same filename on both branches. After the
patch release, delete those same files on `dev`, or they get listed again in the next minor.
