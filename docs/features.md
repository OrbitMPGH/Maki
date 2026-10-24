# Features

## Highlights

- **Automatic monitoring.** Maki checks its sources on a schedule, maps each series to every
  source that carries it, and downloads new chapters from your preferred one on the next check.
- **Reader with progress sync.** Paged, double-page, or continuous scroll. Progress is merged
  with Kavita and sent to AniList, MyAnimeList, Kitsu, MangaBaka, and MangaUpdates. Pages read
  over OPDS in Panels, Chunky, KOReader, or Mihon count as read too, and KOReader's progress
  sync brings back chapters read offline on the device.
- **Recommendations.** Local ONNX embeddings over the MangaBaka catalog find titles similar to
  what is already in your library, using more than shared genre tags. The Browse tab adds genre
  rails, blind spots, **Readers like you**, and a **Your Taste** view of how your reading has
  shifted.
- **Local metadata.** A local copy of the [MangaBaka](https://mangabaka.org) database gives
  instant search with no API rate limits, and links each series to its MyAnimeList, AniList,
  MangaUpdates, and Kitsu IDs. It is required: Maki downloads it on first start and opens once
  it is installed. Until then the reader still opens chapters already on disk, and admins can
  reach Settings and Health.
- **Torrents alongside scrapers.** Search releases through Prowlarr or Jackett and send them to qBittorrent, Transmission and Deluge.
  Maki imports each one when it finishes, repacking anything that is not already CBZ.
- **Built for a household.** Per-user history, preferences, tracker accounts, and library access
  over one shared set of files. It also supports two-factor authentication, API keys, and OpenID
  Connect single sign-on.
- **You own the files.** Everything lands on disk as plain CBZ + ComicInfo.xml, so nothing is
  locked in Maki's database. Any CBZ reader, including Kavita, can use the same folder.

## Reading

- **Built-in reader.** Paged, double-page, 1:1 (original size), and continuous-scroll modes.
  Progress is tracked and merged with Kavita so nothing double-counts. A read meter shows how far
  through the series you are, and time-remaining estimates are based on comparable chapters.
- **Reading profiles.** Per-user defaults for page fit, direction, and mode by series type, with
  per-series overrides on top.
- **Home.** **Continue reading**, **Jump back in**, **Recently added**, **You might like**, and other
  rails, drag-reorderable and switchable per user.
- **Scrobbling.** Sends read progress to AniList, MyAnimeList, Kitsu, MangaBaka, and
  MangaUpdates, whether the reading happened in Maki, over OPDS, in KOReader, or in Kavita. Your lists on those trackers can also be
  imported into the library from Settings.
- **Mihon backup import.** Upload a Mihon backup on the Import page to add its library, with
  categories as tags and read chapters marked read. Needs the local MangaBaka database.
- **Anime tie-in.** For a series with an anime adaptation, Maki can start you at the chapter where
  the anime ended.
- **OPDS server.** OPDS 1.2 with page streaming (OPDS-PSE), so Panels, Chunky, KOReader, and
  Mihon read straight from Maki. Off by default. Each user's feed URL carries its own rotatable
  token, and pages streamed this way count as read.
- **KOReader progress sync.** Maki is a KOReader sync server: chapters downloaded to KOReader
  report the page you reached, even when read offline, and KOReader picks up where you stopped in
  Maki. Signs in with the OPDS token. See [KOReader](koreader.md).
- **Komga-compatible API.** Apps that only connect to a Komga server, like the Mihon Komga
  extension and Tachimanga, can use Maki instead. Point them at `http://your-host:8990/komga` and
  use your OPDS token as the API key (or as the password, with any username). Reading progress
  syncs both ways, including through Mihon's Komga tracker. A client that cannot decode a page's
  format (an AVIF or WebP page) can ask for it as JPEG or PNG, as it would from Komga. Off by
  default, switched on in the OPDS settings.
- **Install as an app and read offline.** Maki installs to a phone or desktop home screen. Save
  chapters or whole series to the device, or let it keep the next few unread chapters of what
  you're reading. Library and series pages you have visited still open offline, and progress read
  offline syncs when the connection returns. Push notifications can announce new chapters. See
  [Offline reading](offline-reading.md).

## Discover

- **Recommendations.** Local ONNX embeddings over the MangaBaka dump find titles similar to your
  library. Seed them from your whole library or from specific titles, and filter by year, rating,
  type, status, genre, chapter count, content rating, and popularity.
- **Browse.** The Browse tab has catalog and genre rails, recent-reading picks, side interests,
  blind spots, and **Readers like you**. **Your Taste** shows reading behavior, grouped interests,
  and how your taste has moved. Every rail has a fullscreen **Show more** view. Detail cards show
  categorized tags, alternate titles, creators, per-source ratings, and MyAnimeList reviews.
- **Metadata.** One MangaBaka search identifies a series and brings along its MyAnimeList,
  AniList, MangaUpdates, and Kitsu IDs. The local
  [MangaBaka dump](https://mangabaka.org/data/database) (nightly snapshot, about a 350 MB
  download and 3.5 GB on disk) answers every metadata lookup, so search and library imports are
  instant. A series MangaBaka added after the latest snapshot shows up with the next one. MangaBaka-original data is
  [CC BY-NC-SA 4.0](https://creativecommons.org/licenses/by-nc-sa/4.0/).

## Stats

- **History.** Overview and Library tabs built from an append-only history of reading and
  downloads: per-series read counts, activity over time, and so on.
- **Progress.** Levels, streaks, achievements, and reading time, with a Reader track (yours) and a
  Library track (the whole instance). Series marked incognito never generate events.

## Library and downloads

- **Monitoring.** Chapter lists refresh on a schedule and new chapters are queued automatically.
  Whole-series monitor modes decide what gets queued, and a per-chapter **Wanted** toggle keeps
  individual chapters or specials out.
- **Automatic source matching.** Adding a series returns instantly; matching runs in the background
  and can be re-run or fixed by hand. Sources are drag-ordered by preference and can be disabled
  globally, and the series page can compare linked sources side by side on sampled page quality.
  A single chapter can also be re-fetched from whichever linked source has the better copy.
- **Torrents.** Search releases through Prowlarr or Jackett, grab them with qBittorrent, Transmission and Deluge, and auto-import on
  completion. Files are hardlinked into the library where possible, and torrents share the queue
  with scraper downloads. Anything that is not already CBZ is converted on the way in: `.zip` is
  placed unchanged (a CBZ is a zip), `.cbr`/`.rar`/`.7z`/`.tar` are repacked, and a folder of
  loose images becomes one CBZ per folder. A PDF is the one exception: it is kept as it is and
  read in place. Manual library import converts files the same way and leaves your originals in
  place.
- **Output.** The default layout is `{Series}/{Series} Vol.X Ch.Y.cbz`, with configurable folder
  and file naming. ComicInfo.xml carries series, chapter number, volume, authors, genres,
  language, and reading direction. Files are written atomically, so a reader never sees a
  half-written CBZ. Maki can also write a `cover.jpg` into each series folder for readers that
  look for one.
- **Page conversion.** Off by default (Settings, Library, Page conversion). Pick WebP or AVIF and
  a quality, and new downloads are stored with their JPEG pages in that format and their PNG pages
  as lossless WebP. A pausable library pass converts the files already on disk, one at a time, and
  shows what it saved. A page or file that would not come out at least 5% smaller is kept as it
  was, and so are WebP and AVIF pages, animations, pages under 16 KB and pages over 16,383 pixels
  on a side. Files hardlinked to a torrent client's copy are skipped. Originals wait in the upgrade
  trash (`.maki-trash/<series id>/conv-*`) for the trash retention period, and the pass pauses
  itself rather than leave less than 1 GB free. WebP opens in every reader Maki works with
  (browsers, Kavita, Komga, KOReader, Mihon, Panels); AVIF does not open in KOReader or on iOS
  before 16. JPEG XL is not offered: the image library Maki bundles cannot write it, and Kavita
  and KOReader cannot read it. Upgrades, source quality estimates and the quality shown on the
  series page keep judging a converted chapter by the size and format it was downloaded in, so
  converting never makes a copy look like a worse release.
- **Multilingual libraries.** A series' link to a source can hold several languages, each with
  its own chapter rows and files, with the language recorded in ComicInfo.xml. Per-user
  title-language preferences pick the display title without renaming anything on disk.
- **Library view.** Grid or list view with density options. Shows download state, read badges,
  and monitor status. Filter by source, chapter count, or user tag, save filters, and set Wanted
  in bulk. Per-series incognito mode keeps a title out of stats and progress without hiding it.
- **Activity queue.** Updates live over SignalR with per-page progress. You can retry or remove
  items and review downloads that need a decision, for example an import that would replace
  existing chapter files.
- **Library health.** Admins can index and verify archives, and find missing, unreadable,
  duplicate, or suspicious files. Replacement and deletion operations are reviewed before they
  run, and unlinked CBZs can be imported.

## Administration

- **Multi-user.** Per-user history, preferences, and tracker accounts over one shared library,
  with per-root-folder access grants. See [Multiple readers](multiple-readers.md).
- **Security.** Two-factor authentication, per-user API keys, HTTPS enforcement, trusted proxies,
  lockout, and OpenID Connect single sign-on. See
  [Exposing Maki to the internet](exposing-to-the-internet.md).
- **Backup & restore.** One-click zip of database and settings, plus an automatic safety backup
  before every schema migration. See [Backup & restore](backup-restore.md).
- **Notifications.** Outbound events (new chapters, download results, health warnings, and more)
  go to Discord, Telegram, Slack, Notifiarr, ntfy, Gotify, Pushover, Apprise, or a plain webhook.
  Connections can be scoped to user tags so each person only hears about their own series.
- **Interface.** Eleven UI languages, five accent colors and several dark and light
  backgrounds.
- **REST API.** Available at `/api/v1` with `X-Api-Key` auth. Swagger is served at `/swagger`
  when running in the Development environment.
