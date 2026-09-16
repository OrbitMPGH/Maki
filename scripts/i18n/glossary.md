# Maki translation glossary

Terms and tone for anyone, human or model, translating Maki's interface.

## Never translate

These are product names, file formats, or protocol names. Translating one makes the string wrong,
not localized. Keep the exact spelling and capitalisation.

**The app and its own features**
Maki, Rewind, Smart Download, Discover, Home

Feature names are capitalised in English because they name a specific screen or behaviour, not a
general idea. A sentence about "the Discover page" is about *that page*. If the target language would
normally translate such a name, still do not: the user has to find it in the navigation, which is not
translated either.

**Formats and protocols**
CBZ, CBR, ComicInfo.xml, OPDS, EPUB, PDF, ZIP, RAR

**Services Maki connects to**
MangaBaka, Kavita, Prowlarr, qBittorrent, FlareSolverr, Kitsu, AniList, MyAnimeList, MangaUpdates,
Discord, Suwayomi, Tachiyomi, SignalR, Quartz

**Sources (site names)**
MangaDex, MangaPill, Weeb Central, MangaFire, MangaPlus, TCB Scans, Asura, WEBTOON, Flame Comics,
TopManhua, Atsumaru

## Loanwords unless the language has a settled form

Use the English word unless the target language genuinely has its own established term that readers
of this kind of software would expect. Do not invent one.

manga, manhwa, manhua, OEL, one-shot, scanlation, tag, chapter, volume

"Chapter" and "volume" **do** translate in most languages. They are listed here only because a few
languages borrow the English in this domain; follow whatever manga readers in that language actually
say.

## Tone

The same rules the English copy follows, in the target language:

- **No em dashes.** Use a comma, a colon, a full stop, or whatever the language's normal punctuation
  is. The validator rejects an em dash in any translation.
- **Plain, direct sentences.** Maki's copy reads like a developer explaining something to a
  colleague. It is not marketing and it is not formal documentation.
- **No throat-clearing.** English avoids "please note that", "it is worth mentioning", "simply".
  Avoid the equivalent padding.
- **Match the register the language uses for software.** Where a language distinguishes formal and
  informal address, use whatever comparable self-hosted software uses. For most European languages
  that is the informal form; for Japanese and Korean it is polite-neutral, not honorific.

## Mechanics

- **Placeholders are `{name}` and must survive exactly.** Same set, same spelling, no additions, no
  removals. Word order around them is free: putting `{service}` at the end of a German sentence is
  correct and expected.
- **Plurals must cover every category the language uses**, not the two English has. Polish and
  Russian need `one`, `few`, `many`, `other`. Japanese, Korean and Chinese need only `other`. The
  validator checks this against CLDR and it is the single most common mistake.
- **Preserve leading and trailing whitespace** exactly as the English has it.
- **Preserve the sentence case of the first character** for UI labels. A button reading "Add series"
  should not become "add series".
- **`<0>`, `<1>` and similar are markup anchors**, not text. They wrap part of the sentence in a
  link or bold. Keep them, keep them paired, and move them so they wrap the equivalent words in the
  target language.

## When you are not sure

Leave the entry empty and say so. An empty entry falls back to English, which is visibly incomplete
and obviously fixable. A confident wrong guess looks finished and nobody catches it, because nobody
here reads eleven of these languages.
