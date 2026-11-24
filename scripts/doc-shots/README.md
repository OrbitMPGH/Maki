# Doc shots

Screenshots of the whole UI for the documentation site, taken from the showcase library so no
real account, path or adult title shows up. One command builds the API, brings the showcase data
up to date, starts its own backend, captures every page and component in `manifest.mjs`, and
stops the backend again. Rerun it after a UI change and every image comes out the same way.

```bash
cd scripts/doc-shots
npm install
npm run shots
```

The images land in `out/`, one folder per area (`pages/`, `phone/`, `home/`, `series/`,
`chapters/`, `reader/`, `discover/`, `activity/`, `health/`, `library/`, `stats/`, `import/`,
`first-run/`, `settings/`). `out/index.html` is a contact sheet of all of them. `out/` is not
committed. A full run takes about half an hour.

## Updating the docs site

Docs pages reference a shot as `assets/ui/<group>/<name>.webp`, the same name it has in `out/`.
After a capture, `npm run publish` writes every referenced shot to `site/src/assets/ui/` as a
2x WebP and deletes the ones no page uses any more. So refreshing the docs after a UI change is:

```bash
npm run shots
npm run publish
```

To add an image to a page, reference `../../../assets/ui/<group>/<name>.webp` (one more `../`
under `guides/`) and run `npm run publish`.

Desktop shots use the top-bar layout (sidebar collapsed). The `pages/*-sidebar` shots show the
sidebar layout.

## Options

```bash
npm run shots -- series/ reader/settings-panel   # only these shots (names or prefixes)
npm run shots -- --list                          # every shot name
npm run shots -- --no-build                      # skip dotnet build
npm run shots -- --running                       # use a backend that is already up, as is
npm run shots -- --no-prepare                    # leave the database alone
```

`MAKI_VERSION` sets the version the app shows (default: the latest tag on `origin/main`).
`MAKI_URL` points at another address. `CHROME_PATH` picks the browser, as in `readme-images`.

A shot that fails leaves a screenshot of the page at that moment in `out/_failed/`, which is
usually enough to see which step went wrong. The other shots still run.

## What it needs

- The showcase config in `.screenshots/` (see `scripts/showcase/README.md`). The script refuses
  any folder without a `.showcase` marker, because the prepare step rewrites its database.
- Its port comes from `.screenshots/config.json` (8994, so it never collides with an instance
  on 8990). Nothing else may be listening there unless you pass `--running`.
- The catalogue: `.screenshots` has no `mangabaka.db` of its own and reads the one in
  `.simulated/` through `MAKI_SHARED_DUMP_DIR`. The recommendation files (`embeddings.db`,
  `coread-edges.*`, `ease-table.*`, `reco-edges.db`, `taste-vectors.db`) and the embedding
  model under `models/` are copies.
- Frieren Ch. 63 and 64 are real files. Every other chapter file in the showcase is a database
  row only, so the reader shots use those two.

## What the prepare step does

Runs with the backend stopped, before each capture, so the library never looks stale:

- **Moves the dates forward.** Every timestamp from 400 days before the last aligned time to 21
  days after it moves by the gap to now, so the last reading session is always a few hours ago,
  the streak holds and the Upcoming spans sit ahead of today. The aligned time is kept in
  `AppConfig` as `docshots.alignedAt`. Anime air dates and config values are left alone.
- **Stages states** from `STATES` in the manifest: read-then-auto-removed chapters, delisted
  chapters (one still has its file, which raises the series alert), files a health scan found
  missing (with open findings for the Health page), wanted chapters with no file, and an
  archived series. It also clears the missing-file records every other faked file would carry.
- **Keeps the trackers and inbox quiet.** The tracker tokens are fake, so a sync only logs
  errors. The last sync is stamped as now, which keeps the hourly tick from firing during a run,
  and error rows from earlier syncs are cleared. Notifications a run added (level-ups, health
  alerts) are deleted, so the bell count stays the same between runs.

## Faked in the browser

Some states only exist for a few seconds or need services the showcase doesn't have, so the page
is handed made-up API answers instead. The answers live in `fakes.mjs`; the manifest names the
ones each shot uses. Shots that rely on one say so in their note on the contact sheet.

- **Queue:** a series torrent at 42% plus two chapters in flight (`QUEUE_BUSY` in the manifest).
- **Source matching:** the series is marked as matching, two of its sources are held back, and
  SignalR `sourceMatchProgress` events mark one as found and two as still searching. Hub events
  need a cookie session, so the script signs in with the showcase password as well as sending
  the API key.
- **Everything in `fakes.mjs`:** rating prompts and history, archive hints and dialog, "Is this
  it?", Compare sources, undo conversion, release search, reader notes, shortlist, hidden
  content, API keys, update states, OPDS, Kavita, a running page conversion, the recycle bin,
  two-factor setup, the setup guide, the metadata gate, first-run account creation, the folder
  import scan and preview, and a Mihon backup review.
- **Everywhere:** the version reads as the latest release with no "new since upgrade" marks,
  local folder paths read as `/config` and `/manga`, and the Link a source search answers from
  here so no shot calls a manga site.

Every other write to `/api/` gets an empty 204, so opening the reader never records progress.
Recommendation queries and page-conversion previews are POSTs that only read, and go through.

## Adding a shot

Add an entry to `SHOTS` in `manifest.mjs`; the comment at its top lists every option. Most
components are found from their visible heading:

```js
{ name: 'series/sources', path: (d) => `/series/${d.series.frieren}`, capture: { panel: 'Sources' } },
{ name: 'home/recently-added', path: '/home', capture: { rail: 'Recently added' } },
{ name: 'settings/reader', path: '/settings?s=reader', capture: { css: '#setting-reader' } },
```

A rewrite in a shot's `rewrites` is either `{ match, apply }`, which edits the server's answer,
or `{ match, respond }`, which replaces it. Add `method: 'POST'` (or PUT) to answer a write; it
then reaches the page instead of the 204. `match` is tested against the path plus query string,
and may name a path outside `/api/` such as `/initialize.json`. `signedOut: true` opens the page
with no key or cookie, for the sign-in and first-run screens.

Run just that shot with `npm run shots -- --running series/sources` against a backend you
started yourself to tune it quickly.
