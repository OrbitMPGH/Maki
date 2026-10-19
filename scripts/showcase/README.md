# Showcase library

Builds a throwaway Maki config that looks like a real, lived-in library, for screenshots and demos.
Nothing in it is a real account: one local user called Maki, 45 popular series, no monitoring, no
upgrades, and a reading history backfilled over the last 74 days.

The config lives in `.screenshots/` (ignored by git). The `backend-screenshots` launch preset runs
the API against it on port 8990.

## Build it

1. Copy the catalogue data from any config that has it into `.screenshots/`: `mangabaka.db`,
   `embeddings.db`, `models/`, `coread-edges.db`, `reco-edges.db`, `reader-cohorts.db`,
   `taste-vectors.db`. Do not copy `maki.db`, `config.json` or `dataprotection-keys`.
2. Start `backend-screenshots`.
3. Create the account, root folder and series, then wait for source matching to finish:

```bash
node scripts/showcase/bootstrap.mjs
```

   The account password and an API key are written to `.screenshots/credentials.json`.
4. Stop the backend, then backfill history, files on disk and counts:

```bash
node scripts/showcase/backfill.mjs data
```

5. Start the backend, open the Stats page once so the achievements are computed, stop the backend
   and date them:

```bash
node scripts/showcase/backfill.mjs achievements
```

6. Start the backend again and run `scripts/readme-images`.

`backfill.mjs data` can be run again. It starts from the chapters the sources returned each time
and uses a fixed seed, so the same library comes out.

## What is fake

- Files on disk are database rows. The `.cbz` files do not exist, so opening one of those chapters
  in the reader fails. Download a few chapters of one series for real and use that series for the
  reader screenshots.
- Reading history, sessions and stats events are generated, dated across the last 74 days.
- Missing counts come from chapters marked wanted without a file.
- Monitoring is off for every series, so the Monitored tile reads 0.
