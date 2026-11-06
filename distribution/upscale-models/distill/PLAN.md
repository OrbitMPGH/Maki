# Upscaler distillation plan

A 2x model for Maki's server enhance that looks like Real-ESRGAN (realesr-animevideov3), handles badly
damaged sources as well as it does, and runs clearly faster on CPU. Target: a paged-manga page in 3-4 s
on the owner's NAS (i5-14500, 10 threads), memory constrained, CPU only (GPU or iGPU is a bonus).

Agreed 2026-11-05 between the lead session and a Fable 5.1 reviewer, after three rounds. Update this file
as runs finish.

## Ground rules

- The owner judges by eye on real pages. PSNR has twice said "close" while the owner saw "clearly worse".
  Numbers decide which run to show; the owner's eye decides what ships.
- The owner rejects grids, mottling, blotches, grain and halos. Any change that risks those needs a
  reason and a check on the reference set.
- Library crops and pages never leave the machine and are never committed. Everything lives in
  `.artifacts/upscale-distill/`.
- Data processes stay under about 2 GB of RAM (one test pushed the machine to 99%). Builders stream to
  disk with `sets.RowWriter`, readers use `sets.DiskArray`, the trainer moves sets to the GPU in chunks.
- Screentone: flat grey, like Real-ESRGAN (owner's choice). Real-ESRGAN flattens even dots the source
  resolves (bake-off page 05).

## What has been tried

| Round | Recipe | Result |
|---|---|---|
| One | Distil Real-ESRGAN on 8,822 library crops (`train.py`) | c48n8: 39.6 dB to the teacher over 40 pages, 2.7x faster in Maki. "Pretty good" |
| Two | Blend Real-ESRGAN and DAT2 labels plus volume ground truth (`train_mixed.py`) | Rejected: softer, faint grid (teachers disagree on tone, L1 to real pages averages) |
| Three | One teacher; damaged volume crops aimed at the teacher on a clean half (`train_look.py`) | Rejected: grid on tone. Real-ESRGAN invents circles and mazes on aliased clean tone |
| Four (first) | Damaged library crops aimed at the teacher on the clean original | Stopped: -1.3 dB on clean for +0.5 dB on damage (heavy targets unreachable) |
| Four (served) | Damaged copies aimed at the teacher on that same damaged input (`make_served.py`) | +0.30 clean, +1.04 damaged, 39.84 dB over 40 pages. Clean on B&W. Owner: colour webtoon "way worse", faint JPEG blotches in flat fills |
| Colour | Round four plus 5,867 colour crops and damaged copies, equal shares (`train_served.py`) | +0.22 to +0.66 dB on every set; owner: "doesn't seem all that better" |

Findings that shaped the plan:

- Fine-tuning round four on one page for a few minutes removed its blotches, so the gap is at least
  mostly training data (Fable reads this as suggestive, not proof of enough capacity).
- The old builder dropped crops with RGB std under 2. A blotchy flat fill sits at about 1 to 3, so no
  set ever held a "teacher smooths this fill" example. That is why more colour pages moved little.
- Uniform crops, per-pixel L1, PSNR and the bake-off's busiest-window crops all favour edges. The
  complaints are all about flat areas.

## The plan

### Step 0: measurement (no training)

Extend `evaluate.py` with a pages option. All at 2x, on luma after Maki's colour pass. T teacher, S
Lanczos 2x source, P student, R = P - T.

Flat-fill mask, 15x15 pools on teacher luma (reflect padding):
- `range15(T) <= 10` (no line within 7 px; a range, not a std, so a thin line cannot hide)
- `10 < mean15(T) < 245` (drops paper white and ink black)
- eroded with a 9x9 min filter
- `flat_damaged` = the above and `mean15(|S - T|) >= 0.75` (the teacher changed something here)

Sanity before trusting it: `flat_damaged` covers 5-30% of the owner's webtoon page, and its overlay
includes the dark sleeve and the jacket folds.

Per page:

| Number | Definition |
|---|---|
| pooled16 mean, p95 | mean and 95th percentile of `|avg_pool16(R)|` on `flat_damaged`, blocks aligned to the page origin |
| removal ratio | `1 - pooled16(P - T) / pooled16(S - T)` on the same mask |
| edge MAE | mean `|R|` where `sobel(T) > 24`, dilated 3x3 |
| whole-page PSNR | for continuity only |
| coverage | share of the page in `flat_damaged` |

Contact sheet per page: the three worst 128x128 windows by pooled16 inside the mask, columns Lanczos,
student, teacher, residual x8 around mid grey, nearest-neighbour 3x.

Pass bars:
- Against the teacher: pooled16 p95 <= 1.5 levels and removal ratio >= 0.85, on the owner's page and on
  the mean of colour validation pages with coverage over 3%.
- Against the colour run: pooled16 mean down at least 30%, edge MAE up no more than 3%, whole-page PSNR
  not below it.

Reference set: the owner's webtoon page, five colour validation pages with coverage over 3%, five B&W
pages including bake-off page 05. Owner verdicts go in a JSON beside the shots. Every page the owner
judges from now on joins it.

First measurement: Lanczos, round four, the colour run.

#### Step 0 result (2026-11-05, `measure.py`)

The flat-fill mask above passes the coverage check (19.2% of the owner's page) but **misses the
blotches the owner pointed at**. Faint creases in a fill count as lines under `range15 <= 10`, so the
fill beside every crease is cut out, and that is where the blotches sit. By this mask the colour run
already passes both bars (owner's page p95 1.00, removal 91.0%; colour pages p95 0.70, removal 92.9%),
which is the same "numbers say close, the eye says worse" failure the ground rules warn about.

Added, and used as the primary number from here on:

- `fill` mask: `10 < mean15(T) < 245`, and more than 5 px from a line with a step of 16 levels or more
  (`dilate11(sobel(T) > 64)` removed). Keeps faint creases and the fill around them. Covers the jacket,
  its folds and the sleeve on the owner's page.
- `blotch`: `|avg7(R) - avg31(R)|` on `fill`, mean and p99. A band-pass: blotch-sized structure, not
  pixel noise and not a whole-area offset of a level (the old pooled16's worst windows on the owner's
  page were a uniform 1-level offset in a gradient, invisible). `blotch removal` is `1 - blotch(P) /
  blotch(S)`, Lanczos against the teacher on the same mask.
- Contact sheets pick the worst windows by `blotch` on `fill`. On the owner's page they land on dots in
  the dark sleeve, cross-shaped patches on skin and spots in hair, each smooth in the teacher.

| model | owner blotch | removal | colour (5) blotch | removal | B&W (5) blotch | removal | owner edge MAE | owner PSNR |
|---|---|---|---|---|---|---|---|---|
| Lanczos | 0.758 | 0% | 0.355 | 0% | 1.305 | 0% | 28.01 | 26.58 |
| round one `c48n8` | 0.403 | 46.9% | 0.187 | 43.5% | 0.511 | 60.3% | 5.45 | 38.59 |
| round four `c48n8-served` | 0.342 | 54.9% | 0.183 | 39.5% | 0.480 | 62.6% | 4.38 | 40.43 |
| colour run `c48n8-colour` | 0.324 | 57.2% | 0.169 | 45.7% | 0.458 | 64.3% | 4.19 | 40.81 |

The owner's verdicts line up with this column and not with the old one: every student leaves about
half of the source's blotch structure. Working bar until the owner's eye says otherwise: blotch
removal at least 80% on the owner's page and on the colour mean, edge MAE up no more than 3%.

Reference set (`reference/pages.json`): `owner-webtoon`, colour-1 to colour-5 (`val_pages_col2` 000,
001, 002, 003, 005; flat_damaged 38%, 68%, 7%, 8%, 61%), grey-1 to grey-5 (`val_pages` 005, 009, 016,
019, 039; 005 is bake-off page 05). Verdicts in `reference/verdicts.json`.

Fill crops are not scarce. With the fill mask (crop_stats.py, at least 50% of the target), 37% of
colour crops qualify: 3,185 in col2 and 2,157 in col_, so no targeted build. The colour run already
trained on that share and still blotches, which points at the loss more than the data: A2 matters
more than A1. The plan's own colour-flat rule (std under 8, flat mask) finds only 210 crops in col2.

### Data

- `col2_` (built: 8,592 train and 798 val crops, 20 whole val pages in `data/val_pages_col2`): colour pages from all three libraries (the NAS library, `X:\meenge`,
  `X:\MangaVolumes` including Sweet Home, Eleceed, SSS-Class Revival Hunter, The Beginning After the
  End, A Returner's Magic Should Be Special, Overgeared), RGB std >= 0.5, up to 20,000 crops.
- Split col2 at load time into "colour flat" (luma std in [0.5, 8), luma mean in (10, 245), and at least
  50% flat-mask coverage by the step 0 definition) and "colour" (the rest). Old `col_` stays in "colour".
- If col2 gives fewer than about 5,000 colour-flat crops, also run a targeted build: luma std band,
  6 candidates per page, up to 6,000 crops, kept disjoint.
- Damaged copies of the new colour sets (`make_served.py`) get their JPEG saves at a random 0-7 px
  block-grid offset (reflect-pad, save, crop back), since Maki's tiles start anywhere. Existing damaged
  sets and validation inputs stay as they are; apply the offset everywhere at the next full rebuild.
- `flat_train`: 512 uniform crops with values 0-255, target equals input, so the student can keep
  Maki's flat-tile skip exact for any grey (Real-ESRGAN only qualifies for 0 and 255).
- The series split ignores release tags in brackets but keeps years (`make_dataset.series_key`), so a
  series under two folder names never lands on both sides. No existing series changed side.

### Run A1: data only

From `student-c48n8-colour.pt`, plain L1 on luma, 40,000 iterations, lr 2e-4 one-cycle, batch 32, slack
0.1 dB on every validation set including the new ones. Shares (`--weights`):

| Set | Share |
|---|---|
| clean B&W | 15 |
| damaged B&W | 25 |
| clean colour | 15 |
| damaged colour | 20 |
| clean colour flat | 8 |
| damaged colour flat | 15 |
| exact flats | 2 |

### Run A2: loss and regularisers

Same init, data and shares as A1, so A1 against A2 isolates these:
- loss `|r|.mean() + 2.0 * |avg_pool2d(r, 8)|.mean()` with `r` the centre luma residual; the pooled term
  charges coherent block bias (a blotch) and ignores incoherent pixel noise
- EMA 0.999 every step; score and save the EMA weights
- one random flip and rot90 per batch, applied to input and target together
- a Sobel term at 0.1 only if edge MAE rises more than 3%

Measure A1 and A2 with step 0, show the owner the better one that passes.

### Overnight queue (2026-11-05, `.artifacts/upscale-distill/logs/queue.sh`)

Started while the owner was away, each step logging to `logs/<tag>.log` and `logs/queue.log`:

1. measure A1 (`c48n8-a1`)
2. A2 as above (`--pooled 2 --ema 0.999 --flips`), measure
3. A3: A2 plus `--band 6`, measure.py's blotch band-pass as a loss term on the whole crop
4. A4: the better of A2 and A3 by mean blotch removal (owner page and colour pages) with owner edge MAE
   within 3% of the colour run (`logs/pick.py`), 80,000 more iterations at lr 1e-4, measure
5. self-ensemble labels for the fill crops of col2_train, col_train and their damaged copies
   (`make_ensemble.py`, sets `*_se`)
6. `se`: 20,000 iterations at 1e-4 from the best of A2 to A4 with the fill sets swapped for `*_se`
7. one measure run over every model, for a single table

A3 and the ensemble fine-tune go past the agreed plan: A3 because step 0 showed the loss, not the data,
is the likelier lever; the ensemble set because it is the held-back step whose trigger is the owner's
eye, and computing it while the machine is idle costs nothing. Nothing ships without the owner's look.

A Fable 5.1 review of step 0 and the queue (before any queued step ran) changed it:
- `--slack 0.3` for A2 to the ensemble run: the decision is measure.py's, and the pooled and band terms
  trade per-pixel PSNR, so the 0.1 dB gate could leave a run with no checkpoint at all. Every step is
  skipped, not crashed, when its init checkpoint is missing; measure.py skips missing models.
- `pick.py` also requires owner-page PSNR no more than 0.1 dB under the colour run, since A3 optimises
  the blotch number directly and would otherwise win the pick by construction.
- The ensemble fine-tune keeps whichever band setting its init was trained with (it used to drop it
  whenever A4 won).
- Ensemble labels only for col2 and its damaged copies, written over those sets' fill rows on the GPU
  (`train_served.py --swap`), so they cost no GPU memory; separate `*_se` sets would not have fit.
- measure.py reports, besides `blotch`: `carried`, the share of the source's own band-pass structure
  still in the output (projection onto `band(S - T)`, 1 for Lanczos, 0 for the teacher), which a model
  cannot fake by adding differences of its own; and removal split into pure flat fill (`flat` mask) and
  crease-adjacent fill (`fill` minus `flat`). Reviewer's caveat: `fill` keeps hair-strand and crease
  rendering, so part of the band residual is line rendering, not blotches; the split shows how much.

| model | owner removal | carried | pure | crease | colour removal | carried | pure | crease | owner edge | owner PSNR |
|---|---|---|---|---|---|---|---|---|---|---|
| A1 `c48n8-a1` | 59.4% | 19.8% | 54.3% | 64.1% | 44.9% | 12.7% | 26.7% | 58.9% | 4.01 | 41.17 |

A1 (data only) moved blotch removal 57.2% to 59.4% on the owner's page and 45.7% to 44.9% on colour
pages: the data change alone does next to nothing, as step 0 predicted. Most of what is left is not
carried source damage (20% and 13%) but structure the student adds, worst in pure flat fills.

**The teacher is not a fixed point for blotch removal, so the bar moved again.** A2 (no flips) scored
60.1% / 45.3% blotch removal, barely above A1. Two CPU probes explained why:
- Capacity probe: c48n8 fine-tuned on the owner's page alone (over-fitting on purpose) stalls at 69%
  removal while `carried` falls to 6%. What is left is not source damage.
- Teacher floor: Real-ESRGAN scored against its own output on the mirrored page reaches only **75%** on
  the owner's page and **64%** on colour-2 (PSNR 41.7 dB to itself, about where the students sit); with
  the tile grid shifted half a tile it is 98% (65 dB). The teacher's fill texture depends on orientation,
  so "residual to the teacher" punishes a student for a different but equally smooth rendering, and the
  80% removal bar was out of reach of the teacher itself. Dropped.

New primary number, added to measure.py: `texture`, the output's own `|band(X)|` over pure flat fill
(`fill & flat`), against the teacher's and the source's, and `smoothing` = share of the Lanczos-to-teacher
texture gap closed (only on pages where the teacher is clearly smoother than the source). Owner's page:

| output | texture in pure fill | smoothing |
|---|---|---|
| Lanczos | 0.667 | 0% |
| Real-ESRGAN | 0.407 | 100% |
| round one `c48n8` | 0.485 | 70% |
| colour run | 0.460 | 79% |
| A2 | 0.445 | 85% |

Over the six colour pages the teacher averages 0.324, the students 0.333 to 0.340, Lanczos 0.410. The
students are measurably blotchier than the teacher in flat fills, by about 9% on the owner's page, and
each round has closed part of it. Working bar: smoothing at least 95% on the owner's page and the colour
mean, edge MAE and PSNR held. `pick.py` now picks on smoothing, capped at 100% (smoother than the teacher
is not a goal and could be lost shading).

A second Fable review of this change (adopted, before A3's pick ran):
- No cap: smoother than the teacher inside pure fill is the expected result of orientation averaging,
  and pure fill has no visible shading to lose. The over-smoothing risk sits beside creases and hair,
  so `texture_crease` (same band energy on `fill & ~flat`) must stay within 5% of the teacher's.
- Colour smoothing is pooled over pages (sum of gaps), not a mean of per-page ratios: only three of the
  five colour pages have a teacher-vs-source gap at all, and on colour-3 a 0.005 texture change is 14
  points. Owner-page smoothing within 3 points is a tie. Edge MAE and PSNR are held on the colour mean too.
- A blotch is a few spots, so `texture_p99` and `spots` (share of pure-fill pixels with |band| over 1
  level) sit beside the mean, and the worst-window sheets now pick where the model has more band
  texture than the teacher inside pure fill: the owner's complaint stated as a number, for the owner to
  confirm or reject by eye.
- Reference rows `teacher` and `teacher-ens` (Real-ESRGAN averaged over the page's 8 orientations) in
  measure.py. Whether the owner prefers teacher-ens fills to Real-ESRGAN's own decides whether the
  target should be the ensemble everywhere (flips and the se runs right) or not (stop averaging).
- `logs/followup.sh`, after the queue: the table with both reference rows, 8-orientation labels for every
  crop of train, served_train, col2_train and col2_served_train (`make_ensemble.py --all`, sets `*_se8`),
  and `ens`, a 20,000-step fine-tune on them from the pick, judged from its `-last` weights.

Pick after A3 (`pick.py`, all holds met by both):

| model | owner smoothing | colour smoothing (pooled) | owner blotch removal | colour blotch removal | owner edge | owner PSNR | colour pooled16 mean / p95 |
|---|---|---|---|---|---|---|---|
| A2 (pooled, EMA) | 85.5% | 85.2% | 60.1% | 45.3% | 4.13 | 41.06 | 0.20 / 0.79 |
| A3 (A2 + band 6) | 89.4% | 89.0% | 62.4% | 50.6% | 4.12 | 41.12 | 0.44 / 1.02 |

A3 is the first run to move the fill texture clearly, at no cost to edges or PSNR. Its one regression is
the old pooled16 offset number on colour pages (0.20 to 0.44 levels, p95 still under the 1.5 bar): the
band term ignores whole-area offsets, so fills drift by a fraction of a level. Watch it in A4; if it
grows, add a small plain avg-pool32 term. A4 continues from A3 with the band term.

| A4 (A3 + 80k at 1e-4) | 90.9% | 92.7% | 64.2% | 52.9% | 4.04 | 41.33 | 0.41 / 0.90 |
|---|---|---|---|---|---|---|---|

A4 is the best on every number so far, including owner-page PSNR (41.33, colour run 40.81) and edge MAE
(4.04, colour run 4.19). On the colour pages its mean fill texture equals the teacher's (0.306 against
0.307); on the owner's page it is still 6% above (0.431 against 0.407). The offset drift did not grow.

| se (A4 + 20k, col2 fill targets swapped for 8-orientation labels) | 92.8% | 94.8% | 64.7% | 51.4% | 4.04 | 41.35 | 0.27 / 0.87 |
|---|---|---|---|---|---|---|---|

The ensemble fine-tune is the best again, passes the PSNR gate on every set (+0.01 to +0.03 dB against
A4) and pulls the offset drift back (0.41 to 0.27). Owner-page fill texture 0.426 against the teacher's
0.407. Smoothing on both groups is just under the 95% working bar.

Reference rows (`logs/measure-final.log`): Real-ESRGAN averaged over the page's 8 orientations
(`teacher-ens`) is **as textured in fills as Real-ESRGAN itself** (owner page 0.413 against 0.407, colour
0.308 against 0.307, grey 1.448 against 1.468), at 44.3 dB from the teacher. So the teacher's fill texture
is mostly real structure, not orientation noise; the mirror probe's low "removal" came from the pattern
moving, not from extra texture. The ensemble is a cleaner target (closer to the teacher than any student
or the mirrored teacher), not a smoother one. A2 with flips (`a2f-last`) is worse than A2 on colour
pages (per-page mean smoothing 66.6% against 73.4%), so flips stay out.

`ens` (8-orientation labels on every crop of train, served_train, col2 and its damaged copies, 20k from se,
judged from `-last`) does not beat se: owner-page fill texture the same (0.426), edge MAE worse (4.29
against 4.04), PSNR lower (40.87 against 41.35). The ensemble-everywhere direction is closed.

Spot share inside pure fill on the owner's page (pixels with |band| over 1 level): teacher 6.16%,
teacher-ens 6.13%, colour run 7.84%, A4 7.35%, se 7.24%. se still has about 17% more visible spots than
the teacher there; the colour run had 27% more. Crease texture is within 1% of the teacher for every
model, so nothing is smoothing away faint detail.

`b12` (band weight 12, 20k from se) changes nothing: owner page identical to se (texture 0.426, spots
7.24%), colour pages slightly worse (0.310 against 0.306). The band term is saturated at 6.

**Capacity is not the limit.** The CPU probe, re-run from se and read on the new numbers: c48n8 fitted to
the owner's page alone (plain L1, over-fitting on purpose) reaches the teacher's smoothness within 750
steps: texture 0.401 against the teacher's 0.407, spots 6.08% against 6.16%, edge MAE 4.04 to 3.31. So
the remaining 5% on the owner's page is generalisation, not model size: c48n10 is not needed for this,
and the next lever is training data that looks like that page (its style and its kind of damage), not
another loss term. Never fine-tune on the reference pages themselves; they are the test.

### Where the night ended (2026-11-05, 14:18)

Best: **`student-c48n8-long.pt`** (se + 100k at 1e-4, band 6, col2 fill targets from the 8-orientation
ensemble), exported to `onnx/student-c48n8-long.onnx`. Same c48n8 architecture, so same speed in Maki.

| model | owner smoothing | colour smoothing (pooled) | owner spots | owner edge | owner PSNR | colour PSNR |
|---|---|---|---|---|---|---|
| colour run (what the owner saw) | 79.7% | (82.8% mean) | 7.84% | 4.19 | 40.81 | 42.88 |
| se | 92.8% | 94.8% | 7.24% | 4.04 | 41.35 | 43.21 |
| **long** | **94.4%** | **97.8%** | 7.09% | **3.96** | **41.53** | **43.28** |
| teacher | 100% | 100% | 6.16% | 0 | | |

Every number moved the right way and nothing regressed (crease texture within 1% of the teacher, all
validation sets above the colour run). By eye on the owner's page the leftover marks (a faint scribble
under the hair, smudges in a white sleeve) are lighter than the colour run's but still visible next to
the teacher at 6x. `show/index.html` (local only) has every reference page.

Next, for the owner to decide:
1. Look at `show/index.html`. If `long` is good enough, wire `onnx/student-c48n8-long.onnx` into Maki
   behind the model catalogue and check the NAS timing and `KeepsFlat` (exact-flat check; `flat_train`
   was in every run since A1).
2. If not, the lever is data like the owner's page, since capacity is not the limit (probe above): more
   colour webtoon pages with that kind of damage, from series not in the reference set. Which series and
   which library is the owner's call.
3. Teacher-ens and `ens` show the ensemble is not smoother than the teacher, so no more orientation work.

`logs/followup2.sh` uses the time left: `b12` (band weight 12, 20k from se) and `long` (100k at 1e-4 from
se, band 6 or 12 by the pick), then the full table and `show/index.html` again.

**Flips are out.** A2 as agreed (pooled, EMA, flips) lost 1.47 dB on clean B&W and 0.3 to 0.7 dB on every
other set within 5,000 steps and stayed there, so it never saved a checkpoint (`logs/a2-flips.log`).
Rerun without flips it sits within 0.04 dB at the same step. Real-ESRGAN is not the same on a page and
its mirror (least of all on screentone, where it invents patterns), so a student trained on flipped
pairs is pushed toward an orientation average it is never scored against. The queue was restarted
with A2 and A3 without flips; the flipped A2 is rerun last and its final weights (`-last`, which the
trainer now always saves) are measured, in case the orientation average looks better to the owner.

### Owner verdict on long, and the rethink (2026-11-05 evening)

Owner, on `show/index.html`: long is "still noticeably more artifacty and muddy than Real-ESRGAN.
Real-ESRGAN flattens out much better without leaving artifacts where there should be just a solid
color. It also has more refined lines." Two complaints now: marks in fills (often beside lines, which
`pure` drops), and softer edges and colour boundaries, which edge MAE (3.96, better than the colour run)
does not see.

**Teacher stays animevideov3 (owner's choice).** RealESRGAN_x4plus_anime_6B (RRDBNet, 6 blocks, 4.5M
parameters; `common.load_anime6b`, row `anime6b` in measure.py and show.py) was tried as a candidate
teacher: flatter fills (texture 0.360 against 0.407 on the owner's page) and crisper lines, but a light
outline along some lines, hard-edged flat shapes in pale fills, and a clearly different look (31 dB to
animevideov3). The owner declined it.

**How Real-ESRGAN was trained** (its `docs/Training.md`, `options/train_realesrgan_x4plus.yml`,
`realesrgan/models/realesrgan_model.py`; read, never run): stage 1 L1 only; stage 2 from it, L1 + VGG19
perceptual (weight 1) + GAN (0.1, vanilla) with a U-Net discriminator with spectral norm and a per-pixel
answer; unsharp-masked targets for L1 and perceptual; EMA 0.999; second-order degradation. The anime
models' data and losses are undocumented, and the published data (DF2K, OST) is photographs. Every
student so far was a stage 1.

**Fable's diagnosis** (CPU probe over the 30 show windows): long matches the teacher on every average
(edge gradient ratio 0.992) and loses in the tail. Committed strong-edge pixels 0.597 against 0.627,
near-line fill excess p95 0.82 against teacher-ens's 0.48. That is L1 hedging under ambiguity. Its cause
with a deterministic teacher is reach: the teacher sees 18 low-res px, c48n8 sees 10 (under three JPEG
blocks), too little to tell a blotch to flatten from a crease to keep. The one-page over-fit probe above
does not refute this, since memorising needs no context. A GAN would make the student commit, but a
student that cannot see enough commits to wrong guesses, so reach comes first.

CPU cost in ONNX Runtime, one thread, a 96 + 2x12 px tile (`scratchpad bench_arch.py`, this machine):

| model | ms per tile | against c48n8 |
|---|---|---|
| c48n8 | 16.3 | 1.00x |
| c48n8d (body convs 3 to 6 dilated 2, 3, 3, 2: reach 16 px) | 16.2 | 0.99x |
| c48n12 | 24.0 | 1.47x |
| c64n8 | 27.9 | 1.71x |
| c48n16 | 31.4 | 1.92x |
| teacher (4x, before the halve) | 58.7 | 3.59x |

Mixed dilation rates on purpose: a single rate leaves a pixel grid unseen, which shows as a checkerboard.
c48n8d's 16 px reach is over Maki's 12 px tile pad, so a winner needs a seam check in Maki's tiling.

New in measure.py (second summary table): `committed` (share of strong-edge pixels within 8 levels of
their 5x5 max or min, beside the teacher's), `fill_excess` mean, p95 and p99 over `fill` (not `pure`), and
`blobs` (spots over 1 level that survive a 3x3 opening where the teacher has none). `--cpu` scores while a
run holds the GPU.

They see what the owner saw (`logs/measure-tails.log`; teacher-ens is the floor, since the teacher scores
0 against itself):

| model | owner committed (teacher 0.449) | owner excess p95 | owner blobs (per mille) | colour blobs | grey blobs |
|---|---|---|---|---|---|
| lanczos | 0.041 | 2.31 | 97.5 | 28.3 | 146.5 |
| colour run | 0.438 | 0.93 | 20.3 | 2.71 | 49.3 |
| long | **0.429** | 0.71 | 11.2 | 2.04 | 27.3 |
| teacher-ens | 0.464 | 0.39 | 1.4 | 0.66 | 5.8 |

Long has 8x the floor's blobs on the owner's page, and its edges are **less** committed than the colour
run's: the band term bought smoother fills partly by softening edges. That is the "muddy" the owner
named, and it is the GAN stage's job; `committed` is a hold for every run from here.

Queued (`logs/arch.sh`, then `logs/gan.sh`):
1. Arch sweep, long's recipe from scratch (`--init none`, 40k at 5e-4): c48n8 control, c48n8d, c48n12,
   c64n8. Pass: a candidate closes over a third of the control-to-teacher gap on both `committed` and
   `fill_excess_p95`, with PSNR and `texture_crease` held. If c48n8d passes and c64n8 does not, it is
   reach, at no cost. If both fail, reach and size are not the lever; stop sizing.
2. Stage 2 for distillation (`gan.py`, `--gan`): from long, 20k at 5e-5, weights 0.005, 0.02 and 0.05.
   Discriminator is ours: a small U-Net with spectral norm (32 features, two downsamples, per-pixel hinge),
   on luma, conditioned on the bilinear-upscaled source so it cannot reward an edge the source lacks. No
   unsharp-masked targets (the target is the teacher's look) and no VGG term (texture on flat art; wrong
   feature space). 1,000 steps of discriminator warm-up. Pass: `committed` and `blobs` at least halfway
   to the teacher, owner PSNR down under 0.3 dB, `texture_crease` within 5%, pure-fill p99 and grey-page
   PSNR not worse (grain tripwires). Kill at the first tripwire; nothing past 0.05.
3. If both pass, the GAN fine-tune on the winning shape, 20k.
4. Before the owner looks: ONNX export, Maki benchmark for a dilated or wider model, `KeepsFlat` check.

Own model (asked 2026-11-05): possible, but it would learn its own look, not Real-ESRGAN's, and needs
`descreen.py` targets since real volumes carry dot tone (rounds two and three failed on exactly that). It
is next if 1 and 2 both fail, starting from the best student, not from zero.

#### Arch sweep result (2026-11-05, 18:51, `logs/measure-arch.log`)

All four from scratch on the same budget, so they compare with each other and not with long (which
has the whole earlier chain behind it). Owner page, then colour and grey means:

| model | committed (teacher 0.449) | excess p95 | blobs ‰ | psnr owner | grey psnr |
|---|---|---|---|---|---|
| c48n8 control | 0.357 / 0.400 / 0.372 | 0.86 | 18.3 / 2.5 / 38.1 | 36.93 | 33.99 |
| c48n8d (reach 16) | 0.328 / 0.375 / 0.349 | 0.72 | 10.6 / 1.8 / 39.1 | 36.93 | 33.72 |
| c48n12 (depth) | 0.334 / 0.388 / 0.361 | 0.79 | 16.2 / 1.5 / 33.1 | 37.24 | 34.34 |
| c64n8 (width) | 0.361 / 0.419 / 0.394 | 0.76 | 14.9 / 1.9 / 25.9 | 39.30 | 36.02 |
| long | 0.429 / 0.452 / 0.435 | 0.71 | 11.2 / 2.0 / 27.3 | 41.53 | 37.96 |

**All three fail.** The bar was a third of the control-to-teacher gap on both `committed` (owner
0.388) and excess p95 (0.57). Reach made edges *less* committed (0.328) while cutting the owner
page's blobs 42%; depth also lost commitment; width gained 0.004. So reach is not why the student
hedges edges, and by the kill rule sizing stops. The bigger lever is training history: long beats
every 40k-from-scratch shape on every column, so whatever the GAN stage finds, it should be applied
to long rather than to a fresh shape. c64n8's PSNR lead (+2.4 dB over the control) is mostly faster
learning at 1.7x the CPU cost and does not show in the tails.

#### GAN stage stopped (2026-11-05, 20:55)

gan005 never reached its first eval in two hours: with the discriminator added the GPU sat at 15.9 of
16.3 GB and Windows fell back to system memory (98% busy at 80 W). Stopped at the owner's request,
with the rest of `gan.sh`; no GAN result exists. Before any rerun, move the validation sets off the GPU
(score them in chunks from host memory) and run the discriminator under bf16, then check the first eval
lands at normal speed. On hold until the research plan (`RESEARCH-PLAN.md`, Fable 5.1 xhigh) is in.

### Research plan adopted (2026-11-05, 21:30)

`RESEARCH-PLAN.md` (Fable 5.1 xhigh, literature plus GitHub) is the plan from here; the owner said go.
It withdraws the arch sweep's "reach is not the lever" and "sizing stops": all four shapes ran 40k from
scratch and sat 3.3 dB under long, so the sweep measured learning speed. Work moved to the
`feat/upscale-distill` branch and its worktree `.claude/worktrees/upscale-distill`; data stays in the
main checkout's `.artifacts/upscale-distill` (`common.WORK` resolves it from a worktree).

Step 0, done:
- `common.student()` takes `ps2-` (pixel-unshuffle 2, body at half resolution, Real-ESRGAN's 2x trick)
  and an `e` suffix (ECBSR branches, BN-free, folded into one 3x3 on every forward, so training costs a
  plain conv; `collapse()` before export). Checked: params 716k (`ps2-c96n8e`) and 1.05M
  (`ps2-c96n12e`), reach 21 and 29 measured by gradient, fold exact to 2e-6, long loads into
  `c48n8e` unchanged. `common.margin(spec)` widens the ignored crop border to reach + 3.
- `train_served.py`: one forward per batch instead of one per set (same loss, 3x faster), cudnn
  benchmark, validation sets read from disk at each eval (off the GPU), `--mssim`, `--grad`, `--ldl`
  (`losses.py`), discriminator under bf16, `--d-gain` (it sees detail over the bilinear source x8) and
  `--d-lr` 3e-4, and a stop at the end of warm-up if the discriminator loss is still over 1.95.
  Validation PSNR is now a mean over different 64-crop batches, so start values differ from the old
  logs by up to 0.2 dB on the fill views; compare within a run.
- `measure.py --probes`: `phase` (mean and p99 luma change in fills when the page moves one low-res
  px) and `seam` (PSNR against the same tiling half a tile over). Long scores 0 / 148 dB (its reach is
  inside Maki's 12 px pad, so it is exactly shift-equivariant); the teacher 0.02 / 65.5 dB on the owner
  page. Kill: phase mean over 0.5 levels, or seam more than 3 dB under the teacher.
- GAN smoke test from long with every new term on: runs at 4.5 it/s, 12.8 GB peak, but the
  discriminator reached only d 1.963 by the end of warm-up and the run stopped itself. Gain and learning
  rate need a sweep before step 2.

Step 1, queued (`logs/step1.sh`, started 21:28, about 7 h): `ps2-c96n8e-s1` and `ps2-c96n12e-s1`,
200k from scratch at 5e-4 (19.5 and about 17.5 it/s), then `c48n8e-ctl` 60k from long at 1e-4, then
`measure.py ... --probes` into `logs/measure-s1.log`. Long's recipe and sets throughout. Pass bars are
RESEARCH-PLAN.md section 5 step 1.

That launch diverged: `ps2-c96n8e-s1` learned normally to step 7,500 (mean 33.26 dB) and collapsed at
step 10,000, exactly the one-cycle peak of 5e-4 (val 9 dB, then 5). Stopped with the owner's OK, who
also asked for check-ins so a bad run cannot burn the night. Relaunched 21:38 as `logs/step1b.sh`:
- peak 2e-4, ECB folds computed in fp32 under autocast, `--clip 4` (gradient norm clipped at 4x its
  running average);
- automatic stops in `train_served.py`: a non-finite loss, any validation set 3 dB under its own best
  (`--drop-stop`), and the mean more than 1 dB behind the old 40k `c48n8-arch` control at the same step
  from 20k on (`--behind arch-c48n8.log:1.0:20000`);
- `logs/checkin.sh` beside each ps2 run: at 50k and 100k it copies the checkpoint, runs `measure.py
  --cpu --probes` against long into `logs/checkin-*.log`, notes phase, seam and the tails in
  `queue.log`, and stops the run if the owner page's phase is over 1.0 (50k) or 0.5 (100k) levels, or
  its seam under 59.5 (50k) or 62.5 dB (100k).
With cudnn warmed the ps2 runs go about 33 it/s, so the night is about 4.5 h.

#### Step 1 result for the pixel-unshuffle shape: failed (2026-11-05, 22:36)

`ps2-c96n8e-s1` check-ins (owner page / colour mean / grey mean; long in brackets):

| | 50k | 100k | long |
|---|---|---|---|
| PSNR | 37.04 / 39.76 / 33.99 | 37.03 / 39.99 / 34.16 | 41.53 / 43.33 / 37.96 |
| committed (teacher 0.449) | 0.310 / 0.371 / 0.332 | 0.323 / 0.384 / 0.339 | 0.429 / 0.452 / 0.435 |
| excess p95 (floor 0.39) | 0.52 / 0.25 / 0.83 | 0.44 / 0.23 / 0.74 | 0.71 / 0.29 / 0.85 |
| blobs per mille (floor 1.4) | 4.86 / 1.12 / 22.4 | 3.08 / 0.94 / 15.7 | 11.2 / 2.04 / 27.3 |
| phase mean (bar 0.5) | 0.518 / 0.389 / 1.451 | 0.562 / 0.419 / 1.134 | 0 |
| seam dB (teacher 65.5) | 62.34 / 62.59 / 63.93 | 61.06 / 61.58 / 62.59 | 148 |

Validation mean flattened at 37.9 dB (long 39.8). The 100k check-in stopped it on phase and seam; by the
owner's rule (decide at 100k) `ps2-c96n12e` was skipped too and the queue moved to `c48n8e-ctl`.
Reading: the half-resolution body buys much cleaner fills (blobs halfway to the floor and better) but
pays with soft edges, a sub-pixel pattern that does not train out (worse on the owner page at 100k,
over 1 level on B&W pages) and seams past the bar, and its PSNR stopped climbing 4.5 dB under long.
Per RESEARCH-PLAN.md section 5, step 2 and 3 now run on long's shape (or `c48n8e-ctl` if it passes).

#### `c48n8e-ctl` (ECB branches on long, 60k at 1e-4; `logs/measure-s1.log`, 23:27)

Owner page / colour mean / grey mean, long in brackets: PSNR 41.69 / 43.45 / 38.04 (41.53 / 43.28 /
37.96); committed 0.431 / 0.454 / 0.437 (0.429 / 0.452 / 0.435); blobs 10.3 / 1.53 / 24.5 (11.2 / 2.03 /
27.3); excess p95 0.67 / 0.28 / 0.81 (0.71 / 0.29 / 0.85); phase 0 and seam 148 (it collapses to c48n8).
Small and better on every number at no inference cost; under RESEARCH-PLAN-2's 0.005 commitment bar
for becoming the base, but since it exports as the same c48n8 it is the better starting point either way.

`RESEARCH-PLAN-2.md` (second Fable 5.1 xhigh review, 23:20) pivots the order: loss first, GAN second,
shape last. Its key finding: on the owner page, commitment fell only at the pooled term (0.441 to 0.433)
and the band term (to 0.425), both applied over the whole crop; masking them to the fill region is its
first run. Awaiting the owner's go.

### RESEARCH-PLAN-2 adopted; data moved to X:\MakiModel (2026-11-05, 23:45)

The owner took every recommendation: loss ladder before the GAN, the hybrid's 1.21x cost is open for
later, step B starts from `c48n8e-ctl`, and a passing B arm may go to Maki while the GAN runs. They
also asked for the 94 GB working folder to leave `.artifacts`: everything (data, checkpoints, logs,
reference, venv) is now in `X:\MakiModel` (robocopy verified 31,937 files and bytes equal, the old
folder deleted), `common.WORK` defaults there (`MAKI_DISTILL_WORK` overrides) and `common.MAIN` is
the main checkout for `.simulated`. The moved venv runs as is.

New code: `losses.py` gains `fill_mask` (measure.py's on a batch), `masked_pooled` and `masked_band`
(the two fill terms from fill residuals only), `gradient_variance`, `top_share`, `power_loss`;
`train_served.py` builds every term in one `terms()` and takes `--fill-mask`, `--gv`, `--topk`,
`--feat` (teacher's last body activation through a 1x1, standardised), `--lp`, and `--quarter`
(weights so a term starts at a quarter of the L1), and logs each weighted term at every eval;
`measure.py --pages`; `checkin.py` (generic check-in with a kill expression over measure.py's JSON);
`probe_page.py` (step A2; reproduces measure.py's owner-page numbers for long exactly at step 0).

Queued `logs/stepAB.sh` at 23:46: A1 `measure-chain.log`, A2 `probe-commit.log`, then the B arms
`b1-mask`, `b2-grad`, `b2b-gv`, `b3-topk`, `b4-mssim`, `b5-feat`, `b6-lp` (20k each from the control,
check-in at 10k with RESEARCH-PLAN-2's kill rule), then `measure-B.log` with probes.

Step A result (23:48):
- A1 (`measure-chain.log`): the commitment drop at the pooled and band terms holds on every group.
  Committed owner / colour / grey: a1 0.441 / 0.461 / 0.446, a2 0.434 / 0.456 / 0.441, a3 0.425 /
  0.451 / 0.436, long 0.429 / 0.452 / 0.435 (teacher 0.449 / 0.480 / 0.445). a1 already matches the
  teacher on grey.
- A2 (`probe-commit.log`, long memorising the owner page, plain L1): committed 0.440 at 500 steps, 0.447
  at 1000, 0.442 at 1500 (teacher 0.449); blobs 11.2 to 2.1 (floor 1.4); PSNR 44.1. The target is inside
  the c48n8 shape, so step B is the right place to look and step D stays on its trigger.

Step B result (02:16, `logs/measure-B-last.log`, final weights; owner / colour mean / grey mean):

| arm | committed (teacher .449/.480/.445) | blobs per mille (floor 1.4/0.66/5.8) | excess p95 | PSNR | pure texture vs teacher |
|---|---|---|---|---|---|
| long | .429 / .452 / .435 | 11.2 / 2.03 / 27.3 | .71 / .29 / .85 | 41.53 / 43.28 / 37.96 | 1.036 / 1.019 / 1.045 |
| c48n8e-ctl | .431 / .454 / .437 | 10.3 / 1.53 / 24.5 | .67 / .28 / .81 | 41.69 / 43.45 / 38.04 | 1.030 / 0.993 / 1.036 |
| b1-mask | .441 / .460 / .446 | 7.19 / 1.31 / 16.2 | .59 / .27 / .72 | 41.54 / 43.24 / 37.84 | 1.004 / 0.978 / 1.024 |
| b2-grad | .442 / .462 / .446 | 7.50 / 1.39 / 16.6 | .59 / .27 / .72 | 41.61 / 43.31 / 37.92 | 1.007 / 0.982 / 1.024 |
| b2b-gv | .442 / .458 / .442 | 7.95 / 1.34 / 18.1 | .62 / .26 / .73 | 41.56 / 43.30 / 37.90 | |
| b3-topk | .442 / .460 / .445 | 7.73 / 1.58 / 17.4 | .61 / .27 / .72 | 41.62 / 43.33 / 37.93 | |
| b4-mssim | .438 / .459 / .441 | 8.10 / 1.64 / 19.4 | .62 / .27 / .74 | 41.57 / 43.31 / 37.92 | |
| b5-feat | .441 / .461 / .446 | 7.54 / 1.39 / 16.5 | .59 / .27 / .72 | 41.53 / 43.23 / 37.82 | |
| **b6-lp** | **.446 / .464 / .448** | 8.28 / 1.30 / 17.9 | .62 / .27 / .74 | 41.60 / 43.39 / 37.93 | 1.015 / 0.987 / 1.028 |

All but b4 pass RESEARCH-PLAN-2's bar (b4 misses committed 0.439 on the owner page); phase 0 and seam 148
for all (c48n8 shape); texture_crease within 0.5% of the teacher for all. By the plan's pick score
(share of the commitment gap plus share of the blob gap, owner and colour averaged): b6-lp 1.06, b2-grad
0.93, b1-mask 0.92, b5 0.88, b2b 0.82, b3 0.81. Masking alone (B1) did most of it, as predicted; the
mode-seeking pixel loss (B6) is the only arm that moved commitment further, to within 0.003 of the
teacher on the owner page and past it on grey. Note: the queued `measure-B.log` scored the gated
checkpoints, which for these arms were the step-2,500 weights (a loss trade never beats the first
eval's mean); `train_served.py` now also writes `-last` at every eval and `checkin.py` reads that.

`show/index.html` rebuilt (local only): long, b1, b2, b6, teacher and ens-last on each page's worst
windows by long's fill texture.

Maki prep for b6 (`export_onnx.py`, `onnx/student-c48n8-b6.onnx`, 679 KB, plain c48n8 layout): ONNX
matches the torch model to 4e-7; one-thread ORT 16.4 ms per 120 px tile (long 17.4 ms on the same run).
`KeepsFlat` fails for b6 and for long alike: a flat tile of any grey value comes back up to one level off
in the kept centre (0 gives 0..1, 9 gives 8..10), so the student must not join `KeepsFlat`, or Maki
fills skipped tiles a level off their neighbours.

Step C queued (`logs/stepC.sh`, 02:28): from b6, four GAN arms 20k at 5e-5 (gan 0.005 and 0.02, each
with and without LDL, LDL first), with the new protocol (`--d-mask`, `--d-noise 0.5`, gain 16, D lr 2e-4,
2,000-step warm-up judged by `--gap-min 0.05`). Smoke test from b6: gap +0.020 at 500 steps, +0.098 at
1,000 (58% of masked pixels real > fake), so the discriminator now learns. Check-ins at 5k, 10k and 15k
on the owner page and grey-1, kill rule relative to b6 (`texture > 0.4256 or spots > 0.0755 or psnr <
41.30 or crease off 5% or blobs > 8.27 or grey_psnr < 40.62`). About 5 it/s, so about 4.5 h.

Step C result (03:35). The discriminator learned in every arm (logit gap +0.07 to +0.09 by step 2,000,
62 to 72% of masked pixels real > fake). Every arm was stopped by a check-in:

| arm | stopped at | owner committed / blobs / PSNR | why |
|---|---|---|---|
| c1 gan 0.005 + LDL | 5k | .445 / 9.59 / 41.57 | blobs |
| c2 gan 0.02 + LDL | 5k | .450 / 8.79 / 41.62 | blobs (+6%) |
| c3 gan 0.005 | 10k | 5k: .450 / 8.16 / 41.59; 10k: .442 / 9.26 / 41.33 | blobs, PSNR, crease at 10k |
| c4 gan 0.02 | 5k | .435 / 11.9 / 40.90 | everything |

Full measure of the two 5k checkpoints (owner / colour / grey; b6 for reference):
- b6: committed .446 / .464 / .448, blobs 8.28 / 1.30 / 17.9, PSNR 41.60 / 43.39 / 37.93.
- c3 at 5k: committed .450 / .469 / .452, blobs 8.16 / 1.54 / 18.7, PSNR 41.59 / 43.34 / 37.84.
- c2 at 5k: committed .450 / .469 / .451, blobs 8.79 / 1.60 / 21.9, PSNR 41.62 / 43.33 / 37.87.
Phase 0 and seam 148 for both. So a short GAN buys the last 0.004 of commitment (teacher level on the
owner page) for a few percent more blobs on colour and grey, and longer GAN training degrades it; LDL did
not help here. None passes the plan's C bar (blobs within 2x the floor), which no model is near.
`show/index.html` rebuilt with long, b1, b6, c3 at 5k, c2 at 5k, teacher and ens-last. The owner picks
between b6 and c3 at 5k by eye.

### Fills next (owner, 2026-11-06 03:40: "I want to clean up marks in fills more")

`logs/stepD.sh`, all from b6: D1 `d1-band12` (masked band term 12) and `d1-p4b12` (pooled 4, band 12),
20k each, check-in at 10k (kill: committed under 0.441, blobs over b6's 8.28, PSNR under 41.30, crease
off 5%); D2 `hyb-c48n8e-b64n4-d2`, RESEARCH-PLAN-2 step D's hybrid (`common.Hybrid`: long's trunk plus
a 2x2-pooled branch of four 64-channel convs, zero-initialised 1x1 projection, bilinear upsample, added
after body conv 4), 60k with b6's loss, check-ins at 20k and 40k with probes (kill adds phase over 0.5
on the owner page or 1.0 on grey-1, and seam under 62.5 dB). Checked before launch: step 0 equals b6
exactly, empirical reach 19 (margin 22), ECB fold exact, ONNX 19.5 ms against b6's 16.1 ms (1.21x).
Then `measure-D.log` with probes.

### Owner's screenshots: static and jagged lines (2026-11-06, 04:00)

On B&W pages (c2 against the teacher) the owner circled large grey panels that are "staticy" where
Real-ESRGAN is solid, and a thin line that is jagged where Real-ESRGAN's is straight. `measure.py` could
not see it: `blobs` opens away anything under 3 px and `texture` averages over 7 px. `grain.py` (and now
`measure.py`'s `grain`, `grain_teacher`, `grain_p99`, `line_hf` in every row) scores |x - box3(x)| in pure
flat fill. Every student has it, not only the GAN (owner / colour mean / grey mean):

| model | grain (teacher .204 / .154 / .279) | grain p99 grey (teacher 0.98) |
|---|---|---|
| long | .206 / .264 / .458 | 2.01 |
| b6-lp | .192 / .285 / .412 | 1.63 |
| c3 at 5k | .198 / .279 / .409 | 1.58 |
| c2 at 5k | .193 / .251 / .424 | 1.73 |

1.5-1.8x the teacher on colour and B&W pages, equal on the owner's own page, which is why tuning on it
never showed it. New terms: `losses.masked_fine` (mean |high_pass(r)| over fills, `--fine`) and the
same over a 5 px band around strong edges (`--fine-edge`). The stepD queue was stopped before the hybrid
(d1-p4b12 finished), and `logs/stepE.sh` runs e1-fine, e2-fine-edge and e3-fine-band12 from b6 (check-in
kill adds grey-1 grain over b6's 0.373), then the hybrid, then `measure-E.log`.

D1 results (owner / grey-1): d1-band12 committed .448 / .468, blobs 6.47 / 17.1 (b6 8.27 / 21.6), grain
.189 / .368 (b6 .192 / .373): fewer clumps, edges held, grain untouched as expected. d1-p4b12 blobs 6.42
/ 18.0 but owner grain .251, so the band-12 arm is the better of the two.

E results (owner / grey-1; b6 committed .447 / .466, blobs 8.27 / 21.6, grain .192 / .373, PSNR 41.60 /
40.92; teacher grain .204 / .259):
- e1-fine, stopped at 10k on blobs 8.64: grain .190 / .306, committed .446, PSNR 41.48. The term works.
- e2-fine-edge, stopped at 10k: grey grain .311 but owner grain .273 and blobs 8.92; line_hf 1.85
  against 1.91, so the edge term does not straighten lines and costs the owner page. Dropped.
- **e3-fine-band12** (final, 20k): committed .447 / .469, blobs 6.66 / 16.4, grain .195 / .304, PSNR
  41.48 / 40.76. The new best: static on B&W 0.373 to 0.304 (teacher 0.259), clumps down a quarter,
  edges held, 0.12 dB PSNR.
Jagged thin lines remain open: no loss term so far moves `line_hf`.

The hybrid restarted on e3's recipe at the owner's OK (`logs/stepF.sh`, 05:10, from e3, 60k, 12 it/s,
check-ins at 20k and 40k with probes, kill relative to e3 incl. grey-1 grain over 0.320), then
`measure-F.log`.

Hybrid result (06:11, `measure-F.log`, owner / colour mean / grey mean):

| | e3 | **hyb f-hyb (60k)** | teacher |
|---|---|---|---|
| committed | .447 / .465 / .451 | .459 / .472 / .451 | .449 / .480 / .445 |
| blobs per mille (floor 1.4 / 0.66 / 5.8) | 6.65 / 1.33 / 15.6 | **1.94 / 0.71 / 8.98** | |
| excess p95 (floor .39) | .58 / .27 / .67 | .43 / .25 / .55 | |
| grain | .195 / .279 / .341 | .171 / .229 / .296 | .204 / .155 / .279 |
| PSNR | 41.48 / 43.23 / 37.73 | **42.15 / 43.80 / 38.34** | |
| phase / seam | 0 / 148 | .044 / 64.7, .025 / 67.4, .069 / 66.4 | .022 / 65.5, .017 / 66.9, .021 / 65.7 |

Check-ins at 20k (blobs 2.80, PSNR 41.83) and 40k (2.20, 42.08) both passed. The half-resolution context
branch did what the ps2 run suggested context would do, without its costs: blobs at the floor on colour
and close to it on the owner page, grey blobs a third of long's, PSNR +0.6 dB everywhere, edges at or past
the teacher on the owner and grey pages, phase and seams at the teacher's own level (its reach of 19
passes Maki's 12 px pad, as the teacher's 18 does). Remaining gaps: colour grain 1.5x the teacher's,
colour commitment .472 against .480, and `line_hf` (jagged thin lines) only slightly lower (2.31 against
2.43 on grey). Fill texture is 3 to 6% smoother than the teacher's (smoothing 106-108%).
ONNX `onnx/student-hyb-f.onnx` (1.2 MB): matches to 3e-7, 19.8 ms per tile against b6's 16.4 (1.21x).
`show/index.html` rebuilt with long, b6, e3, the hybrid and the teacher.

### Owner on the hybrid (2026-11-06, 06:20): "looks really good", line art still softer

On a new colour page (in `compare/owner-2026-11-06/`) the owner circled strand lines inside dark hair:
"sharper on real-esrgan and more diffused on our model". `faint.py` (and now `measure.py`'s `sharp_faint`
and `sharp_strong`: the model's mean Sobel magnitude on the teacher's edge pixels over the teacher's own)
confirms it on that page: faint lines (Sobel 16-64) long 1.01, b6 0.97, e3 0.96, hybrid 0.95; strong
lines about 0.98 for every student. Two causes: the fill mask only steps around Sobel-over-64 edges, so
the fill terms act on faint lines; and nothing rewards sharpness directly. New: `--fill-faint 24` (fill
terms also 2 px off lines over 24) and `--sharp` (`losses.sharpness_deficit`: one-sided, the target's
gradient minus the model's steepest within 1 px, on the target's edges including faint ones, so a sharp
line a pixel off costs nothing and hedging is not rewarded). `logs/stepG.sh` (06:26): g1-faint,
g2-sharp, g3-both, 20k each from the hybrid, check-in at 10k with probes (kill also on owner sharp_faint
under 0.95), then `measure-G.log` and `faint-G.log` (with the owner's new page).

Results (07:45). First a metric fix: the 16-64 "faint" pixels included the flanks of strong lines, which a
steeper strong line narrows, so it read sharpening as diffusion. `sharp_faint` (here and in measure.py)
now leaves out pixels within 3 px of a strong edge. On that definition every student's faint lines are
softer than the teacher's: long .94 / .89 / .83 (owner / colour / grey), hybrid .81 / .88 / .75.
- g1-faint (fill terms off faint lines): faint .75 / .88 / .72 at 10k. No help; dropped.
- g2-sharp (sharpness at the quarter rule's 0.61): faint 1.01 / .96 / 1.22, strong 1.00, but committed
  .493 and PSNR -0.6 (owner) / -1.0 dB (grey-1). Stopped at 10k. g3 (both) the same.
- h1-sharp015 (0.15, stopped at 10k by a too-tight committed > 0.475 guard): faint .89 / .92 / .98,
  strong .993 / .992 / .998, PSNR 42.04 / 43.92 / 38.17 (hybrid 42.15 / 43.80 / 38.34), blobs 2.09 /
  0.80 / 8.96, grain .158 / .156 / .307 (colour grain now the teacher's .155; the hybrid had .229).
- h2-sharp03 (0.3, stopped at 10k the same way): faint .94 / .93 / 1.07, strong .997 / .998 / 1.006,
  PSNR 41.85 / 43.78 / 37.89, blobs 2.10 / 0.93 / 8.62, grain .155 / .166 / .307.
Crops of the owner's hair page: `compare/owner-2026-11-06/sharpness-weights.png`.

### Shipped as Maki 2x (owner, 2026-11-06 07:50: "0.15 looks great. I'm ready to start using this model")

`logs/stepFinal.sh`: final015, 20k from the hybrid with the e3 recipe plus `--sharp 0.15`, guard loosened
to committed > 0.49. `measure-final.log` (owner / colour mean / grey mean): PSNR 42.03 owner, committed
.478 / .479 / .452, blobs 2.07 / 0.83 / 10.51 per mille, grain .170 / .148 / .297 (teacher .204 / .155 /
.279), faint lines .898 / .917 / .995, strong .992 / .991 / .996, phase .052 / .029 / .083, seams 64.1 /
66.7 / 66.3 dB (teacher 65.5 / 66.9 / 65.7). Exported as `onnx/student-maki-hyb-2x.onnx` (1.2 MB), 19.6 ms
a 120 px tile on one thread against long's 16.2.

In Maki it is `maki-2x` ("Fast line art"), Plain2x, float32. `prepare.py --maki` copies it into the
release folder; `X:\MakiModel\release` holds a ready one (the published Real-ESRGAN file kept, since a
re-export changes its bytes and every install would download it again).

Flat tiles: on a perfectly flat page tiled 96/12 it comes back as a ramp up to 6 levels off near the tile
edges at grey 200 (std 1.2; within a level deep in a tile), where the teacher gives an even +4. Its reach
(19 low-res px) is past Maki's 12 px pad and training scored nothing nearer than 22 px to a crop edge.
Maki fills one-value tiles for it at any level (`KeepsFlat`), which covers flat panels; near-flat ones
still get the ramp. Real fix, not done: train the loss out to Maki's pad, on targets the teacher made with
real context beyond the crop (today's targets are the teacher on the crop itself, so their own edges are
contaminated the same way).

### Artifacts next (owner, 2026-11-06 07:50: "get the artifacts down")

The owner circled a dark smudge by a light hair-strand tip, on every student, clean on Real-ESRGAN.
`blobs` only scores fill more than 5 px from a strong line, so it never saw it. `measure.py` now reports
`marks` / `marks_dark` / `marks12`: |model - teacher| over 20 (12) levels, opened 3x3 so a line half a
pixel off drops out, share of the page; `smudge.py` does the same plus worst-window sheets for extra
pages (`show/smudge/`). Per mille, marks / marks12, owner / colour / grey: long .037/.366, .016/.242,
.137/1.132; hybrid .018/.175, .010/.139, .091/.879; final015 .015/.136, .006/.103, .065/.732. 99-100% of
them sit within 6 px of a line. On the hair page the worst window is the circled spot: a dark notch where
the white strand meets the grey tip.

The p=0.7 power loss is redescending, so the big residuals a mark is made of are cheap. New
`--marks` (`losses.coherent_error`: 3x3-averaged |r| over `--marks-at` 8 levels, squared) and
`--marks-gain`. `logs/stepK.sh` (08:09): k1 at the quarter rule (weight 347), k2 at four times that, 20k
each from final015, check-in at 10k (kill also when the owner page's marks12 is not under final015's).

Results (`measure-K.log`, `smudge-K.log`; owner / colour / grey):
- k1-marks (20k): marks .010 / .004 / .037 (final015 .015 / .006 / .065), marks12 .106 / .087 / .486,
  hair page .006 (.009); PSNR 42.61 / 44.09 / 38.79 (+0.50 / +0.12 / +0.49); blobs 1.99 / 0.76 / 8.41
  (fewer); committed .465 / .475 / .448 (nearer the teacher); faint .890 / .919 / .977, strong unchanged.
  Cost: grain .162 / .170 / .311 (final015 .170 / .148 / .297, teacher .204 / .155 / .279).
- k2-marks4: stopped at 10k on owner blobs 2.55; no fewer marks than k1, grain worse (.165 / .249 / .334).
Crops of the hair page (Lanczos, final015, k1, teacher): `compare/owner-2026-11-06/marks-k1.png`.

`--marks-gain` became `--gain term=x` (any term, after `--quarter`). `logs/stepL.sh` (08:46): k1's recipe
with the fine term at 2x (l1-fine2) and 3x (l2-fine3), 20k each from final015, to buy the grain back;
kill also on grey-1 grain over .28 (k1 at 10k: .278).

Results (`measure-L.log`; owner / colour / grey):
- l1-fine2: marks .009 / .004 / .037 (k1's), marks12 .103 / .090 / .485, hair page .006; grain .169 / .150
  / .300 (final015's .170 / .148 / .297); PSNR 42.58 / 44.06 / 38.76; blobs 2.14 / 0.70 / 8.10; seams as
  final015. Cost: faint lines .879 / .911 / .952 (final015 .898 / .917 / .995), strong unchanged.
- l2-fine3: the same marks, owner grain worse (.184), faint lower (.875 / .909 / .947). Dropped.
By eye on the hair page (`compare/owner-2026-11-06/marks-l1-crops.png`: final015, l1, teacher; full page
`marks-l1.png`) the circled notch, a dark gap where the white strand meets its grey tip, is smaller but
still there. The teacher joins the two without a gap. The count fell; that shape did not go away.

`logs/stepM.sh` (09:30): m1-sharp02, l1's recipe with `--sharp 0.2`, to win the faint lines back.

Results (`measure-M.log`; owner / colour / grey, final015 in brackets): marks .008 / .004 / .037 (.015 /
.006 / .065), hair page .004 (.009); faint lines .899 / .919 / .989 (.898 / .917 / .995), strong .993 /
.991 / .993; grain .156 / .139 / .301 (.170 / .148 / .297); PSNR 42.55 / 44.06 / 38.74 (42.11 / 43.97 /
38.30); blobs 2.07 / 0.77 / 9.41 (2.07 / 0.83 / 10.51); seams 64.41 / 66.80 / 65.99. Better than final015
on everything but grey grain (+.004) and grey faint (-.006). Exported as `onnx/student-maki-hyb-2x-m1.onnx`
(19.6 ms a tile, same as final015). Crops `compare/owner-2026-11-06/marks-m1-crops.png` (final015, m1,
teacher), full page `marks-m1.png`. The circled notch is lighter but still there.

`show/index.html` (`show.py`, local only) now shows Lanczos, final015, the E3 side branch, m1 and the
teacher, with smudge windows (`--marks-from`) beside the blotch ones and the hair page via `--extra`.

### m1 ships as Maki 2x (owner, 2026-11-06 ~10:30: "It's very good. Now I only nitpick")

m1 replaced final015 as `maki-2x.onnx` in `X:/MakiModel/release` (sha 68be9942, manifest rewritten,
the other two files untouched). final015 was never shipped, so it is a drop-in swap. Flat tiles drift as
final015's did (45 of 52 grey values, worst 6 levels at a tile edge), so `KeepsFlat` stays.

The owner's circles on the comparison page, in three kinds:
- smudges and kept source marks (red): faint scratches and specks in skin and white fill the teacher
  removes, a dark mark at a line junction, JPEG speckle in colour-5's lavender fill, blotchy shading
  between colour-5's stripes, and the light/dark step at colour-1's yellow-white edges;
- line sharpness (green): strand tips, thin grey-page lines, lettering, a sword's gloss band, a
  brown band's edge;
- a hair stroke's gradient (blue).
Measured in the saved windows these are small: colour-1's yellow edge goes 248 / 224 / 185 against the
teacher's 251 / 214 / 187, and the lettering's bottom edge 164 against 179. On the owner page windows
8-19% of pixels are over 4 levels off the teacher, against 1-8% on colour pages.

m1's hardest tenth of training crops (3x3-averaged |r| over 4 levels, per crop) carries 43-49% of its
coherent error, so a uniform draw rarely visits what the owner circles. New `--hard q` in
`train_served.py`: each set's draws are a (1 - q) uniform and q hardness-weighted mix, hardness capped
at its 99.5th percentile and rescored every `--hard-every` (5000) steps by the EMA weights.

`logs/stepN.sh` (17:23): three arms, 20k each from m1 on m1's recipe, check-ins at 10k on m1's numbers:
n0-more (control: is it just more steps? m1's validation was still rising at 20k), n1-hard05
(`--hard 0.5`), n2-hard05-sharp03 (plus `--sharp 0.3`).

Results (`measure-N.log`, `smudge-N.log`, `faint-N.log`; owner / colour / grey, m1 in brackets):
- n0-more: marks .005 / .003 / .028 (.008 / .004 / .037), marks12 .094 / .072 / .429, hair page .001
  (.004); PSNR 42.71 / 44.13 / 38.85 (42.55 / 44.06 / 38.74). Costs: owner grain .196 (.156, teacher
  .204), owner faint .890 (.899), owner seams 63.81 (64.41) and phase .063 (.047), grey blobs 9.95 (9.41).
- n1-hard05: stopped at 10k, faint .870 (grey .935 at 20k). Hard crops alone soften faint lines: the
  hardest crops are damaged sources where the teacher wipes marks away. Its `--quarter` weights were also
  set from hard-weighted batches, so its marks and fine terms did not match m1's.
- n2-hard05-sharp03: the fewest marks, .004 / .003 / .026, marks12 .088 / .067 / .386, hair page .003;
  faint .894 / .924 / 1.009 (sharpness 0.3 bought back what hard crops cost). Cost: colour grain .216
  (.139, teacher .155), 1.4x the teacher, the static the owner rejects; colour PSNR 43.96 (44.06).
No arm beats m1 on everything. `show/index.html` now has Lanczos, m1, n0, n2 and the teacher for the
owner's eye (`smudge.py` no longer crashes on a model with no marks to sheet).

### Held back, each with its trigger

- Specialist students (owner's idea, 2026-11-05): fine-tune long twice, colour sets only and the grey
  crops of `train`/`served_train` only (those sets are 23% colour; needs a `@grey` view), and route per
  page in Maki on the existing `IsGreyscale` test. Same speed. Trigger: the owner decides once the GAN
  result is in; nothing is queued.

- Self-ensemble labels (teacher averaged over 8 flips and rotations) for the colour-flat sets only,
  fine-tune from A2 for 20,000 iterations at 1e-4: if A2 passes the numbers but the owner still sees
  blotches.
- Chained teacher for the heavy tier: if a per-tier validation split shows heavy damage lagging (needs
  `make_served.py` to record each copy's tier).
- c48n10: if A2 and the self-ensemble run both fail the bars with edge MAE held.
- On the Maki side, measured in the real pipeline: 192 px tiles for the student (overhead 1.56x to about
  1.27x, but fewer blank tiles skipped) and a one-channel luma output head.

### Own-model track (parked)

Ground-truth training with a GAN stage, from `X:\MangaVolumes`. Parked because its failure modes (grain,
mottling, halos) are exactly what the owner rejects, scaled-down volume pages do not match the scale of
real library pages, and filtered tone targets would teach a look neither the teacher's nor approved.

Revisit when both hold: the distilled student matches the teacher on the reference set by the owner's
eye, and the owner names a specific teacher behaviour they want changed. The first task then is measuring
the teacher's tone-flattening cutoff on the reference set.

`descreen.py` and `make_hr.py` stay as they are for that day. `descreen.py` was rewritten by a delegated
agent on 2026-11-05 (dot-pattern peak test, stroke detection on the notched page, stroke-aware local
average, texture left alone): plain tone comes out smooth grey, lines off tone are bit-identical, strokes
over tone keep 91-97% of their contrast. Known failures: thin white lines over dark dot tone, patchy
flattening where tone mixes with drawn texture, blotchy coarse halftone. Sheets and `harness.py` are in
`.artifacts/upscale-distill/descreen/`.

## Files

| File | Role |
|---|---|
| `common.py` | paths, student architecture, teacher loading, luma |
| `degrade.py` | damage tiers (`damage` for crops at reading size) |
| `make_dataset.py` | teacher-labelled library crops; `--colour-only`, `--min-std`, `--max-crops`, `--prefix` |
| `make_served.py` | damaged copies labelled by the teacher on that damaged input |
| `sets.py` | on-disk sets: `DiskArray`, `RowWriter`, `open_set`, `to_gpu`, npz conversion |
| `train_served.py` | the current trainer: several sets with shares, luma targets, per-set validation |
| `evaluate.py` | whole-page scoring and ONNX export |
| `measure.py` | step 0: reference set, flat and fill masks, blotch numbers, worst-window sheets, mask overlays |
| `crop_stats.py` | per-crop std, mean, flat and fill shares, so a trainer can sample `set@fill` and `set@rest` |
| `gan.py` | stage-2 discriminator and hinge losses for `train_served.py --gan` |
| `make_ensemble.py`, `show.py` | 8-orientation teacher labels; the local comparison page |
| `bakeoff.py`, `compare_page.py` | teacher and student comparison crops and the local comparison page |
| `descreen.py`, `make_hr.py` | own-model track, parked |
| `train.py`, `train_mixed.py`, `train_look.py`, `train_robust.py`, `label_*.py`, `make_gt_dataset.py` | earlier rounds, kept for reproducibility |

Checkpoints in `.artifacts/upscale-distill/`: `student-c48n8.pt` (round one), `student-c48n8-served.pt`
(round four), `student-c48n8-colour.pt` (colour run, current best and the init for A1 and A2).

### Research round 3 and step O (2026-11-06 evening)

`RESEARCH-PLAN-3.md` (a Fable 5.1 research agent, probes and seven 10k arms in `X:/MakiModel/probe3/`):
m1 fits training crops no better than validation crops, 85-98% of its error is a tail on edges, and the
teacher itself is noise-sensitive on faint lines (one level of input noise moves its faint-line gradient
20-27% on owner and grey pages, under 1% on strong edges), so the student learns the mean of a coin flip.
A 64-channel trunk from m1's own function moved 0.05 dB in 10k steps. The unguarded relative faint term lifted
the faint lower quartile 0.61 to 0.82 but overshot (mean 1.14-1.27, owner blobs +45%); the thin-line probe
took thin-line depth p25 0.92 to 1.00 and overshot too. No swap, no EMA and a front-only widening were flat.

`logs/stepO.sh` (21:26, owner's go-ahead): its steps 1 to 3 from m1. o1-line025 and o2-line05 (the guarded
`--sharp-line` at 0.25 and 0.5 of the quarter rule, kill also on faint over 1.03); the survivor with the
larger faint p25 lift (at least 0.1) seeds o3-thin (`--thin` at 0.25); o4-gan (10k at 5e-5, masked, warm-up
gate 0.05) from o3 or its base, killed at 2.5k/5k/7.5k on 10% over its base's blobs or grain, 0.3 dB of
PSNR, or more marks12. Then measure, smudge and faint over the survivors.

Results (`measure-O.log`, `smudge-O.log`, `faint-O.log`; owner / colour / grey, m1 in brackets):
- o1-line025, o2-line05: both stopped at 10k on owner blobs (2.71, 3.03 against the 2.5 bar); faint .917 /
  .922 on the owner page (.899) but grey faint fell to .932 / .909 (.989). The guard did not stop the blobs.
- o3-thin (from m1, neither step 1 arm qualified): marks12 .090 / .068 / .435 (.106 / .089 / .503), faint .916
  / .926 / 1.042, PSNR owner 42.69 (42.55). Costs: owner grain .177 (.156), grey blobs 11.37 (9.41),
  phase and seams a touch worse (owner 64.09 against 64.41).
- o4-gan (o3 + 10k masked GAN, warm-up gap +0.46, passed every check-in): marks12 .095 / .076 / .405,
  faint .922 / .929 / 1.047, owner grain .151 and blobs 2.06 (back to m1's), PSNR owner 42.76; colour grain
  .170 (.139, teacher .155), grey blobs 11.64. `show/index.html`: Lanczos, m1, O3, O4, teacher.
Owner verdict (2026-11-06, late): "Still can't really see any improvements" on O3 or O4. m1 stays as Maki 2x.
With N0/N2 and now O3/O4 invisible by eye, the measurable gains left at this speed are below what the owner
sees; RESEARCH-PLAN-3's reading (the teacher's own noise sensitivity on faint detail) stands. Tuning stops here.

NAS (i5-14500, owner's "Test speed", 2026-11-06): Maki 2x 1.5 s a page, Real-ESRGAN 4.2 s, 2.8x.
