# Recommendation Feedback Lab: status and next steps

Status snapshot: 2026-09-17. This tracks the working tree implementation of the [original plan](recommendation-feedback-lab-plan.md). The original document remains the product and architecture reference. The feature code is still uncommitted, so the statuses below describe local work, not a released version.

## What is done

| Area | Status | Current implementation |
| --- | --- | --- |
| Feedback state and policy | Implemented | Per-user hide, 30-day dismissal, manga/anime exposure, independent clear actions, revision checks, undo, and 90-day mutation receipts. Policy and entities live in `Maki.Core`; orchestration lives in `Maki.Api`. |
| Persistence | Implemented | Additive SQLite migration for feedback, events, signal overrides, profile revisions, mutation receipts, and nullable personal-add provenance. Existing users begin without inferred feedback or invented add history. |
| Feedback API | Implemented, contract refinements remain | Lab, current state, history, mutation, undo, and signal-override endpoints exist under `/api/v1/recommendations`. Writes check ownership, payloads, revisions, and mutation IDs. |
| Recommendation suppression | Implemented, broader regression coverage remains | Exact catalogue IDs are suppressed in the main recommendation pool, Taste picks, cohort rails, related/similar responses, and Discover overlays. Dismissal expiry is checked when reading. Main-pool paging returns a version and restart signal. |
| Personal add influence | Implemented, lifecycle hardening remains | Intentional completed adds record the attributable user and origin. Full-incognito adds do not create provenance. Seed weighting is bounded and respects explicit ratings; ignoring a source excludes it from inferred seeds without changing the shared library. Request approval credits the requester. |
| Taste Lab and feedback controls | Implemented, browser coverage remains | Taste shows evidence counts, recent feedback, library activity, and management controls. Recommendation detail menus offer Hide, Dismiss, and read/seen actions. Client mutations remove affected cards optimistically and invalidate recommendation queries. |
| Retention and flags | Implemented | A background service prunes old feedback events and receipts. Lab visibility and personal-add weighting have separate settings; recorded suppression remains effective when weighting is disabled. |

The last completed checks passed: 903 API tests, 512 Core tests, frontend production build, lint, and `git diff --check`. In the browser, Hide removed a Home recommendation, Undo restored it, and the Taste Lab's Feedback, Library activity, and Manage views rendered. Lint reported existing warnings. Browser testing used an isolated `.devconfig` instance.

## What remains

| Priority | Gap | Why it matters |
| --- | --- | --- |
| 1 | Finish the API and add lifecycle contract. Feedback pages currently use numeric row-ID cursors, and the Lab exposes feedback/signal revisions without the planned opaque input version. Feedback activity and library-derived activity are separate. Add receipts replay a created series ID, but do not persist the full terminal setup state and warnings specified in the original plan. | Clients need stable history paging and an unambiguous result after a disconnected or retried add. |
| 2 | Prove privacy, cache, and producer behavior with focused integration tests. Existing tests cover key transitions, ownership, a hidden root, stale writes, migration, and request approval. The full two-user, content-ceiling, full-incognito, expiry, and every-producer matrix has not been exercised end to end. | A hidden title must not leak through a secondary rail, cached explanation, or private activity response. |
| 3 | Add structured recommendation explanations. Current recommendation records expose matched genres/tags, relation titles, and channel flags. They do not yet expose the planned reason codes, permitted evidence references, ranking mode, and profile version as one explanation contract. The Lab dimensions currently have counts and confidence labels but no source references. | Feedback should explain only the evidence actually used, under current visibility and spoiler rules. |
| 4 | Complete browser interaction coverage. The Home Hide/Undo path and Taste Lab rendering passed. Dismissal, exposure combinations, source exclusion/restore, stale conflicts, failed optimistic rollback, pending requests, keyboard use, and narrow-screen layout still need browser checks. | The frontend has no interaction-test runner, and the requested frontend test method is the browser tool. |
| 5 | Evaluate ranking and rollout cost. The original plan's top-K overlap, relevance, diversity, franchise concentration, cold-start, cache-hit, refill, and scan-cost comparisons are not recorded. Aggregate local operational counters are also absent. | Enablement decisions need evidence from representative libraries rather than one development library. |

## Continue in this order

### 1. Close the API and add lifecycle gaps

- [ ] Replace numeric feedback/history cursors with opaque, versioned cursors. Use stable ordering for history by `(OccurredAtUtc, Id)` and reject or restart stale paging after relevant mutations.
- [ ] Return one Lab input version alongside feedback and signal revisions. Make the client use that version when refreshing Lab and recommendation pages.
- [ ] Include clearly labeled live-provenance library additions in the activity contract, or explicitly revise the original API contract if the separate Lab view is the intended design. Do not attribute historical shared-shelf rows to a user.
- [ ] Persist add operation state and warnings in the receipt. Define and test responses for committed, committed-with-warnings, and setup-pending retries, including a retry while setup is in progress.
- [ ] Add contract tests for replay with the same mutation ID, conflicting payloads, stale revisions, 90-day receipt expiry, and missing catalogue metadata.

Primary files: `RecommendationFeedbackController.cs`, `RecommendationFeedbackService.cs`, `SeriesCreationService.cs`, `SeriesController.cs`, `frontend/src/api/recommendationFeedback.ts`, and `frontend/src/api/types.ts`.

Done when a client can page history without skipped or repeated rows and can retry any add with the same mutation ID without creating a second series or losing the first result.

### 2. Verify privacy and all recommendation paths

- [ ] Add focused SQLite/API tests for two users with the same shelf but different feedback, hidden roots, content-rating ceilings, full incognito, duplicate local rows for one catalogue ID, deletion/re-add, and user cascade.
- [ ] Exercise main semantic and fallback ranking, Taste groups, cohort, related/similar, and Discover cached rails with one suppressed title. Confirm the title is absent after refill and shared cache entries remain unchanged.
- [ ] Test dismissal expiry without running the prune job and stale pool-version restart after feedback, access, or library-input changes.
- [ ] Check that exposure does not mutate chapter progress, reading state, ratings, stats, scrobble queues, or reader cohorts. Confirm a low explicit rating still wins over recent-add weight.

Primary files: `RecommendationService.cs`, `DiscoverService.cs`, `TasteInsightsService.cs`, `ReaderCohortService.cs`, `ReaderCohortRailService.cs`, `SeedWeightService.cs`, and the matching `tests/Maki.Api.Tests` suites.

Done when each producer and privacy boundary has a targeted regression test and no suppressed or inaccessible title appears in a result or explanation.

### 3. Complete explanations and the Lab contract

- [ ] Add structured reason codes and safe evidence references to recommendation DTOs. Report semantic versus fallback mode and the input version; do not claim weighted effects in fallback mode.
- [ ] Resolve source titles through current root, content, incognito, and spoiler rules when building each private response. Keep personal evidence out of shared caches.
- [ ] Give Lab dimensions permitted source references and an explicit effect label. Keep observed library evidence separate from effective recommendation inputs, and show limited evidence honestly.
- [ ] Validate and retain recommendation context on feedback events only when its surface and version are legitimate; do not trust client-supplied titles or covers.

Primary files: `MangaBakaRecommendation.cs`, recommendation and Taste services/controllers, `RecommendationFeedbackController.cs`, `FeedbackLab.tsx`, and the existing recommendation-card components.

Done when each visible explanation can be traced to a permitted input and cannot expose a hidden or spoiler-restricted source.

### 4. Finish browser testing and rollout review

- [ ] Use the browser tool against an isolated `MAKI_CONFIG_DIR` to check the remaining feedback, undo, management, request, failure, keyboard, and mobile flows. Record concrete failures and fix them before release.
- [ ] Build deterministic mixed-library fixtures and compare old/new top-K overlap, relevance, diversity, franchise concentration, and cold-start behavior in semantic and fallback modes.
- [ ] Measure warm/cold cache and scan cost. Add aggregate local counters for mutation errors, undo, suppressed-title leakage, refill exhaustion, cache hits, and completed-add conversion.
- [ ] Re-run affected .NET suites, `npx tsc -b`, frontend build/lint, migration upgrade, and `git diff --check`. Review the two feature flags on an isolated test instance before broad enablement.

Done when the browser matrix passes, evaluation results and performance limits are written down, and the test instance shows no private-data leakage or suppressed-title leakage.

## Working rules for the next session

- Keep the original plan's distinction between shared-library membership, personal feedback, and inferred taste. Hide and read/seen affect only the exact catalogue title; neither is a negative genre signal.
- Use exact MangaBaka IDs. Do not infer merges from similar titles or franchise membership.
- Keep frontend interaction testing in the browser, as requested. The existing backend/Core test suites remain useful for policy and persistence checks.
- Use an isolated `MAKI_CONFIG_DIR` for migration or browser work. Do not use the real APPDATA library.
- Preserve unrelated working-tree changes, including the original untracked plan and local settings file.
