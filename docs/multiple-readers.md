# Multiple readers

Each account has its own reading history, preferences, and tracker connections. The library itself
is shared: one copy of the files, one set of series and chapters, so a second reader costs no disk.

- **Per user:** read state and resume position, bookmarks, ratings, reader defaults and per-series
  overrides, saved filters, start page, Home layout, title language, series-page rails,
  content-rating ceiling, OPDS feed, and the tracker accounts (AniList, MyAnimeList, Kitsu,
  MangaBaka) your progress is sent to. Ratings land on *your* tracker profile.
- **Shared, admin-only:** root folders, download clients and indexers, sources and their
  priority, metadata and recommendation settings, notifications, backups, and tracker app
  registrations. The client ID and secret are shared, but each person connects their own account.
- **Library access** is granted per root folder under **Settings → Users**. An account with no
  grants sees an empty library. Series, chapters, covers, search, and OPDS all respect it. The
  same grants decide where someone with the **Add series** permission may add a title.
- **Kavita is one server with one API key**, so everything it reports is a single person's
  reading. **Settings → Kavita** chooses which Maki account Kavita's reading belongs to (unset
  means the first admin). Only that account can sync read status with Kavita.
