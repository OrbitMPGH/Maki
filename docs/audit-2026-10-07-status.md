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
| A | AUTH-05 | done b657082e (owner decided 2026-10-10: those PUTs stay open to API keys) |
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
| HOSTING | HOSTING-08 | owner decided 2026-10-10: scheduled backup job, wave 3 lane |
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
| AUTH | AUTH-06 | done 204d4dd2 (owner decided 2026-10-10: admins stay scoped) |
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

One lane per slice, lows then nits; plus the HOSTING-08 backup job lane. Started 2026-10-10.

| Lane | ID | Tier | State |
|---|---|---|---|
| Kavita repair | (wave 2 review) | low | skipped (owner decided 2026-10-09: leave existing rows; imported rows carry no Kavita series id, so a repair cannot tell a wrong row from a right one, and the scoped match stops new ones) |
| DOWNLOADS | DOWNLOADS-02 | medium | done 5e4c759e (missed in wave 2) |
| HOSTING-08 job | HOSTING-08 | medium | done a17238bd, 0cb22018, 5ae81857 (setting backup.scheduled, default off) |
| AUTH | AUTH-07 | low | done 07efa3dc |
| AUTH | AUTH-08 | low | done 07efa3dc, d28caf5c |
| AUTH | AUTH-12 | low | done f8ef9264, 6e3f8f27 |
| AUTH | AUTH-14 | low | done ade1c02d (recent sign-in within 10 min for passwordless) |
| AUTH | AUTH-17 | low | done 07efa3dc |
| AUTH | AUTH-21 | low | done 1c72caa8, 37a64386 (revocations carried forward on restore) |
| AUTH | AUTH-22 | low | done 07efa3dc, b94fa23b, d28caf5c |
| AUTH | AUTH-23 | low | done 12480045, b4dbf33f |
| AUTH | AUTH-28 | low | done 1798eb54, 5eabe2e5 (TOTP replay guard) |
| AUTH | AUTH-24 | low | done 8bb2893a |
| AUTH | AUTH-10 | low | skipped (already fixed by HOSTING-01) |
| AUTH | AUTH-18 | low | done 0214e824 |
| AUTH | AUTH-20 | low | done 4098ef34 |
| AUTH | AUTH-15 | low | done 5e6b5d60, 981f212f |
| AUTH | AUTH-19 | low | done 0214e824, 389a3a8a |
| AUTH | AUTH-26 | low | done 6d096810, 90fa3430 |
| AUTH | AUTH-30 | nit | done 70881826 |
| AUTH | AUTH-13 | nit | done be8b2713 |
| AUTH | AUTH-29 | nit | done 5e6b5d60 |
| AUTH | AUTH-11 | nit | skipped (per-GET reissue is deliberate) |
| AUTH | AUTH-09 | nit | done 07efa3dc |
| AUTH | AUTH-25 | nit | done 6f2c9bcb |
| AUTH | AUTH-16 | nit | done (wave 2) |
| AUTH | AUTH-27 | nit | done 6f2c9bcb |
| CORE | CORE-10 | low | done bef5715d |
| CORE | CORE-20 | low | done 3539885a |
| CORE | CORE-23 | low | done e597547c (culture only; "One-shot", "Vol." and "Ch." labels stay English, a structured-parts design change for the owner) |
| CORE | CORE-06 | low | done 724f9e2f, 5f4ead85, 8ba7da67 |
| CORE | CORE-07 | low | done 668b2d95, 62de2859 |
| CORE | CORE-21 | low | done 3539885a, 9786a048 |
| CORE | CORE-25 | low | done f91b6f3c |
| CORE | CORE-26 | low | done b735d624 |
| CORE | CORE-03 | low | done 6eb5697f, 62de2859 |
| CORE | CORE-02 | low | done 6eb5697f |
| CORE | CORE-29 | low | done dc10a04d |
| CORE | CORE-28 | low | skipped (a cached PDF reader would hold the file open on Windows and block moves and deletes; perf-only) |
| CORE | CORE-27 | low | done 392f4984, 0206fd0e, d275940a |
| CORE | CORE-11 | nit | done 5619b70f |
| CORE | CORE-33 | nit | done 92b8c15e |
| CORE | CORE-04 | nit | done 6eb5697f |
| CORE | CORE-12 | nit | done d6065757 |
| CORE | CORE-16 | nit | done 5619b70f |
| CORE | CORE-30 | nit | done 79fe6292 |
| CORE | CORE-14 | nit | done 871a793c |
| CORE | CORE-15 | nit | done 5619b70f |
| CORE | CORE-17 | nit | done 5619b70f |
| CORE | CORE-31 | nit | done 79fe6292 |
| CORE | CORE-32 | nit | skipped (unscored AnimeSignalPolicy branches: delete or make live is a recommendation-behaviour choice for the owner) |
| DATA | DATA-04 | low | done 2c4313e8 |
| DATA | DATA-09 | low | done 2c4313e8, 79d4c05d |
| DATA | DATA-13 | low | done b03f93fd |
| DATA | DATA-01 | low | skipped (already fixed by c2de0e39, 399a9cb1: MigrationErrorMarker expires after 7 days) |
| DATA | DATA-08 | low | done bc365743, 79d4c05d |
| DATA | DATA-12 | low | skipped (behaviour change: refreshing CompletedAt on a re-read moves the documented first-flip rule and the stats that date rows by it; owner may revisit) |
| DATA | DATA-03 | low | done 2c4313e8 |
| DATA | DATA-14 | low | done 77cb6d67, 33bdd262 (HealthScans only; the history, operations and file-version journals stay unpruned on purpose) |
| DATA | DATA-06 | low | done b73fe53a, a55ccaf5 |
| DATA | DATA-05 | low | done 2c4313e8 |
| DATA | DATA-10 | nit | done b73fe53a |
| DATA | DATA-07 | nit | done 987a6e4a |
| DATA | DATA-15 | nit | done ed88032f, 99c162e8 |
| DATA | DATA-11 | nit | done d9a73712 |
| DBPERF | DBPERF-03 | low | pending |
| DBPERF | DBPERF-07 | low | pending |
| DBPERF | DBPERF-10 | low | pending |
| DBPERF | DBPERF-12 | low | pending |
| DBPERF | DBPERF-15 | low | pending |
| DBPERF | DBPERF-11 | low | pending |
| DBPERF | DBPERF-16 | low | pending |
| DBPERF | DBPERF-13 | low | pending |
| DBPERF | DBPERF-01 | low | pending |
| DBPERF | DBPERF-02 | low | pending |
| DBPERF | DBPERF-05 | low | pending |
| DBPERF | DBPERF-06 | low | pending |
| DBPERF | DBPERF-08 | low | pending |
| DBPERF | DBPERF-09 | low | pending |
| DBPERF | DBPERF-04 | low | pending |
| DBPERF | DBPERF-18 | nit | pending |
| DBPERF | DBPERF-19 | nit | pending |
| DBPERF | DBPERF-20 | nit | pending |
| DBPERF | DBPERF-22 | nit | pending |
| DBPERF | DBPERF-17 | nit | pending |
| DBPERF | DBPERF-21 | nit | pending |
| DBPERF | DBPERF-24 | nit | pending |
| DBPERF | DBPERF-14 | nit | pending |
| DBPERF | DBPERF-23 | nit | pending |
| DOWNLOADS | DOWNLOADS-09 | low | done d6ae9d72 |
| DOWNLOADS | DOWNLOADS-10 | low | done d2f47522 |
| DOWNLOADS | DOWNLOADS-13 | low | done 45a8c486 |
| DOWNLOADS | DOWNLOADS-14 | low | done e2254868 (early access only) |
| DOWNLOADS | DOWNLOADS-17 | low | done e2254868 |
| DOWNLOADS | DOWNLOADS-28 | low | done 7e72929c |
| DOWNLOADS | DOWNLOADS-20 | low | done a8f43419 |
| DOWNLOADS | DOWNLOADS-21 | low | done 63b03c4a |
| DOWNLOADS | DOWNLOADS-22 | low | done d7cca99b, 6defdd86 |
| DOWNLOADS | DOWNLOADS-24 | low | done 2903f282 |
| DOWNLOADS | DOWNLOADS-29 | low | done 3c5675bf |
| DOWNLOADS | DOWNLOADS-03 | low | done efa806d0 |
| DOWNLOADS | DOWNLOADS-06 | low | done dd02082e |
| DOWNLOADS | DOWNLOADS-16 | low | done 0df1dbc9 |
| DOWNLOADS | DOWNLOADS-05 | low | skipped (owner decision: imported reads count as finished) |
| DOWNLOADS | DOWNLOADS-08 | low | done 9c36f853 |
| DOWNLOADS | DOWNLOADS-18 | low | done 9b524237, 56d9edfa |
| DOWNLOADS | DOWNLOADS-23 | low | done d7cca99b |
| DOWNLOADS | DOWNLOADS-26 | low | done f8d52544 |
| DOWNLOADS | DOWNLOADS-04 | low | skipped (narrow read, tens of ms at 50k rows) |
| DOWNLOADS | DOWNLOADS-25 | low | done 5ef23800 |
| DOWNLOADS | DOWNLOADS-27 | low | skipped (owner decision: Retry stays allowed) |
| DOWNLOADS | DOWNLOADS-19 | low | done 3c5675bf (server Vol./Ch. labels left) |
| DOWNLOADS | DOWNLOADS-07 | low | done f59a0ef2 |
| DOWNLOADS | DOWNLOADS-36 | low | done 2042fa96 |
| DOWNLOADS | DOWNLOADS-30 | nit | done c49ba63e |
| DOWNLOADS | DOWNLOADS-34 | nit | done 6a214ffb |
| DOWNLOADS | DOWNLOADS-15 | nit | done c49ba63e |
| DOWNLOADS | DOWNLOADS-33 | nit | done d7cca99b |
| DOWNLOADS | DOWNLOADS-32 | nit | done dd02082e |
| DOWNLOADS | DOWNLOADS-35 | nit | skipped (behaviour change, timezone-dependent tests) |
| DOWNLOADS | DOWNLOADS-31 | nit | done c49ba63e |
| FE-INFRA | FE-INFRA-14 | low | pending |
| FE-INFRA | FE-INFRA-04 | low | pending |
| FE-INFRA | FE-INFRA-13 | low | pending |
| FE-INFRA | FE-INFRA-16 | low | pending |
| FE-INFRA | FE-INFRA-17 | low | pending |
| FE-INFRA | FE-INFRA-24 | low | pending |
| FE-INFRA | FE-INFRA-10 | low | pending |
| FE-INFRA | FE-INFRA-21 | low | pending |
| FE-INFRA | FE-INFRA-23 | low | pending |
| FE-INFRA | FE-INFRA-11 | low | pending |
| FE-INFRA | FE-INFRA-18 | low | pending |
| FE-INFRA | FE-INFRA-20 | low | pending |
| FE-INFRA | FE-INFRA-19 | low | pending |
| FE-INFRA | FE-INFRA-27 | nit | pending |
| FE-INFRA | FE-INFRA-09 | nit | pending |
| FE-INFRA | FE-INFRA-12 | nit | pending |
| FE-INFRA | FE-INFRA-25 | nit | pending |
| FE-INFRA | FE-INFRA-26 | nit | pending |
| FE-LIBRARY | FE-LIBRARY-39 | low | pending |
| FE-LIBRARY | FE-LIBRARY-53 | low | pending |
| FE-LIBRARY | FE-LIBRARY-21 | low | pending |
| FE-LIBRARY | FE-LIBRARY-41 | low | pending |
| FE-LIBRARY | FE-LIBRARY-26 | low | pending |
| FE-LIBRARY | FE-LIBRARY-48 | low | pending |
| FE-LIBRARY | FE-LIBRARY-55 | low | pending |
| FE-LIBRARY | FE-LIBRARY-38 | low | pending |
| FE-LIBRARY | FE-LIBRARY-11 | low | pending |
| FE-LIBRARY | FE-LIBRARY-02 | low | pending |
| FE-LIBRARY | FE-LIBRARY-04 | low | pending |
| FE-LIBRARY | FE-LIBRARY-05 | low | pending |
| FE-LIBRARY | FE-LIBRARY-16 | low | pending |
| FE-LIBRARY | FE-LIBRARY-18 | low | pending |
| FE-LIBRARY | FE-LIBRARY-19 | low | pending |
| FE-LIBRARY | FE-LIBRARY-23 | low | pending |
| FE-LIBRARY | FE-LIBRARY-25 | low | pending |
| FE-LIBRARY | FE-LIBRARY-28 | low | pending |
| FE-LIBRARY | FE-LIBRARY-29 | low | pending |
| FE-LIBRARY | FE-LIBRARY-31 | low | pending |
| FE-LIBRARY | FE-LIBRARY-33 | low | pending |
| FE-LIBRARY | FE-LIBRARY-34 | low | pending |
| FE-LIBRARY | FE-LIBRARY-42 | low | pending |
| FE-LIBRARY | FE-LIBRARY-43 | low | pending |
| FE-LIBRARY | FE-LIBRARY-51 | low | pending |
| FE-LIBRARY | FE-LIBRARY-57 | low | pending |
| FE-LIBRARY | FE-LIBRARY-07 | low | pending |
| FE-LIBRARY | FE-LIBRARY-40 | low | pending |
| FE-LIBRARY | FE-LIBRARY-46 | low | pending |
| FE-LIBRARY | FE-LIBRARY-58 | low | pending |
| FE-LIBRARY | FE-LIBRARY-03 | low | pending |
| FE-LIBRARY | FE-LIBRARY-12 | nit | pending |
| FE-LIBRARY | FE-LIBRARY-35 | nit | pending |
| FE-LIBRARY | FE-LIBRARY-60 | nit | pending |
| FE-LIBRARY | FE-LIBRARY-08 | nit | pending |
| FE-LIBRARY | FE-LIBRARY-44 | nit | pending |
| FE-LIBRARY | FE-LIBRARY-13 | nit | pending |
| FE-LIBRARY | FE-LIBRARY-30 | nit | pending |
| FE-LIBRARY | FE-LIBRARY-47 | nit | pending |
| FE-LIBRARY | FE-LIBRARY-54 | nit | pending |
| FE-LIBRARY | FE-LIBRARY-59 | nit | pending |
| FE-LIBRARY | FE-LIBRARY-06 | nit | pending |
| FE-LIBRARY | FE-LIBRARY-22 | nit | pending |
| FE-LIBRARY | FE-LIBRARY-50 | nit | pending |
| FE-LIBRARY | FE-LIBRARY-09 | nit | pending |
| FE-LIBRARY | FE-LIBRARY-10 | nit | pending |
| FE-LIBRARY | FE-LIBRARY-24 | nit | pending |
| FE-LIBRARY | FE-LIBRARY-36 | nit | pending |
| FE-LIBRARY | FE-LIBRARY-45 | nit | pending |
| FE-LIBRARY | FE-LIBRARY-49 | nit | pending |
| FE-LIBRARY | FE-LIBRARY-56 | nit | pending |
| FE-LIBRARY | FE-LIBRARY-61 | nit | pending |
| FE-READER | FE-READER-08 | low | done 1c28fad9 |
| FE-READER | FE-READER-24 | low | done d3762af8 |
| FE-READER | FE-READER-20 | low | done 2a8da8e9 |
| FE-READER | FE-READER-07 | low | done 1c28fad9, dd7ed05b |
| FE-READER | FE-READER-10 | low | skipped (documented write-on-open that Continue relies on) |
| FE-READER | FE-READER-06 | low | done dd7ed05b (Next chapter completes only from the last page) |
| FE-READER | FE-READER-09 | low | done d1b40371, dd7ed05b, 0d142894 |
| FE-READER | FE-READER-34 | low | done d7e48f3c |
| FE-READER | FE-READER-19 | low | done 6cc7b6c1 (partial) |
| FE-READER | FE-READER-15 | low | done d852dae9 |
| FE-READER | FE-READER-17 | low | done d852dae9 |
| FE-READER | FE-READER-18 | low | done 6cc7b6c1 |
| FE-READER | FE-READER-21 | low | done d3762af8 |
| FE-READER | FE-READER-22 | low | done 5c701ea7, dd7ed05b |
| FE-READER | FE-READER-23 | low | done 5c701ea7 |
| FE-READER | FE-READER-25 | low | done 4efc724f |
| FE-READER | FE-READER-26 | low | done 2a41d3f1, dd7ed05b |
| FE-READER | FE-READER-12 | low | done d1b40371 |
| FE-READER | FE-READER-27 | nit | done d3762af8 |
| FE-READER | FE-READER-31 | nit | skipped (under 2 s, touches incognito semantics) |
| FE-READER | FE-READER-29 | nit | done d3762af8 |
| FE-READER | FE-READER-28 | nit | done d852dae9 |
| FE-READER | FE-READER-30 | nit | done 4efc724f |
| FE-READER | FE-READER-33 | nit | skipped (needs a backend payload change) |
| FE-READER | FE-READER-35 | nit | done 40e82886 |
| FE-READER | FE-READER-32 | nit | done 1c28fad9 |
| FE-SETTINGS | FE-SETTINGS-23 | low | done f8cb0007, 70c6eabc, 371679e3 |
| FE-SETTINGS | FE-SETTINGS-20 | low | done f8cb0007 |
| FE-SETTINGS | FE-SETTINGS-26 | low | done 25cd85ed, c6155de0 |
| FE-SETTINGS | FE-SETTINGS-28 | low | done 25cd85ed |
| FE-SETTINGS | FE-SETTINGS-40 | low | done ff20624e |
| FE-SETTINGS | FE-SETTINGS-43 | low | done ff20624e |
| FE-SETTINGS | FE-SETTINGS-02 | low | done 25cd85ed |
| FE-SETTINGS | FE-SETTINGS-05 | low | done 76d39667 |
| FE-SETTINGS | FE-SETTINGS-06 | low | done 76d39667 |
| FE-SETTINGS | FE-SETTINGS-34 | low | done 9b3ca879 |
| FE-SETTINGS | FE-SETTINGS-48 | low | done f725c08a |
| FE-SETTINGS | FE-SETTINGS-03 | low | done 9ebd63fb (global MutationCache toast) |
| FE-SETTINGS | FE-SETTINGS-11 | low | done 76d39667 |
| FE-SETTINGS | FE-SETTINGS-13 | low | done 76d39667 |
| FE-SETTINGS | FE-SETTINGS-17 | low | done f8cb0007, 9ebd63fb |
| FE-SETTINGS | FE-SETTINGS-21 | low | done f8cb0007, 371679e3 |
| FE-SETTINGS | FE-SETTINGS-22 | low | done f8cb0007 |
| FE-SETTINGS | FE-SETTINGS-27 | low | done 25cd85ed |
| FE-SETTINGS | FE-SETTINGS-35 | low | done 9b3ca879 |
| FE-SETTINGS | FE-SETTINGS-37 | low | done f8cb0007 |
| FE-SETTINGS | FE-SETTINGS-38 | low | done f8cb0007, 70c6eabc, 371679e3 |
| FE-SETTINGS | FE-SETTINGS-42 | low | done ff20624e |
| FE-SETTINGS | FE-SETTINGS-44 | low | done ff20624e (Play Rewind hidden when viewing another reader) |
| FE-SETTINGS | FE-SETTINGS-45 | low | done ff20624e |
| FE-SETTINGS | FE-SETTINGS-49 | low | done f725c08a |
| FE-SETTINGS | FE-SETTINGS-50 | low | done f725c08a |
| FE-SETTINGS | FE-SETTINGS-18 | low | done f8cb0007 |
| FE-SETTINGS | FE-SETTINGS-19 | low | done f8cb0007 |
| FE-SETTINGS | FE-SETTINGS-04 | low | done 76d39667 |
| FE-SETTINGS | FE-SETTINGS-29 | low | done 9b3ca879, 93830f66 |
| FE-SETTINGS | FE-SETTINGS-41 | low | done ff20624e, 93830f66 |
| FE-SETTINGS | FE-SETTINGS-46 | nit | done ff20624e |
| FE-SETTINGS | FE-SETTINGS-14 | nit | done 76d39667 |
| FE-SETTINGS | FE-SETTINGS-31 | nit | done 9b3ca879 |
| FE-SETTINGS | FE-SETTINGS-07 | nit | done 76d39667 |
| FE-SETTINGS | FE-SETTINGS-32 | nit | done 25cd85ed |
| FE-SETTINGS | FE-SETTINGS-36 | nit | done 9b3ca879 |
| FE-SETTINGS | FE-SETTINGS-12 | nit | done 76d39667 |
| FE-SETTINGS | FE-SETTINGS-24 | nit | done 9b3ca879 |
| FE-SETTINGS | FE-SETTINGS-39 | nit | done f8cb0007 |
| FE-SETTINGS | FE-SETTINGS-47 | nit | done ff20624e, 93830f66 |
| FE-SETTINGS | FE-SETTINGS-51 | nit | done f725c08a |
| FE-SETTINGS | FE-SETTINGS-30 | nit | done 9b3ca879 (accountSecurity stays outside the switchable categories, commented) |
| FE-UX | FE-UX-03 | low | pending |
| FE-UX | FE-UX-05 | low | pending |
| FE-UX | FE-UX-07 | low | pending |
| FE-UX | FE-UX-11 | low | pending |
| FE-UX | FE-UX-12 | low | pending |
| FE-UX | FE-UX-15 | low | pending |
| FE-UX | FE-UX-16 | low | pending |
| FE-UX | FE-UX-23 | low | pending |
| FE-UX | FE-UX-25 | low | pending |
| FE-UX | FE-UX-20 | low | pending |
| FE-UX | FE-UX-26 | low | pending |
| FE-UX | FE-UX-02 | low | pending |
| FE-UX | FE-UX-21 | low | pending |
| FE-UX | FE-UX-10 | low | pending |
| FE-UX | FE-UX-13 | nit | pending |
| FE-UX | FE-UX-18 | nit | pending |
| FE-UX | FE-UX-19 | nit | pending |
| FE-UX | FE-UX-22 | nit | pending |
| FE-UX | FE-UX-24 | nit | pending |
| FE-UX | FE-UX-27 | nit | pending |
| FE-UX | FE-UX-04 | nit | pending |
| FE-UX | FE-UX-09 | nit | pending |
| FE-UX | FE-UX-17 | nit | pending |
| HOSTING | HOSTING-26 | low | done ae3a7864, 1723f02e |
| HOSTING | HOSTING-32 | low | done 27a3a327 (proxy bypass in CORE lane) |
| HOSTING | HOSTING-19 | low | done ae3a7864, 75957f9e |
| HOSTING | HOSTING-20 | low | done fdf94fcd |
| HOSTING | HOSTING-24 | low | done c7a42239 |
| HOSTING | HOSTING-27 | low | done 940816a1 |
| HOSTING | HOSTING-29 | low | done 6f2de970 |
| HOSTING | HOSTING-33 | low | done (wave 2) |
| HOSTING | HOSTING-37 | low | done a4957257 |
| HOSTING | HOSTING-40 | low | done f4f9ceb6 |
| HOSTING | HOSTING-47 | low | done aece72a1 |
| HOSTING | HOSTING-11 | low | done 3ca1ccb0 |
| HOSTING | HOSTING-38 | low | done ae1e7fbe |
| HOSTING | HOSTING-39 | low | skipped (owner decision: keep failing startup) |
| HOSTING | HOSTING-23 | low | done 46135206 |
| HOSTING | HOSTING-34 | low | done eef1bbd1 |
| HOSTING | HOSTING-13 | low | done e7bc3e83, 6327a67a |
| HOSTING | HOSTING-15 | low | done 58c40a8f (resolved toward RECO gate) |
| HOSTING | HOSTING-16 | low | done 58c40a8f |
| HOSTING | HOSTING-30 | low | done 6f2de970, 1723f02e |
| HOSTING | HOSTING-04 | low | skipped (write probe is the point of the check) |
| HOSTING | HOSTING-21 | low | done 50b23db2 (owner decided 2026-10-09: every manual run goes onto the background job, 202 started) |
| HOSTING | HOSTING-17 | low | done 58c40a8f |
| HOSTING | HOSTING-18 | low | done 890943d4 |
| HOSTING | HOSTING-28 | low | done d67793ae (UrlBase removed) |
| HOSTING | HOSTING-31 | low | done 0f02480a |
| HOSTING | HOSTING-36 | low | done ce6701d5 |
| HOSTING | HOSTING-51 | nit | done 5d4d9608, 75957f9e |
| HOSTING | HOSTING-14 | nit | skipped (window is milliseconds) |
| HOSTING | HOSTING-49 | nit | skipped (theoretical) |
| HOSTING | HOSTING-50 | nit | skipped (behaviour change) |
| HOSTING | HOSTING-25 | nit | done c7a42239 |
| HOSTING | HOSTING-43 | nit | skipped (behaviour change) |
| HOSTING | HOSTING-45 | nit | skipped (too large) |
| HOSTING | HOSTING-46 | nit | done e6b572f1, 75957f9e |
| HOSTING | HOSTING-48 | nit | done 9f199317 |
| HOSTING | HOSTING-41 | nit | skipped (behaviour change) |
| HOSTING | HOSTING-42 | nit | done 42896055 |
| HOSTING | HOSTING-35 | nit | done fa68c82e |
| HOSTING | HOSTING-44 | nit | done eef1bbd1, 703f8a7f |
| METADATA | METADATA-12 | low | done 5961fc2e, 3ec2c387 |
| METADATA | METADATA-13 | low | done 5961fc2e |
| METADATA | METADATA-15 | low | done 968d5400 |
| METADATA | METADATA-17 | low | done 4cd1d49d |
| METADATA | METADATA-23 | low | done 968d5400 |
| METADATA | METADATA-24 | low | done d40b598d |
| METADATA | METADATA-10 | low | done e78eec04 |
| METADATA | METADATA-11 | low | done 7b2885f0 |
| METADATA | METADATA-18 | low | done b4df95f1 |
| METADATA | METADATA-03 | low | done 128f4617 |
| METADATA | METADATA-14 | low | done 76fc517e, 8f569062, 94860047 |
| METADATA | METADATA-05 | low | done fab5861c (ArrayPool part left; empty-channel path no longer allocates) |
| METADATA | METADATA-06 | low | done 418cd4bf |
| METADATA | METADATA-16 | low | done 016375a2, 3d6cde5f |
| METADATA | METADATA-20 | low | done 876c98eb |
| METADATA | METADATA-22 | low | done 99af3675, 8d0ae457 |
| METADATA | METADATA-19 | low | done b4df95f1 |
| METADATA | METADATA-08 | low | done 167d4d1b, 5367f6f9 (index still builds while embeddings are off; gating it broke never-show and tag filters) |
| METADATA | METADATA-21 | low | done 5ad60dd1, 016375a2 (shared checks and failure memo; no base class) |
| METADATA | METADATA-28 | low | done 8a91df93, 5961fc2e, 158ebb32 and others (no Invalidate-during-build test) |
| METADATA | METADATA-27 | low | done 7983e1e1 |
| METADATA | METADATA-25 | nit | done 60bb0d95 (COLLATE, LIKE ESCAPE, HtmlDecode, DropAfterScan left alone) |
| METADATA | METADATA-26 | nit | done 8720e872 |
| PROGRESS | PROGRESS-27 | low | done eccdea66 |
| PROGRESS | PROGRESS-10 | low | done 363e5bc7 |
| PROGRESS | PROGRESS-22 | low | done 71138ce0 |
| PROGRESS | PROGRESS-28 | low | done 5919a390 (culture only) |
| PROGRESS | PROGRESS-11 | low | done 363e5bc7 |
| PROGRESS | PROGRESS-14 | low | done 363e5bc7 |
| PROGRESS | PROGRESS-15 | low | done 3e5633de, f8a6c69c, fe3fdbc7 |
| PROGRESS | PROGRESS-17 | low | done 9b48cec6 |
| PROGRESS | PROGRESS-18 | low | skipped (already fixed by CORE-22) |
| PROGRESS | PROGRESS-19 | low | skipped (already fixed by CORE-18) |
| PROGRESS | PROGRESS-26 | low | done 556e0502, 057a09fa |
| PROGRESS | PROGRESS-30 | low | done b6e0d3dd |
| PROGRESS | PROGRESS-32 | low | done 9b48cec6 |
| PROGRESS | PROGRESS-33 | low | done 71138ce0 |
| PROGRESS | PROGRESS-01 | low | done 71138ce0, 149b6730, dba5c355 (CountedAt column, migration) |
| PROGRESS | PROGRESS-02 | low | done 33aa057d |
| PROGRESS | PROGRESS-16 | low | done 9b48cec6, 0c9249e7 |
| PROGRESS | PROGRESS-35 | low | done 363e5bc7 |
| PROGRESS | PROGRESS-25 | low | skipped (client refetch coalesced in wave 2; audience cache would delay revocation) |
| PROGRESS | PROGRESS-03 | low | done 71138ce0 |
| PROGRESS | PROGRESS-29 | low | done 71138ce0 |
| PROGRESS | PROGRESS-34 | nit | skipped (documented in reader-progress.md) |
| PROGRESS | PROGRESS-31 | nit | skipped (existing test pins intended behaviour) |
| PROGRESS | PROGRESS-20 | nit | done 59b0d22c (em dash only) |
| PROGRESS | PROGRESS-04 | nit | done 71138ce0 |
| PROGRESS | PROGRESS-21 | nit | done 59b0d22c |
| PROGRESS | PROGRESS-36 | nit | done 59b0d22c, a927697b, 907d10d4 (partial: d, e, f skipped) |
| RECO | RECO-19 | low | done a4f665d3 |
| RECO | RECO-08 | low | done 7907e866 |
| RECO | RECO-14 | low | done 92ab6d4e |
| RECO | RECO-21 | low | done 3fc3d04b |
| RECO | RECO-22 | low | done 51848b48 (instance switches still wait 60 s) |
| RECO | RECO-13 | low | done 9e70ba8b, 818ebb82 |
| RECO | RECO-15 | low | done cdf7e5da |
| RECO | RECO-11 | low | done 7da4dd5e |
| RECO | RECO-10 | low | done 3c9ca0cf |
| RECO | RECO-12 | low | done 57e92fa6 |
| RECO | RECO-24 | low | done 9881e8b1 (partial) |
| RECO | RECO-23 | low | done 7f4fd4cb |
| RECO | RECO-29 | low | done b11301d6 |
| RECO | RECO-09 | low | done 2dec52ce (Intl.ListFormat on the client) |
| RECO | RECO-34 | low | done 72c8dafd, 761927a2 (never-show rails still lack an end-to-end test) |
| RECO | RECO-20 | nit | skipped (already fixed by b956feea) |
| RECO | RECO-16 | nit | done cdf7e5da |
| RECO | RECO-26 | nit | done c1a19d7a |
| RECO | RECO-28 | nit | done 39553041 |
| RECO | RECO-17 | nit | done cdf7e5da |
| RECO | RECO-32 | nit | done 19fa5ab5, 818ebb82 |
| RECO | RECO-25 | nit | done cdf7e5da |
| RECO | RECO-27 | nit | done 39553041 (partial) |
| RECO | RECO-18 | nit | done cdf7e5da |
| RECO | RECO-30 | nit | done 57e92fa6 |
| RECO | RECO-31 | nit | done 34ba8b03 (grouped route removed) |
| RECO | RECO-33 | nit | done 1f15adc6 |
| SERIES | SERIES-28 | low | done 4b07c3c7 |
| SERIES | SERIES-21 | low | skipped (already fixed by AUTH-01/02) |
| SERIES | SERIES-20 | low | done a1381c15, e20a2c4b |
| SERIES | SERIES-03 | low | done 4e3b4256 |
| SERIES | SERIES-10 | low | done 2af33d7c |
| SERIES | SERIES-25 | low | done 99d04c7d |
| SERIES | SERIES-27 | low | done c2943f00, 7acc2fd1 |
| SERIES | SERIES-35 | low | done 38ac67bb, 9761515b (500-row paging left) |
| SERIES | SERIES-15 | low | done c482b14e, bf9ef5fb |
| SERIES | SERIES-26 | low | done 201c4527, 4e2a9e73 |
| SERIES | SERIES-33 | low | done e1359b21, 80740652 |
| SERIES | SERIES-34 | low | done e1359b21, dfa4a6e2 |
| SERIES | SERIES-36 | low | done c76dd650 |
| SERIES | SERIES-37 | low | done bba31b84 |
| SERIES | SERIES-04 | low | done 142fa627 |
| SERIES | SERIES-11 | low | done 2af33d7c |
| SERIES | SERIES-14 | low | done 4208d220 |
| SERIES | SERIES-29 | low | done 253738de |
| SERIES | SERIES-30 | low | skipped (rows capped at 10 per mapping) |
| SERIES | SERIES-32 | low | done 68c6596b, 6081dbf9 |
| SERIES | SERIES-05 | low | done bdf2e782, 3afbb752 (cover version from file write time, cached) |
| SERIES | SERIES-24 | low | skipped (frontend coalesced in wave 2; server projection buys little) |
| SERIES | SERIES-07 | low | done (wave 2) |
| SERIES | SERIES-16 | low | done (wave 2) |
| SERIES | SERIES-22 | low | done (wave 2) |
| SERIES | SERIES-39 | nit | done df34afee (partial) |
| SERIES | SERIES-42 | nit | skipped (already uses SourceLanguages.Same) |
| SERIES | SERIES-41 | nit | done e60abca7 (partial) |
| SERIES | SERIES-31 | nit | done df34afee |
| SERIES | SERIES-38 | nit | done df34afee (partial) |
| SERIES | SERIES-12 | nit | skipped (owner decision: admins stay scoped) |
| SERIES | SERIES-40 | nit | done e60abca7 |
| SOURCES | SOURCES-26 | low | pending |
| SOURCES | SOURCES-17 | low | pending |
| SOURCES | SOURCES-21 | low | pending |
| SOURCES | SOURCES-22 | low | pending |
| SOURCES | SOURCES-24 | low | pending |
| SOURCES | SOURCES-20 | low | pending |
| SOURCES | SOURCES-06 | low | pending |
| SOURCES | SOURCES-18 | low | pending |
| SOURCES | SOURCES-19 | low | pending |
| SOURCES | SOURCES-33 | low | pending |
| SOURCES | SOURCES-23 | low | pending |
| SOURCES | SOURCES-05 | low | pending |
| SOURCES | SOURCES-04 | low | pending |
| SOURCES | SOURCES-15 | low | pending |
| SOURCES | SOURCES-25 | low | pending |
| SOURCES | SOURCES-08 | low | pending |
| SOURCES | SOURCES-27 | low | pending |
| SOURCES | SOURCES-14 | low | pending |
| SOURCES | SOURCES-16 | low | pending |
| SOURCES | SOURCES-13 | low | pending |
| SOURCES | SOURCES-32 | low | pending |
| SOURCES | SOURCES-30 | nit | pending |
| SOURCES | SOURCES-28 | nit | pending |
| SOURCES | SOURCES-29 | nit | pending |
| SOURCES | SOURCES-34 | nit | pending |
| SOURCES | SOURCES-35 | nit | pending |
| SOURCES | SOURCES-31 | nit | pending |
