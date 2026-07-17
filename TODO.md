# TODO

A running list of things to check, fix, or add. Add items freely — newest at the top of each section. When something is done, delete it. After each completed item, push and commit the changes.

## To do

_Nothing open._

## Known issues / to investigate

- Add Series' per-result "Add" modal and Discover's filter/detail modals can't be visually verified in the headless preview (empty modal shell, screenshots time out) — a Mantine + preview-pane rendering limitation, not an app bug. Drive these via DOM reads/events instead of screenshots.

## Answered

- **Settings secrets are not encrypted at rest** — verified against a live DB: `qbittorrent.password`, `kavita.apikey`, `prowlarr.apikey`, `scrobble.malclientsecret` and tracker tokens are stored as plaintext in `AppConfig`. Accepted as a deliberate trade-off (matches Sonarr/Radarr/Prowlarr; any key would live beside the DB anyway). Documented in [CLAUDE.md](CLAUDE.md) — the real boundary is filesystem permissions on the config dir.
- **Library filter textbox clears fine** — earlier "select-all + delete didn't clear" was a browser-automation artifact. Verified: typing filters 36 → 1 card, clearing restores all 36.
- **`/series/59` "Monitor: none" combobox opens and works** — earlier "no visible listbox" was the same preview-pane rendering limitation. Verified: dropdown reports `aria-expanded=true` / visible with all 3 options, and selecting one fired the mutation ("Monitoring 105/123 chapter(s)").
- **Add Series reusing `/recommendations/detail|reviews`** is intentional — `AddSeriesPage` renders the shared `DiscoverDetailModal` and pads a search result into a `RecommendationItem` (see the comment above `toRecommendationItem`).
