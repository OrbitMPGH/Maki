# Maki translation glossary

Terms and tone for anyone, human or model, translating Maki's interface.

## Never translate

These are product names, file formats, or protocol names. Translating one makes the string wrong,
not localized. Keep the exact spelling and capitalisation.

**The app and its own features**
Maki, Rewind, Smart Download, Main, Smart

Main and Smart are the two download modes. They are short enough to look like ordinary adjectives,
and four languages translated the dropdown value while leaving the sentence that explains it quoting
the English, which leaves the user unable to tell which option the explanation is about. Keep both in
English in both places.

Rewind is the year playback the Stats page launches, and it is a name in the way an album title is.
It has no navigation tab to find it by, so translating it buys nothing and costs the connection to
what the button says.

Home and Discover **used to be on this list and are not any more.** They name two screens with two
ordinary words, and the next section says which word each language uses.

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

## Page names that are ordinary words, and do translate

The never-translate list is short on purpose. It holds names Maki uses as proper nouns for one
particular screen. Everything else that names a page is an ordinary descriptive word, and translates
like any other:

Health, Settings, Stats, Library, Reader, Downloads, Home, Discover

Home and Discover are the two that moved. They were kept in English on the theory that a reader has
to find the tab in the navigation, but the navigation is the very thing that was not translated, so
the argument was circular: a Swedish reader was hunting for an English word because we had decided
they would be hunting for an English word. These are the words each language uses, and every sentence
that mentions either page uses the same one:

| | Home | Discover |
|---|---|---|
| de | Start | Entdecken |
| fr | Accueil | Découvrir |
| es | Inicio | Descubrir |
| pt-BR | Início | Descobrir |
| pl | Start | Odkrywanie |
| ru | Главная | Обзор |
| tr | Ana Sayfa | Keşfet |
| ja | ホーム | 見つける |
| zh-Hans | 首页 | 发现 |
| ko | 홈 | 탐색 |

Two shapes to keep when you touch one of these. Polish and Russian decline the word, so a sentence
takes Odkrywania or Обзоре rather than the nominative that sits in the tab, and the stem is what has
to match, not the ending. Japanese quotes a UI name inside a sentence, 「見つける」, the way this
catalogue already writes 「Add Series」, but the tab itself is the bare word.

Where the new word for Home sat next to that language's phrase for "start page", the second half was
reworded rather than left to collide. German now ends on Einstiegsseite, Dutch on openingspagina,
Polish on strony początkowej.

Health used to be the one that got asked about, because it names a page the way Home does. Now they
are the same case and both translate.

**The rule is consistency, not English.** Whatever word you put in the navigation, use that same
word everywhere the text mentions that page. A sentence that explains a screen by a name the screen
does not have is worse than either choice made consistently, and it is the single most common defect
found in this catalogue so far. It has turned up on Main, on Smart, on Discover, on Home and on
Stats.

## Terms that need care

**streak** is the run of consecutive days you have read something. The literal translation collides
with the word for a manga series in several languages, and some strings use both in one sentence, so
pick a word that cannot be confused with a series. Italian uses "sequenza" rather than "serie" for
exactly this reason.

**seed** appears in two unrelated senses. In the recommendation engine it is the title a set of
suggestions was built from. In the torrent settings it is the BitTorrent sense. Do not use one word
for both. If the language borrows the English for the torrent sense, translate the recommendation
sense to something else.

**Suggestive** is a content rating tier, between Safe and Erotica. It means mildly sexual, and it is
a false friend: the cognate in several Romance and Slavic languages means "evocative" or "striking"
instead, which is not what the rating says. Use the word your language actually uses for mildly
sexual content.

**Watched** means the reader has seen the anime adaptation and is marking the series off, not that
they are monitoring it for new chapters and not that they watched a video. The video sense is the
correct one to reach for, and the three CJK catalogues all get this right.

## Genre and demographic tags

Keep the demographic and style tags as loanwords, the way readers of manga in that language use
them:

Isekai, Josei, Seinen, Shoujo, Shounen, Mecha, Ecchi, Harem, Boys Love, Girls Love, OEL

Translate the plain descriptive genres, which are ordinary words: Action, Comedy, Drama, Horror,
Mystery, Romance, Sci-Fi, Slice of Life, Sports, Thriller.

"Translate" here means the word manga readers in that language use **for the genre**, which is
sometimes the English one. Several of these collide with an unrelated everyday sense, and the
catalogue entry is the bare word with nothing around it to disambiguate. Three languages translated
Action as the sense a button has, and the genre filter offered German readers "Aktion", Swedish
readers "Åtgärd" and Korean readers "작업", none of which is a kind of story. The genre is "Action"
in German and Swedish and 액션 in Korean. Check the same trap on Drama, Mystery, Historical and
Sports before writing one.

## Self-hosting and protocol jargon

Not in the never-translate list, because some languages do have settled terms, but most do not.
Follow what people running this kind of software in that language actually write, which is usually
the English:

indexer, seed (torrent sense), client secret, scope, personal access token, claim, webhook, token

Whichever way you go, go the same way every time the term appears.

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
