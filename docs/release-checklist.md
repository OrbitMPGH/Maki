# Release checklist

Manual smoke test for a release candidate. It covers what CI and code review can't: the built
image, a real browser, a fresh install, an upgrade, and the plain-HTTP LAN setup most self-hosters
run. `distribution/release.ps1` already runs the tests, the frontend build and the i18n gates, so
none of that is repeated here.

A person or an agent can work through it. Items marked **[manual]** need something an agent can't
do on its own (an authenticator app, a phone, an external account) and should be handed back to
the owner.

## Ground rules

- Test the candidate build, not a dev server. Build the image with
  `./distribution/build-local.ps1 -NoPush -Tag rc` and run that. Vite in dev mode hides bundling,
  lazy-chunk and `wwwroot` problems.
- Never point a test run at a real library or the real `%APPDATA%\Maki` config. Use a scratch
  config directory and a scratch library folder, both of which you are fine deleting afterwards.
- Every browser step is done with the devtools console open (or, for an agent,
  `read_console_messages` after each page). A red console error on any page is a failure even if
  the page looks fine.
- After the run, read the Maki log in `{config}/logs/` and look at every `[ERR]` and `[WRN]` line
  written during the session. Anything that isn't explained by a deliberate failure test is a
  finding.

## Two origins

Items tagged **[both]** must pass on both origins, because browsers switch off `crypto.randomUUID`,
`crypto.subtle` and `navigator.clipboard` outside a secure context and this has shipped broken
before (issue #104, the Import button did nothing over HTTP).

| Origin | URL | `window.isSecureContext` |
|---|---|---|
| Secure | `http://localhost:8990` (or HTTPS, see below) | `true` |
| Insecure | `http://<this machine's LAN IP>:8990` | `false` |

Check `window.isSecureContext` in the console before starting each pass so you know which one you
are on. `127.0.0.1` also counts as secure, so it is not a substitute for the LAN IP.

Optional real-HTTPS pass, worth doing when anything touched auth, cookies or proxies: put a reverse
proxy in front (for example `caddy reverse-proxy --from https://localhost:8443 --to :8990`), set
`auth.requirehttps` on and `auth.trustedproxies` to the proxy, restart, and repeat section 1.

## Setup

Fixtures live in `.release-test/` (git-ignored) and are made by the scripts in
`scripts/release-test/`. Make them once, then reuse them for every run:

- `node scripts/release-test/make-import.mjs` writes `.release-test/template/import`, a root folder
  of generated series covering every import format and edge case (see section 3). It takes a couple
  of seconds and needs 7-Zip for the `.cb7`/`.7z` cases.
- `./scripts/release-test/seed-config.ps1` writes `.release-test/template/config`: the last
  released image, set up with an admin account, `/library` and `/import` as root folders, and the
  catalogue and recommendation index already downloaded. The login is in
  `.release-test/template/README.txt`. Re-seed now and then so the template stays close to the
  current release.

For each run:

1. Build the candidate: `./distribution/build-local.ps1 -NoPush -Tag rc`.
2. Start it on fresh copies of the templates:

   ```bash
   ./scripts/release-test/new-instance.ps1
   ```

   It prints the secure and insecure URLs and the run folder, which holds that run's `config`,
   `library` and `import`. The templates are never mounted, so nothing a run does carries over.
   Pass `-Fresh` for an empty config (sections 0 and 1.1), and `-Port` if 8990 is taken by a dev
   backend. Delete old folders under `.release-test/runs/` when done.
3. Pick a short, finished series on MangaDex (under ten chapters) to use as the download test.

Because the config template was made by the previous release, starting the candidate on it is
already an upgrade. Sections 0 and 1.1 need `-Fresh`; everything else runs on the template.

## 0. Boot

- [ ] **0.1** Container starts and stays up. `docker logs maki-test` shows migrations applied and no
      exception.
- [ ] **0.2** The sidebar footer shows the version being released (a `build-local.ps1` image
      shows `-nightly`, which is expected).
- [ ] **0.3** `/` loads the first-run setup page, not a blank page or a 401. Hard-refresh a deep
      link (`/library`) and it still loads.

## 1. Accounts and sign-in [both]

- [ ] **1.1** First-run setup creates the admin account and lands signed in.
- [ ] **1.2** Sign out, sign back in. A wrong password shows the generic error, not a hint about
      which part was wrong.
- [ ] **1.3** Restart the container. You are still signed in (data-protection keys persisted to
      `/config`).
- [ ] **1.4** Settings > Account: create an API key. The secret is shown once and the **Copy**
      button actually puts it on the clipboard (paste it somewhere to check).
- [ ] **1.5** The key works: `curl -H "X-Api-Key: <key>" http://localhost:8990/api/v1/series`
      returns JSON. The same call with `?apikey=<key>` instead of the header is refused.
- [ ] **1.6** **[manual]** Enable 2FA: the QR code renders, **Copy** on the shared key works, a code
      from an authenticator app is accepted, recovery codes are shown and **Copy codes** works.
      Sign out and back in with a TOTP code, then once with a recovery code.
- [ ] **1.7** Create a second, non-admin user limited to one root folder. Signed in as them: they
      see only that folder's series, `/health` redirects away, admin settings are hidden.

## 2. Add a series [both]

- [ ] **2.1** Add page: searching a title returns results with covers.
- [ ] **2.2** Add the MangaDex test series. It appears in the library straight away and source
      matching finishes in the background (series page shows at least one linked source).
- [ ] **2.3** The chapter list fills in, with numbers and titles that match the source.
- [ ] **2.4** Metadata is attached: description, genres, cover, and tracker links (MyAnimeList,
      AniList and so on) on the series page.
- [ ] **2.5** Add a second series from the Discover page instead of the Add page.

## 3. Import [both]

The fixture's pages are generated: each one shows its chapter or volume, `P n/4`, and a bar that
grows with the page number, so wrong page order is obvious in the reader.

| Folder | What it tests | Expected |
|---|---|---|
| `Dandadan (2021)` | Plain CBZs, a decimal chapter, ComicInfo.xml in Ch.1 | 4 chapters including 2.5 |
| `Chainsaw Man` | `.zip` | 3 chapters, placed as CBZ without a repack |
| `Sousou no Frieren` | `.cbr`, romanised title | Matches Frieren, 3 chapters repacked to CBZ |
| `Blue Period` | `.cb7`, `.7z`, `.cbt`, and a 7z named `.cbz` | 4 chapters, all real CBZ afterwards |
| `Vagabond` | Folders of loose images | 2 volumes |
| `Oyasumi Punpun` | `.pdf` | 2 volumes kept as PDF, 4 pages each |
| `Spy x Family` | Ch.1 as both `.cbr` and `.cbz`, plus `.txt`/`.nfo` | 2 chapters, Ch.1 once, other files ignored |
| `Berserk` | Ch.002 is a truncated archive | Ch.001 imports, Ch.002 is skipped or reported, nothing crashes |
| `ワンパンマン` | Non-ASCII folder and file names | Matches One-Punch Man, names survive the import |
| `Totally Made Up Series 9999` | No catalogue match | Offers no match and can be left out |
| `Empty Series Folder` | No comics | Listed with 0 comics, nothing to import |

- [ ] **3.1** Scan `/import` from the Import page. Every row matches the table above.
- [ ] **3.2** Import everything that matched. It finishes without errors and the series show up in
      the library with their chapters marked as on disk.
- [ ] **3.3** In the run's `import` folder, every chapter is now a CBZ except the PDFs, and
      unrelated files were left alone.
- [ ] **3.4** Open one chapter of each kind in the reader (a repacked one, a loose-image volume, a
      PDF). Pages are in order.

## 4. Download

- [ ] **4.1** On the test series, download a few chapters. They show up in Activity with live
      per-page progress (SignalR working), without refreshing the page. **[both]**
- [ ] **4.2** They complete. The files are at `{Series}/{Series} Vol.X Ch.Y.cbz` (or the
      configured naming) in the library folder.
- [ ] **4.3** Open one CBZ: pages are in order and `ComicInfo.xml` has series, number, volume,
      authors, genres and language.
- [ ] **4.4** Force a failure (download a chapter, then block the source or pull the network).
      The item shows as failed with a readable reason and **Retry** works once the network is back.
- [ ] **4.5** The chapter list's **Wanted** toggle and the series monitor mode stick after a reload.

## 5. Reader [both]

Use a scratch series here: reaching a chapter's last page marks it read and adds stats and XP.

- [ ] **5.1** Open a downloaded chapter. Pages load, and paging with the keyboard, click zones and
      the page strip all work.
- [ ] **5.2** Switch paged, double-page, 1:1 and continuous scroll. Each renders correctly.
- [ ] **5.3** Leave mid-chapter and reopen: it resumes on the right page.
- [ ] **5.4** Finish the chapter. The end screen offers the next chapter, and the chapter now shows
      as read on the series page and in **Continue reading** on Home.
- [ ] **5.5** Incognito toggle: reading with it on writes no progress.
- [ ] **5.6** Phone width (375px): the reader and the series page are usable, nothing overflows
      sideways.

## 6. Library, Home, Discover, Stats [both]

- [ ] **6.1** Library: grid and list view, a filter, a saved filter, bulk Wanted. Nothing errors.
- [ ] **6.2** Home: every rail loads or shows its empty state. Drag-reorder a rail, reload, the
      order stuck.
- [ ] **6.3** Discover: recommendations and Browse rails load once the catalogue has downloaded.
      Hide a recommendation from its menu and it disappears (this path uses `randomUUID`).
- [ ] **6.4** Stats: Overview, Library and Progress tabs render and reflect the reads from
      section 5.
- [ ] **6.5** Notifications inbox opens and entries can be marked read.

## 7. Settings and admin [both]

- [ ] **7.1** Every Settings tab opens without a console error. Change a setting, reload, it stuck.
- [ ] **7.2** Manage sources: disable a source and reorder two, reload, both stuck.
- [ ] **7.3** Notifications: add a webhook connection (any request bin) and **Test** delivers.
- [ ] **7.4** OPDS: enable it, **Copy** the feed URL, and fetch the URL with `curl`. The feed lists
      the library. The OPDS token is refused on `/api/v1`.
- [ ] **7.5** Backup: create one and download it. The zip has `maki.db`, `config.json` and
      `manifest.json`, and no `dataprotection-keys`.
- [ ] **7.6** Health page: run a scan. It completes and lists the library files.
- [ ] **7.7** Switch the UI language, then theme and accent colour. Pages redraw with no raw message
      ids or English left in obvious places.

## 8. Upgrade from the previous release

Most self-hosters upgrade rather than install fresh, and migrations only run forward. The config
template is already an upgrade, but it holds no downloads or reading history, so this section
builds some on the old version first.

- [ ] **8.1** `new-instance.ps1 -Image ghcr.io/orbitmpgh/maki:latest` (the previous release). Add
      a series, download and read a chapter, import one fixture series.
- [ ] **8.2** `new-instance.ps1 -Reuse <that run folder>` starts the `rc` image on the same data.
      It boots, the log shows a pre-migration backup was taken if the release has migrations, and
      one exists in the run's `config/backups`.
- [ ] **8.3** You are still signed in. The series, its files, read progress and settings from 8.1
      are all intact.
- [ ] **8.4** Queue another download on the upgraded instance and read it: the same flow still
      works end to end.

## 9. Release-specific

- [ ] **9.1** For every `feat` and `fix` in `git log <previous tag>..dev`, do the thing it describes
      once in the browser on the insecure origin. This is where most regressions show up.
- [ ] **9.2** Anything new that copies to the clipboard, generates an id, or hashes in the browser
      gets tried on the insecure origin specifically.

## Recording a run

Report each item as pass, fail or skipped. For a fail, give the steps, what happened, and the
console or log line. For a skip, say why. An agent should finish with a table of the failures and
skips only, not a full list of passes, and should not attempt fixes during the run.
