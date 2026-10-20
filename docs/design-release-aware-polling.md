# Release-aware polling and an "upcoming" view

Research and plan written 2026-10-20; implemented the same day after the owner's review (see "Decisions and what shipped" at the end).

Two questions, kept apart because they tolerate error very differently:

- **(A) Adaptive polling.** Refresh quiet series less often and active ones more often. A wrong guess costs latency or requests, never correctness, as long as there is a floor and a ceiling.
- **(B) Upcoming view.** A promise to the user ("next chapter expected around X"). A wrong guess costs trust.

## Verdict

| | Verdict | Why |
|---|---|---|
| **(A) Adaptive polling** | **Go, without prediction.** Back off on time since the last new chapter, with a floor and a ceiling. | The simple backoff cuts refresh traffic to about 13% of today's on the `.simulated` library, with a median detection delay of about an hour and a hard worst case of 24 h. Prediction-driven windows were tested too: they cost more requests than the plain backoff and barely improve latency, because the predictions are right only about half the time. |
| **(B) Upcoming view** | **Narrow go: a factual cadence line on the series page, no calendar.** An "expected around" date only behind a strict gate. | Ungated, a predicted date is within ±2 days only 50 to 57% of the time. The strictest useful gate lifts that to 71 to 75%, but then covers only 13 of the 48 series that released in the last 90 days, and still misses one time in four. That is too weak for a calendar, and too sparse for an Activity tab to be worth having. |

## 1. Data and method

**Source.** A read-only copy of `.simulated/maki.db` (plus its WAL) in the session scratchpad. The live file was never opened. The library has 104 series, 293 source mappings and 22,700 per-source chapter links (`ChapterSourceLinks`, which keep each source's own `ReleaseDate`). The last syncs ran on 2026-10-07 to 10-12.

**Caveat on `.simulated`.** Every source is in `sources.disabled` there, so the live `RefreshMonitoredSeriesJob` would refresh nothing on that instance. All the numbers below assume the sources are switched on, which is the real deployment case. The stored dates are what the sources reported at the last sync, so the release history is real.

**Method.**
- A release event is one distinct calendar day per mapping, so a same-day batch counts as one drop.
- Each release in the last 365 days is predicted walk-forward from the median of up to 8 previous gaps, and scored by its error: ±1 d, ±2 d, ±3 d, ±7 d, and a relative band of ±max(1 d, 25% of the interval).
- "Series level" means the earliest date any source gave a chapter. That is the closest thing to "when it first appeared anywhere", and it is what a per-series poller would see.

**Sample-size limits, stated plainly.**
- 71 mappings and 35 series have enough recent history to backtest (at least 5 predictions in the year).
- The official sources are almost absent. Four WEBTOON mappings, of which one backtests (17 predictions), and one MANGA Plus mapping that carries no dates at all. Naver, GigaViewer and ComicWalker are not in the library. **Nothing below says anything statistically about official sources**, only that the one sample looks regular.
- The library leans weekly-romcom and manhwa. A library heavy on monthly seinen or Chinese daily manhua would look different.

## 2. What a source's "release date" actually is

From reading every `ListChaptersAsync` (file:line in the sources), checked against the stored data:

| Source (in this library) | Kind | Field | What it really measures | Seen in the data |
|---|---|---|---|---|
| mangadex | Scanlator | `publishAt` (`MangaDexSource.cs:121`) | Group upload time, a real publication time for the scanlation | Full timestamps, no batches in the last 90 d; lags the earliest source by p50 0.9 h, p90 2 h |
| weebcentral | Aggregator | `time[datetime]` (`WeebCentralSource.cs:149`) | Mirror time, but mirrored fast | First for 65% of shared chapters; p90 lag 12 h; no batches in the last 90 d |
| mangafire | Aggregator | API `createdAt` (`MangaFireSource.cs:155`) | Row creation on the mirror, **re-stamped on re-uploads** | 68% of last-90-day links sit in groups of 3+ sharing one timestamp; p75 lag **72 days** behind other sources |
| topmanhua | Aggregator | `.chapter-release-date`, day only (`TopManhuaSource.cs:120`) | Day-precision site date; "x ago" strings parse to null | 83% of last-90-day links in same-timestamp batches; p75 lag 57 days |
| mangakakalot | Aggregator | `updated_at` (`MangakakalotSource.cs:230`) | An **update** time, not a release time | p50 lag 8 h, p90 21 days |
| webtoons | Official | `exposureDateMillis` (`WebtoonsSource.cs:287`) | Real publish time | Always first, zero lag |
| flamecomics | Scanlator | `release_date` | Real for new chapters, midnight-only for old ones | 1 mapping, quiet since 2025 |
| Suwayomi bridge | Aggregator | extension `uploadDate` (`SuwayomiSource.cs:313`) | Whatever the Mihon extension produces | One mapping has all 232 chapters at the sync moment itself (the extension falls back to fetch time) |
| mangapill, asura, atsumaru, tcbscans, mangaplus | mixed | **null** | Nothing | 4,900 links with no date |

Across all 41 sources (not just this library):
- 9 supply no date: animesama, asura, atsumaru, baozimanhua, mangapill, mangaplus, tcbscans, plus most chapters on manhuagui, which dates only the newest.
- Two decay or clock-stamp: toonily turns "N ago" into `UtcNow - delta`, and naverwebtoon gives today's episodes today's date.
- Two give update rather than release times: comicwalker (`updateDate`) and mangakakalot.
- The rest parse a published absolute date.

Nothing in Maki decides anything from these dates today. They show in the chapter table and go into ComicInfo, and `Chapter.ReleaseDate` is first-writer-wins (`ChapterSyncService.cs:142`), not priority-ordered.

**MANGA Plus is the odd one out.** It is the most regular publisher in the set and Maki stores no date for it at all (`MangaPlusSource.cs:167`). Its `title_detailV3` protobuf is widely reported (by the Tachiyomi/Mihon extension) to carry a per-chapter start timestamp and a title-level next-chapter timestamp. **Unverified for Maki's reader**, but if true it is the one source that states its next release outright. See the open questions.

**Maki's own history gives no first-seen times.**
- `Chapter` has no created or discovered timestamp.
- Chapter inbox rows are per series per refresh, and `.simulated` holds none.
- `DownloadQueue.QueuedAt` and `StatsEvents.ChapterDownloaded` record download time. On `.simulated` that is one manual batch of 100 and backfilled stats days (430 events on 2026-07-21), not discovery.

So for the five undated sources, Maki has nothing to go on today, and needs to start recording first-seen time (section 7).

## 3. How regular are releases?

Walk-forward backtest over the last 365 days, per mapping. "Rel" is ±max(1 d, 25% of interval).

| Kind | Mappings | Backtested (predictions) | Median interval | Median MAD | ±1 d | ±2 d | ±3 d | ±7 d | Rel | Mappings with rel ≥ 60% |
|---|---|---|---|---|---|---|---|---|---|---|
| Official | 4 | 1 (17) | 10 d | 1 d | 76% | 76% | 94% | 100% | 76% | 1/1 |
| Scanlator | 47 | 17 (272) | 17 d | 4.5 d | 34% | 43% | 49% | 62% | 48% | 5/17 |
| Aggregator | 123 | 52 (1,112) | 13 d | 4.8 d | 42% | 50% | 58% | 79% | 49% | 13/52 |
| All | 178 | 71 (1,417) | 14 d | 4.5 d | 41% | 48% | 56% | 76% | 49% | 19/71 |

Per source, aggregated over mappings with at least 5 predictions:

| Source | Backtested (predictions) | Median interval | ±1 d | ±3 d | Rel | Rel ≥ 60% |
|---|---|---|---|---|---|---|
| weebcentral | 21 (521) | 11 d | 55% | 65% | 62% | 10/21 |
| mangadex | 17 (272) | 17 d | 34% | 49% | 48% | 5/17 |
| mangafire | 28 (532) | 13 d | 33% | 52% | 39% | 3/28 |
| topmanhua | 2 (43) | 13 d | 16% | 37% | 30% | 0/2 |
| mangakakalot | 1 (16) | 27 d | 12% | 44% | 25% | 0/1 |
| webtoons | 1 (17) | 10 d | 76% | 94% | 76% | 1/1 |

What this says:
- **Kind is a poor predictor; the date field is a good one.** MangaDex, the "scanlator" source, is *less* predictable than WeebCentral, an aggregator, because it carries every group's upload with each group on its own cadence. MangaFire's re-stamping wrecks its own series: *Imaizumi* scores 83% on WeebCentral and 0% on MangaFire.
- **The same series scores differently per source.** A prediction must therefore come from a chosen date source, not from all of them mixed.
- **Series level is no better than the best source.** Taking the earliest date across all sources gives 50% rel, 57% ±3 d over 35 series. Dropping mangafire and topmanhua lifts it to 60% rel, 57% ±2 d.
- **Errors are symmetric and long-tailed.** Series-level error percentiles (days, positive = later than predicted): p5 -14, p25 -2, p50 0, p75 +3, p95 +20. The model is unbiased, just often wrong by a lot.
- **Regime changes are common.** Of 961 series-level gaps in the year, 94 were under a third of the trailing median (a burst, catch-up or batch) and 53 were over three times it (a break).

### Confidence gating (what (B) could show)

Series-level union of trusted sources only (mangafire, topmanhua and Suwayomi left out); 751 predictions:

| Gate (computed from history only) | Coverage | ±1 d | ±2 d | Rel | Later than +7 d |
|---|---|---|---|---|---|
| none | 100% | 51% | 57% | 60% | 14% |
| MAD/median ≤ 0.15, ≥ 6 gaps | 59% | 68% | 72% | 73% | 6% |
| MAD/median ≤ 0.2, ≥ 6 gaps | 67% | 65% | 69% | 70% | 8% |
| **MAD ≤ 1 d, ≥ 7 gaps, interval 5 to 35 d** | 58% | 71% | 75% | 73% | 5% |
| MAD = 0, ≥ 7 gaps, interval 5 to 35 d | 32% | 71% | 72% | 72% | 5% |

Coverage is of *predictions*, which over-weights frequent releasers. Measured in series instead, the bold gate passes **13 series today**: those with a release in the last 60 days, at least 8 release days, and a median gap of 5 to 35 days. That is 13 of the 48 series that released in the last 90 days, nearly all weekly or fortnightly. The ceiling of about 72% does not move with a tighter gate, which suggests the remaining misses are breaks and bursts that no history-only model can see coming.

## 4. Prior art

**Mihon 0.21** rewrote this on 2026-10-11 ([v0.21.0 notes](https://github.com/mihonapp/mihon/releases/tag/v0.21.0); code in [ReleaseSchedule.kt](https://raw.githubusercontent.com/mihonapp/mihon/v0.21.2/domain/src/main/java/tachiyomi/domain/manga/interactor/ReleaseSchedule.kt), [ReleasePattern.kt](https://raw.githubusercontent.com/mihonapp/mihon/v0.21.2/domain/src/main/java/tachiyomi/domain/manga/interactor/ReleasePattern.kt) and [FetchInterval.kt](https://raw.githubusercontent.com/mihonapp/mihon/v0.21.2/domain/src/main/java/tachiyomi/domain/manga/interactor/FetchInterval.kt); I checked the constants against the source):
- **History.** The last 10 distinct release days (3 for series with 8 chapters or fewer), from `dateUpload`. It falls back to `dateFetch`, its own first-seen time, when fewer than 3 upload days exist, and to a 7-day default after that. Future-dated days are dropped, as are chapters from excluded scanlators.
- **Interval.** The median gap, clamped to 1 to 28 days. v0.21 adds pattern detection (batches, monthly by date or weekday, twice monthly, fixed weekdays) and a `lateness` allowance of up to half an interval.
- **Grace and backoff.** A ±1 day grace window. Past 4 intervals of silence, checks double up to 28 days.
- **What the updater does with it.** The library update skips a title whose `nextUpdate` is beyond tomorrow ("Predict next release time" restriction, on by default). It still checks only once a day: Mihon's floor is the daily library job, not minutes.
- **Upcoming tab.** A month calendar plus a list grouped by date, driven only by `nextUpdate`, for favourites that are ongoing or publishing-finished. It has **no confidence filter**: a series with no usable history appears on its 7-day default. The [FAQ](https://mihon.app/docs/faq/updates/upcoming) disclaims it, saying it does not promise a chapter on a given day and that wrong source dates, hiatuses and batches throw it off.
- **Complaints.** [#214](https://github.com/mihonapp/mihon/issues/214): MANGA Plus weekly titles read as monthly because only the first and last chapters are listed. [#568](https://github.com/mihonapp/mihon/issues/568): a prediction-gated check lands just before an upload and then misses it for a whole interval. [#2528](https://github.com/mihonapp/mihon/issues/2528): let extensions supply the schedule. One fork (Kahon) turns smart update off by default because users never saw why updates were skipped (from a search snippet, unverified).

**Uchiyomi** is a self-hosted server ([repo](https://github.com/AngeloSha/uchiyomi), [CHANGELOG](https://raw.githubusercontent.com/AngeloSha/uchiyomi/main/CHANGELOG.md)), not a Mihon fork:
- **Release radar.** Estimates only for a series with 4 or more evenly spaced releases that is still running, falling back to arrival times when the source has no dates. It is always worded as an estimate: "~Friday", "usually Fridays", "due any day", "running late".
- **Smart checks.** Off by default. Quiet series are checked less often but at least every 3 days, and a series is checked normally while it is due. The author claims about half the requests; that figure is unverified, and the thresholds are not documented.

**Sonarr** polls one RSS feed for everything (default 15 min) and does no per-series prediction. Its calendar works because TV air dates come from metadata, not inference. Manga has no equivalent.

**MangaBaka gives nothing to seed a prediction.**
- The dump and DTOs carry `status` (releasing, completed, hiatus, cancelled), `total_chapters`, `final_volume`, `year` and `published_start_date` (`MangaBakaDtos.cs`, `MangaBakaDumpService.cs:241`).
- There is no frequency, schedule, magazine cadence or last-chapter date.
- `status` is still useful for (A): `Series.Status` is already the gate in `RefreshableSeriesIdsAsync`, and hiatus or cancelled is a fair reason to start in the quiet tier.

**What to take from the prior art.** Mihon's own users report the failure modes measured here. Uchiyomi's design, a gate plus hedged wording plus a cheap floor, is the right shape. And neither project polls more often than every few hours, so neither had to solve a 30-minute floor.

## 5. Payoff for (A)

**What runs today** (`Program.cs:1078`, `RefreshMonitoredSeriesJob.cs`, `ChapterSyncService.cs:42-175`):
- The job fires every 30 minutes, `DisallowConcurrentExecution`, 3 series in parallel, in shuffled order.
- Each run takes every series that has an enabled mapping and is not (Completed and caught up with MangaBaka's total).
- For each, `ChapterSyncService` calls `ListChaptersAsync` uncached on **every** enabled, globally enabled mapping.
- There is no per-series or per-mapping interval, and nothing reads `SourceMapping.LastRefresh` to skip one.

**Request cost per list call** (from the sources):
- One request for most sources.
- MangaDex: one per 500 feed items. Mangakakalot: one per 100. Naver: one per 20. WEBTOON's HTML fallback: one per 10.
- **MangaFire: a headless Chromium page load** plus captured API calls and pager clicks, behind FlareSolverr clearance and one scrape at a time.
- Mangakakalot and others on the shared `challenge-fetcher` go to FlareSolverr only when challenged.

**`.simulated` with sources enabled:**

| | Value |
|---|---|
| Refreshable series | 74 (42 ongoing, 29 completed but behind MangaBaka's total, 3 hiatus) |
| Mapping list calls per pass | 218 (≈221 HTTP requests) |
| Per day at 48 passes | **10,464 list calls, ≈10,600 requests** |
| Of which MangaFire headless scrapes | 2,448 per day (51 mappings × 48) |
| Of which FlareSolverr-path sources (mangafire, mangakakalot) | 2,784 per day |
| Series whose newest dated chapter is > 60 days old | 32 of 74, holding 93 of the 221 requests per pass |

The 29 "completed but behind" series include finished titles whose sources may never reach MangaBaka's count; today they are refreshed 48 times a day indefinitely.

**Simulation.** Every trusted-union release in the last year was replayed against each policy. Latency is the time from the source's timestamp to the first check at or after it.

| Policy | Checks / series / day (active, 34 series) | Latency p50 / p90 / p99 / max (h) | Checks / series / day (dormant, 29 series) |
|---|---|---|---|
| Fixed 30 min (today) | 48 | 0.27 / 0.46 / 0.49 / 0.5 | 48 |
| Flat 2 h | 12 | 0.86 / 1.75 / 1.96 / 2.0 | 12 |
| Flat 6 h | 4 | 2.9 / 5.5 / 5.9 / 6.0 | 4 |
| Prediction window (30 min inside the expected window, 6 h outside, 24 h when quiet) | 19.1 | 0.36 / 4.1 / 9.2 / 23.9 | 9.8 |
| **Tiered backoff 2 h / 6 h / 24 h** (see section 6) | **8.5** | **1.0 / 4.5 / 18.6 / 23.9** | **1.9** |
| Tiered + prediction boost (30 min from 12 h before to 36 h after the expected day, gated series only) | 9.6 | 0.49 / 4.4 / 18.0 / 23.9 | 1.9 |
| Tiered 1 h / 4 h / 12 h | 15.9 | 0.59 / 3.1 / 8.7 / 11.9 | 3.6 |

Applied to the 221-request pass, by each series' current tier:
- Tiered 2 h / 6 h / 24 h comes to **≈1,300 requests per day, about 12 to 13% of today's 10,600**, with the hot-period floor included.
- MangaFire and Mangakakalot calls fall from 2,784 to about 370 per day.

**Worst case for a series that suddenly speeds up** (a synthetic burst replayed against the policy). The first chapter after a quiet spell waits at most one tier interval:
- up to 6 h if the series was quiet 14 to 60 days;
- up to 24 h if quiet longer.

That first find puts the series back in the hot tier, so the rest of a daily burst are each found within 2 h (1.4 h in the replay). The 24 h tail hits only a series coming back from a two-month gap, which is the case where a day's delay matters least.

**Prediction adds almost nothing to (A).** The window policy is about as fast as a flat 2 h at p90 and costs over twice the requests. The prediction boost improves the median by half an hour for 13% more checks. With predictions right only about half the time, the backoff alone captures the saving.

## 6. Recommendation (A): tiered backoff

**Algorithm.** Per series, `next = last checked + interval`, where the interval comes from the age of the newest chapter Maki has *discovered*. Use the newer of the source's date and Maki's own first-seen time, so undated sources work as well.

| Age of newest chapter | Interval | Notes |
|---|---|---|
| < 6 h | 30 min (floor, today's cadence) | Catch fixes, double drops and batch tails |
| ≤ 14 days | 2 h | Weekly and fortnightly series live here |
| ≤ 60 days | 6 h | Monthly series and short breaks |
| > 60 days, or status Hiatus/Cancelled, or Completed but behind | 24 h (ceiling) | |
| Never refreshed, or no dates and no first-seen yet | 6 h | Until first-seen history accrues |

Overrides:
- A manual refresh (`SeriesController.cs:208`), an add, a re-match or health recovery always syncs at once and records a check.
- A sync that **fails** keeps the series' current interval rather than resetting, so a broken source is not hammered at 30 min.
- A per-series override (Auto / Always fast / Slow) is cheap to add and answers the "I need this one the moment it drops" case.

**Job shape.** Keep the 30-minute Quartz tick. Each tick picks only the series with `NextCheckAt <= now`, in order of how overdue they are rather than shuffled. The job then does a fraction of the work on a typical tick, and its pass time depends on how much is due rather than on library size.

**Data model.**
- `Series.NextCheckAt` (DateTime?, indexed) and `Series.LastCheckAt`. Or put them on `SourceMapping` if per-mapping scheduling is wanted later (see the open questions). `SourceMapping.LastRefresh` exists already but is per mapping and is not read for scheduling.
- **`ChapterSourceLink.FirstSeenAt`** (DateTime, set when the link row is created, never updated), plus `Chapter.DiscoveredAt` as the minimum over its links.
  - The first sync of a new mapping stamps its whole back catalogue at once. Treat a link created in the same sync as the mapping's first snapshot (or in a burst over `monitoring.bulkholdthreshold`) as backfill, and exclude it from cadence.
  - This is also what makes (B) possible for MANGA Plus, Asura, MangaPill, Atsumaru and TCB.
  - No migration can backfill it. History starts on deploy.
- Optional, a settings key such as `monitoring.adaptiverefresh` (default on) with the tier intervals as constants, not knobs. If knobs are wanted, expose only the ceiling.

**Where it shows.** The Sources section of the series page (`SourceMappingsSection.tsx:528`) already shows each mapping's last refresh. Add "next check in N h" next to it, with a tooltip naming the tier ("no new chapter in 23 days"). This answers the Kahon complaint: users can see why a series is checked less often. Activity needs nothing.

**What it does not change.** Smart download, the bulk hold, enqueue and notifications all hang off `RefreshSeriesAsync` and are untouched. Only the selection in `RefreshableSeriesIdsAsync` and the stamping in `ChapterSyncService` change.

**Before shipping**, re-run this measurement on a library with real first-seen data (two to four weeks of `FirstSeenAt`), and on the owner's real library size, since 74 series understates the FlareSolverr load a 500-series install sees.

## 7. Recommendation (B): cadence line, gated date, no calendar

**Show a factual pattern, not a promise.** On the series page, near the chapter count or in the Progress panel:
- "New chapters usually every 7 days. Last: 3 days ago."

This is true about the past whatever the future does, so it cannot erode trust. Show it for any ongoing series with at least 4 release days in the last 180 days. It answers most of what "upcoming" is for.

**Add an expected date only behind the gate.**
- **Gate:** at least 8 release days, at least 7 gaps, MAD ≤ 1 day, median gap between 5 and 35 days, last release within 2 × median, and the dates come from a trusted source.
- **Trusted** means real release times: Official sources, MangaDex and WeebCentral, or Maki's own `FirstSeenAt` once it exists. MangaFire, TopManhua, Mangakakalot (`updated_at`), Toonily and Suwayomi are left out.
- **Wording when the gate passes:** "Next expected around Fri 24 Oct", always "around", never a countdown.
- **Past the expected day plus the MAD allowance:** "Running late" (Uchiyomi's word).
- **More than 2 intervals late:** drop the date and fall back to the cadence line. No date is better than a stale one.
- **On this library** that is 13 series showing a date. On past data it lands within ±2 days 75% of the time and more than a week late 5% of the time.

**No Upcoming calendar or Activity tab for now.** A calendar for 13 of 74 series looks broken, and filling it the way Mihon does (defaults for everything) is exactly the trust problem. Home has no discovery rails by owner decision, and a "releasing this week" list there would be a new rail. If anything list-shaped is wanted later, a Library sort ("Expected soon", gated series first, the rest by cadence) is the least invasive place. Revisit once `FirstSeenAt` has a few months of data and a re-measure shows gated precision of 80% or more at ±2 days.

**Data model.** Nothing new beyond (A)'s `FirstSeenAt`. Compute the cadence on read for one series (cheap: 8 to 10 dates). If a Library sort is added, cache `Series.CadenceDays`, `CadenceMad` and `ExpectedAt` at sync time.

**i18n.** Every string goes through `t`/`<Trans>`. Day names come from `Intl`, never from a hand-built list.

## 8. Open questions for the owner

1. **Is a 2-hour worst case acceptable for an active series?** It is today's 30 minutes made four times slower for weekly titles. The alternative is a 1 h hot tier (1 h / 4 h / 12 h), at roughly a quarter to a third of today's traffic instead of 13%.
2. **Per-series or per-mapping scheduling?** WeebCentral is first for 65% of shared chapters and MangaDex trails the earliest source by under 1 h at the median and 2 h at p90. A per-mapping scheme could poll the fastest mapping on the hot tier and the rest on the warm tier, cutting further. It is more complex, and the downloader already resolves across mappings. Recommendation: per series first.
3. **Should MANGA Plus dates be wired up?** Its `title_detailV3` reportedly carries chapter start timestamps and a next-chapter timestamp (not verified against Maki's protobuf reader). If it does, MANGA Plus series could show a *stated* date rather than an inferred one, the only honest calendar entry available.
4. **The 29 "Completed but behind MangaBaka" series.** Should they keep refreshing at all, or move to the 24 h tier for good? The tiered scheme puts them there once they are 60 days quiet anyway.
5. **Wording and placement of the cadence line.** Progress panel or chapter header? It must not crowd the series page layout the owner has already settled.
6. **Is a per-series "Always fast" override wanted,** or is the manual refresh button enough?

## Appendix: reproducing the numbers

The analysis scripts are in this session's scratchpad (`intervals.py`, `series_level.py`, `gate_and_sim.py`, `sim2.py`, `sim3.py`, `sim4.py`, `requests.py`), not in the repo. Each opens its database copy with `mode=ro`.
- **Backtest:** distinct release days per mapping, a median of up to 8 previous gaps, scored over the 365 days before 2026-10-12.
- **Simulation:** each policy is stepped through the same 365 days, and a release counts as found at the first check at or after its timestamp.

## Decisions and what shipped

The owner's answers (2026-10-20), and how each landed:

1. **A 2-hour worst case is acceptable.** Tiers ship as recommended: 30 minutes for 6 hours after a new chapter, 2 hours up to 14 days, 6 hours up to 60 days, 24 hours after that (`RefreshSchedule`). Hiatus, cancelled and completed series drop to 24 hours once 14 days pass without a chapter; a chapter puts any series back on the fast tiers whatever its status. The job wakes every 15 minutes and syncs only due series, most overdue first.
2. **Per series.** One `Series.NextCheckAt`. Per-mapping scheduling stays an option for later if FlareSolverr load still matters.
3. **MANGA Plus is wired up.** Checked against the live API: `TitleDetailView` field 5 is the next chapter's time (One Piece read 1 Nov 15:00 UTC, the week after a break) and `Chapter` field 6 is when each chapter opened. Chapters now carry those dates, the announcement is stored per mapping (`SourceMapping.AnnouncedNextReleaseAt`), the check is scheduled five minutes after it, and the series is watched every 30 minutes for six hours past it until the chapter shows. It is the one date Maki shows; everything else is a span.
4. See below.
5. **An Upcoming view after all, as spans with a confidence, never a marked date.** It is a tab in Activity: a five-week day grid with one bar per series over its likely window, solid for high confidence and lighter for medium, a diamond on any day a source announced, and separate "Running late" and "Further out" lists. Phones get the same series as week-grouped lines. Irregular, stalled and history-less series are left out rather than guessed at. The series page shows the same outlook in the sources panel ("Next chapter"), with the cadence line, the confidence and when Maki checks next.
6. **No per-series override** for now; the manual refresh is there. Off switch: Settings, Downloads, "Check quiet series less often" (`monitoring.adaptiverefresh`), which restores 30 minutes for every series.

**The confidence is the series' own track record.** For each of its last ten releases, `ReleaseCadence` draws the window it would have drawn from the history before it (median gap of up to ten, half-width the larger of one day, the MAD, or a fifth of the gap) and counts whether the release landed in it. The shown figure is that hit rate smoothed toward one half, `(hits + 1) / (tested + 2)`, so three lucky hits read 80%, not 100%. Below 0.55 (more than half the tested releases must hit), with fewer than three tested releases, a gap over 90 days, or a window wider than two thirds of the gap, there is no window.

**Which dates count.** A chapter's time is the earliest of Maki's own first sighting (`ChapterSourceLink.FirstSeenAt`, new) and the dates of sources whose `ReleaseDatesAreReliable` holds. MangaFire, TopManhua, MangaKakalot, Toonily, ComicWalker and every Suwayomi source are marked unreliable, from the measurements in section 2 and their code. First sightings start at deploy: no migration can backfill them, and a mapping's first snapshot or a burst of more than 20 new links is never stamped. Undated sources (MangaPill, Asura, Atsumaru, TCB) therefore gain a cadence only as Maki watches chapters arrive.

### Question 4, the "completed but behind" series

`RefreshMonitoredSeriesJob` skips a series only when it is Completed **and** holds a chapter numbered at or above MangaBaka's total. On `.simulated` 29 of the 74 refreshed series are Completed without reaching that number. They fall into three groups:

- **Still being translated.** The original run finished but the scanlation is catching up: *A Boy Who Was Just a Friend for Ten Years* (37 of 59, last chapter 3 Oct), *Hey, Wanna Go Out with Me?* (48 of 62, 2 Oct), *Semantic Error* (94.5 of 109, 29 Sep). These are active series and must keep the fast checks. Status alone would have been the wrong signal, which is why activity overrides status in the tiers.
- **Done, but the numbers never meet.** The source's last chapter sits a little under MangaBaka's count because of numbering or extras: *Heesu in Class 2* (89.2 of 90), *Ayakashi Triangle* (144.5 of 146), *Call of the Night* (201.5 of 203), *Pandora Hearts* (104 of 107). Nothing new is coming, but the job cannot tell.
- **Stuck for good.** Licensed or dropped translations: *Someday I'll Fall for You* (7 of 10, last chapter in 2019), *Sarara Urushiha Is Not Into Romance* (8 of 33, 2024), *Dekoboko Sugar Days* (7 of 8, 2024).

Under the old schedule all 29 were listed 48 times a day forever. Under the tiers the first group stays fast, and the other two drop to once a day after 14 quiet days. A daily check on a dead series is cheap (one listing per source), and it still catches the occasional revival or late upload. Possible next steps, none built:

- a further tier, say weekly, for a completed series quiet for over a year;
- treating "Completed and within one chapter of the total" as caught up, which would stop the second group outright but would miss a real final chapter numbered past the total;
- a per-series "stop checking" switch.
