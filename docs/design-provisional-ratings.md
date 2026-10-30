# Provisional ratings ("so far" scores)

Proposal written 2026-10-30, reviewed the same day against the code. Phase 1 is in progress on `feat/provisional-ratings`; see "Implementation status" at the end. The points marked **Owner call** need a decision before work starts.

## The problem

A rating today is one number per reader per series (`UserSeriesState.Rating`, 1 to 10). Setting it does three things at once:

- It is pushed to every connected tracker with "Sync ratings" on (`ScrobbleService.QueueRatingPush`, skipped for incognito series).
- It replaces the behavioural seed weight outright. `SeedWeightService.Weigh` uses `rating / 5.0`, so a 10 steers at 2.0 and a 5 sits at the neutral 1.0. `TasteTuning.RatingBlendAlpha` ships at 1, so read depth never softens a rated seed.
- A rating of 4 or under removes the series from the positive seeds and puts it in the avoid channel (`RecommendationFeedbackPolicy.AvoidStrength`).

That is the right weight for a considered verdict on something you finished. It is too much for "I'm 30 chapters into a 300-chapter series and enjoying it". Readers who want to record an early opinion either rate early (and push a half-formed score to AniList at full taste strength) or don't rate at all (and the recommender only has read depth to go on). The finished-series prompt (`RateHint`, de651ebf) only fires when a Completed series is read to its last chapter, which for an ongoing series may be years away or never.

## Recommendation in one paragraph

Add a second, softer score on the same row: a **provisional rating** that means "how it's going so far". It is set with the same stars, labelled "so far", not pushed to trackers unless the reader opts in, and steers recommendations at reduced strength that grows as they read further. It becomes the real rating when they finish (one tap to confirm) or whenever they say so. Ask for it at a handful of natural checkpoints, not per chapter, and never interrupt reading to do it. Don't build per-chapter or per-volume scores as separate entities; keep a small history of provisional scores instead, which gives the "volume 3 was a 7, volume 6 was a 9" story without a second rating system.

## Why not rate each chapter or volume

Chapters are short and get read in runs of five or twenty. A prompt per chapter turns into something people click through to get to the next page, and the data it collects then means nothing. Beyond the fatigue, there are three structural problems:

1. **Nothing downstream is per chapter.** The recommender works on series vectors, trackers store one score per series, and Stats compares your score to the community's per series. A chapter score has to be averaged back into a series score before anything can use it, and a mean of chapter scores is not how people judge a series. A slow first arc followed by something great is a 9, not a 6.5.
2. **Volumes are patchy.** `Chapter.Volume` is often null: webtoons and manhwa have no volumes, many sources don't report them, and ongoing series have a trailing run of chapters with no volume yet. A volume-based design works well for a tankōbon reader and does nothing for half the library.
3. **The reader rarely sees a "between chapters" moment.** `ChapterEnd` only renders when the reader runs off the last page with auto-advance off, or there's no next chapter. With auto-advance on, the natural place for a per-chapter prompt doesn't exist, and adding one would mean interrupting a page turn.

The one per-chapter thing I'd consider is a single tap with no scale, a "loved this chapter" heart on `ChapterEnd`. It costs nothing to skip and gives Rewind something to show ("your favourite chapters this year"). It would not feed taste. That's a separate, optional idea and not part of this proposal's core.

## Data model

**Owner call 1: a separate column, or a flag on the existing one.** I recommend a separate column.

| | Separate `ProvisionalRating` column | `RatingIsProvisional` flag on `Rating` |
|---|---|---|
| Existing readers of `Rating` | Unchanged. Every current consumer keeps meaning "final" with no audit. | Every one must learn to check the flag, or a provisional score gets pushed to AniList or weighted at full strength. Several test `Rating` inside SQL. |
| Final plus provisional at once | Possible, and briefly useful (see "Finishing" below). | Not possible. |
| Migration | Additive, nullable columns. | Same, but with a semantic change to an existing column. |

Current readers of `UserSeriesState.Rating`:

- Backend: `SeedWeightService` (seeds and avoid), `SideInterestRailService` (excludes `Rating < 5`), `StatsStandingService` (rating gap vs community), `RecommendationFeedbackController` (Feedback Lab counts), `ScrobbleService` (push, and the rating import preview), `SeriesController.RateHintAsync`, and the series list DTOs.
- Frontend: the `SeriesHero` stars, `ArchivedSeriesCard`'s stars (wired through `LibraryPage.onRate`; active library cards have no stars), the library `rating` sort, and the library filters (rated/unrated state and the "My rating" range).
- Komga, OPDS, KOReader, Kavita sync and Rewind do not read it.

Writers are `SeriesController.SetRating` and `ScrobbleService.ApplyRatingImportAsync`, which sets `state.Rating` directly rather than going through `SetRating`.

With a separate column, none of the readers change unless we decide they should (decisions below).

On `UserSeriesState`:

- `ProvisionalRating int?` (1 to 10, null = none)
- `ProvisionalRatedAt DateTime?`
- `ProvisionalAtChapter double?`: the highest chapter *number* read when it was set, for the caption "8 so far, at ch. 34". The confidence curve uses read *counts*, not this number, because chapter numbers have gaps and halves.
- `RatingCheckpointsSeen` (flags enum: `Hooked`, `VolumeEnd`, `CaughtUp`, `Leaving`), `LastCheckpointAt DateTime?` and `RatingPromptDismissals int`. See "Asking".

A new table `RatingSnapshots` (`IUserOwned`, so it needs the global query filter): `UserId`, `SeriesId`, `Rating`, `Final bool`, `AtChapter double?`, `AtVolume int?`, `Origin` (`manual`, `checkpoint`, `finish`, `import`), `CreatedAt`. One row per change. This is the per-volume rating people would actually look at, as a history rather than a second system. It can come in a later phase; the columns above are enough to ship.

**Rule: final wins.** When `Rating` is set, by either writer, `ProvisionalRating` is cleared (its value survives in the snapshot history). Clearing a final rating does not resurrect the old provisional one. Two live scores that disagree would leave the series page showing one number while the recommender uses another.

## Taste strength

A provisional score should move the seed less than a final one, and more the further the reader has got. The way to do that is to shrink the rating's distance from neutral by a confidence `c`:

```
weight = 1 + c * (rating / 5.0 - 1)
```

This replaces the behavioural weight for that series, the same way a final rating does today. It deliberately does not go through `TasteWeights.Blend`. Blending toward the behavioural weight looks natural but is wrong: behavioural weight on a long, recently read series saturates at `MaxWeight` 1.8, so a blended provisional 6 or 8 would come out *stronger* than the same score finalised. It would also have nothing to blend with when behavioural weighting is switched off. Shrinking toward neutral makes every provisional score strictly weaker than its final counterpart, whatever the behavioural channel is doing.

`c` runs from 0.35 to 0.8 and never reaches 1:

```
c     = 0.35 + 0.30 * depth + 0.15 * ratio
depth = min(1, log(1 + read) / log(1 + 60))    // same log shape as TasteWeights.Curve
ratio = min(1, read / TotalChapters)  when Status == Completed and TotalChapters is known, else 0
```

Depth carries most of it, so the same ten chapters earn about the same confidence on a finished epic as on an ongoing webtoon, and a metadata refresh that flips `Status` nudges `c` rather than switching formulas. An ongoing series tops out at 0.65 after about 60 chapters; only reading most of a finished series reaches 0.8. Using "read / chapters released" for an ongoing series would give a caught-up reader of chapter 12 the same confidence as someone who finished, which is exactly the reader this feature is for.

`read` is the `ReadCounts.ReadFor` count (genuine reads, no `Watched` rows), so marking 50 chapters watched from an anime doesn't inflate confidence. `Weigh` already has it as `SeriesReadSignal.Completed`, so this adds no query.

What that gives, for an ongoing series (rounded to the 0.01 `WeightQuantum`):

| | ch. 1 | ch. 10 | ch. 60+ | Final |
|---|---|---|---|---|
| 10 | 1.40 | 1.52 | 1.65 | 2.00 |
| 8 | 1.24 | 1.31 | 1.39 | 1.60 |
| 6 | 1.08 | 1.10 | 1.13 | 1.20 |

Low provisional scores go to the avoid channel at `AvoidStrength(rating) * c`, and leave the positive population first, exactly like a low final rating does (the rule in `recommendations.md`: a low row has to leave before `Weigh` runs, or behavioural weighting brings it back as a positive seed on read depth alone). That removal is binary, not scaled: a provisional 4 at chapter 2 takes the series out of the seeds entirely while adding only a small penalty. That is intended. The reader said "less of this", and leaving it in as a positive seed on read depth would contradict them.

Two smaller points in `Weigh`. A recently added unrated series gets the personal-add weight, up to 1.5 (`RecommendationFeedbackPolicy.AddedWeight`); a provisional row takes `max(provisionalWeight, addedWeight)`, the same shape `LikedWeight` uses, so rating something you added yesterday a provisional 10 never makes it steer less than leaving it unrated. A final rating keeps overriding the add weight, as today. Low provisionals have already left before `Weigh` runs, so the max only ever applies to 5 and above. The provisional weight is also computed whether or not behavioural weighting is on.

One limit: **the seed-side weighting has no isolated measurement.** `recommendations.md` notes the eval harness builds its own seed sets and never calls `SeedWeightService`, so the curve above is a judgement call and can't be validated with `run-reco-suite.ps1`. The constants live in `ProvisionalRatingPolicy` (`Maki.Core.Recommendations`), beside `RecommendationFeedbackPolicy`'s, not in `TasteTuning`, which describes the behavioural channel only. Changing them is a code change; check the result on `.simulated`, not `.devconfig`.

The seed snapshot is cached for 60 seconds and keyed on the library, not on rating writes, and the recommender's pool key carries the weights through `SeedSnapshot.Fingerprint`. A provisional change shows up in recommendations within a minute with nothing extra to invalidate.

## Trackers

Don't push provisional scores by default. It's the main reason the feature exists.

Some people do want them pushed: on AniList and MAL it's normal to score a series while its status is still Reading. So add a per-tracker switch "Also sync 'so far' ratings" under the existing "Sync ratings" in `TrackerSyncControls`, default off. When on, a provisional change goes through `QueueRatingPush` like a final one, with the same incognito rule. When off, finalising is the moment the score is pushed. See owner call 3 for how a reader who already rates mid-read finds this switch.

**Owner call 2: the rating import.** `ApplyRatingImportAsync` writes remote scores into `Rating`. Each remote entry also carries its tracker status (`RemoteEntry.Status`: Reading, Completed, PlanToRead, Other). Options:

1. **Route by status (my pick).** A score on a Reading entry imports into `ProvisionalRating`; on Completed or anything else, into `Rating`. That matches what the score meant on the tracker.
2. Always import into `Rating` (today's behaviour), and say "replaces your 'so far' 7" in the preview row.
3. Always import into `ProvisionalRating` when the reader hasn't finished it in Maki.

Whichever it is, two fixes are needed: an import into `Rating` must clear `ProvisionalRating` (final wins), and the preview's "has this changed" test (`entry.Score != localRating`, which compares against the final only) must compare against the score the reader currently sees, so a provisional 7 against a remote 7 isn't listed as a change.

## UI

### Stars everywhere

There are two star controls: `SeriesHero` and `ArchivedSeriesCard` (in the Library's Archived view). Both route through one shared rate helper (`useRateSeries`, with the rule in `frontend/src/lib/rating.ts`) that decides provisional or final by the reader's progress. Otherwise a part-read series rated from a card gets a final rating, and the default below only holds on the series page.

- **Finished, or nothing read yet:** stars set the final rating, as today. (Rating something you haven't opened is a deliberate verdict, usually a re-read or a series read elsewhere.)
- **Part-read, no final rating:** stars set the provisional rating. It renders with muted stars and a caption "so far, at ch. 34". On the series page a check button, "Make this my rating", sits beside the clear button and finalises in place.
- **Final rating set:** as today.

**Owner call 3: the default on a part-read series.** The above flips the default from final to provisional for part-read series. With the tracker switch off by default, a reader who rates mid-read today and relies on it reaching AniList would stop seeing those scores there, and for an ongoing series "finished" may never come. Options:

1. **Provisional by default, plus a one-time ask (my pick).** The first time a reader sets a provisional score while a rating-syncing tracker is connected, ask once, inline: "Send 'so far' scores to AniList too?" The answer sets the per-tracker switch. Nobody is surprised, and nobody has to find the setting.
2. Provisional by default, switch off, no ask. Simplest, but it silently changes what mid-read rating does.
3. Final by default, with a "Just so far" choice in the menu. No surprise, but most people will never discover the provisional option.

Existing ratings are all final and stay final; nothing is migrated.

### Finishing

`RateHint` becomes the finalising step. With a provisional score it reads "You had this at 8 so far. Keep it?" with the 8 pre-filled, a Keep button that finalises in one tap, and the stars still live for a different number. Without one, it's unchanged.

### Library

The card badge and the `rating` sort use the final rating, falling back to the provisional one shown in the "so far" style. Sorting provisional scores to the bottom would hide most of the opinions in a library heavy on ongoing series. The "Rated" filter includes provisional scores, and the "My rating" range applies to whichever score the card shows.

## Asking, without nagging

The prompt is the part most likely to go wrong. The constraints I'd hold to:

- **Never interrupt reading.** No modal, no toast mid-chapter. A prompt shows only on a screen the reader is already stopped on: `ChapterEnd` when it renders, or the series page (like `RateHint`).
- **Armed by reading, not by state.** Checkpoints are only evaluated on a genuine chapter completion, in `ReaderService.OnChapterCompletedAsync` (native reader or OPDS; bulk mark-read deliberately skips it), and counted with `ReadFor` except where noted. Evaluating them on a page load would arm every series at once after a Kavita or Mihon import, or after marking chapters watched from an anime. Each checkpoint is a condition checked on every such completion ("threshold reached and its flag not set"), not a one-off crossing, so a checkpoint the daily cap held back arms on that series' next completion instead of being lost.
- **One tap to answer, zero taps to ignore.** Stars inline, pre-filled with the current provisional score, and a "Still a 7" button. Ignoring it is not a dismissal; only the X is.
- **Back off.** Three dismissals in a row on one series and it stops asking on that series; any answer resets the count. At most one new checkpoint is armed per UTC day across the whole library, so a binge across six series doesn't leave six prompts waiting; the "last armed" stamp is a per-user setting. A user setting turns checkpoints off entirely.

The checkpoints, in order of how much I'd trust them:

| Checkpoint | Fires when | Why it's a good moment |
|---|---|---|
| **Caught up** | The `ReadCounts.Read` count (the number `ChapterEnd` shows, which includes `Watched` rows) reaches the wanted chapter count on a series whose status is not Completed. This one compares position, not evidence, so it uses `Read` rather than `ReadFor`; otherwise a reader who marked 150 chapters watched from the anime and read the next 50 would never be caught up. Not merely "no next downloaded chapter": a download gap is not caught up, and `ChapterEnd` already tells those apart ("N more chapters are wanted"). | They're about to wait days or weeks, and `ChapterEnd` usually renders here because there's nothing to advance to, so this works with auto-advance on. (Finishing a gap chapter out of order is the exception; the series page picks it up.) Re-arms on later catch-ups only when the score is stale (below). Never overlaps Finished, which requires Completed. |
| **Hooked** | `ReadFor` is past about 10 chapters (or 15% of a known total, whichever comes first) on a series with no rating of either kind. Once per series. | The "is this working for me" point. Early enough to be useful to the recommender, late enough to have an opinion. |
| **Volume end** | The completed chapter is the last of its volume, its chapters carry `Volume`, and the score is stale. | A natural break for tankōbon readers. The staleness gate stops it firing on every volume of a 100-volume series. |
| **Leaving** | The reader hides a part-read series from Home (`HiddenFromHomeAt`, per user), or archives it. | Offers a *final* rating, since "dropped at ch. 80, it's a 4" is a real verdict. Archive is a library-wide action on the shared row, so the stars in `ArchiveSeriesDialog` rate for the archiver only and show only when the archiver has read some of it. In multi-user, hide-from-Home is the per-reader version of the same moment. |
| **Finished** | Existing `RateHint`. | Finalises. Exempt from the arm-on-completion rule and the daily cap: `RateHintAsync` is a query on finished state, and reaching the end of a series is rare enough not to need throttling. |

**Stale** means no provisional score yet, or one set more than 30 days ago, or one set before the reader read another 25% of the series' wanted chapter count.

The checkpoint to show is worked out on the server and sent as a nullable `ratingCheckpoint` on the reader manifest and on `SeriesDto`, so `ChapterEnd` and the series page ask the same question. A checkpoint armed while auto-advance skipped `ChapterEnd` waits on the series page instead of being lost. Readers using OPDS, Komga or KOReader never see `ChapterEnd`; OPDS completions still arm checkpoints, and the series page is where they're asked.

**Owner call 4:** whether checkpoint prompts are on by default. I'd ship them on with only Caught up and Hooked enabled, and Volume end opt-in, then see if it feels like too much.

## What a provisional score does and doesn't touch

- **Stats' rating gap** (your score vs the community): final only, which needs no change. A "so far" score compared against a community verdict on the whole series isn't a fair comparison.
- **`SideInterestRailService`'s below-neutral exclusion**: should include provisional scores under 5. Someone who says "a 3 so far" shouldn't get a rail built around that series' tags.
- **Feedback Lab**: show provisional scores as their own count, so the taste explanation can say how much of it rests on unfinished opinions.
- **Rewind**: nothing at first. Later, the snapshot history allows "series you changed your mind about this year", which is the most interesting thing this data could produce.

## API sketch

- `PUT series/{id}/rating/provisional` `{ rating: int | null }`. Separate from `PUT series/{id}/rating` so the existing endpoint keeps its behaviour (pushes to every tracker with "Sync ratings" on, unless incognito). Pushes only where the "so far" switch is on, with the same incognito rule.
- `POST series/{id}/rating/finalize`. Copies the provisional score through the existing `SetRating` path, so the scrobble push and everything else runs exactly as for a normal rating.
- `POST series/{id}/rating-checkpoint/dismiss` `{ checkpoint }`.
- `SeriesDto` and the reader manifest gain `provisionalRating`, `provisionalAtChapter`, `ratingCheckpoint`.

The new mutating endpoints need entries in `ControllerAuthorizationInventoryTests` (`auth.md`), and need no permission beyond being signed in, like `SetRating`. All new copy goes through `t`/`<Trans>`. The new `TasteTuning` constants get a bullet in `recommendations.md` next to `AvoidRatingCeiling`.

## Phases

1. **Columns, endpoints and seed weighting.** Provisional score settable from all three star controls, "so far" styling, finalise from the series page menu and from `RateHint`, library sort and filters, the import fixes. No prompts, no tracker switch yet (so provisional never syncs). This alone solves the core problem.
2. **Checkpoints.** Caught up and Hooked on `ChapterEnd` and the series page, with dismissal, back-off and the daily cap. Leaving stars on hide-from-Home and in the archive dialog.
3. **Tracker switch** and the one-time ask.
4. **Snapshot history.** `RatingSnapshots`, a small "your rating over time" line on the series page, the Volume end checkpoint.
5. Optional: chapter hearts on `ChapterEnd`, Rewind uses.

## Open questions for the owner

1. Separate `ProvisionalRating` column (recommended) or a flag on `Rating`?
2. Rating import: route by tracker status (recommended), always final, or always provisional for unfinished series?
3. Default for stars on a part-read series: provisional with a one-time tracker ask (recommended), provisional without it, or final?
4. Checkpoint prompts on by default, and which ones?
5. Is "so far" the right label? Alternatives: "for now", "provisional", "early rating". "So far" reads most naturally next to a chapter number.

## Implementation status

Phase 1 on `feat/provisional-ratings`, committed 2026-10-30:

- `UserSeriesState.ProvisionalRating`, `ProvisionalRatedAt`, `ProvisionalAtChapter` and the `ProvisionalRating` migration.
- `PUT series/{id}/rating/provisional`, `POST series/{id}/rating/finalize`; `SetRating` clears the provisional score. `SeriesDto` carries `provisionalRating`, `provisionalAtChapter` and `finished`.
- `ProvisionalRatingPolicy` and its use in `SeedWeightService`; `SideInterestRailService` excludes provisional scores under 5.
- The rating import clears the provisional score when it writes a rating, and its preview compares against the shown score. It still writes every remote score as final: owner call 2 (route by status) is not built.
- Frontend: `useRateSeries`, so-far styling and finalise on the series page, the pre-filled `RateHint` with a Keep button, the archived card's stars, library sort and filters on the shown score.

Phase 2, committed 2026-10-30:

- `UserSeriesState.PendingRatingCheckpoint`, `RatingCheckpointsSeen` (`RatingCheckpoints` flags: `Hooked`, `CaughtUp`), `LastRatingCheckpointAt`, `RatingPromptDismissals`, and the `RatingCheckpoints` migration.
- `RatingCheckpointService` arms Hooked and Caught up from `ReaderService.SaveProgressAsync`. The daily cap reads the latest `LastRatingCheckpointAt` across the reader's rows rather than a separate setting. Caught up re-arms at most every 30 days per series, and only while the score is stale. Staleness by chapters compares the highest chapter number read against `ProvisionalAtChapter`, since that number is what the score records.
- Checkpoints only arm without a final rating, and a Full incognito series never arms one.
- `GET series/{id}/rating-checkpoint`, `POST series/{id}/rating-checkpoint/dismiss`; `SeriesDto.RatingCheckpoint` on the detail endpoint; answering is any rating write.
- Per-user switch `ratings.prompts` ("off", unset = on): `GET/PUT settings/rating-prompts` and a "Rating prompts" card in Settings > Reading.
- Frontend: `RatingCheckpointPrompt` on the reader's end screen and the series page, and optional stars in `ArchiveSeriesDialog` that set a final rating when the archive goes through (series page only).

Not started: the Feedback Lab count, stars on hide-from-Home (a one-click menu action today, so it needs its own design), the tracker switch and one-time ask (phase 3), Volume end and snapshots (phase 4).
