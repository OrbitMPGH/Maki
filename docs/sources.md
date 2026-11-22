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

A site that isn't built in can be added as a **custom source**: a JSON definition that tells Maki
where the site's search, chapter list and page images are (CSS selectors, regular expressions or a
path into JSON the page embeds). Admins add one in **Manage sources** with **Add source → Set up a
site**: paste a series page and a chapter page, and Maki guesses the chapter list, the page images and
the search, shows what each guess reads off the live site, and tests the result before it can be
saved. Sites on the Madara, MangaThemesia, Keyoapp and HeanCMS themes are recognised and need no guessing. **Import definition**
takes a definition someone else wrote. A saved source shows up marked Custom and works like any other
source, and Health checks it every six hours. An admin can also start from a series page: paste a
URL no source knows into **Link a source** and pick **Set up this site as a source**. **Add source → Repositories** adds a list of definitions someone publishes: install from it, review and apply its updates, or let an https repository keep its sources up to date on its own. An update that could change which chapters a linked series has always waits for you. Nothing in a definition runs as code, so sites that scramble their pages or need
signed API calls still need a built-in source. A site with no search can still be added: link each
series to it by pasting the series URL. The format is described in
[custom-sources-plan.md](custom-sources-plan.md).

## Sending a source through a proxy

Some sites block VPN and datacenter addresses. An admin can give those sources a proxy under
**Settings → Downloads → Source proxy**: one plain HTTP proxy (`http://host:port`, no username or
password yet), plus the sources that use it. Everything Maki does for those sources goes through it:
the headless browser MangaFire and TopManhua need, FlareSolverr's challenge solves, chapter lists,
page images and covers. Every other source keeps connecting the way it does now. Suwayomi sources
are not listed, since Suwayomi does their fetching; set a proxy in Suwayomi for those.

- **Maki never falls back to a direct connection.** When the proxy is down, the source fails with
  "The source's proxy could not be reached or refused the request" until it is back.
- **FlareSolverr connects to the proxy itself**, from its own network. Use an address both can reach,
  usually the proxy's LAN IP: a Docker name such as `http://gluetun:8888` may resolve for Maki and not
  for FlareSolverr.
- **The proxy needs one fixed outgoing IP.** Cloudflare ties its clearance cookie to the IP that
  earned it, so a proxy that rotates its address between requests breaks the browser sources.
- **Make the proxy refuse private addresses.** A site picks the image addresses Maki fetches, and the
  proxy looks those names up itself, so a proxy that will connect to its own network can be pointed
  at it. tinyproxy and Squid both have rules for this. **Test** warns when the proxy accepts them.

**Test** fetches the first chosen source's site through the proxy and, for a source that needs
FlareSolverr, has FlareSolverr try the proxy too. A site behind Cloudflare answers that request with
403 whether or not your address is blocked, so a pass means the proxy got through, not that the site
will work.

## Built-in sources

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
