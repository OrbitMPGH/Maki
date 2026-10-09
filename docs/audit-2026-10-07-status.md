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

Ten lanes started 2026-10-09; i18n sweep and FE-UX run last because they cut across the others.

| Lane | ID | State |
|---|---|---|
| RECO/META | RECO-01 | done be62a02f |
| RECO/META | RECO-02 | done 4d0c5c90 |
| RECO/META | RECO-03 | done 6727883d |
| RECO/META | RECO-04 | done 2bf65a11 |
| RECO/META | RECO-05 | done 8ccb0b50, e93d9e4f |
| RECO/META | RECO-06 | done 6a8a5fff, 9ad6fcb5 (reader refresh is now a no-op) |
| RECO/META | RECO-07 | done 4245f741 (rails built 80 deep, reverses documented choice) |
| RECO/META | METADATA-01 | done a3f7bb2e, e5c94f5c |
| RECO/META | METADATA-02 | done c2454bd2, b1fa6a11 (SharedBuild) |
| RECO/META | METADATA-04 | done 2adf8681 (pool membership needs run-reco-suite check) |
| RECO/META | METADATA-07 | done 87e17050, 9a6d8d33 |
| RECO/META | METADATA-09 | done 1e1aa45d |
| SOURCES | SOURCES-01 | done b014d5ba |
| SOURCES | SOURCES-02 | done ef67c288 (MangaFire zh code unconfirmed) |
| SOURCES | SOURCES-03 | done 712e082c |
| SOURCES | SOURCES-07 | done 344434a4, 60ccc2c7 |
| SOURCES | SOURCES-09 | done 5c4b7132 |
| SOURCES | SOURCES-10 | done 92b8c15e, 722960e9, b17c0a1f, b336be63, 26a778a0 |
| SOURCES | SOURCES-11 | done 9ee93b62 |
| SOURCES | SOURCES-12 | done 3fbd0c6d |
| FE-LIBRARY | FE-LIBRARY-01 | done 7ffe8338 |
| FE-LIBRARY | FE-LIBRARY-14 | done cc58dfd3 |
| FE-LIBRARY | FE-LIBRARY-15 | done 5cbb41cc |
| FE-LIBRARY | FE-LIBRARY-17 | done 5881646a, 1e31ede0 |
| FE-LIBRARY | FE-LIBRARY-27 | done 63fcb3c0, 41408ed3 |
| FE-LIBRARY | FE-LIBRARY-52 | done 35ad8d17, 66a9547a |
| FE-SETTINGS | FE-SETTINGS-01 | done fa89f09f, 17fabf0e |
| FE-SETTINGS | FE-SETTINGS-08 | done c3c5c8cf |
| FE-SETTINGS | FE-SETTINGS-09 | done 70c006f5, 9eae2c6c |
| FE-SETTINGS | FE-SETTINGS-15 | done 61088ef4 |
| FE-SETTINGS | FE-SETTINGS-16 | done 2f3d141d, 17fabf0e |
| FE-SETTINGS | FE-SETTINGS-25 | done 76297a17 |
| FE-INFRA | FE-INFRA-06 | done bd642688 |
| FE-INFRA | FE-INFRA-08 | done 29d697c3, f364aab6 |
| FE-INFRA | FE-INFRA-15 | done 8a662524, dc48ac3d |
| FE-INFRA | FE-INFRA-22 | done cf8fd2c2 |
| FE-INFRA | FE-LIBRARY-20 | done a13b0a4e, f1211048 |
| FE-INFRA | FE-LIBRARY-37 | done cb6029df, a6786a4e |
| HOSTING | HOSTING-02 | done 7125e0aa, 903e249a |
| HOSTING | HOSTING-03 | done d76c9720, 2a989248 |
| HOSTING | HOSTING-05 | done 5225ecc0 |
| HOSTING | HOSTING-06 | done 2f4e93ac, 30873750 |
| HOSTING | HOSTING-08 | decision (scheduled backups: Quartz job when newest zip older than BackupDays, or default check off until opted in) |
| HOSTING | HOSTING-09 | done c2de0e39, 399a9cb1 |
| HOSTING | HOSTING-10 | done a4f5db3c |
| HOSTING | HOSTING-12 | done 65b64cdc, 0fae3751 |
| SERIES | SERIES-01 | done 95fd865f |
| SERIES | SERIES-02 | done 46688d07 |
| SERIES | SERIES-09 | done 470d8c34, b72b4f8f |
| SERIES | SERIES-13 | done 668fa167, 24d4f860 (safe part; native titles drive search only on script-matched sources) |
| SERIES | SERIES-17 | done 56cc5164 |
| SERIES | SERIES-23 | done 1da392b0, e8a4127e |
| SERIES | DATA-02 | done 05177281, 8722dcfa |
| AUTH | AUTH-03 | done c9d8940e, 478f7cde |
| AUTH | AUTH-04 | done e572a343 |
| AUTH | AUTH-06 | done 204d4dd2 (500 only; admin exemption from root-folder scope: owner decision) |
| PROGRESS | PROGRESS-07 | done 4d847a6a |
| PROGRESS | PROGRESS-08 | done ae732901 |
| PROGRESS | PROGRESS-09 | done 4b7970dc |
| PROGRESS | PROGRESS-12 | done 4c50d533, 52dbf5c7, 8d1aa1ba (BulkMarked column, migration) |
| PROGRESS | PROGRESS-13 | done d55e28c5, 45ce3d1c |
| PROGRESS | PROGRESS-23 | done 78d0ef47, 1ac83551, 8d1aa1ba |
| PROGRESS | PROGRESS-24 | done fd22db55 |
| PROGRESS | FE-READER-04 (server clamp) | done f3f85434, 4460c2e8 |
| FE-READER-2 | FE-READER-11 | done 3d410c75, fc936e68 |
| FE-READER-2 | FE-READER-14 | done 4ba0da23 |
| FE-READER-2 | FE-READER-16 | done 14b960dd, 5cfce219 |
| i18n sweep | HOSTING-22 | done 392f4984, b0def6a8 (HealthScan.Error and SourceMapping.LastError stay English: need key columns) |
| i18n sweep | SERIES-06 | done 3566c97f |
| i18n sweep | SERIES-07 | done 52cfa8af |
| i18n sweep | SERIES-16 | done 91904bbd |
| i18n sweep | SERIES-22 | done 80ee0d53 |
| i18n sweep | AUTH-16 | done 9b5496b3 (restore-by-name now 404, upstream failures 502) |
| FE-UX | FE-UX-01 | done 9ebd63fb, 2cb7875f (AnimeSignalsSection, FeedbackLab, ManageSignalsModal still double-toast via shared hooks: Tier 3) |
| FE-UX | FE-UX-06 | done e8468b3b, 2cb7875f |
| FE-UX | FE-UX-08 | done 416d3e11, 2cb7875f |
| FE-UX | FE-UX-14 | done 59d5df06 |

## Wave 3: Tier 3 and Tier 4 per slice

Rows added when the wave starts.
