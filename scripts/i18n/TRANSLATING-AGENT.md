# Translating Maki: instructions for an AI agent

You are translating the interface of **Maki**, a self-hosted manga collection manager. It monitors
sites for new chapters, downloads them as CBZ files, and has a built-in reader.

This document is self-contained. You do not need any prior conversation.

## Your job

Fill in the message catalogues under `locales/` for the thirteen non-English languages.

**Only touch files under `locales/`, and never under `locales/en/`.** English is the source language:
it is generated from the source code and hand-maintained alongside it. Do not edit
`frontend/src`, `src/`, or anything else in the repo.

## The one rule that matters

> **Only ever write an entry whose translation is empty, or which is marked `#, fuzzy`.**
> **Never modify a translation that has text and no fuzzy flag.**

A translation with no fuzzy flag is one a human reviewed, or one the translation platform pushed.
Overwriting it destroys somebody's work. This is checked automatically and a run that breaks it
fails.

**Keep `#, fuzzy` on every entry you write.** It means "needs review", which is exactly what a
machine translation is. Clearing it is a claim that a human checked it, and you are not one.

## The loop

Work one language at a time, in chunks, committing as you go.

```bash
# 1. See what is pending. Start with a couple of hundred, not everything.
node scripts/i18n/todo.mjs --locale sv --limit 200

# 2. Edit locales/sv/client.po and locales/sv/server.po, filling the msgstr of each listed key.
#    Add "#, fuzzy" above each entry you fill, if it is not already there.

# 3. Check your work. This must pass before you commit.
node scripts/i18n/validate.mjs --locale sv

# 4. Commit, then go back to step 1 until todo.mjs reports 0 untranslated.
```

`--limit` exists so each commit is small enough for a person to review. Do not translate 2,400
entries in one commit.

### "Pending" is two different things, and only one of them can reach zero

`todo.mjs` lists an entry when it is empty **or** fuzzy, and prints both numbers:

```
2259 entries pending in pl (0 untranslated, 2259 awaiting review).
```

**You are finished when `untranslated` is 0.** The other number is the pile waiting for a human, and
it stays high on purpose: you are told to keep `#, fuzzy` on everything you write, and keeping the
flag is exactly what keeps the entry listed. A fully translated language still reports every entry
as pending, and that is correct.

So do not chase the total to zero. The only way to get there is to clear fuzzy flags, which is a
claim that a person reviewed the text, which is false and is the one thing this spec asks you not to
do. If you find yourself reasoning toward clearing them, stop: the instruction is wrong somewhere,
not the flag.

### Do Swedish first, then stop

Translate `sv` completely, then **stop and report**. The repo owner reads Swedish and will check it
before the other twelve are worth doing. If the glossary or the tone is wrong, it is much cheaper to
find out on one language than on thirteen.

After Swedish is approved, the remaining order does not matter:
`de fr es pt-BR it nl pl ru tr ja zh-Hans ko`

## The two catalogues

Each language has two files, and they are keyed differently. This trips people up.

**`client.po`** is the app's own interface: buttons, labels, page copy. Its `msgid` **is the English
text**, because the extractor generates these from the source.

```po
#: src/pages/SettingsPage.tsx
msgid "Start page"
msgstr "Startsida"
```

**`server.po`** is what the API sends back: error messages, notifications. Its `msgid` is a **dotted
key**, not English, because C# code has to name it. The English is in the `#. English:` comment, and
`todo.mjs` shows it to you.

```po
#. English: Unknown start page: {page}
#: src/Maki.Api/Controllers/SettingsController.cs
msgid "error.settings.unknownStartPage"
msgstr "Okänd startsida: {page}"
```

In `server.po`, **never translate the msgid.** It is an identifier.

## Rules for the text itself

Read `scripts/i18n/glossary.md`. It lists every term that must not be translated (product names,
file formats, the names of the sites Maki downloads from), the loanwords, and the tone. The short
version:

- **Never translate**: Maki, CBZ, ComicInfo.xml, OPDS, MangaBaka, Kavita, Prowlarr, qBittorrent,
  FlareSolverr, Kitsu, AniList, MyAnimeList, MangaUpdates, Discord, Suwayomi, the feature names
  Rewind / Smart Download / Discover / Home, and every source name (MangaDex, MangaPill, Weeb
  Central, MangaFire, MangaPlus, TCB Scans, Asura, WEBTOON, Flame Comics, TopManhua, Atsumaru).
- **No em dashes.** The validator rejects them.
- **Plain, direct sentences.** This copy reads like a developer explaining something to a colleague.
- **Placeholders `{name}` must survive exactly** — same set, same spelling. Word order around them
  is free and should follow the target language.
- **Plurals must cover every category the language needs**, not the two English has. This is the
  most common mistake and is invisible without the validator. See below.

## Plurals

An English plural has two forms. Most languages do not.

```po
# English
msgid "{count, plural, one {# chapter} other {# chapters}}"

# Polish needs four
msgstr "{count, plural, one {# rozdział} few {# rozdziały} many {# rozdziałów} other {# rozdziału}}"

# Japanese needs one
msgstr "{count, plural, other {#章}}"
```

`#` inside a plural block prints the number. Keep it.

If you are unsure which categories a language needs, the validator will tell you exactly which ones
are missing. Run it and read the message.

## What the validator checks

`node scripts/i18n/validate.mjs --locale sv` fails on:

- a placeholder you dropped or invented
- a plural missing a category the language requires, or using one it does not have
- a translation that does not parse as ICU (usually an unbalanced brace)
- an em dash
- a markdown code fence, which means model output leaked into the file
- leading or trailing whitespace the English does not have
- a key missing from the catalogue, or one that is not in English
- **a reviewed (non-fuzzy) translation that was changed or deleted**

It names the language, the key, and the problem. Fix and re-run until it passes. You do not need a
human for any of this.

## When you are not sure

**Leave the entry empty and list it in your commit message.** An empty entry falls back to English:
visibly incomplete, obviously fixable. A confident wrong guess looks finished, and nobody here reads
eleven of these languages well enough to catch it.

Good reasons to leave one empty: the English is ambiguous without seeing the screen; it is a term of
art you cannot place; the placeholder makes the grammar impossible without knowing what fills it.

## Reporting back

When you stop, say:

- which languages you finished, and how many entries each
- every entry you left empty, with the key and why
- anything in the English source that looked wrong, ambiguous, or untranslatable. That is useful
  feedback and the English can be changed.

## Things that are not your job

- Editing any source code
- Editing `locales/en/`
- Clearing fuzzy flags
- Adding or removing keys (they are generated; if one looks wrong, report it)
- Adding new languages
