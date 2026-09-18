# Putting Maki's translations on Weblate

Weblate is where people who actually speak these languages fix what the machine got wrong. Maki is
GPL-3.0 and public, which qualifies for [Hosted Weblate](https://hosted.weblate.org/)'s libre plan at
no cost. Self-hosting works the same way; only the URL changes.

Nothing below is required for the app to ship in fourteen languages. It already does. This is about
making corrections easy for someone who is not going to open a pull request.

## Why it composes with machine translation instead of fighting it

Gettext has one flag that carries the whole protocol:

| in the PO file | in Weblate | what it means here |
|---|---|---|
| `#, fuzzy` | "Needs editing" | a machine wrote this, nobody has checked it |
| no flag, has text | translated | a person confirmed it, or Weblate pushed it |
| empty | untranslated | falls back to English at runtime |

`scripts/i18n/validate.mjs` enforces the one write rule against `git HEAD`: **only an entry that is
empty or fuzzy may be written.** So a machine run and a Weblate contributor cannot clobber each
other, and neither needs to know the other exists. The only operational rule is the obvious one:

> **Pull Weblate's commits before starting a translation run.**

## Project setup

One project, **two components**, because the two catalogues are keyed differently and a translator
needs to know which they are looking at.

### Component 1: Interface

| field | value |
|---|---|
| File format | `gettext PO file` |
| Filemask | `locales/*/client.po` |
| Monolingual base file | *(leave empty)* |
| Template for new translations | `locales/en/client.po` |
| Adding new translation | `None` |

Its `msgid` **is** the English source text, because `lingui extract` generates the file from the
source. Weblate shows the English as the source string, which is what you want.

### Component 2: Server messages

Same, with `locales/*/server.po`, linked to the Interface component's repository so there is one
clone.

Its `msgid` is a dotted key (`error.series.notFound`), so Weblate shows the key as the source string
and the English sits in the `#.` comment beside it. That is unavoidable with keyed PO and it is why
the sync script writes the English into that comment. Set **Key filter** off and leave
**Manage strings** off: keys are generated from C# and must not be edited here.

### Both components

- **Translation flags**: `icu-message-format`. This is the one that matters. It turns on Weblate's
  ICU checks, so a contributor who drops a `{count}` or leaves Polish without its `few` form is told
  immediately rather than at review time.
- **Adding new translation**: `None`. The language list is fixed at fourteen by
  `SupportedLanguages.All` and `frontend/src/i18n.ts`; a language added in Weblate would produce a
  catalogue the app never loads and would fail `LocalizationCatalogTests`.
- **Push on commit**: on, with a push branch of `weblate` rather than `dev`, so corrections arrive as
  a pull request you can look at.
- **Repository browser**: `https://github.com/OrbitMPGH/Maki/blob/{{branch}}/{{filename}}#L{{line}}`,
  which turns the `#:` source references into links. Those references are the most useful thing in
  the file: the same English word is a navigation tab in `nav.ts` and a failure sentence in
  `DownloadQueueService.cs`, and they do not translate the same way.

## Before you invite anyone

Point contributors at `scripts/i18n/glossary.md`. It is the do-not-translate list (product names,
file formats, the sites Maki downloads from, the feature names Rewind / Smart Download / Discover /
Home) and the tone. Weblate has a glossary feature; the terms in that file are worth entering into it
so they show up inline while somebody translates.

Swedish has been reviewed by the repo owner and is the reference for tone. The other twelve are
machine output, every entry fuzzy, waiting for exactly this.

## What CI already guarantees

A Weblate pull request cannot break the build in the ways that matter, because `npm run i18n:validate`
runs on every PR and rejects a dropped placeholder, a plural missing a category the language
requires, unbalanced ICU, an em dash, or an overwritten reviewed translation. `LocalizationCatalogTests`
separately rejects a key set that has drifted from English.

So a translator can be given commit access to the translation branch without anyone reading thirteen
languages to check their work.
