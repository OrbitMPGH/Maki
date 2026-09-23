# Putting Maki's translations on Weblate

Weblate is where people who actually speak these languages fix what the machine got wrong. Maki is
GPL-3.0 and public, which qualifies for [Hosted Weblate](https://hosted.weblate.org/)'s libre plan at
no cost. Self-hosting works the same way; only the URL changes.

The project lives at <https://hosted.weblate.org/projects/maki/>. What follows is how it is set up,
so it can be checked or rebuilt.

Nothing below is required for the app to ship in eleven languages. It already does. This is about
making corrections easy for someone who is not going to open a pull request.

## Why it composes with machine translation instead of fighting it

Gettext has one flag that carries the whole protocol:

| in the PO file | in Weblate | what it means here |
|---|---|---|
| `#, fuzzy` | "Needs editing" | a machine wrote this, nobody has checked it |
| no flag, has text | translated | a person confirmed it, or Weblate pushed it |
| empty | untranslated | falls back to English at runtime |

So Weblate reporting every language at about 1% is expected: the machine output counts as "Needs
editing" until a person approves it.

`scripts/i18n/validate.mjs` enforces the one write rule against `git HEAD`: **only an entry that is
empty or fuzzy may be written.** So a machine run and a Weblate contributor cannot clobber each
other, and neither needs to know the other exists. The only operational rule is the obvious one:

> **Merge Weblate's open pull request before starting a translation run.**

## Project setup

One project, **two components**, because the two catalogues are keyed differently and a translator
needs to know which they are looking at. Both track `dev`.

### `maki-frontend`

| field | value |
|---|---|
| File format | `gettext PO file` |
| Filemask | `locales/*/client.po` |
| Language filter | `^(?!en$).+$` |
| Monolingual base file | *(empty)* |
| Template for new translations | *(empty)* |

Its `msgid` **is** the English source text, because `lingui extract` generates the file from the
source. Weblate shows the English as the source string, which is what you want.

`locales/en/client.po` has to stay in the repo, because Lingui's source locale is `en` and extract
regenerates it anyway. Without the language filter Weblate sees it as a translation into the source
language and raises a "Duplicated translation" alert.

### `maki-backend`

| field | value |
|---|---|
| Repository | `weblate://maki/maki-frontend`, so there is one clone |
| File format | `gettext PO file (monolingual)` |
| Filemask | `locales/*/server.po` |
| Monolingual base file | `locales/en/server.po` |
| Edit base file | off |
| Manage strings | off |

Its `msgid` is a dotted key (`error.series.notFound`) and the English is the msgstr of
`locales/en/server.po`. Monolingual PO is what lets Weblate show that English as the source string
instead of the bare key. The base file stays read-only here because keys are named in C# and
`LocalizationCatalogTests` holds the English to them; edit it in the repo.

### Both components

- **Translation flags**: `icu-message-format`. This is the one that matters. It turns on Weblate's
  ICU checks, so a contributor who drops a `{count}` or leaves Polish without its `few` form is told
  immediately rather than at review time.
- **Adding new translation**: "Disable adding new translations". The language list is fixed at
  eleven by `SupportedLanguages.All` and `frontend/src/i18n.ts`; a language added in Weblate would
  produce a catalogue the app never loads and would fail `LocalizationCatalogTests`.
- **Version control system**: GitHub (via Weblate GitHub app). Weblate pushes to a fork and opens a
  pull request against `dev`, so the push branch field is locked and does not need setting.
- **PO line wrap**: "No line wrapping". Lingui writes long lines unwrapped, and Weblate's default of
  77 columns would rewrap every file on its first commit and have the next extract undo it.
- **Repository browser**: `https://github.com/OrbitMPGH/Maki/blob/{{branch}}/{{filename}}#L{{line}}`,
  which turns the `#:` source references into links. Those references are the most useful thing in
  the file: the same English word is a navigation tab in `nav.ts` and a failure sentence in
  `DownloadQueueService.cs`, and they do not translate the same way.

## Glossary

The project glossary is Weblate-local and not in the repo. It holds what `glossary.md` says never to
translate (product and feature names, formats, services, source sites, demographic tags) as
untranslatable terminology, plus Home and Discover with each language's word from the table there.
The loanword and jargon lists are left out on purpose: whether they translate depends on the
language, which a fixed glossary entry cannot express.

When `glossary.md` changes, change the Weblate glossary to match.

## Before you invite anyone

Point contributors at `scripts/i18n/glossary.md` for the reasoning and the tone; the Weblate glossary
only carries the terms.

All ten non-English catalogues are machine output, every entry fuzzy, waiting for exactly this.

## What CI already guarantees

A Weblate pull request cannot break the build in the ways that matter, because `npm run i18n:validate`
runs on every PR and rejects a dropped placeholder, a plural missing a category the language
requires, unbalanced ICU, an em dash, or an overwritten reviewed translation. `LocalizationCatalogTests`
separately rejects a key set that has drifted from English.

So a translator can be given commit access in Weblate without anyone reading thirteen languages to
check their work.
