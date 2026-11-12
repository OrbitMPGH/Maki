<!--
Title: Conventional Commits, `<type>(<scope>): <summary>`, e.g. `feat(activity): group the history by series`.
See CONTRIBUTING.md "Pull request titles". Release PRs are titled `vX.Y.Z`.

Reviewers of this PR are agents. They read this body, then the diff. Write for a reader who has
the repo but none of your context: name files, types and settings exactly, state what you checked
and what you didn't. Answer every section; write "None" rather than deleting one, so a reviewer
knows it was considered. A one-line fix can keep each section to a line. Delete these comments.
-->

## Intent

<!-- One or two sentences: what this PR does and why. A reviewer judges every hunk against this. -->

Closes #

## Why

<!-- The problem or request behind it. For a fix: the symptom, the root cause, and why the fix
addresses the cause rather than the symptom. Link discussion or mockups that settled a choice. -->

## What changes for users

<!-- Behaviour a user of the last release would notice, as plain bullets. "None" for refactors,
tests, CI and docs. Should agree with the changes/ entry. -->

## How it works

<!-- The approach, in the order a reviewer should read it. Name the entry point and the key types
or files. Explain decisions the diff can't: rules, thresholds, ordering, why this layer.
Mention alternatives you rejected and why, so a reviewer doesn't propose them again. -->

**Start reading at:** `path/to/File.cs`

## Rules that apply

<!-- The `.claude/rules/*.md` and nested CLAUDE.md files covering what this touches. A reviewer
loads them before reading the diff. Say which invariant from them this change leans on. -->

-

## Touches

<!-- Answer each. "No" is a useful answer. Expand any "Yes" in a sentence. -->

- **Migration:** No <!-- name it; migrations auto-apply on startup and can't be rolled back by users -->
- **Files on disk** (library, downloads, covers, config dir): No <!-- writes, moves or deletes? -->
- **Settings or config:** No <!-- new keys, renamed keys, changed defaults -->
- **API contract** (REST, SignalR, OPDS, Komga, KOReader, Mihon): No <!-- external clients break silently -->
- **Auth or permissions:** No
- **Background jobs** (Quartz, download workers, polling): No
- **Performance** on a NAS-sized library: No <!-- new queries per series, per chapter or per poll -->
- **User-facing strings:** No <!-- extracted with `lingui extract --clean`, English only; Weblate fills the rest -->

## Out of scope

<!-- What this deliberately doesn't do, and known limits, so a reviewer doesn't report them as
missing. Follow-ups you plan go here too. -->

## Verification

<!-- What you ran, with results, and what you checked by hand and how. Be exact: test names,
counts, the library or config used. Then list what was NOT checked. A reviewer treats an
unchecked path as the first place to look. -->

- [ ] `dotnet test -c Release`:
- [ ] `npx tsc -b` (from `frontend/`):
- [ ] Storybook stories for touched components:
- [ ] Manual check:
- **Not checked:**

## Review focus

<!-- Where you want scrutiny: the part you're least sure of, a concurrency or ordering risk, an
edge case you reasoned about but didn't test. "None" if nothing stands out. -->

## Checklist

- [ ] Title follows Conventional Commits and targets `dev`
- [ ] `changes/` entry added or edited, or none needed (see `changes/README.md`)
- [ ] New user-facing text goes through `t`/`<Trans>` or `ILocalizer`, no raw English
- [ ] Tests added or updated for changed behaviour
- [ ] No em dashes in code, strings, docs or this description
