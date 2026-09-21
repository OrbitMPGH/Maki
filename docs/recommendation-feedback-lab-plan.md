# Recommendation Feedback Lab implementation plan

Status: proposed implementation, no application code changed. Prepared against the `dev` checkout on 2026-09-16.

## 1. Product decisions

Keep library membership, recommendation feedback, and inferred taste as separate concepts. Maki's library is shared and root-folder scoped; a visible library title is not evidence that each viewer personally chose it.

| Action | Queue effect | Taste effect | Lifetime and reversal |
| --- | --- | --- | --- |
| Add to library | Existing owned-title exclusion applies after a successful add | A bounded positive signal for the attributable user who intentionally added it | Derived from live library provenance; removal ends the signal without creating dislike |
| Hide this title | Suppress that catalogue title on recommendation surfaces | Title-level negative feedback only; profile weights/facets stay unchanged | Until restored; no automatic genre, author, tag, or franchise suppression |
| Dismiss for now | Suppress that title for 30 days | None by default | Undo immediately or restore from history; expiry is evaluated at read time |
| Already read / Already seen | Suppress that title | Neutral exposure, never a positive or negative seed | Until exposure is cleared; record manga, anime, both, or unspecified |
| Stop using as a taste signal | Does not change library membership or candidate ownership exclusion | Exclude this work from inferred seed input | Until restored; preserve underlying ratings and reading history |

Apply personal add influence to intentional additions from every entry point, not only recommendation cards. The entry point is useful provenance, not a multiplier. Automated imports, background additions, and historical shared shelf entries receive no invented personal attribution. Retain the existing visible-shelf baseline for compatibility and label it as shared shelf evidence.

Do not reuse chapter read/watched mutations for catalogue exposure. Those operations change reading progress and can affect tracker synchronization. A person may have read a manga elsewhere or disliked its anime adaptation; neither fact establishes a manga preference.

Future explicit “I liked this” and “I disliked this” actions would be valuable for unowned works. They are outside the initial implementation. Reserve a separate sentiment concept rather than overloading exposure, ratings, or dismissal. Do not copy anime sentiment into manga preference without an explicit manga-level choice.

## 2. Repository evidence and integration map

Paths in this document are relative to `C:/Users/Orbit/Documents/GitHub/Maki`. Existing symbols and proposed symbols are distinguished throughout the plan. Investigation used graph discovery followed by current-source verification because graph metadata reported changed files; initial graph generation was `2026-09-16T11:50:15Z`, with frontend verification against the later `2026-09-16T13:45:19Z` generation. The partial parse range at `TasteInsightsService.cs:550` was read directly. The root `AGENTS.md` supplied in the request was applied; no root file with that name was present on disk.

| Existing file | Confirmed responsibility and planned integration |
| --- | --- |
| `src/Maki.Api/Services/RecommendationService.cs` | `GetAsync`, `RecommendationsResult`, `RecommendationRequest`, `Spread`; add personal output policy, versioned paging, and cache identity inputs |
| `src/Maki.Api/Services/SeedWeightService.cs` | `BuildAsync`, `SeedWeights`; central integration for personal library provenance and seed exclusions |
| `src/Maki.Api/Services/BehavioralTasteService.cs` | `ReadSignalsAsync`, `WeightsAsync`; reuse real-read signal collection and its visibility/incognito semantics |
| `src/Maki.Core/Recommendations/TasteWeights.cs` and `TasteTuning.cs` | Pure behavioural weighting, rating precedence, bounded weights, recency; keep new add tuning separate |
| `src/Maki.Api/Services/TasteInsightsService.cs` | Facet-group recommendations use direct `Scan` calls; integrate suppression into selection rather than relying solely on the main service |
| `src/Maki.Api/Services/TasteProfileService.cs` | `BuildAsync`/`Aggregate` consume seed weights for facet totals; split observed evidence from effective recommendation input to avoid silently rewriting history |
| `src/Maki.Api/Controllers/RecommendationController.cs` | Existing `POST /api/v1/recommendations`, `GET taste-profile`, and `GET taste-insights`; use its current-user conventions and add a separate proposed feedback controller under the same route prefix |
| `src/Maki.Metadata/Embedding/SemanticRecommender.cs` | `GetSimilarAsync` accepts weighted seeds and channel switches, then hydrates structured reasons; preserve channel meanings |
| `src/Maki.Metadata/MangaBaka/MangaBakaLocalStore.cs` | `GetRelatedAsync`, `GetSimilarAsync`, `GetProfileRowsAsync`; SQL/dump fallback accepts no seed weights, so expose degraded weighting honestly |
| `src/Maki.Api/Services/SimilarSeriesService.cs` | Content-only related-title recommendations and shared cache; apply a private response overlay without changing its cached entries |
| `src/Maki.Api/Services/ReaderCohortService.cs` and `ReaderCohortRailService.cs` | `PlaceAsync`, `GetCandidatesAsync`, `Rank`, `Draw`; reuse the pre-selection `accept` predicate for suppressed candidates and filter ignored source IDs for placement |
| `src/Maki.Data/Identity/MakiUser.cs` | `MakiUser` account, permissions, root access, and content ceiling; do not create a second user model |
| `src/Maki.Data/Identity/UserSeriesState.cs` | Per-user rating, reader preferences, notification mode; extend with attributable library-add provenance |
| `src/Maki.Core/Entities/Series.cs` and `src/Maki.Data/MakiDbContext.cs` | Shared library entity, user/root-folder query filters, and unique `(UserId, SeriesId)` state mapping; configure new user-owned tables and indexes here |
| `src/Maki.Api/Controllers/SeriesController.cs` | `Add` calls shared creation; `Delete` removes shared library state globally. The Lab must not present deletion as a private “unsave” operation |
| `src/Maki.Api/Services/SeriesCreationService.cs` | `CreateAsync` is the reusable add path; add explicit nullable user attribution and origin to the same persistence save as the created series |
| `src/Maki.Api/Controllers/SeriesRequestsController.cs` | `Approve` runs as an admin while `request.UserId` identifies the requester; pass attribution explicitly and handle transaction/retry boundaries |
| `frontend/src/App.tsx` and `frontend/src/pages/DiscoverPage.tsx` | `/discover/:tab?` routes the `taste` tab to `TasteTab`; extend this route rather than introducing another top-level page |
| `frontend/src/pages/discover/TasteTab.tsx` | `TasteTab`, `BehaviourSection`, `CompositionCard`, `CreatorsCard`, `TagsCard`; reuse `useTasteProfile(view)`, `useTasteInsights(view)`, and `useReadingBehaviour()` |
| `frontend/src/components/ui/DiscoverRail.tsx` | `RecommendationCard`, `EngineCard`, `reasonFor`, `engineWhy`; preserve existing card navigation and structured recommendation reasons |
| `frontend/src/components/discover/DiscoverDetailModal.tsx` | Shared detail surface; add optional recommendation context so valid recommendations expose feedback actions without enabling them on synthetic activity items |
| `frontend/src/components/discover/DiscoverLibraryRail.tsx` | Existing permission-aware Add/request action, root selection, incognito/monitoring controls, warnings, and errors; reuse intact |
| `frontend/src/api/hooks.ts` and `frontend/src/main.tsx` | `RecommendationItem`, `useAddSeries`, query hooks, and global error handling; add typed Lab contracts and central invalidation |

Read and preserve the relevant rules in `.claude/rules/recommendations.md`, `auth.md`, `reader-progress.md`, `series-matching.md`, `stats-notifications.md`, and `infra.md`, together with `CLAUDE.md`. In particular, watched chapters differ from actual reading, source-title explanations must respect visibility, tag spoiler flags are per candidate, and shared-library events do not establish personal authorship.

Confirmed architectural constraints:

- `Series` is shared; `Maki.Data.Identity.MakiUser`, `UserSeriesState`, reading state, and settings provide per-user data. `SeriesAdded` statistics have null `UserId` and cannot establish a personal add history.
- `SeedWeightService.BuildAsync` reads the visible shelf, applies personal `Rating / 5.0`, then fills/blends behavioural weights. Unspecified weights default to 1.0. These are positive seed weights, not a signed dislike model.
- `BehavioralTasteService.ReadSignalsAsync` uses `ReadCounts.ReadFor`, excludes full-incognito reading, and intersects with visible library IDs. Duplicate local rows mapping to one catalogue work are combined rather than counted as independent evidence.
- `RecommendationService.GetAsync` caches 200 similar picks for 12 hours, pages them in groups of 40, and has both semantic and local-store fallback paths. Related results are a separate list. Its current key represents seed/library/weight/filter/channel inputs rather than explicit feedback identity.
- `TasteInsightsService` also creates recommendations through its own group-constrained index scan. Changing only `RecommendationService` cannot cover the Taste page.
- `DataScope` defaults to unrestricted. A new child scope must explicitly apply the current user before querying personal records.

## 3. Persistence and domain boundaries

All names in this section are proposed unless explicitly identified as existing. Keep domain enums, state reducers, and weighting rules in `Maki.Core`; EF relationships and Identity integration belong in `Maki.Data`; orchestration belongs in `Maki.Api`.

Proposed file ownership: add `RecommendationFeedback.cs`, `RecommendationFeedbackEvent.cs`, `RecommendationSignalOverride.cs`, `RecommendationProfileState.cs`, and `RecommendationMutationReceipt.cs` under `src/Maki.Core/Entities/`; put pure policy/tuning under `src/Maki.Core/Recommendations/RecommendationFeedbackPolicy.cs` and `RecommendationFeedbackTuning.cs`; add `src/Maki.Api/Services/RecommendationFeedbackService.cs`, `RecommendationInputService.cs`, and `src/Maki.Api/Controllers/RecommendationFeedbackController.cs`. Configure user foreign keys in `Maki.Data` without introducing an ASP.NET Identity reference into Core. Existing `UserSeriesState` remains in its current Data/Identity location.

**Current feedback state: `RecommendationFeedback`.** One row per `(UserId, Provider, ProviderId)`, initially restricted to valid MangaBaka catalogue IDs. Do not require a local `SeriesId`: most feedback targets are not in the library, and feedback must survive library deletion. Fields:

- `Suppression`: none, hidden, dismissed; `DismissedUntilUtc` only for dismissed state.
- `IsExposed` plus manga/anime medium flags. `IsExposed = true` with no medium flags means unspecified; false means no exposure assertion. Marking another known medium unions the flags, so manga followed by anime becomes both. Marking unspecified never removes existing medium detail. Clearing exposure resets only this dimension. Exposure is independent of suppression so clearing “seen” cannot accidentally clear a hide.
- `Revision`, `UpdatedAtUtc`, and a minimal server-resolved title snapshot for unavailable titles.
- Optional local-series reference is only a convenience, nullable with `SetNull` on deletion. It is never the canonical feedback identity.

**Activity and idempotency: `RecommendationFeedbackEvent`.** Append one event for each effective feedback transition or successful undo, with user, target key, action, previous/new state, state revision, timestamp, `ClientMutationId`, and optional validated recommendation context. Unique `(UserId, ClientMutationId)` prevents replay. Do not use event count as a training signal. Equivalent actions with new IDs are no-ops while their state is already effective. In particular, retrying an active dismissal must not extend its expiry.

Keep current state authoritative; events are for history and safe undo, not an event-sourced ranker. Retain active state until cleared, then retain its revision as an empty tombstone to avoid delete/recreate concurrency races. Retain detailed history for 90 days, capped at 2,000 displayed events per user; provide restore/clear operations on current state even after its original event ages out. Retain compact mutation receipts for a documented 90-day retry window if event compaction would otherwise break replay safety. Beyond that window clients refetch state before issuing a fresh command; permanent monotonic state revisions still reject old state-changing commands.

Suppression transitions are explicit: Hide replaces an active dismissal with persistent hidden state; undo restores the previous dismissal with its original expiry, never a fresh timer. Dismiss on an already hidden title is a no-op and cannot weaken the hide. Clear-suppression clears either hidden or dismissed state, leaving exposure intact. An expired dismissal is logically inactive even before cleanup; a fresh command after expiry starts a new 30-day window. Effective queue suppression is `hidden OR active dismissal OR IsExposed`. Repeating Hide/active Dismiss/existing exposure is a no-op; changing state requires the current revision. Undoing Hide to an already expired previous dismissal makes that suppression inactive immediately.

**Library provenance.** Extend existing `UserSeriesState` with nullable `AddedToLibraryAtUtc` and an optional bounded `AddedFrom` value, written transactionally by the existing library creation workflow for the attributable user. This records a library fact, not an additional “saved” feedback row. Keep one effective contribution per catalogue work even when several local rows exist. A request submitted by one user and approved by another must distinguish the requester from the approving operator; attribute personal interest to the requester only when the requested add completes, not to the approver merely doing administration. Pending or rejected requests are not completed additions.

Resolve the effective add-time incognito mode before recording provenance. If it is Full, omit personal-add provenance and personal activity permanently for that add; turning incognito off later must not reconstruct the past add from its operation receipt. The receipt may still retain minimal identity needed for safe retries, but never supplies taste/activity evidence. Current full-incognito state also excludes a previously public source from displayed and effective profile inputs.

When a series is removed, derive no add weight from it. Do not retain a permanent positive seed from a historical add event. Existing shared library statistics may still describe the removal, but they must not become negative feedback. A re-add creates fresh library provenance once, without accumulating past bonuses.

Initial private library activity is explicitly live-provenance-only: attributable add rows disappear from this list when their `UserSeriesState` is removed. Shared library statistics can remain visible with their existing visibility rules and an explicit “Shared library” label, but cannot recover the former actor. Do not add a second private add-history ledger in this release. Feedback history remains independent and survives library removal.

`SeriesCreationService.CreateAsync` currently saves the new series before later cover/source work, and request approval updates its own state later. Persist the provenance row in the series creation save. For approval, keep potentially slow source/network work outside a short database transaction and persist the existing `SeriesRequest.SeriesId` association atomically with series/provenance creation so a retry completes resolution without creating or crediting another series. Preserve `deferSourceMatching = false` for approval because `QueueRangeAsync` needs the chapter list. If creation commits but later setup fails, the series and its provenance remain valid; report source/setup failure separately. A state lookup for the requester while running in an admin scope must use an explicit requester predicate with the appropriate filter bypass, never ambient admin state. Implementing this boundary is a dependency of enabling requester attribution. If the request is resolved as already present because someone else added the title, do not invent an add by the requester; its existing request record can explain interest separately without a second add bonus.

Claim an approval operation once with a conditional request-state update or a unique request-operation receipt before creation. Association alone does not stop two admins approving the same still-pending request concurrently. Retry can resume source matching/queueing with existing duplicate-queue guards, but must never repeat creation, attribution, or a resolved notification.

**Signal exclusion: `RecommendationSignalOverride`.** Unique `(UserId, Provider, ProviderId)`, with `IgnoreAsSeed`, revision, and timestamps. This durable catalogue-level override can outlive local removal/re-add. “Clear this inferred preference” means stop using this source work, not erase the book, rating, progress, or an observed historical chart. Restoring deactivates the override while preserving its monotonic revision. The explanatory profile must show that the evidence still exists but is excluded from recommendation input.

**Revision: `RecommendationProfileState`.** One per user, storing monotonic `FeedbackRevision` and `SignalRevision` for lightweight consistency checks. `SignalRevision` covers source overrides and personal-add provenance creation/removal/re-add, not just feedback commands. Advance it atomically with provenance writes; before a shared series deletion cascades state, identify and increment the affected provenance owners' revisions in that transaction. Rating/reading/visibility changes are also represented in the observed/effective input fingerprint, so no cache relies on the counters alone. Include the resulting version in add responses, Lab responses, pool identity, and paging. Update feedback state, event, receipt, and relevant revision in the same transaction. Separate queue-only changes from seed changes so dismissing one title does not require an expensive full index scan.

**Retry receipt: `RecommendationMutationReceipt`.** Unique `(UserId, ClientMutationId)`, with operation kind, canonical target, normalized payload hash, original result, and expiry. Share the receipt mechanism across feedback, overrides, and attributed library adds. A library-add receipt is operational deduplication, never a feedback record or ranking input. Write it in the same transaction as the state it protects; retain receipts independently of the shorter displayed activity cap.

**Recommendation history.** Initial scope stores action-linked context, not every rendered impression. Include surface, generation time, algorithm version, and safe reason codes where available. Do not persist entire candidate pools or treat display as preference. If exposure analytics are later needed, use a separately bounded, opt-in impression record with a unique impression ID and no ranking weight.

## 4. Ranking and serving

Build a shared proposed `RecommendationFeedbackService` for mutations and reads, and a pure `RecommendationFeedbackPolicy` for effective suppression at a supplied UTC time. Centralize signal selection in `SeedWeightService` or a proposed helper used by both the ranker and its explanation layer.

Introduce a proposed `RecommendationInputService` boundary with separate observed evidence and effective inputs. Observed evidence retains actual shelf/read membership, existing rating/behaviour weighting, and source facts. Effective inputs add personal-add influence and remove ignored sources. Keep `TasteProfileService`'s observational counts/facets and `TasteInsightsService`'s descriptive groups/drift on observed evidence; show the effective input summary explicitly as what recommendations use. For group suggestions, compute the centroid from eligible members of that observed group, preserving its named facet constraints. If every member is ignored, keep the historical group visible with no personalized suggestions. Do not mine new historical groups or rewrite reading counts because a source was ignored.

Keep owned/excluded candidate IDs separate from seed IDs. Ignoring a library title as a seed must not make that owned title eligible for recommendation. Signal overrides apply to automatically selected personal recommendation inputs. A deliberate one-off “More like this” or explicit seed selection may still use that requested title; it does not reinstate the title in the user's inferred profile. Candidate suppression still applies to the resulting recommendations.

Recommended initial add weighting, subject to offline evaluation:

1. An explicit 1–10 personal rating keeps its existing authoritative `rating / 5.0` weight.
2. For an unrated work with attributable live addition, take the maximum of the existing behavioural weight and `1 + 0.5 * 2^(-ageDays / 90)`.
3. No addition provenance means the existing weight is unchanged. Full-incognito and inaccessible source works produce no new personal add signal.
4. Apply signal exclusions before representative-seed selection and aggregation. Deduplicate by canonical catalogue ID. Never sum duplicate adds, repeated clicks, or multiple feedback events.

This makes a recent personal add clearly positive while keeping it below a maximum explicit rating and limiting its novelty effect over time. A low rating wins over a past add. Keep the existing behavioural tuning, including its 240-day half-life, separate from this proposed add bonus. No negative vectors are introduced by Hide. Dismiss and neutral exposure have exactly zero seed weight contribution.

Avoid overreaction through bounded per-work weight, duplicate collapse, finite decay, existing diversity/franchise spacing, and no inferred cross-attribute penalty. Ten dismissals of related titles suppress those ten titles for their respective cooldowns; they do not silently become a genre or franchise ban. Offer an explanation of this behavior rather than inventing a dislike threshold. For new summary claims, show the supporting distinct-work count and use “limited evidence” below three works rather than confidently naming a lasting preference from one addition.

Apply suppression on every personal recommendation path, including related results, semantic and fallback pools, Taste group picks, recent-activity recommendations, and cohort recommendations. Preserve ordinary search and library access: recommendation suppression is not a catalogue access restriction.

The required integration matrix is:

| Producer | Implementation boundary |
| --- | --- |
| Main Recommended, Home recommendations, recent activity, side interests | `RecommendationService.GetAsync`, with callers preserving its user context and effective seeds |
| Taste group suggestions | `TasteInsightsService.GroupsAsync` / `Scan` / `Claim`: exclude before picks are claimed and hydrate only eligible output |
| Reader cohort rail | Add suppressed-ID filtering to `ReaderCohortRailService`'s `GetCandidatesAsync` predicate; use eligible actual-read IDs in `ReaderCohortService.PlaceAsync` |
| Series related/similar endpoints | `SeriesController`'s `/api/v1/series/{id}/related` and `/similar`, plus `SimilarSeriesService`; both bypass the main service |
| Shared Discover catalogue/genre rails and their expanded recommendation views | Clone/filter response after `DiscoverService` cache retrieval, with bounded refill; never store viewer state in the shared cache |

Source exclusions apply to cohort placement as well as automatic semantic seeds. A personally added but unread title must not enter finished-reader cohort placement. Existing one-off More like this keeps content channels only; do not turn crowd channels on to implement feedback.

For reusable cached candidate pools, apply a user-specific overlay before paging and calculating `HasMore`, then reapply the existing franchise spacing to the filtered similar pool. Never mutate a shared cached rail. For small group/cohort scans, pass suppressed IDs into selection or overfetch/refill within a bounded budget, then apply a final response filter. Resolve suppression once per request, not once per candidate. Return a shorter list honestly if exhausted.

Cache keys for personalized outputs include user identity, effective visibility/content ceiling, relevant signal revision, algorithm version, and the existing seed/filter inputs. Queue overlays use feedback revision and the nearest dismissal expiry. Recheck expiry at read time; a cleanup job is not required for a title to return. Shared library mutations and root-folder access changes must invalidate or recompute every affected user's derived inputs, not only the adding user's view.

Concretely, keep existing `_pools` immutable and overlay immediately after retrieving the complete pool, before its current `Skip`/`Take`. Preserve `SimilarSeriesService._entries` similarly. `TasteProfileService` and `TasteInsightsService` currently cache by `UserId:view` for 30 minutes; replace this with a key containing the input fingerprint computed from the shared observed/effective snapshot before accepting a hit. Include sorted visible/eligible IDs, rating and reading evidence, incognito eligibility, content/visibility scope, relevant revisions, and catalogue/artifact generation. Reuse that snapshot within the request rather than repeating its EF reads for each component. This adds bounded local input reads on a warm hit, not a full catalogue/vector scan. Cohort placement also needs `SignalRevision` and current visibility in its key, not just its existing user/read-count/age inputs. Neutral queue feedback changes output filtering, not observed profile evidence. A refreshed client query alone is insufficient if the backend still serves an old 30-minute profile.

Paging carries a pool/profile version. After a mutation, the frontend resets loaded pages; a request for a stale version receives a restart indication rather than silently skipping or repeating titles. Retain legacy paging for older clients, with server-side suppression still enforced.

The fallback ranker does not currently accept weighted seeds. For the initial release, retain its existing scoring and report `rankingMode: fallback`: adding a library title changes its seed population, but the additional personal weight is unavailable until semantic ranking is ready. Suppression, history, undo, and source exclusion must work in both modes. Do not show fabricated weighted-score changes when the fallback cannot apply them. A weighted fallback query is a subsequent evaluated improvement rather than an unbounded requirement for this feature.

## 5. API contract

All endpoints below are proposed under `/api/v1/recommendations`. Derive user identity from `ICurrentUser`; no request accepts a client-selected `UserId`.

| Endpoint | Contract |
| --- | --- |
| `GET feedback-lab` | Profile evidence summary, effective capability flags, current feedback/signal revisions, ranking mode, supported dimensions, change summary, and first activity page |
| `GET feedback?state=hidden&cursor=...&limit=...` | Cursor-paged current hidden/dismissed/exposed state, including unavailable targets with restricted display data |
| `GET feedback/activity?cursor=...&limit=...` | Cursor-paged explicit feedback plus clearly labeled library-derived activity; stable `(occurredAt,id)` ordering |
| `PUT feedback/{mangabakaId}` | One typed command: hide, dismiss, mark-exposed, clear-suppression, or clear-exposure; payload includes `clientMutationId`, `expectedRevision`, optional medium, optional validated context |
| `POST feedback/events/{eventId}/undo` | `clientMutationId` and `expectedRevision`; restores prior state only if the affected state has not since changed |
| `PUT signal-overrides/{mangabakaId}` | Set `ignoreAsSeed`, with `clientMutationId` and `expectedRevision` |
| `DELETE signal-overrides/{mangabakaId}` | Clear the override with equivalent concurrency/idempotency metadata |

`feedback-lab` returns `{ capabilities, rankingMode, versions, summary, dimensions, changes, activity }`. `versions` contains feedback/signal revisions plus an opaque current input version. `summary` distinguishes visible shelf count, personal-add count, rated/read source counts, and current hidden/dismissed/exposed counts. Each supported dimension returns `{ kind, label, evidenceCount, confidence, sources, effect }`; it is not an invented universal signed score. `changes` contains only effects attributable to committed actions and indicates whether weighted ranking is currently available. Paged endpoints return `{ items, nextCursor, versions }`, with bounded limits and opaque cursors. Activity items have stable IDs, kind, target, permitted display data, occurrence time, effect, and optional revision-checked undo information. DELETE uses `If-Match` and a mutation-ID header rather than relying on a request body.

Example feedback mutation:

```json
{
  "action": "mark-exposed",
  "medium": "anime",
  "clientMutationId": "8fd332ba-909a-48e2-af0d-f4c19b409c87",
  "expectedRevision": 0,
  "context": { "surface": "taste", "profileVersion": "opaque-version" }
}
```

Example response shape:

```json
{
  "changed": true,
  "eventId": "opaque-event-id",
  "state": {
    "mangabakaId": 123,
    "suppression": "none",
    "exposure": ["anime"],
    "dismissedUntilUtc": null,
    "revision": 1
  },
  "feedbackRevision": 18,
  "signalRevision": 4,
  "effect": { "queue": "suppressed", "taste": "unchanged" },
  "undo": { "available": true, "expectedRevision": 1 }
}
```

The add action calls the existing series add/request workflow, with optional source context and a client mutation ID. The server derives actor identity, validates the origin enum, and persists an operation receipt with creation/provenance so replay cannot create or credit another copy. Do not solve retry duplication with a global unique MangaBaka index: multiple local rows representing one catalogue work are an existing supported condition. Older clients may omit the new fields and retain existing duplicate-add behavior. There is no `feedback/save` endpoint and no second client write that could fail after a successful library add.

Keep the current add response fields and append a proposed `operation` object: `{ id, state: "committed" | "committed-with-warnings" | "setup-pending", seriesId, requestId?, signalRevision }`. Persist the committed identity in the receipt with the first save. If later source/cover/setup work fails, return the existing created identity and warnings, not a response implying that creation failed. A concurrent retry while setup runs can return 202 with that same identity and pending status; terminal retries replay the stored terminal result. A disconnected request may deliver no response, but its retry still finds the committed receipt. No post-commit retry performs another creation or add credit. Feedback mutation receipts, which have no setup phase, continue replaying their original response directly.

Return structured validation errors for unsupported actions/media or malformed IDs; 401/403 using existing authentication/permissions; 404 for another user's event or inaccessible resource; 409 plus current permitted state for stale revision or reuse of a mutation ID with a different payload. Replaying an identical mutation ID returns its original outcome. Same effective action under a new ID returns `changed:false`. Use optimistic concurrency suitable for SQLite, not a SQL Server rowversion assumption.

For Hide, return `effect: { queue: "suppressed", feedback: "negative-title", taste: "unchanged" }`. Dismiss and exposure return `feedback: "temporary"` or `"neutral-exposure"` with the same unchanged taste effect. These fields prevent the UI from describing title suppression as a learned negative genre preference.

A missing catalogue item may still have its existing feedback restored or cleared. A new feedback target must be server-validated against currently permitted catalogue metadata. Initial implementation returns a retryable metadata-unavailable error for new unknown targets while the catalogue is unavailable; it does not block managing existing feedback. A signed recent recommendation reference could relax this later, but is not required for the initial schema. Client-supplied title/cover snapshots are never authoritative.

## 6. Taste page and explanations

Retain the existing Taste page structure and read-versus-shelf distinction. Add an interactive Feedback Lab below the current overview and before long recommendation groups. It contains:

- A short “What is shaping recommendations” summary, separating shared shelf, personal ratings, actual reading, personal additions, and explicit feedback.
- Evidence dimensions supported by the existing profile: genres, story themes/tags, authors, formats, franchises, and reading patterns only where data exists. Missing evidence is “not enough data,” not a zero preference.
- Recent activity with filters for Feedback and Library activity. Every row says what happened and its effect: “Hidden title,” “Dismissed until…,” “Seen anime, taste unchanged,” or “Added to shared library by you.” Historical activity without provenance must never say “you added.”
- Manage hidden, dismissed, exposed, and excluded-source lists, with per-item restore/clear actions available beyond the transient undo toast.
- A change indication tied to committed revisions: “This title is excluded now,” “Your recent add has more influence,” or “No taste change.” An optional refresh shows updated recommendations. Do not promise that every positive action changes every top-ranked title.

Reuse the existing add detail/modal flow and permission-aware request alternative. On recommendation cards, keep Add to library as the primary positive action and put Hide, Dismiss, and Already read/seen in an accessible menu. Already read/seen opens a compact medium choice with explicit neutral copy. Provide keyboard focus handling, large touch targets, and accessible labels; avoid a row of four tiny ambiguous icons.

Implement the Lab as proposed `frontend/src/pages/discover/FeedbackLab.tsx`, with a shared proposed `RecommendationFeedbackMenu.tsx` under `frontend/src/components/discover/`. Place the menu in the shared detail action area and optionally provide one accessible overflow trigger on genuine recommendation cards. Pass a typed `feedbackContext` from each recommendation surface. Hide controls on ordinary synthetic detail items without a valid catalogue ID. Preserve the existing card poster's full-click navigation target.

Centralize invalidation in a proposed feedback hook: refresh Lab/state/activity for every mutation; refresh recommendation outputs for suppression or seed changes. Existing affected keys include `['recommendations']`, `['discover-recent-activity']`, `['discover-side-interests']`, `['discover-cohort']`, `['discover-rails']`, `['discover-feed']`, and `['discover-genres']`. Audit the actual Taste insight/profile, Home, and similar-series keys during implementation and include them by producer responsibility. Refresh Taste insight picks after queue-only feedback, but do not recompute or alter taste facets for Hide, Dismiss, or exposure. Add and signal overrides refresh the effective input summary and applicable profile queries. `useAddSeries` currently invalidates only `['series']`; extend it for these consumers and for non-recommendation entry points as well.

Optimistically remove a title across loaded recommendation surfaces, show pending state, and offer Undo once the server confirms. Roll back the affected state on failure, without overwriting newer concurrent mutations. If undo is requested before the original response arrives, queue it against that response's revision. Preserve scroll/focus and announce the result. Disable duplicate submission for the same work while pending; server idempotency remains authoritative.

A stale-revision conflict refreshes current state and gives a concise message. Offline/timeout failure leaves a retry action using the same mutation ID. Add errors retain the existing dialog selections and do not claim a positive taste signal until creation succeeds. A pending series request is labeled “Requested,” not “In library.”

Keep observational charts factual when source influence is disabled. Show “Excluded from recommendations” beside that source and update the recommendation-input summary, rather than rewriting historical reading behavior.

**Explanation contract.** Reuse existing matched tags, genres, source-title attribution, and crowd/taste channel flags. Add structured reason codes and evidence references, not generated narrative. Preserve per-candidate spoiler filtering. A proposed explanation DTO includes `reasonCodes`, permitted source catalogue IDs/titles, safe matched facets, ranking mode, profile version, and signal provenance. Display only reasons actually used by that recommendation path. Taste group suggestions explain their constrained group facets; cohort picks explain cohort evidence rather than claiming the main ranker's score.

Signed negative facet weights do not currently follow from low positive seed weights. Hide is shown as explicit title rejection; lower-rated source works are shown as contributing less influence. Do not label every genre on a hidden title as disliked. Direct genre/author/franchise dislikes and anime-specific sentiment are a separate future feature requiring explicit controls and evaluated scoring.

The confirmed `TasteProfile` axes are creators, genres, tags, types, and years; reading behaviour is supplied separately. Franchise identity is available for recommendation relations/spacing, but an existing signed franchise preference is not established. In the initial Lab, explain franchise relationships on relevant recommendations and mark franchise-level preference controls unsupported. Add a franchise summary only if it can be computed from verified catalogue relationships with source-work counts; do not infer one from title similarity. “Clear individual preferences” initially means clear a title-level feedback state or ignore/restore a named evidence source. A group/genre drill-down lists those source controls rather than offering a misleading destructive “erase genre history” button.

## 7. Authorization, privacy, and failure behavior

Every new user-owned entity implements the established ownership convention and receives an EF query filter. Explicitly scope all child contexts. Background work uses explicit user predicates. Keep existing `AddSeries`, root-folder, content-rating, and request permissions; giving feedback does not grant permission to add, download, or edit shared metadata.

Resolve activity snapshots and explanations through current visibility/content rules. Never expose hidden-root titles through source names, activity rows, counts by title, covers, cached profiles, or explanation contexts. Full-incognito library-derived actions must not be included in profile/activity evidence. Catalogue feedback is private to its author; history access has no automatic cross-user admin browsing route.

There is a confirmed pre-existing inconsistency to fix deliberately: behavioural reads and `TasteInsightsService.LibraryRowsAsync` exclude full-incognito works, but `TasteProfileService` shelf aggregation currently receives all root-visible works. Exclude full-incognito sources from the new observed/effective profile inputs and automatic recommendation seeds, while retaining them in owned-title candidate exclusion. This privacy alignment can change an existing shelf profile and recommendations for users with full-incognito titles; document that specific compatibility effect instead of claiming every historical profile remains byte-for-byte identical.

Apply existing cookie antiforgery and API-key scope rules. Notifications, if any, go to the existing per-user hub group. A shared library broadcast may trigger refetching but must not carry someone's private feedback.

Shared candidate caches contain only catalogue-safe reasons and metadata. Personal source-title reasons, source activity, and effective-signal explanations are attached or redacted after current visibility/content checks in the private response layer. Never cache a per-user explanation inside Discover's shared rails or `SimilarSeriesService._entries`.

User deletion cascades private feedback, events, overrides, profile state, and mutation receipts. Receipts receive the same retention and privacy treatment as their contained target metadata. Series deletion does not delete catalogue feedback. Metadata disappearance preserves manageable state with a safe placeholder. Follow only verified canonical-ID redirects/merges, resolve collisions deterministically, and never use fuzzy title matching or franchise membership as identity.

## 8. Migration, delivery, and verification

Create additive EF migrations for the proposed feedback/state/event/override/receipt tables and nullable library provenance columns. Include composite unique constraints, cursor indexes `(UserId, OccurredAtUtc, Id)`, lookup indexes, user FKs, concurrency columns, and nullable local-series references as appropriate. Update the model snapshot and DI wiring. Existing users start with no feedback and no invented provenance. Preserve previous recommendation behavior except for the explicitly documented full-incognito privacy alignment.

Use the existing pre-migration backup and forward migration conventions. Exercise actual SQLite `Migrate()` from a populated pre-feature database; `EnsureCreated()` alone cannot validate this upgrade. Keep schema and DTO changes additive. Extend relevant `.claude/rules` path globs for newly named subsystem files.

Suggested delivery order:

1. Feedback domain policy, schema, current-state/history APIs, permissions, idempotency, and undo.
2. Shared suppression policy integrated into all recommendation producers, paging, and cache overlays.
3. Transactional personal library provenance, bounded seed influence, source exclusions, and matched explanation input.
4. Taste Lab, card menu, history management, query invalidation, and change indications.
5. Migration/privacy regression tests, offline recommendation evaluation, controlled enablement, and performance review.

Proposed feature flags separate Lab UI availability from additional seed weighting. Once users have recorded hides/exposure, keep honoring suppression even when experimental weighting is disabled. Flag changes participate in cache identities. No flag rollback deletes feedback. Enable on a test instance first, then evaluate representative libraries and multiple users before broad default enablement.

Tests must cover:

- Pure policy: action transitions, independent exposure/suppression, expiry boundaries, duplicate commands, stale undo, exact-title scope, deterministic decay, rating precedence, no repeat-action accumulation.
- Transition matrix: hidden then dismiss stays hidden; dismissal then Hide escalates; Undo restores the original cooldown; exposure survives clear-suppression; an expired cooldown can be deliberately renewed; Hide emits negative-title feedback but leaves both observed and effective facet weights unchanged.
- SQLite integration: uniqueness races, same mutation ID/different payload, transactional add provenance, migration preservation, user cascade, local-series deletion/re-add, missing catalogue item, duplicate local catalogue mappings.
- Add lifecycle: commit followed by cover/source failure returns the created identity with warnings; receipt retries do not duplicate the row or its influence; full-incognito add then incognito-off never synthesizes provenance; deletion removes live-provenance activity and advances affected versions; deleting a user removes receipts too.
- Authorization/API: two users with identical shelves and different feedback, hidden roots, content ceilings, full incognito, child-scope setup, cookie antiforgery, API keys, and attempts to address another user's history.
- Recommendation regression: semantic/fallback/related/Taste/cohort paths all suppress; shared cached Discover data stays unchanged; expiry works without a job; filtered pagination remains stable; an ignored seed remains owned; an add never overrides a low explicit rating.
- Exposure: marking anime, manga, both, or unspecified changes no `ChapterProgress`, `ReadingState`, ratings, stats, scrobble queues, taste weights, or completed-reader cohorts.
- Frontend: successful/failed optimistic updates, queued Undo, pending requests, keyboard/mobile menus, unavailable targets, query invalidation across surfaces, stale revisions, expiry, and empty/error states.
- Evaluation: compare old/new top-K overlap, relevance, diversity, franchise concentration, cold-start behavior, and cache/scan cost on the simulated mixed library and a deterministic fixture set. One narrowly themed development library is insufficient evidence.

Extend the existing patterns in `tests/Maki.Api.Tests/PerUserIsolationTests.cs`, `BehavioralTasteServiceTests.cs`, `TasteProfileServiceTests.cs`, and `RecommendationServiceTasteTests.cs`. Proposed focused additions are `RecommendationFeedbackTests.cs`, `RecommendationFeedbackApiTests.cs`, and `RecommendationFeedbackMigrationTests.cs`, plus pure policy tests in `tests/Maki.Core.Tests`. Include an approval retry after creation commits but before request resolution, and verify that only the requester is credited once.

Run the affected .NET test projects, frontend `npx tsc -b`, and the repository lint/build checks when implementation exists. The inspected frontend has no configured interaction-test runner. Introduce Vitest and React Testing Library as a proposed focused testing setup, selecting compatible versions during implementation; type-checking is not an interaction test. Use an isolated `MAKI_CONFIG_DIR` for tests and migrations, never the real APPDATA library.

Operational metrics should be aggregate and local by default: mutation/error/undo counts, suppressed-title leakage (target zero), refill exhaustion, cache hit rate, scan duration, and add conversion from actual completed adds. Do not equate a high hide rate with failure or maximize clicks at the expense of relevance. No external telemetry is required.

## 9. Edge-case acceptance criteria

| Scenario | Required behavior |
| --- | --- |
| Disliked an anime and chooses Already seen | Title leaves queue, anime exposure recorded, no manga preference inferred; optional future sentiment stays medium-specific |
| Read elsewhere, never in Maki library | Exposure works by catalogue ID without creating a local series or reading progress |
| Add then remove | Live positive add influence disappears; no negative feedback is synthesized |
| Repeatedly dismiss related titles | Independent fixed cooldowns, no cumulative similarity penalty or automatic franchise ban |
| Hide one title but wants similar ones | Only exact canonical title excluded; related works remain eligible |
| Existing account, no history | Existing profile remains usable; honest empty feedback state and no fabricated past events |
| Same action submitted twice | One effective transition/weight, stable response, no extended cooldown from a retry |
| Title unavailable after feedback | History remains manageable, safe placeholder, undo/clear still works |
| Add a previously hidden title | Library action succeeds if permitted; hide remains an independent setting until explicitly cleared |
| Seen and hidden coexist | Clearing either dimension leaves the other intact; effective suppression remains until all active reasons end |
| All candidate titles suppressed | Honest empty state and management controls, no hidden-title fallback leak |
| Profile changes mid-page | Restart paging against new version; no silent duplicate or skipped cards |

The numerical add weight, history limits, and 30-day dismissal duration are recommended starting values, not measured repository defaults. The implementation review should confirm the canonical-ID merge source and validate the weighting constants against representative fixtures. Until a trustworthy merge source is verified, retain exact provider IDs and require explicit restoration of the old ID; never infer aliases from titles. These are bounded engineering decisions, not prerequisites for starting the plan.
