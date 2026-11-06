# Research plan: a fast 2x upscaler for manga, manhua and manhwa

Written 2026-11-05 by the research sub-agent. Research and reading only; nothing here was trained.
`PLAN.md` is the record of what was tried; this file says what the literature and the community do,
why our student still falls short of Real-ESRGAN by the owner's eye, and what to do next. Verdict on
`PLAN.md` is in section 6: keep the harness and the teacher, change the architecture question and the
GAN stage, drop the "sizing stops" conclusion.

Sources are cited inline as (author, section) with links collected at the end. Library pages were not
sent anywhere; every number about our own data comes from `PLAN.md`, the logs under
`.artifacts/upscale-distill/logs/` and one CPU benchmark run in the scratchpad.

## 0. The short version

1. The owner's two complaints (marks left in fills, less committed edges) are what an L1 student does
   when it cannot resolve the teacher's decision: it outputs the median, which is a half-flattened
   blotch and a half-sharp edge. Our student sees 10 low-res pixels of context against the teacher's
   18 and has 28% of its parameters. Nothing in the loss can fix a decision the network cannot see.
2. The arch sweep that concluded "sizing stops" compared models trained from scratch for 40k steps
   that sat 3.3 dB below `long` on every validation set (`logs/arch-c48n8.log`: val 33.61 against
   36.89). It measured learning speed, not capacity or reach. The conclusion is unsafe and should be
   withdrawn.
3. There is a same-cost architecture change the sweep never tried: a pixel-unshuffle front end, the
   thing Real-ESRGAN itself uses for its 2x and 1x models (Wang et al. 2021, section 3.4, figure 4).
   Measured today in ONNX Runtime on one thread, `ps2-c96n8` costs the same 16.4 ms per tile as
   `c48n8` with 4.1x the parameters (716k, more than the teacher's 619k) and 21 low-res pixels of
   reach (more than the teacher's 18). `ps2-c96n12` is 23.6 ms with 1.05M parameters. Add ECB-style
   re-parameterised branches at training time (Zhang et al. 2021; Wang et al. 2022), which collapse
   into plain 3x3 convolutions, so inference cost does not move.
4. A GAN stage is still right, but second: every anime SR system that gets crisp lines uses one
   (Real-ESRGAN, AnimeSR, APISR, VQD-SR), and a student that cannot see enough context will commit
   to wrong guesses. Run it on the new shape, with an LDL-style artifact weight (Liang et al. 2022) so
   flats stay clean, at tiny weights, under the existing tripwires.
5. One general model first; specialists are a 2 x 20k fine-tune experiment with a defined trigger.
   Distillation from animevideov3 stays; the own-model track stays parked.
6. Tonight's GAN run was stopped by the lead at 20:55 (GPU memory spilled into system memory; see
   `PLAN.md`, "GAN stage stopped"). The one checkpoint line it did print, at 20:53, adds a second
   problem to fix before a rerun: after 2,500 steps the discriminator loss was still at its initial
   value (`d 1.998`, the hinge loss of a discriminator that outputs zero). See section 8.

## 1. What produces clean anime and manga SR, per the research

### 1.1 Data and degradation model

- **Second-order synthetic degradations** are the baseline everyone starts from. Real-ESRGAN
  (section 3.2, 4.1) chains blur, resize, noise and JPEG twice with these ranges: blur sigma
  [0.2, 3] then [0.2, 1.5], Gaussian or Poisson noise, grey noise with probability 0.4, JPEG quality
  [30, 95], a sinc filter with probability 0.8 at the end to synthesise ringing and overshoot, and
  the order of the last sinc and JPEG swapped at random. The paper's ablation (figure 8) shows models
  without sinc filters amplify ringing around text and lines, which is the failure that would show
  on manga.
- **Non-aligned double JPEG is a separate failure mode.** FBCNN (Jiang et al. 2021, section 3.3)
  shows blind models trained on single JPEG fail on double-compressed images whenever the two block
  grids are not aligned and the first quality is at or below the second, "even if there is an only
  one-pixel shift". Their fix is training data built as JPEG(shift(JPEG(x, QF1)), QF2) with a random
  0 to 7 px shift (equation 5), which they say "can also be applied to other tasks such as blind
  single image super-resolution". Our `degrade.py` medium and heavy tiers already produce this shape
  (resize between generations shifts the grid), and the owner's library is exactly this kind of
  source (saved small at low quality, enlarged, saved again). `make_served.py --grid-offset` is the
  same idea for the final save and is only applied to `col2` so far.
- **Anime-specific degradations are learned from real low-quality footage** when a team has it.
  AnimeSR (Wu et al. 2022) trains tiny "learnable basic operators" (three 3x3 convs, 64 channels) on
  real LQ frames rescaled to about 0.5x, because "animation's lines and flat colour regions keep
  detail under mild downscaling". VQD-SR (Tuo et al. 2023) stores real degradations in a VQ
  codebook. Both report that the web-sourced "HR" training videos are themselves compressed and
  must be cleaned first. For us the equivalent of "real LQ" is the library itself, and the teacher
  already supplies a clean target for it. That is the strongest argument for the served-target
  design in `PLAN.md`.
- **APISR (Wang et al. 2024)** adds video-codec compression (H.264 CRF 23 to 38, H.265 28 to 42,
  MPEG) to the degradation chain and a shuffled resize (first resize [0.1, 1.2], second [0.15, 1.2]).
  Video codecs do not matter for manga. The shuffled resize and WebP in the first compression step
  ({JPEG, WebP} at {0.4, 0.6}) do, and `degrade.py` has WebP already.
- **Small, curated datasets win for small models.** APISR trains on 3,740 frames picked by image
  complexity (IC9600) from 562 videos. Community guidance for Compact models (Sirosky; Kim2091,
  traiNNer-redux guide) is "datasets larger than 5,000 images can be detrimental" and "quality beats
  quantity"; the published 2xHFA2kCompact used 2,568 images, 93k iterations at batch 12 x 384 px HR
  from a Compact pretrain. Our sets (8.8k library crops plus 14.5k colour crops, each with damaged
  copies) are in that range. More data of the same kind is not where the gain is, which matches
  what run A1 measured.
- **Flat fills must be in the data.** `PLAN.md` found the old `std < 2` filter dropped exactly the
  blotchy fills the owner complains about; waifu2x's trainer rejects "single-colour background
  areas" on purpose (`--reject-rate`, nunif training.md) because it optimises PSNR on busy content.
  We want the opposite. Keeping `flat_train` and the fill views is right.

### 1.2 Losses

- **Two stages, everywhere.** Real-ESRGAN: L1 for 1000k iterations, then L1 + VGG19 perceptual +
  GAN at {1, 1, 0.1} for 400k with a U-Net discriminator with spectral norm, EMA (section 3.4, 4.1).
  AnimeSR: 300k L1, then 300k L1 + perceptual + GAN at {1, 1, 1} with a three-scale PatchGAN with
  spectral norm. APISR: 300k L1 at 2e-4, then 300k with perceptual 0.5 and adversarial 0.2 at 1e-4,
  batch 32 of 256 px HR. The GAN stage is what commits lines; nobody gets the Real-ESRGAN look from
  stage 1.
- **Perceptual features from photographs cause colour artifacts on anime.** APISR replaces the VGG
  term with a "balanced twin perceptual loss": a ResNet50 pretrained on Danbooru tagging plus VGG,
  layer weights {0.1, 20, 25, 1, 1} and {0.1, 1, 1, 1, 1}. The reason they give is that ImageNet
  features "differ significantly" from anime. `PLAN.md`'s decision to skip the VGG term in our GAN
  stage is in line with this; we have no anime-pretrained feature network and do not need one,
  because the teacher output is the target.
- **GAN artifacts in flats have a dedicated fix.** LDL (Liang et al. 2022) computes the local
  variance of the residual over a 7x7 window, scales it by (var(R))^(1/5) per patch, zeroes it where
  the live model already beats its EMA copy, and uses the map as a per-pixel weight on |HR - SR| with
  weight 1 beside the usual ESRGAN losses. It is reported to suppress Real-ESRGAN's artifacts in
  dense windows, which is the photo equivalent of marks in a fill. It is cheap, plugs into our
  stage 2 as written, and measures almost the same thing as `measure.py`'s `blobs`.
- **Structural losses for flat areas.** Zhao et al. 2017 ("Loss functions for image restoration")
  established MS-SSIM + L1 as the restoration default; traiNNer-redux's current Compact default is
  MS-SSIM + perceptual + HSLuv + GAN with EMA 0.999 (its training guide's sample log). SSIM is a local
  mean, variance and covariance comparison, so a blotch in a flat fill costs it far more than per-pixel
  L1, and a softened edge costs covariance. This is the cheapest candidate for a "commitment" term
  that is not adversarial.
- **Frequency-domain terms** (Real-CUGAN's "frequency-domain supervision", focal frequency loss) are
  the generic version of our band term. Our band term is better targeted; keep it.

### 1.3 Architecture

- **Plain VGG-style stacks are the CPU-fastest family per MAC.** ONNX Runtime keeps them in NCHWc
  layout end to end; anything with per-pixel attention or elementwise mixing (SPAN's sigmoid gates,
  the Sub in each SPAN block) pays a reorder (`enhance.md`: a third of kernel time at 16 threads is
  `ReorderInput/Output`). SPAN won NTIRE 2024's runtime track on a 3090 at 5.6 ms for 0.151M
  parameters (Ren et al. 2024, table 1), and SPAN variants took first and second overall in 2025,
  but those are GPU timings. On the NAS the plain stack is the right family, which is why
  animevideov3 is one.
- **Re-parameterisation is free capacity.** ECBSR (Zhang et al. 2021, section 3) trains each 3x3 as
  a sum of a plain 3x3, a 1x1-expand (2C) then 3x3-squeeze path, and scaled Sobel-x, Sobel-y and
  Laplacian paths behind 1x1 convs, then folds all of it into one 3x3 for inference. RepSR (Wang,
  Dong, Shan 2022) uses two expand-and-squeeze branches with a BN trick and reports it is what makes
  deep plain nets trainable at all (table 4: plain VGG training at their large size reaches 26.26 dB
  on Set5 against 32.17 with RepSR). Gains at tiny sizes are modest (RepSR table 2: +0.02 to +0.13 dB
  over ECBSR at equal inference cost, both well over plain training). The top three teams in both
  NTIRE 2024 and 2025 used it. The BN variant is the one with a documented artifact risk ("feature
  statistics deviate from the population statistics and explode"), so use the BN-free ECB form.
- **Compute at reduced resolution.** Real-ESRGAN's own 2x and 1x models pixel-unshuffle the input
  first "so that the most calculation is performed in a smaller resolution space" (section 3.4,
  figure 4). For a 2x model the body runs at half the input resolution with four times the channels
  per unit of cost and twice the receptive field per layer. Section 4 has our measurements.
- **Distillation is now standard in efficient SR.** NTIRE 2025's overall winner (EMSR) used spatial
  affinity distillation plus a pixel-level distillation loss from a teacher; the organisers write
  "distillation is an effective manner to maintain the PSNR performance without increasing
  computation cost during inference". For distilling a GAN-trained teacher, SDAKD (2025) names the
  obstacle as the capacity mismatch between a small student and a teacher-scale discriminator and
  fixes it with a student-scale discriminator, which is what `gan.py` already is.

### 1.4 Distillation versus training on ground truth

Keep distillation. The owner's target is a specific look, and that look is a function (animevideov3)
we can query for any input, including damaged library pages for which no ground truth exists. Every
ground-truth route brings back the problems `PLAN.md` already hit: screentone in volumes needs a
target (rounds two and three failed on exactly that), scaled-down volumes do not match library scale,
and an own GAN stage needs its own taste iteration with photo-domain perceptual features that the
anime literature says produce colour artifacts. The manga-specific literature also optimises for the
opposite of the owner's choice on tone (next point). The own-model track should stay parked with its
existing triggers.

### 1.5 Screentone: the research wants to preserve it, the owner wants it flat

Three lines of work treat tone as content to be kept: MangaJaNai trains seven resolution-specific
models "so halftones come out at the correct size and frequency" and says its models "mostly restore
dots that were no longer visible"; Screentone-Aware Manga SR (2023) segments dot, line and grid tone
with a U-Net and runs a separate SR model per class because one model "misreads line crossings as
grid patterns"; Exploiting Aliasing for Manga Restoration (Xie et al. 2021) recovers bitonal tone
from its own aliasing. All of them need tone-aware machinery and still list blended tone (shaded
cheeks) as a failure. Flattening, which is what Real-ESRGAN does and the owner chose, is the simpler
problem and the distillation target encodes it already. Worth stating plainly so nobody re-opens it:
the owner's choice is a deliberate departure from what manga-specific SR research optimises for, and
it is the cheaper one to ship.

## 2. Why the student hedges edges and leaves marks, and what fixes it

### 2.1 Diagnosis

The teacher is deterministic, so in principle a student could copy it exactly. It cannot, for two
reasons that compound:

| | teacher (animevideov3) | `c48n8` student |
|---|---|---|
| 3x3 convs | 18 | 10 |
| reach (low-res px radius) | 18 | 10 |
| parameters | about 619k | 173k |
| MACs per low-res pixel | 1.0x | 0.28x |

Where the student's 10 px window cannot tell a JPEG block beside a line from a crease, or cannot
resolve which side of an edge a pixel belongs to after the teacher's GAN-trained decision, an L1 loss
gives the conditional median: part of the blotch stays, the edge pixel lands between its two sides.
That is what `committed` (0.429 against 0.449) and `fill_excess_p95` (0.71 against 0.39) measure.
The one-page over-fit probe (`logs/probe-se.log`) does not contradict this: memorising one page needs
no context, generalising does. The band term bought smoother fills partly by softening edges, which
is the trade an under-sized L1 student makes when asked for both.

The arch sweep did not test this. All four shapes were trained from scratch for 40k steps and ended
3.3 dB under `long` on clean B&W (`arch-c48n8.log` val 33.61, `long.log` 36.89); `long` has about
300k steps of history behind it. At 40k the comparison is dominated by how fast each shape learns,
and the one shape with more capacity (`c64n8`) did learn fastest (+2.4 dB). Dilation (`c48n8d`)
widens the window but leaves holes in it, and it measured worse on edges, which is what holes do to
edge profiles. So reach and capacity remain untested, not refuted.

### 2.2 Fixes, ranked by expected gain per effort

1. **Same-cost reach and capacity: pixel-unshuffle front end plus ECB re-parameterisation.**
   `ps2-c96n8` is 16.4 ms (equal to `c48n8`), 716k parameters, 21 px reach. Attacks the cause
   directly. Effort: one new spec in `common.student()`, a `MARGIN` tied to reach, a collapse step
   before ONNX export, two probes in `measure.py` (section 5). Risk: a 2 px phase pattern from the
   unshuffle (the owner would see a grid), so the phase probe is a kill criterion. Expected gain:
   the largest available, because every other lever acts on a network that cannot see the decision.
2. **Stage 2 GAN on the new shape, with LDL weighting.** Our `gan.py` is already the right kind of
   discriminator (small, conditioned, spectral norm, per pixel, hinge). Add the LDL artifact map as
   a per-pixel weight on the luma L1 so the adversarial term cannot buy commitment with marks in
   fills, weights 0.005 and 0.02, kill on blobs or grain. Expected gain: the remaining edge
   commitment and the "refined lines" the owner named. Effort: small. Risk: the usual GAN artifacts,
   bounded by the tripwires. Running it on `long` first is still informative as a floor, but
   section 8 says tonight's run is not going to finish on time.
3. **MS-SSIM (plus a gradient L1) as a fine-tune term.** 20k steps from the best checkpoint,
   MS-SSIM at 0.1 to 0.2 beside L1, optionally Sobel-gradient L1 at 0.1 (the Sobel option already in
   `PLAN.md`'s A2 list, never run). Cheap, non-adversarial, and directly scored by `committed` and
   `blobs`. Expected gain: moderate on edges, small on fills. Do it as a control arm beside the GAN
   so the owner sees both.
4. **Rebuild every served set with `--grid-offset`.** `PLAN.md` already says to do this at the next
   rebuild; FBCNN's result (one-pixel misalignment defeats blind models) says it matters more than
   it looks. Maki's context tiles put the page's JPEG grid at a 4 px phase inside every tile
   (12 px pad), while the un-offset served sets always have the final save's grid at phase 0.
   Effort: GPU time for labels only. Expected gain: small on averages, possibly visible on the
   owner's page, whose damage is block-shaped.
5. **Longer, progressive training for the new shape.** NTIRE 2024 and 2025 both list "several phases
   with increased patch size and reduced learning rate" as the common recipe. Our crops are fixed at
   160 px, so the lever is schedule length. The from-scratch candidates need the history `long` has:
   plan 200k steps, not 40k.
6. **Data like the owner's page.** The owner's call on which series. Hold until 1 and 2 have run,
   because A1 showed data alone does little for the current shape, and a bigger shape may make
   better use of what is already there.
7. **Luma-only output head.** `c48n8-luma` saves 2% and stops the final conv spending 8 of 12
   outputs on chroma nobody reads. Keep the RGB input (colour edges with equal luma are invisible in
   a luma input). Needs a Maki code path; low priority, bundle with the catalogue change.

## 3. One general model or two specialists

Recommendation: **one general model first, specialists as a measured experiment with a trigger.**

For specialists: the community's manga models are specialists by design (MangaJaNai for B&W, split
again by page height; IllustrationJaNai for colour), and the two kinds of page are different problems
(tone and line art against fills, gradients and block damage). Our tails show a gap on both kinds
(owner page blobs 11.2 against a 1.4 floor; grey 27.3 against 5.8), and a 173k-parameter model
serving both is the obvious suspect. Routing exists (`UpscalePixels.IsGreyscale`) and the cost is
unchanged.

Against, or at least not yet: the same-cost capacity in section 2.2 item 1 removes the main reason
to specialise, and specialising has costs of its own. A colour page with grey panels or a grey page
with a red title goes to the colour model, so the colour specialist must keep grey data (never filter
its set); only the grey specialist's set gets the `@grey` view. Two models also means two looks that
can differ across consecutive pages of one chapter (colour cover, B&W pages), two sets of tripwires,
and two downloads.

Decision rule: after the general winner exists, fine-tune it twice for 20k steps (colour sets only;
grey crops of `train` and `served_train` only, which needs the `@grey` view since those sets are 23%
colour by the `IsGreyscale` rule). Ship specialists only if a kind improves by at least a third of its
remaining tail gap (`committed`, `blobs`) with the other kind not regressing, and the misrouting check
passes (grey model on spot-colour pages and colour model on B&W pages both within the general model's
numbers). If one specialist pays and the other does not, ship that one plus the general model. The
B&W specialist is the likelier to pay: screentone has no counterpart on colour pages, and more B&W
data is available from `X:\MangaVolumes`.

## 4. Architectures inside the CPU budget

Measured today (`scratchpad/bench/bench_arch.py`), ONNX Runtime fp32, one intra-op thread, one
120 px context tile (96 + 2 x 12), median of 40 runs, this machine. Absolute numbers will differ on
the NAS; ratios are what to read. `ps2-` means pixel-unshuffle 2 in front, body at half resolution,
pixel-shuffle 4 at the end, nearest-upsampled input added as the residual.

| model | ms per tile | x c48n8 | params | GMAC per tile | reach (LR px) | notes |
|---|---|---|---|---|---|---|
| `c48n8` (current) | 16.4 | 1.00 | 173k | 2.48 | 10 | control |
| `c48n8-luma` | 16.1 | 0.98 | 168k | 2.42 | 10 | 1-channel output head |
| `c48n8-k5first` | 16.5 | 1.01 | 175k | 2.52 | 11 | not worth it |
| `c32n12` | 11.4 | 0.70 | 115k | 1.65 | 14 | |
| `c48n6` | 12.6 | 0.77 | 131k | 1.88 | 8 | |
| `c40n10` | 20.0 | 1.22 | 150k | 2.15 | 12 | 40 channels is slower than 48 for fewer MACs |
| `c56n8` | 27.8 | 1.70 | 234k | 3.36 | 10 | costs the same as 64; never use 56 |
| `c64n8` | 27.7 | 1.70 | 304k | 4.37 | 10 | |
| `c48n12` | 23.7 | 1.45 | 256k | 3.68 | 14 | |
| **`ps2-c64n12`** | **10.8** | **0.66** | 478k | 1.72 | 28 | cheaper than today with 2.8x the parameters |
| **`ps2-c96n8`** | **16.4** | **1.00** | 716k | 2.58 | 21 | same cost as today, more parameters than the teacher |
| **`ps2-c96n12`** | **23.6** | **1.45** | 1.05M | 3.77 | 28 | the budget ceiling |
| `ps2-c128n8` | 27.4 | 1.68 | 1.25M | 4.50 | 21 | over budget |
| teacher (4x, before the halve) | 58.7 | 3.59 | 619k | | 18 | from `PLAN.md` |

Three things the table says:

- ORT's NCHWc kernels want channel counts that are multiples of 16. `c40` costs more than `c48` for
  fewer MACs; `c56` costs the same as `c64`. Candidates are 32, 48, 64, 96, 128.
- The pixel-unshuffle family dominates: same cost, four times the parameters, twice the reach per
  layer. Real-ESRGAN ships its 2x models this way. The open question is not cost but whether a plain
  stack handles the four sub-pixel phases without a visible 2 px pattern; RRDB does, a plain stack
  must be checked (phase probe, section 5).
- ECB-style re-parameterisation adds training-time branches and nothing at inference, so it applies
  to every row. Use the BN-free ECB form (plain 3x3 + 1x1-expand-2C-then-3x3 + scaled Sobel-x,
  Sobel-y, Laplacian behind 1x1), collapse before export, and assert the collapsed ONNX matches the
  training graph to within 1e-4 on a random tile.

Maki side for the `ps2` family: input dims must be even. `OnnxPageUpscaler` already passes a model
multiple (1 for the 4x teacher, 4 for SPAN-S), so a multiple of 2 or 4 is a catalogue entry, not new
code. Context tiles are 120 px, already divisible by 4. Reach beyond the 12 px pad needs the seam
check; the teacher already runs at 18 px reach with the same pad and measures 65 dB against its own
shifted-grid output, so the bar is relative to that, not absolute.

Training-side constraint: `common.MARGIN` (the crop border the loss ignores) is 20 low-res px and
must be at least the student's reach, or the student learns from zero-padded context it never sees in
Maki. `ps2-c96n8` needs 24, the n12 shapes need 30 (100 of 160 px scored per crop).

## 5. Step-by-step plan

Pass and kill numbers use `measure.py` as it stands. Reference values (owner page / colour mean /
grey mean): `long` committed 0.429 / 0.452 / 0.435, teacher 0.449 / 0.480 / 0.445, teacher-ens floor
0.464 / 0.489 / 0.462; `long` blobs 11.2 / 2.0 / 27.3 per mille, floor 1.4 / 0.66 / 5.8; `long`
fill_excess_p95 0.71 on the owner page, floor 0.39; `long` PSNR 41.53 / 43.28 / 37.96. "Halfway" below
means halfway from `long` to the teacher (committed) or to the floor (blobs, excess).

### Step 0: code and probes, CPU only, before the GPU frees up

- `common.student()`: accept `ps2-c96n8` style specs (pixel-unshuffle 2, body at half resolution,
  pixel-shuffle 4, nearest residual) and an `e` suffix for ECB branches. `MARGIN` becomes a function
  of the spec (reach plus 2). Add `collapse()` that folds the ECB branches into plain convs and a
  test that the collapsed model matches to 1e-4.
- `measure.py`: two new numbers. `phase`: run the model on the page and on the page shifted by
  (1, 1) px with reflect padding, undo the shift, mean and p99 |difference| in luma inside `fill`,
  plus the worst-window sheet. Kill at mean over 0.5 levels or any visible 2 px grid. `seam`: PSNR
  between Maki's tiling (96 + 12) and the same tiling shifted 48 px, reported beside the teacher's on
  the same page (65.6 dB on the owner page per `logs/teacher-floor.log`); kill if more than 3 dB
  under the teacher's.
- `train_served.py`: `--mssim w` (MS-SSIM on the luma centre, 11 px window, 3 scales fit in the
  scored crop), `--grad w` (Sobel-gradient L1), `--ldl` (LDL artifact map as a per-pixel weight on the
  luma L1, 7x7 window, exponent 5, zeroed where the live model beats the EMA, needs `--ema`). No new
  loss is on by default.
- Data: queue `make_served.py --grid-offset` for `train` and `col_train` (new names, old sets kept
  for A/B), and a `@grey` view in `crop_stats.py` using the `IsGreyscale` rule (more than 1 in 2000
  sampled pixels with channel spread over 16, or mean spread over 6, means colour). GPU needed for
  labels only; run after the GAN queue.

### Step 1: the shape, at equal training history (one night of GPU)

Same recipe and sets as `long` (pooled 2, EMA 0.999, band 6, no flips, `--swap` ensemble fill
targets, `--slack 99` so every run saves), three runs:

| run | init | steps | lr | purpose |
|---|---|---|---|---|
| `c48n8e-ctl` | `long` weights into the 3x3 branch, ECB branches at zero | 60k | 1e-4 | does re-parameterisation alone move the tails |
| `ps2-c96n8e` | none | 200k | 5e-4 one-cycle | the same-cost candidate |
| `ps2-c96n12e` | none | 200k | 5e-4 one-cycle | the budget-ceiling candidate |

If the GPU night is short, drop the control, not a candidate. Measure all with `long` and `teacher`
in the same table, then the phase and seam probes.

Pass for a candidate: `committed` at least halfway to the teacher on the owner page (0.439 or more)
and not under `long` on colour or grey; `blobs` at least halfway to the floor on the owner page
(6.3 or less) and on grey (16.5 or less); `fill_excess_p95` 0.55 or less; PSNR not under `long` on
any group; `texture_crease` within 5% of the teacher; `phase` and `seam` clean. If `ps2-c96n8e`
passes, prefer it over `ps2-c96n12e` unless the n12 is clearly better on grey (where `long` is
weakest) and the NAS timing is still inside 3 to 4 s per page. If both fail on `committed` but pass
on `blobs`, go to step 2 anyway: fills were the capacity problem, edges are the GAN's job. If both
fail on everything, the reach hypothesis is wrong; the fallback is step 3 on `long`.

Show the owner nothing yet unless a candidate passes; then `show/index.html` with the candidate,
`long` and the teacher, marked crops of the owner page's worst windows.

### Step 2: stage 2 on the winner (a second night)

From the step 1 winner, 20k steps at 5e-5, `--gan 0.005` and `--gan 0.02`, each with and without
`--ldl` (four runs, the two LDL arms first). Discriminator warm-up 1,000 steps, and a check that the
discriminator loss leaves 2.0 during warm-up; if it is still 1.99 at step 1,000 the stage is inert
(see section 8) and the run should stop. Pass: `committed` at the teacher's value on the owner page
(0.449) and within 0.01 on the others; `blobs` within 2x of the floor; owner-page PSNR down no more
than 0.3 dB; `texture_crease` within 5%; pure-fill p99, grey PSNR and `phase` not worse than the
step 1 winner. Kill at the first tripwire. The owner sees this one regardless of pass or fail,
with the step 1 winner and the teacher beside it, because the look is their decision.

### Step 3: the non-adversarial control (can run beside step 2)

20k from the step 1 winner with `--mssim 0.15`, and a second arm adding `--grad 0.1`. Same pass
numbers as step 2 minus the GAN caveats. If it matches the GAN arm on the tails it ships instead,
because it cannot grow artifacts later.

### Step 4: specialists (only if a trigger fires)

Trigger: the owner, having seen step 2 or 3, still names one kind of page as worse, or the tails
show one kind more than a third of the gap behind the other. Two 20k fine-tunes as in section 3,
decision rule as written there.

### Step 5: Maki

Collapse and export the chosen checkpoint; catalogue entry with multiple 2 (or 4) and the
`KeepsFlat` exact-flat check (`flat_train` is in every run); NAS timing on a paged manga page and
a tall webtoon slice; raise `UpscaleCache.Generation`. Optional luma-only output head in the same
change. Then the owner's eye on the reference set plus two new pages from series not in any set.

## 6. Verdict on `PLAN.md`

Keep:

- The ground rules, the reference set, the owner-verdict JSON, and the rule that numbers pick what
  to show and the eye picks what ships.
- The teacher (animevideov3, BSD-3-Clause) and the served-target design. The anime SR literature's
  biggest lesson is that the degradation must come from the real sources; our teacher labelling of
  real damaged crops is a stronger version of that than any synthetic chain.
- `measure.py` as a whole: `fill`, `texture`, `committed`, `fill_excess`, `blobs` map onto the
  owner's words better than anything in the papers' metric tables (their real-world results are
  figures and user studies, not numbers).
- Pooled and band terms, EMA 0.999, no flips (the teacher is not equivariant, so a flipped pair is a
  different function), `flat_train`, the grid offset, fill views, the 2 GB RAM rule.
- The GAN stage's design: small conditioned U-Net discriminator, spectral norm, hinge, no VGG term,
  no unsharp-masked targets. That is the right shape for distilling a GAN-trained teacher (SDAKD's
  argument) and avoids the photo-feature problem APISR documents.

Change:

- Withdraw "reach is not why the student hedges" and "by the kill rule sizing stops". The sweep
  compared 40k-from-scratch models 3.3 dB under `long`; it answers nothing about capacity or reach.
  Replace the shape question with section 5 step 1: same-cost capacity and reach through the
  pixel-unshuffle front end and ECB branches, measured at a comparable training history.
- Run stage 2 on the new shape, not only on `long`, and add the LDL weight. Keep tonight's `long`
  result as a floor if it ever finishes.
- `MARGIN` becomes spec-dependent.
- Add the phase and seam probes before any new shape is shown.
- Specialists: not "once the GAN result is in" but on the trigger in section 5 step 4, after the
  shape question is settled, because a bigger general model changes the answer.
- Training length for from-scratch shapes: 200k, not 40k.

Drop:

- `c48n8d` and dilation in general: holes in the window, measured worse on edges, and the `ps2`
  family gives more reach without them.
- The held-back "c48n10 if A2 and the self-ensemble both fail": superseded.
- The held-back "chained teacher for the heavy tier": no evidence for it anywhere, and the served
  targets already cover heavy damage.
- Self-ensemble labels for colour-flat only: closed by the `teacher-ens` measurement already.

Unchanged: the own-model track stays parked with its triggers. The research adds one more reason:
the manga-specific literature's goal (preserve tone) is the opposite of the owner's, so none of its
recipes transfer directly.

## 7. Owner decisions and licences

- **Look.** Nothing here changes the target; animevideov3 stays. If a step 2 GAN arm looks crisper
  than the teacher, that is a deviation and the owner decides, same as with anime6B.
- **Pixel-unshuffle shape.** A yes to trying it means a model whose failure mode (a 2 px pattern) is
  on the owner's rejection list; the probe exists to catch it before they see it. Their call whether
  a candidate that passes the probe numerically is shown.
- **Licences.** The student is trained on animevideov3's outputs (BSD-3-Clause, no restriction on
  outputs) and the owner's own scans and volumes; the architecture is a reimplementation. Shipping
  it under Maki's GPL-3.0 has no third-party string attached. Two things to say out loud: the
  training data is copyrighted scans the owner holds, which the whole community does and which the
  owner should decide on knowingly; and the pages never leave the machine, the model is 0.7 to
  4 MB and cannot carry them. Models consulted but not used: MangaJaNai and IllustrationJaNai
  (CC BY-NC 4.0, already shipped under notice as SPAN-S with the non-commercial constraint
  recorded), RealESRGAN_x4plus_anime_6B (BSD-3-Clause, declined by eye), Real-CUGAN (no LICENSE file
  in `bilibili/ailab` as of today, so unusable until clarified), waifu2x (MIT, too slow per
  `enhance.md`). ECBSR, RepSR and LDL code would be reimplemented, not imported.
- **Data.** Which series and library to add if step 1 and 2 pass and the owner still sees the gap on
  colour webtoons.

## 8. Tonight's GAN run (read at 20:53, stopped by the lead at 20:55)

`PLAN.md` records the stop and its cause: with the discriminator added the GPU sat at 15.9 of
16.3 GB and Windows spilled into system memory, so the run crawled (2,500 of 20,000 steps in two
hours, 0.35 it/s against 22 for the plain runs). The lead's fix list (validation sets off the GPU,
discriminator under bf16, check the first eval lands at normal speed) is right. One more thing from
the single checkpoint line that did land (`logs/gan005.log`, step 2,500): `d 1.998`, `g -0.110`,
every validation set within 0.02 dB of the start.

A hinge discriminator loss of 1.998 is a discriminator that outputs about zero on both real and
fake, 1,500 steps after warm-up ended. With spectral norm on every layer and inputs 41 dB apart,
logits stay tiny for a long time and the generator term is nearly inert, which also fits the
unchanged validation numbers. So the rerun needs a learning check beside the memory fix: the
discriminator loss should leave 2.0 during the 1,000-step warm-up. If it does not, raise the
discriminator learning rate (3e-4 to 1e-3 is normal for a hinge U-Net) or drop spectral norm from
the first and last layers before touching the generator weight. Section 5 step 2 has this check
built in.

## 9. What is uncertain

- Whether a plain pixel-unshuffle stack is phase-clean on line art. Real-ESRGAN's RRDB is; ours is
  a different network and the probe is the only answer.
- Whether 200k steps from scratch reaches `long`'s level. `long` had about 300k steps of history in
  several runs; if a candidate is close but short, continue it rather than conclude.
- CPU timings transfer as ratios, not milliseconds. The NAS is the only timing that counts and
  `ps2-c96n12` is near the edge of the budget there.
- The GAN stage's stability on this discriminator is unmeasured (section 8).
- The owner's eye is the test, and PSNR has twice disagreed with it. Every pass bar above is a bar
  for showing, not for shipping.

## Sources

Papers the owner attached (read in full):

- Wang, Xie, Dong, Shan, "Real-ESRGAN: Training Real-World Blind Super-Resolution with Pure Synthetic
  Data", arXiv 2107.10833 (ICCVW 2021). Sections 3.2 (second-order degradation), 3.3 (sinc), 3.4
  (pixel-unshuffle for 2x and 1x, U-Net SN discriminator, two stages), 4.1 (ranges, 1000k + 400k
  iterations, losses {1, 1, 0.1}, EMA), 4.3 (ablations), 4.4 (limitations: GAN artifacts).
- Jiang, Zhang, Timofte, "Towards Flexible Blind JPEG Artifacts Removal" (FBCNN), arXiv 2109.14573
  (ICCV 2021). Section 3.3 (non-aligned double JPEG, QF1 at or below QF2 defeats blind models even at
  a 1 px shift; equation 5 degradation model with a 0 to 7 px shift), 4.1 (batch 256, 128 px, QF 10
  to 95), 4.3 (FBCNN-A results).
- Zamir et al., "Restormer: Efficient Transformer for High-Resolution Image Restoration", arXiv
  2111.09881 (CVPR 2022). Section 3.3 (progressive learning: small patches early, larger later, batch
  reduced to hold time per step). Architecture is far outside the CPU budget; the training schedule
  idea is what transfers.
- Zhu et al., "Denoising Diffusion Models for Plug-and-Play Image Restoration" (DiffPIR), arXiv
  2305.08995 (CVPRW 2023). Up to 100 network evaluations per image; not applicable to a 16 ms budget.
  Noted and set aside.

Papers and reports read online:

- Wang et al., "APISR: Anime Production Inspired Real-World Anime Super-Resolution", CVPR 2024,
  https://arxiv.org/html/2403.01598v2 (degradation ranges, XDoG line pseudo-GT, twin perceptual loss,
  300k + 300k schedule, 3,740-image dataset).
- Wu, Wang, Li, Shan, "AnimeSR", NeurIPS 2022, https://ar5iv.labs.arxiv.org/html/2206.07038
  (learnable basic operators, input rescaling, 300k + 300k, three-scale PatchGAN SN).
- Tuo et al., "VQD-SR", ICCV 2023, https://arxiv.org/abs/2303.09826 (learned degradation codebook,
  cleaning web HR data).
- Liang, Zeng, Zhang, "Details or Artifacts: A Locally Discriminative Learning Approach to
  Realistic Image Super-Resolution" (LDL), CVPR 2022, https://ar5iv.labs.arxiv.org/html/2203.09195
  (artifact map: 7x7 local residual variance, exponent 5, EMA refinement, weight 1).
- Zhang, Zeng, Zhang, "Edge-oriented Convolution Block for Real-time Super Resolution on Mobile
  Devices" (ECBSR), ACM MM 2021, https://www4.comp.polyu.edu.hk/~cslzhang/paper/MM21_ECBSR.pdf
  (block components, D = 2C, Sobel and Laplacian branches, Y-channel training, batch 32 x 64 px).
- Wang, Dong, Shan, "RepSR", ACM MM 2022, https://ar5iv.labs.arxiv.org/html/2205.05671 (two
  expand-and-squeeze branches, frozen-BN trick and its artifact mechanism, table 2 and 4 numbers).
- Wan et al., "SPAN", CVPRW 2024, https://arxiv.org/abs/2311.12770.
- Ren et al., "The Ninth NTIRE 2024 Efficient Super-Resolution Challenge Report",
  https://arxiv.org/html/2404.10343v2 (runtime track on an RTX 3090, table 1, trends).
- Ren et al., "The Tenth NTIRE 2025 Efficient Super-Resolution Challenge Report",
  https://arxiv.org/html/2504.10686v1 (distillation and re-parameterisation as trends, EMSR).
- "SDAKD: Student Discriminator Assisted Knowledge Distillation for Super-Resolution GANs", 2025,
  https://arxiv.org/abs/2510.03870.
- Xie et al., "Exploiting Aliasing for Manga Restoration", CVPR 2021, https://arxiv.org/abs/2105.06830.
- "Screentone-Aware Manga Super-Resolution Using DeepLearning", 2023,
  https://ar5iv.labs.arxiv.org/html/2305.08325.
- Zhao, Gallo, Frosio, Kautz, "Loss Functions for Image Restoration with Neural Networks", IEEE TCI
  2017, arXiv 1511.08861 (MS-SSIM + L1), cited from memory.

Community and model zoos:

- Real-ESRGAN repository docs and release v0.2.5.0 (animevideov3: SRVGGNetCompact 64 features,
  16 convs, BSD-3-Clause; training data and degradations for the anime models are not documented),
  https://github.com/xinntao/Real-ESRGAN/releases/tag/v0.2.5.0,
  https://openmodeldb.info/models/4x-realesr-animevideo-v3.
- MangaJaNai and IllustrationJaNai, https://github.com/the-database/MangaJaNai (seven height-specific
  B&W models, dots restored, colour handled separately; weights CC BY-NC 4.0).
- traiNNer-redux training guide and loss reference,
  https://github.com/the-database/traiNNer-redux/wiki (Compact defaults MS-SSIM + perceptual + HSLuv
  + GAN, EMA 0.999, lq 96, batch 8, "datasets larger than 5,000 images can be detrimental", OTF
  warning), https://trainner-redux.readthedocs.io/en/stable/loss_reference.html.
- Sirosky, "Training a Model in NeoSR", https://github.com/Sirosky/Upscale-Hub/wiki (Compact
  finished under 40k iterations from a pretrain, 5k to 7k images at most).
- Phhofm, 2xHFA2kCompact release notes, https://github.com/Phhofm/models/releases/tag/2xHFA2kCompact
  (2,568 images, 93k iterations, batch 12 x 384 px, Compact pretrain, CC BY 4.0).
- OpenModelDB compact anime models (AnimeJaNai Compact 64nf16nc, CC BY-NC-SA 4.0),
  https://openmodeldb.info/models/2x-AnimeJaNai-v2-Compact.
- Real-CUGAN, https://github.com/bilibili/ailab/blob/main/Real-CUGAN/README_EN.md (million-patch
  dataset, CUNet, no licence file found in the repository).
- nunif waifu2x training doc, https://github.com/nagadomi/nunif/blob/master/waifu2x/docs/training.md
  (reject flat tiles, `lbp5` loss, MIT).
- Anime4K training threads, https://github.com/bloc97/Anime4K/discussions/180 (tiny CNNs trained
  from scratch; no official recipe).
