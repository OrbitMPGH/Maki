# README images

Builds the banner and gallery tiles shown at the top of the main README. Two steps: `capture`
takes raw screenshots of a running Maki, `compose` lays them out with headlines and writes the
finished PNGs to `docs/readme/`.

Run it against the showcase library, never a real one. `scripts/showcase/README.md` explains how
to build that config and start it:
`MAKI_CONFIG_DIR=$PWD/.screenshots dotnet run --project src/Maki.Api`, which listens on port 8990.

## Run it

```bash
cd scripts/readme-images
npm install
npm run capture
npm run compose
```

`capture` reads the API key from `.screenshots/credentials.json`. Set `MAKI_API_KEY` to use
another instance.

Both steps take image names to redo only some: `npm run capture -- phone-home` and
`npm run compose -- banner recommendations`.

## Settings

| Variable | Default | Use |
|---|---|---|
| `MAKI_URL` | `http://localhost:8990` | Where the instance runs |
| `MAKI_API_KEY` | from `credentials.json` | Sent as `X-Api-Key` |
| `MAKI_SERIES` | `Dandadan` | Title text of the series used for the series page |
| `MAKI_READER_CHAPTER` | none | Id of a chapter that really has a file. Without it the reader shot is skipped |
| `MAKI_BACKGROUND` | `tinted` | Background stored in the browser before the page loads |
| `MAKI_VERSION` | none | Version the sidebar shows, for example `v0.34.0`. A local build reports `v0.0.0-dev` |
| `CHROME_PATH` | found automatically | Chrome, Edge or Chromium executable |

Each shot sets its own accent in `capture.mjs`, and `captions.json` carries the matching accent
for the finished image.

## Tuning the layout

`captions.json` holds every headline, tag, accent and crop. For a tile, `x`, `y` and `w` place the
screenshot inside the frame in units of the frame's width: `w` is how large it is, `x` and `y`
slide it. Raise `w` to zoom in, make `x` negative to hide the sidebar. The Files tile is drawn from
the values in its entry, not captured. Layout and styling live in `compose.html`.

In `capture.mjs`, a shot can set `viewport`, `scrollY`, `click` (the text of a tab or segment to
open first), `wheelScreens` (how far to scroll the reader), `session` (page state such as the
Library sort) and `trackersConnected` (shows every tracker as connected, since the showcase has no
real tracker accounts).

## Notes

- Capture blocks every write request to `/api/`, so opening the reader does not record progress or
  XP. Writes get an empty 204 rather than an abort, which the PWA build would read as offline, and
  are also stubbed inside the page's `fetch`, because the reader's keepalive flush on close
  outlives request interception. The service worker is bypassed for the same reason.
- Raw captures go to `raw/` and are not committed. Only `docs/readme/*.png` is.
- Fonts load from Google Fonts, so compose needs a network connection.
- Commit new images once per release. Every version of a PNG stays in the repository history.
