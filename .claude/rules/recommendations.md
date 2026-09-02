---
paths:
  - "src/Maki.Api/Services/*Recommendation*.cs"
  - "src/Maki.Api/Services/*Rail*.cs"
  - "src/Maki.Api/Services/*Discover*.cs"
  - "src/Maki.Api/Services/*Taste*.cs"
  - "src/Maki.Api/Services/SeedWeight*.cs"
  - "src/Maki.Api/Services/ReaderCohort*.cs"
  - "src/Maki.Api/Jobs/ReaderCohort*.cs"
  - "src/Maki.Api/Controllers/Recommendation*.cs"
  - "src/Maki.Api/Controllers/Settings*.cs"
  - "src/Maki.Metadata/Taste/**"
  - "src/Maki.Metadata/ReaderCohorts/**"
  - "src/Maki.Core/Recommendations/**"
  - "distribution/**"
---

# Recommendations, taste, home layout

Migrated out of the root CLAUDE.md so this only loads when touching recommendation/discover/home-layout code.

- **The behavioural channel is a second vector space, not a third graph** (`Maki.Metadata.Taste`, `{ConfigDir}/taste-vectors.db`, kill switch `recommendations.tastevectors`). Item vectors factorized from AniList reading lists, scored as a cosine beside the text one in the same row pass. It reaches ~95k of the ~126k indexed rows against the co-read graph's ~41k, which is the point: a pair table is a lookup and is empty for most of the catalogue. `MangaBakaRecommendation.TasteMatch` is its own "why" and must not be folded into `CoRead` — they answer different questions off the same file, and switching co-read off still costs measurable relevance.
- **`VectorIndexCache` loads the taste artifact, so installing it invalidates the whole index** rather than swapping a file in the way the graph caches do. The scan needs a vector per row, and a dictionary lookup per candidate per query would cost more than the dot product it feeds. That is why `TasteVectorJob` runs last of the four staggered artifact downloads: doing it while the index is still building the first time throws that work away.
- **`Maki.Core.Recommendations.TasteTuning` and `Maki.Metadata.Taste.TasteVectorTuning` are unrelated.** The first is seed *weighting* (how much each of a reader's series steers the profile); the second is the behavioural *channel*. Any file using both needs one of them qualified, and `RecommendationService`'s local is `tasteVectors` because its constructor parameter `taste` is `BehavioralTasteService`.
- **Anything measured only against the crowd pair graphs is suspect in two specific directions** (details and numbers in `distribution/CLAUDE.md`): they over-represent famous titles, and they under-represent *affinity* — same creator, same franchise. A change that suppresses an affinity relationship looks free there and costs real relevance against held-out reading lists. Both biases surface in the eval's `pop` column before they surface anywhere else.
- **Discover's "Based on your recent activity" rail is the one per-user rail on that page** (`RecentActivityRailService`, `GET recommendations/discover/recent`), fetched separately from `GET discover` because those rails are cached once instance-wide with no viewer in scope. It only narrows the seeds — scoring, the owned-series exclusion, the rating clamp and the 12-hour pool cache all stay `RecommendationService`'s, which is also why it costs nothing on a warm pool. Seeds are the 8 most recently read series by `ReadCounts.ReadFor`; `Incognito.Full` is excluded by hand here (its `ChapterProgress` rows exist, and the subtitle names the titles). Its `Feed` is not a `BrowseFeed` — the client branches on `DiscoverRail.SeedIds` to page the recommender instead of `GetFeedAsync`, which rejects the name.
- **The reader-cohort surfaces are a hint, not a second score, and that was a measurement rather than a taste call** (`Maki.Metadata.ReaderCohorts`, `{ConfigDir}/reader-cohorts.db`, kill switch `recommendations.readercohorts`). Groups of AniList readers with their per-group completion and score aggregates; the instance places its own reader locally. A cohort mean beats the plain item mean on held-out readers by 0.18 points on a 100-point scale — real, and 0.018 of a rendered star. Its median distance from that mean is 1.87 points, so shown on every series it is the same digits as the aggregate beside it nine times in ten. `ReaderCohortTuning.MinDivergence` is what turns it into something worth showing; the numbers are in `distribution/CLAUDE.md` under "v5: reader cohorts".
- **The hint compares against the artifact's own all-readers mean, never against the MangaBaka rating rendered next to it.** That aggregate averages several metadata providers; the cohorts are one crowd on one site. On DICE the catalogue says 7.0 and the reader crowd says 6.6, so gating on the visible number would fire the hint on a gap between two populations rather than on anything about the reader.
- **Cohort placement reads FINISHED series, never the shelf** — the same `TasteView.Read` against `TasteView.Shelf` distinction the taste page draws, sharing `BehavioralTasteService.ReadSignalsAsync` so the two cannot drift on incognito or root-folder visibility. Somebody with two hundred unread action titles and forty finished romcoms is a romcom reader.
- **`ReaderCohortCache` is its own cache and must stay one.** Nothing it holds is scanned per catalogue row — placement is one lookup per series the reader finished — so it swaps a file in like the graph caches rather than invalidating the vector index the way `TasteVectorInstaller` has to. Its index is item-major CSR for that reason: the per-request question is "which cohorts carry this series", not "what does this cohort carry".
- **Home sections are user-ordered** (`ui.homesections`, `HomeLayoutSpec`). `Merge` runs on every read/write: unknown keys dropped, new keys appended enabled (never re-slotted) so a release adding a section doesn't scramble the user's ordering.
- **Home's "recently added" rail reads `ChapterFile.DateAdded`**, not `StatsEvents` (which is aggregated to one row/series/day — can't name the newest chapter). Both reading rails do a bounded scan + in-memory group, not an unbounded `GROUP BY`, since after a Kavita import that'd aggregate the whole library on every page load.
