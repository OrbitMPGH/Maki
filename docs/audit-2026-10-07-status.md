# Audit 2026-10-07: fix status

Tracks every finding from `docs/audit-2026-10-07.md` as it is worked. Integration branch `audit-fixes`; lane branches `audit/lane-*` merge into it. Owner verifies `audit-fixes` before it reaches `dev`.

States: `done <sha>`, `skipped (reason)`, `decision (question)`, `pending`.

## Fixed on dev before this branch

| ID | State |
|---|---|
| AUTH-01 | done b956feea |
| SERIES-08 | done 3c22320c |
| DOWNLOADS-01 | done 6f63ea6f |
| FE-INFRA-01 | done 5275806a |
| FE-INFRA-02 | done 5275806a |
| FE-INFRA-03 | done 5275806a |
| FE-INFRA-05 | done 5275806a |
| FE-INFRA-07 | done 5275806a |

## Wave 1: remaining Tier 1, Tier 2 security, CORE

| Lane | ID | State |
|---|---|---|
| A | AUTH-05 | done b657082e (kavita, prowlarr, qbittorrent PUTs left open to API keys: owner decision) |
| A | AUTH-02 | done f418a4cf, 5b64df10 |
| A | HOSTING-01 | done 047a1654, f5f7e0fe, 6f7524d2 |
| B | SERIES-18 | done 48ab4d59, ce0783f6 |
| B | SERIES-19 | done 4987621a, ad4cd931 (grab needs a search from the last hour in this process) |
| B | DOWNLOADS-12 | done 77e5c8e5, d0d6fbd3, 3b5f435d, ac1a3e50 |
| B | DOWNLOADS-11 | done 0525a536 |
| C | PROGRESS-05 | done 540b0a0a, c54f13c7 |
| C | PROGRESS-06 | done 012bb4a0, e2a4bd79 |
| D | FE-READER-01 | done 7ba82f8a |
| D | FE-READER-02 | done d28b59d2, 6f6d315f |
| D | FE-READER-03 | done c4d989c5, a262bee8 |
| D | FE-READER-04 | done 16053ee8 (server-side clamp left for PROGRESS lane) |
| D | FE-READER-05 | done f81d09d7 |
| D | FE-READER-13 | done aeabffdb |
| E | FE-SETTINGS-33 | done 9c5b9ba2 |
| CORE | CORE-01 | done e0ef8f89, 0e393cbd |
| CORE | CORE-05 | done 3383f9c7, 19cee1ff |
| CORE | CORE-08 | done 06cda549 |
| CORE | CORE-09 | done e3db0669 |
| CORE | CORE-13 | done 3a909429 (verified against qBittorrent 4.6.7 source) |
| CORE | CORE-18 | done 707a52ec |
| CORE | CORE-19 | done 1495b2f7 |
| CORE | CORE-22 | done 9786a048 |
| CORE | CORE-24 | done 7f9d8081 |
| CORE | HOSTING-07 | done 7f9d8081 |
| CORE | HOSTING-33 | done 69a17ff7 (pulled forward) |

## Wave 2: Tier 2 remainder

Lanes: HOSTING, SERIES, AUTH, PROGRESS, RECO/META, SOURCES, FE-INFRA, FE-LIBRARY, FE-SETTINGS, FE-READER-2, i18n sweep, FE-UX (last). Rows added when the wave starts.

## Wave 3: Tier 3 and Tier 4 per slice

Rows added when the wave starts.
