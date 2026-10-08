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
| A | AUTH-05 | pending |
| A | AUTH-02 | pending |
| A | HOSTING-01 | pending |
| B | SERIES-18 | pending |
| B | SERIES-19 | pending |
| B | DOWNLOADS-12 | pending |
| B | DOWNLOADS-11 | pending |
| C | PROGRESS-05 | done 540b0a0a, c54f13c7 |
| C | PROGRESS-06 | done 012bb4a0, e2a4bd79 |
| D | FE-READER-01 | done 7ba82f8a |
| D | FE-READER-02 | done d28b59d2, 6f6d315f |
| D | FE-READER-03 | done c4d989c5, a262bee8 |
| D | FE-READER-04 | done 16053ee8 (server-side clamp left for PROGRESS lane) |
| D | FE-READER-05 | done f81d09d7 |
| D | FE-READER-13 | done aeabffdb |
| E | FE-SETTINGS-33 | done 9c5b9ba2 |
| CORE | CORE-01 | pending |
| CORE | CORE-05 | pending |
| CORE | CORE-08 | pending |
| CORE | CORE-09 | pending |
| CORE | CORE-13 | pending |
| CORE | CORE-18 | pending |
| CORE | CORE-19 | pending |
| CORE | CORE-22 | pending |
| CORE | CORE-24 | pending |
| CORE | HOSTING-07 | pending |

## Wave 2: Tier 2 remainder

Lanes: HOSTING, SERIES, AUTH, PROGRESS, RECO/META, SOURCES, FE-INFRA, FE-LIBRARY, FE-SETTINGS, FE-READER-2, i18n sweep, FE-UX (last). Rows added when the wave starts.

## Wave 3: Tier 3 and Tier 4 per slice

Rows added when the wave starts.
