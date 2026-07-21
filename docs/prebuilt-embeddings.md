# Design note — shipping a prebuilt embedding index

Status: **built and verified end to end.** Written 2026-07-21.

Publishing (`distribution/publish-embeddings.ps1`) and installing (`PrebuiltIndexInstaller`,
`PrebuiltIndexJob`, Settings → Recommendation index) both exist. Verified against the real
95,835-series index served over HTTP: downloaded, checksum-verified, swapped in and searched in
5 s, returning results identical to the locally-built index.

Not yet done: nothing is published at the default URL, so the job no-ops until the first upload.

## Why

`embeddings.db` is a pure function of two public inputs: the MangaBaka dump and a pinned
embedding model. Every install derives byte-identical vectors from identical inputs, and on a
mid-range CPU that costs **~55 minutes for ~96k series** (measured: 29 rows/s with
bge-base-en-v1.5 int8). Nothing about it is user-specific.

Maki already solved this shape of problem once: `mangabaka.db` is downloaded nightly rather than
built locally. This proposes the same treatment for the vectors.

Payoff: first-run search and semantic recommendations go from *an hour of CPU* to *a download*.
That is a bigger, simpler win than GPU offload, which would need a second model export (GPU
execution providers support S8S8 only, so our int8 model would fall back to CPU), vendor-split
packages, and per-vendor Docker variants.

## Non-goals

- Replacing local indexing. It stays as the fallback for air-gapped installs, users on the
  MangaBaka API instead of the local dump, and any incompatible or unreachable artifact.
- Shipping anything user-specific. The artifact is derived public data, nothing else.
- Bundling the file into the Docker image (see below).

## Why not bundle it in the image

The original suggestion was to ship the file with each deployment and overwrite on update. Two
problems:

1. Image layers are immutable per release, but this data moves with the nightly dump. Bundling
   means republishing the image nightly, and every user pulling the payload again per release.
2. It couples data freshness to release cadence. A user on a three-week-old release gets
   three-week-old vectors, with no way to refresh without upgrading.

A separately-downloaded artifact with its own refresh job decouples the two, and reuses the
staging/swap machinery already in `MangaBakaDumpService` (`.partial` staging → sanity check →
`File.Move` → timestamp in settings).

## Size: ship int8, not float32

Vectors are near-random floats and compress badly — **measured 72%** (zlib-6, 60 MB sample of
real `embeddings.db` data); zstd lands in the same range.

All measured on the full 95,835-series index at 768 dims:

| format | on disk | compressed (zstd-10) | notes |
|---|---|---|---|
| float32 (was) | 401 MB | 283 MB | too heavy to refresh regularly |
| **int8 + per-row scale (now)** | **106 MB** | **67 MB** | what search already uses in memory |

Shipped: the conversion is in `EmbeddingStore.EnsureSchema` and runs in place on first start
(3 s for the full catalogue, no re-embed — quantizing stored vectors is pure arithmetic). It must
`VACUUM` afterwards: shrinking rows leaves the freed bytes stranded inside their pages, so
without it the file stays at 401 MB and the whole exercise gains nothing.

Building the in-memory search index also got faster (7.8 s → 3.9 s), since the payload now copies
in verbatim instead of being parsed from float32 and re-quantized.

Search quantizes to int8 on load anyway (`EmbeddingMath.Quantize`), and a quantized round-trip
agrees with the float32 cosine **to three decimals** (`VectorIndexTests.Quantize_RoundTrips_WithinTolerance`) —
far finer than the gap between adjacent results. Paying 3.5× the bandwidth to ship precision that
is discarded at load makes no sense.

### How it works

`series_vectors` is `(id, hash, scale REAL, vec BLOB)` with an int8 payload, so the artifact *is*
the database — no conversion step on install. Readers (`GetVector`, `GetMeanVector`, …) still hand
back `float[]`, so `SemanticRecommender` was untouched; the only visible change is that a
round-trip is accurate to ~3 decimals instead of exact, which is far below what ranking notices
(a normalized round-trip holds cosine 1.0000 against the original).

No `ModelVersion` bump: the *storage* changed, not the model, and the stored `hash` values are
preserved, so no user re-embeds anything.

## Artifact

Two published files per build:

- `embeddings-<modelVersion>-<dumpDate>.db.zst` — the database.
- `manifest.json` — small, polled frequently so the client can check compatibility and freshness
  without pulling ~80 MB.

```json
{
  "modelVersion": "bge-base-en-v1.5-q3",
  "dimensions": 768,
  "quantized": true,
  "dumpDate": "2026-07-20",
  "rowCount": 95835,
  "generatedAt": "2026-07-21T02:00:00Z",
  "sha256": "…",
  "sizeBytes": 83421000,
  "url": "https://…/embeddings-bge-base-en-v1.5-q3-2026-07-20.db.zst"
}
```

The same fields go in a `meta` table **inside** the database, so a file that arrives by any route
still self-describes.

Contents: `series_vectors`, `series_tags`, `tag_vocab`. The tag tables are derived too and are
small; shipping them saves the tag-vocabulary pass as well. The `hash` column must be included —
without it the user's next incremental pass can't tell what's current and would re-embed
everything, defeating the point.

## Install sequence (implemented in `PrebuiltIndexInstaller`)

1. **Poll** `manifest.json` (ETag / `If-None-Match`).
2. **Compatibility gate** — require `modelVersion == EmbeddingOptions.ModelVersion` **and**
   `dimensions == options.Dimensions`. This is the critical guard: a 384-dim file dropped into a
   768-dim build has every row filtered as wrong-width, and search goes silently empty while
   falling back to title matching. Refuse, log once, leave local indexing alone.
3. **Freshness gate** — skip if `generatedAt` is not newer than the recorded local marker, and
   skip if the local index is already complete for the user's dump. Never install *older* than
   what's on disk; that would throw away work and move backwards.
4. **Download** to `{ConfigDir}/cache/embeddings.db.partial`, decompress, then verify: sha256
   against the manifest, `PRAGMA quick_check`, `meta` matches the manifest, row count within
   tolerance of the stated count.
5. **Quiesce** — refuse the swap while `EmbeddingIndexStatus.Running`; take
   `VectorIndexCache`'s build lock so no reader is mid-build.
6. **Swap** — `File.Move(overwrite: true)`, and delete stale `-wal` / `-shm` sidecars. Stores open
   with `Pooling=False`, so no connection outlives its call.
7. **Invalidate** `VectorIndexCache`, record `embeddings.prebuiltGeneratedAt` in settings.

Overwriting is safe here in a way that restore is not: every byte is derived public data, so a
bad swap costs CPU time, never user content.

### Dump skew

The artifact is generated against dump date D; the user's dump may be a day off. The build joins
vectors to the dump, so extra vectors are dropped and missing ones are simply absent — the next
incremental pass embeds those few locally in seconds. No special handling needed; just don't
require the dates to match.

## Cadence and cost

Nightly full artifacts would be ~80 MB × every user × 365. Better: **publish weekly, let the local
incremental pass cover the daily delta.** Once the bulk exists, a day's new series is a few
hundred rows — well under a minute locally. That cuts hosting ~7× with no meaningful freshness
loss. Per-dump-date delta artifacts are a possible v2 if even weekly proves heavy.

## Generation — maintainer-run, not CI (implemented)

CI generation was considered and set aside: the maintainer already runs a full index locally, so
the simpler path is to publish *that* file. Built:

- `distribution/publish-embeddings.ps1` — validates, packs, writes the manifest, and uploads to a
  GitHub release tag via `gh`. Dry run by default; `-Publish` uploads behind a y/N gate.
- `distribution/embeddings-artifact.cs` — a .NET 10 file-based app doing the SQLite validation and
  zstd compression. It exists because **Windows PowerShell 5.1 is .NET Framework and cannot load
  the project's .NET 10 SQLite assemblies**, so the script cannot inspect the database itself.

Refusals that matter (all proven against a live database): mixed vector widths (a model change
mid-migration), vectors whose width disagrees with `EmbeddingOptions.cs`, an empty `tag_vocab` or
`series_tags` (pass never finished), a failed `PRAGMA quick_check`, a row count under the floor,
and a non-empty `-wal` sidecar meaning Maki is still writing.

A CI workflow remains possible later if generation should stop depending on one machine; it would
need the dump (~3.5 GB) cached or re-downloaded per run, and a first build of ~1 h.

## Trust

Same posture as the MangaBaka dump — a downloaded SQLite file from the project's own release
channel over HTTPS — so this adds no new trust boundary. It does deserve: a sha256 in the
manifest, `PRAGMA quick_check` before the file is used, and, if a URL override env var is offered
for self-hosters, documentation that it must point at a trusted source. A hostile `embeddings.db`
is a hostile SQLite file.

## Failure modes

| failure | behaviour |
|---|---|
| manifest unreachable | log at debug, keep local indexing; retry next cycle |
| model version / dimension mismatch | refuse install, log once, local indexing continues |
| download truncated or sha mismatch | discard staging file, no swap |
| `quick_check` fails | discard, no swap |
| indexing pass running | defer swap to the next cycle |
| artifact older than local | skip |

## Rollout

1. ~~**Int8 native storage**~~ — done (401 MB → 106 MB per user, index build 7.8 s → 3.9 s).
2. ~~**Generator + manifest**~~ — done, maintainer-run rather than CI.
3. ~~**Client fetch/install + settings toggle + refresh job**~~ — done, default on.
4. *(optional)* Delta artifacts, if weekly full downloads prove heavy.

## Remaining

- **Publish the first artifact.** Until something exists at `PrebuiltIndexInstaller.DefaultManifestUrl`,
  the job simply no-ops and everyone builds locally as before.
- **Decide the cadence.** Weekly is the recommendation above; the local incremental pass covers
  the days in between.
- The `url` field in the manifest is written by the publish script from `gh repo view`, so a fork
  publishing its own artifact gets its own URL for free — but the *default* URL compiled into the
  client still points at this repo. Forks need the `recommendations.prebuilturl` setting.
