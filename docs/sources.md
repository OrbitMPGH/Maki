# Sources

Sources are built into Maki, so there are no extensions to install.

If you already run [Suwayomi](https://github.com/Suwayomi/Suwayomi-Server), or want more sites than
the list below, Maki can also use the sources of the extensions installed there. This is optional:
set Suwayomi's URL under **Settings → Integrations → Suwayomi**, and its sources show up next to
the built-in ones in **Manage sources**, marked Suwayomi and switched off until you turn them on.
A source that mixes languages asks which one to file its chapters under first. Extensions
are installed and configured in Suwayomi itself. Maki reads search results, chapter lists and
pages through Suwayomi's API, so a source that works in Suwayomi works here. Without a Suwayomi URL
set, nothing changes. Suwayomi v2.1 or later is supported, v2.2 or later for its UI login mode.

| Source | Notes |
|---|---|
| MangaDex | Official API, language filter |
| MANGA Plus | Official (Shueisha), language-specific mappings |
| WEBTOON | Official (webtoons.com), ORIGINALS and CANVAS in en, id, th, es, fr, zh-Hant, de |
| Naver Webtoon | Official (comic.naver.com), Korean, WEBTOON tier only |
| GigaViewer sites | Official Japanese: Shonen Jump+, Comic Days, Sunday Webry, MAGCOMI, Tonari no Young Jump, Comic Zenon, Kurage Bunch (free episodes only) |
| KadoComi (Comic Walker) | Official, Japanese |
| MangaPill | |
| Weeb Central | Requires FlareSolverr |
| MangaFire | Language filter, requires [FlareSolverr](https://github.com/FlareSolverr/FlareSolverr) |
| TCB Scans | Domain override via `MAKI_SOURCE_TCBSCANS_BASEURL` |
| Asura Scans | Manhwa/manhua |
| Flame Comics | Manhwa/manhua, requires FlareSolverr |
| TopManhua | Manhwa/manhua, requires FlareSolverr |
| MangaKatana | |
| MangaKakalot | Requires FlareSolverr |
| Atsumaru | atsu.moe |
| Dynasty Scans | English, yuri |
| Toonily | Manhwa, adult, requires FlareSolverr |
| Manhwa18.net | Adult, requires FlareSolverr |
| Baozi Manhua | Simplified Chinese manhua |
| Manhuagui | Simplified Chinese, mirror override via `MAKI_SOURCE_MANHUAGUI_BASEURL` |
| Manga Livre | Brazilian Portuguese |
| Taiyō | Brazilian Portuguese |
| ManhwaWeb | Spanish |
| Olympus Scanlation | Spanish, requires FlareSolverr |
| Anime-Sama | French, requires FlareSolverr |
| Manga-Tube | German |
| MangaWorld | Italian |
| MangaLib | Russian |
| MangaDenizi | Turkish |
| Shinigami | Indonesian, manhwa/manhua/manga |
| Cứu Truyện | Vietnamese, FlareSolverr when challenged, mirror override via `MAKI_SOURCE_CUUTRUYEN_BASEURL` |
| Team-X | Arabic, requires FlareSolverr; the domain rotates, override it with `MAKI_SOURCE_TEAMX_BASEURL` |
| Sen Manga | Japanese raws |
| Rawkuma | Japanese raws, FlareSolverr when challenged, mirror override via `MAKI_SOURCE_RAWKUMA_BASEURL` |
