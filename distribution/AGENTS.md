# AGENTS.md: distribution

Operational gotchas for the search/recommendation evaluation harnesses, crowd-graph fetchers, and artifact builders/publishers in `distribution/` (also relevant to `Maki.Metadata` embedding/recommendation code).

Historical experiment write-ups, benchmark tables, and "measured and NOT shipped" records live in `distribution/history/` (indexed at the bottom). Read the relevant history file before revisiting a past tuning decision or proposing a channel/knob change.

## Tool map

- **Search eval**: `eval-search.cs` (fuses dense + lexical + tags; `--engine title` scores FTS5 fallback; writes per-query RR CSVs to `.artifacts/eval/rr-<variant>-search[-<class>].csv`). `eval-embeddings.cs` scores the embedding model in isolation and never runs fusion.
- **Recommendation eval**:
  - `eval-reco.cs`: `spread` (synthetic narrow-and-deep profiles, over-fit/diversity check), `loo` (leave-one-out on `MAKI_CONFIG_DIR` library; tripwire only), `dial` (avoid-channel tag suppression).
  - `eval-reco-labels.cs`: held-out grading against `reco-edges.db` (`--labels reco`), `coread-edges.db` (`--labels coread`), `mu-edges.db` (`--labels mu` / `mu-human`), and real AniList reading lists (`library` mode, `--next` for the next-read grader).
  - `eval-compare.py`: paired bootstrap/McNemar comparison on RR/nDCG CSVs. Pass a fourth arg naming the metric when grading `--csv ndcg|r40`.
  - `eval-next-gates.py`: acceptance gates for `library --next` (`wantN`, `passed`, floor).
  - `run-reco-suite.ps1`: standard regression suite.
- **Fetchers & builders**:
  - `fetch-reco-graph.cs` (AniList + MAL votes -> `.artifacts/reco-graph.db` working state + `reco-edges.db` shipped).
  - `fetch-coread-graph.cs` (AniList reading lists -> `.artifacts/coread-graph.db` working state + `coread-edges.db` shipped).
  - `build-mu-graph.cs` (folds MangaUpdates columns from the **full** dump into `.artifacts/mu-edges.db`; refuses standard dump where those columns are NULL).
  - `build-taste-vectors.cs` (`--fold-out k/n`), `build-ease-table.py` (`--pop-damp 0.35`), `build-page-share.py`, `build-reader-cohorts.cs`, `build-embeddings.cs`.
- **Packers & publishers**:
  - `graph-artifact.cs <reco|coread|...>` and `publish-graph.ps1 -Graph reco|coread|both` pack and publish artifacts. Tags are hardcoded per graph and must match `ReleaseTag` constants on the installers.

## Critical operational gotchas

- **File-based C# app cache (`%TEMP%\dotnet\runfile`)**: editing defaults in `src/` and re-running a `.cs` tool silently scores the **previous** build. Delete `%TEMP%\dotnet\runfile` when comparing `default` against edited defaults.
- **Bare variant name trap**: in `eval-reco-labels.cs`, passing `-- library farthest medoid` runs `default` twice; overrides need `name:key=value` (e.g. `medoid:seedselection=medoid`). Variant prefixes: `graph` for `RecoGraphTuning`, `coread` for `CoReadTuning`; shorthands `nograph`, `nocoread`, `nocrowd`.
- **`.artifacts/` vs `{ConfigDir}`**: fetchers and builders write to `.artifacts/`. `eval-reco.cs` reads installed `reco-edges.db`/`coread-edges.db` from `MAKI_CONFIG_DIR` (e.g. `.simulated/`) and silently no-ops if they are absent; copy them in by hand when testing against `.simulated/`.
- **Privacy gates (non-negotiable)**: `coread-graph.db` holds millions of per-user reading rows (`user_entry`, `user_state`, `pending_user`) and must **never** leave the machine. `graph-artifact.cs` and `CoReadInstaller` refuse any file with those tables before shape checks, and `force` never bypasses it. Only `pair` and `meta` ship.
- **SQLite NaN check**: always write `strength IS NULL OR NOT (strength > 0)`, never `strength <= 0` (SQLite stores NaN as NULL, which `<= 0` silently passes).
- **Fold partitioning**: `UserFold.Of` (FNV-1a, not `HashCode.Combine`) is duplicated verbatim in `build-taste-vectors.cs` and `eval-reco-labels.cs` and **must stay byte-identical**. Never grade an artifact whose `meta.trainingFold` includes the evaluated fold.
- **Locale pinning**: `eval-search.cs` pins `CultureInfo.InvariantCulture` so comma-locale machines do not emit uncomparable tables.
- **Grader integrity & metrics**:
  - `eval-reco-labels.cs` force-disables the graded channel (`reco` kills vote graph, `coread` and `library` kill co-read).
  - Never read recall, nDCG, or diversity without `pop` (median popularity rank) and `--strata` (seed popularity strata). Read `--feel` on `single` and `small` (it saturates in `library` mode except franchise rate).
  - Grade fame/sameness changes on `CURRENT` (or `CURRENT,COMPLETED`) shelves too; `COMPLETED` shelves hide ongoing-hit crowding.
  - `--labels mu` (tag-derived) cannot grade tag changes; use `--labels mu-human`.
  - Acceptance for ranking changes is `library --next` + `eval-next-gates.py`, not plain nDCG@40.
- **AniList & MAL fetcher traps**:
  - AniList live ceiling is 30 req/min (`Pacer.Observe` clamps to 0.8 of `X-RateLimit-Limit`). Batching aliases (`--batch 20`, capped 25) is the only throughput lever; latency-bound, not budget-bound.
  - One private AniList user in a batch 404s the whole request and nulls every alias; never trust a null above batch size 1. Recover offenders via GraphQL `locations[].column` on `status: 404` only.
  - `fetch-coread-graph.cs build` uses hash partitions (`PairsPerPass = 12M`) to bound memory; print progress in any multi-minute phase.
  - MAL has no working API; `fetch-reco-graph.cs` scrapes `manga/{id}/_/userrecs` with browser UA + gzip at 30 req/min. AniList and MAL votes are different units and are rescaled by per-provider p90 in `RecoGraphCache.ReadPairs`; `meta.providers` must be derived from actual edges, never hardcoded.
- **Staggered artifact jobs**: startup jobs are staggered (prebuilt index, reco graph, co-read graph, taste vectors last because installing taste vectors invalidates `VectorIndexCache`).

## Experiment history

All measurements, sweeps, and rejected experiments from earlier iterations are preserved verbatim in `distribution/history/`:

| File | Topics |
| --- | --- |
| [history/pre-v4-and-graphs.md](history/pre-v4-and-graphs.md) | Search RRF/popularity/tag tuning, 4-bit vector index, vote & co-read graphs, genre cosine fix, seed weights, `MaxSeedQueries`, `SeedSelection` |
| [history/v4.md](history/v4.md) | MangaUpdates labels (`mu`/`mu-human`), `--feel`, hierarchical tags (not shipped), behavioural taste vectors (shipped), franchise collapse (not shipped), credits, familiarity dial, reco passage (not shipped) |
| [history/v5.md](history/v5.md) | Reader cohorts, negative signals / avoid channel (`v5`, `v5.1`, `v5.2`, `rel-p12` shipped), `v5.3` 52k-reader re-sweep |
| [history/v6-v7.md](history/v6-v7.md) | `v6` 256-dim embeddings, `v7` judged panel, cleaned plot text (not shipped), EASE item-item channel + popularity damping, votes on the series rail, `--shelf-judged` |
| [history/v8.md](history/v8.md) | Shelf-relative fame cap, page-share penalty, `library --next` grader & gates, card captions (not changed), EASE at weight 1, `reco-profile-probe.py`, wider EASE table (not shipped) |
| [history/v9.md](history/v9.md) | MangaUpdates quality priors (not shipped), EASE `--statuses`, calibrated re-rank & side-taste lanes (`lanes2` shipped at 0.5), `tastemissing` (not shipped), co-read per-user cap |
