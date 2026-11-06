# Research plan 2: after the pixel-unshuffle shape failed

Written 2026-11-05, 23:20, by the research sub-agent (Fable 5.1), on the step 1 result and the GAN smoke
test. Research, reading and two small CPU measurements; nothing was trained and no run was touched.
`RESEARCH-PLAN.md` is the previous plan, `PLAN.md` the record. This file replaces the previous plan's
steps 1 to 3 with section 5; the verdict on the lead's proposed next step is section 3.

Numbers about our own data come from `PLAN.md`, `logs/`, and two scripts run tonight on the CPU with one
or two threads (`bench/bench_shapes.py`, `bench/attribute_commit.py`, in this session's scratchpad). The
second imports `measure.py`'s own functions, so its definitions are the tails table's. No library page
left the machine.

## 0. The short version

1. **Pivot: loss first, GAN second, shape last.** The previous plan reasoned "the student cannot see the
   decision, so give it reach, then a GAN". Step 1 bought reach at equal cost and failed on edges, the same
   way the arch sweep's dilated run had. Reach is now twice unsupported as the lever for edges, and the
   evidence below says the edge problem was made by our own loss terms.
2. **The edge regression has a measured cause.** On the owner page, commitment fell at exactly two steps
   of the chain from the colour run to long: the pooled term (0.441 to 0.433) and the band term (0.433 to
   0.425). The other five steps moved it by 0.003 or less. Both terms are applied to the whole crop, edges
   included, where a residual 3 to 15 px across is an edge the student placed a pixel off. They bought
   fills with edges. The first run of the new plan masks both to the fill region and changes nothing
   else; it is the control for everything after it.
3. **Step 1 failed structurally, not for lack of steps.** `ps2-c96n8e` plateaued at the PSNR a
   from-scratch c48n8 reaches at 40k, its phase pattern grew between 50k and 100k, and its seams got worse.
   Its one real result is that a half-resolution body cleans fills (blobs 3.1 per mille against long's
   11.2). The dilated run showed the same pair, cleaner fills with softer edges, so context does help
   fills and what it costs edges depends on how it is bought. The only shape worth one more run is a
   hybrid: long's full-resolution trunk for edges, a half-resolution side branch for context, initialised
   from long so the run is an hour, not a night. Measured tonight at 20.1 ms a tile (1.21x c48n8), and it
   stays in ONNX Runtime's blocked layout.
4. **The GAN did not fail; it is slow, and the stop rule measured the wrong thing.** At 41 dB most pixels
   carry no evidence, so a per-pixel hinge loss cannot approach 0 even for a perfect discriminator, and
   "loss under 1.95 after 1,000 steps" does not test learning. Replace it with the logit gap on the pixels
   that can differ, give the discriminator those pixels only, add instance noise so quantisation is not a
   shortcut, and run it after the loss ladder has done what it can.
5. **Things that stay closed.** Longer training of long's recipe (it gained 0.06 dB on clean B&W over its
   last 100k steps), training on ground truth, another teacher (owner's choice, and rounds two and three).
   `c48n8e-ctl` should be read when it lands, it is free; the prediction is a small PSNR gain (+0.1 dB on
   colour at 42k) and tails within noise.

## 1. Reading of the step 1 result

### 1.1 What happened

`ps2-c96n8e-s1`, 200k planned at a 2e-4 peak, stopped at 100k by the check-in (owner page / colour mean /
grey mean; long in the last column):

| | 50k | 100k | long |
|---|---|---|---|
| PSNR | 37.04 / 39.76 / 33.99 | 37.03 / 39.99 / 34.16 | 41.53 / 43.33 / 37.96 |
| committed (teacher 0.449 / 0.480 / 0.445) | 0.310 / 0.371 / 0.332 | 0.323 / 0.384 / 0.339 | 0.429 / 0.452 / 0.435 |
| excess p95 (floor 0.39) | 0.52 / 0.25 / 0.83 | 0.44 / 0.23 / 0.74 | 0.71 / 0.29 / 0.85 |
| blobs per mille (floor 1.4 / 0.66 / 5.8) | 4.86 / 1.12 / 22.4 | 3.08 / 0.94 / 15.7 | 11.2 / 2.04 / 27.3 |
| edge MAE | 6.45 | 6.27 | 3.96 |
| phase mean (bar 0.5) | 0.518 / 0.389 / 1.451 | 0.562 / 0.419 / 1.134 | 0 |
| seam dB (teacher 65.5) | 62.34 / 62.59 / 63.93 | 61.06 / 61.58 / 62.59 | 148 |

Validation mean 37.86 dB at 100k against long's 39.83, flat from 60k, and slightly falling on every one of
the eight sets between 100k and 115k.

### 1.2 Why it plateaued

- **Feature bandwidth, not parameters.** A body at half resolution with 96 channels is 24 channels per
  low-res pixel; c48n8 has 48. The output is full-resolution detail (a 2x luma residual on top of a
  nearest upsample), so the body's spatial resolution is the bottleneck, and 4.1x the parameters do not
  buy it back. The plateau level says so: 33.7 dB on clean B&W at 100k is where c48n8 from scratch was
  at 40k (`arch-c48n8.log`: 33.61) and under c64n8 at 40k (35.25). Real-ESRGAN's own x2 model survives
  the same front end with a 23-block RRDB of 16.7M parameters; an 8-conv plain stack does not.
- **Four output functions, one body.** After the unshuffle, each body position must produce the 4x4
  output block for 2x2 low-res pixels: four sub-pixel phases, each a different mapping, sharing the same
  activations. The teacher runs its body at full low-res resolution and only shuffles at the end.
- **Possibly the start of over-fitting.** The EMA'd validation score fell about 0.1 dB on all eight sets
  from 100k to 115k while the one-cycle learning rate was still near 1.1e-4. With no augmentation (flips
  are out because the teacher is not equivariant) and about 60k fixed crops, 100k steps at batch 32 is
  about 50 epochs for a 716k-parameter model. This is a 0.1 dB effect and could be learning-rate noise;
  it matters only if a larger student is trained again, in which case log the training loss beside the
  validation score and treat divergence as the signal to label more crops (GPU for labels only).

### 1.3 Why the pattern, and why it did not train out

A stride-2 front end is equivariant to even shifts only. For an odd shift every detail lands on the other
sub-pixel phase and is handled by different weights. The probe measured that directly: 0.52 levels mean
in the owner page's fills at 50k and 0.56 at 100k, 1.45 and 1.13 on B&W pages, p99 4 to 6 levels. It grew
on the owner page while PSNR was flat, so it is the shape, not under-training. Seams worsened for the same
reason: a 21 px reach runs past Maki's 12 px pad, and the tile's reflected context differs from the page's.

### 1.4 What it did show

The fills are real. Blobs 3.08 against long's 11.2 on the owner page (floor 1.4), 0.94 against 2.04 on
colour, 15.7 against 27.3 on grey, excess p95 0.44 against 0.71; pure-fill texture at the teacher's
(0.407), smoother than the teacher on colour pages (123% smoothing), crease texture within 1%. Nothing
before it came close, and it did this with 4.5 dB less PSNR.

The confound: committed 0.323 and edge MAE 6.27. The dilated arch run shows the same pair at equal
PSNR to its control (blobs 10.6 against 18.3, committed 0.328 against 0.357). Two readings fit: context
cleans fills, or a model that cannot render fine structure cannot render blotches either. The hybrid in
section 5 step D is the experiment that separates them, because it keeps a full-resolution trunk for the
edges while adding a half-resolution branch for context.

### 1.5 Is a ps2 variant worth one more try

Not as a whole model: the bandwidth bottleneck and the phase dependence are both properties of
unshuffling the input. The variant worth one run keeps the output path at full resolution and adds the
half-resolution computation as a side branch whose result is bilinearly upsampled and added to the trunk
features. Bilinear upsampling of a low-frequency side signal has no sub-pixel weights, so the phase
dependence is limited to the stride-2 pooling (Zhang 2019: blur before subsampling is what reduces it; a
2x2 average pool is the weakest form, a 3x3 binomial blur with stride 2 the standard fix). Measured
tonight, ONNX Runtime fp32, one intra-op thread, one 120 px context tile, median of 40 runs, this machine
(absolute numbers differ on the NAS; read the ratios):

| shape | ms per tile | x c48n8 | params | reach (LR px) | layout |
|---|---|---|---|---|---|
| `c48n8` (long) | 16.6 | 1.00 | 173k | 10 | 1 reorder (the output) |
| `c48n8-k7first` (7x7 first conv) | 17.1 | 1.03 | 179k | 12 | 1 |
| `c48n8-dw2` (two depthwise 7x7 inserts) | 17.3 | 1.04 | 178k | 16 | 1 |
| `c48n8-x4head` (4x head, halve in Maki) | 18.7 | 1.13 | 188k | 10 | 1 |
| `c48n10` | 20.2 | 1.22 | 214k | 12 | 1 |
| `c64n6` | 21.5 | 1.29 | 230k | 8 | 1 |
| `c48n12` | 24.1 | 1.45 | 256k | 14 | 1 |
| `c64n8` | 28.0 | 1.69 | 304k | 10 | 1 |
| `ps2-c96n8` | 16.4 | 0.99 | 716k | 21 | 1 |
| **`hyb-c48n6-b64n4`** | 16.1 | 0.97 | 273k | 16 via branch, 8 via trunk | 1 |
| **`hyb-c48n8-b64n4`** | 20.1 | 1.21 | 314k | 18 via branch, 10 via trunk | 1 |
| `hyb-c48n8-b64n6` | 21.7 | 1.31 | 388k | 22 / 10 | 1 |
| `hyb-c48n8-b96n4` | 23.4 | 1.41 | 468k | 18 / 10 | 1 |

`hyb-c48nT-bCnB`: long's trunk (first conv, T body convs, head, nearest residual); after trunk conv 3 the
features are 2x2 average-pooled, run through B 3x3 convs of C channels at half resolution, projected by a
1x1 conv to 48 channels, bilinearly upsampled and added to the trunk after conv 4. The NCHWc transformer in
`onnxruntime/core/optimizer/nchwc_transformer.cc` converts AveragePool, linear Upsample with half_pixel
coordinates, and Add between blocked tensors, which the benchmark confirms: every hybrid has exactly one
ReorderOutput, the same as c48n8. LeakyReLU is fused into the convs before that pass, so it costs nothing
either (the control has one reorder too). A depthwise 3x3 stride-2 blur (BlurPool) is also an NCHWc op
(depthwise conv is supported), so anti-aliased pooling costs about the same if the 2x2 pool shows a
pattern.

The 4x head and the depthwise inserts were considered and dropped: the 4x head would need 4x teacher
labels (four times 6 GB for col2 alone) or a differentiable halve inside the loss, which adds nothing the
2x loss does not already constrain; the depthwise inserts are reach for its own sake, which is the lever
the evidence does not support.

## 2. Reading of the GAN failure

Two runs: `d 1.998` after 2,500 steps with no gain and the GPU thrashing (`gan005.log`), then `d 1.963`
at the end of a 1,000-step warm-up with gain 8 and D lr 3e-4, after which the run stopped itself.

**Why it is slow, not broken.** The hinge loss `relu(1 - D(real)) + relu(1 + D(fake))` is 2.0 when D
outputs 0, and it is averaged over every output pixel. The student sits 41 dB from the teacher: inside a
flat fill far from any mark, or on paper white, the two inputs are identical to the bit, and no
discriminator can move those pixels' terms below 2.0. Only the pixels in fills with marks and along edges
carry evidence, perhaps a fifth of a crop. A discriminator that was already perfect on those pixels would
still read about 1.6. So 1.963 after 1,000 steps is a discriminator that has begun to learn on the pixels
that matter, and the sweep the lead proposes (pick the configuration whose loss "clearly leaves 2.0"
within 1,000 steps) would select for whatever drops the loss fastest, which is a shortcut, not a judge
of edges and fills. One shortcut is right there: the real input is the stored uint8 luma target, the fake
is the student's continuous output. Standard GAN practice for near-identical distributions is instance
noise, uniform in plus or minus half a level, added to both inputs before D.

**Is a GAN on a 41 dB distillation sound?** Yes, with modest expectations. The usual stage 2 starts from a
student 25 to 30 dB from its target with texture missing wholesale; here the residual is one or two levels
and spatially sparse, so the discriminator is an anomaly detector for subtle, structured differences, and
it will learn slowly and from few pixels. SDAKD (2025) does distil GAN-trained SR models through a
student-sized discriminator, which is what `gan.py` is. The gradient it gives the student points at
teacher-like local statistics, which is what commitment needs; in fills, teacher-like statistics include
the teacher's own 6% spots, which LDL's weight and the texture and spots tripwires are there for. The
honest expectation is a small gain on `committed` at a real risk to fills, bought with a 74-minute run
per arm at the smoke test's 4.5 it/s. That is why it goes third: the loss ladder in step B is cheaper and
attacks the same tails, and whatever it leaves is what the GAN has to do.

**Protocol changes** (section 5, step C): the hinge and the generator term averaged over the pixels that
can differ (the target's `fill` mask union the dilated strong edges), instance noise, gain 16, D lr 2e-4
against G lr 5e-5, warm-up 2,000 steps judged by the logit gap `mean D(real) - mean D(fake)` and the share
of masked pixels where `D(real) > D(fake)`, both logged every 500 steps, not by the loss.

## 3. Verdict on the lead's next step

1. **Read `c48n8e-ctl`: agree.** It costs nothing. At 42.5k its validation sets sit +0.04 (clean B&W) to
   +0.15 dB (colour fills) over long, so re-parameterisation does move PSNR a little. Prediction for the
   tails: within noise (committed within 0.005, blobs within 10%). If it moves `committed` by 0.005 or
   more, the ECB form becomes the base shape for step B; otherwise long stays the base and the `e` suffix
   is a free add-on for the final run.
2. **The discriminator tuning sweep: change it.** Section 2: the loss threshold is the wrong instrument,
   the sweep would select shortcuts, and the GAN should run after the loss ladder, from its best arm, so
   there is less for it to do. Keep the two weights (0.005, 0.02) and the LDL arms; change the warm-up
   test and the inputs as in step C.
3. **GAN and MS-SSIM on long: partly keep.** The MS-SSIM and gradient controls become arms of the loss
   ladder (B2, B4). Both run on top of the masked band term, not instead of it, because the masking is
   the one change the evidence demands (section 4.1) and every other arm has to be read against it.
   The GAN moves to step C with the protocol change.

In the lead's terms: this is a pivot in order and emphasis, not in machinery. Every run below is a 20k to
60k fine-tune of long with the trainer, measure.py and the check-in script as they stand, plus three
small loss options and one mask.

## 4. What the student's problem is, refreshed

### 4.1 Edges: our loss terms, not reach and not training length

Tonight's attribution (owner page, measure.py's definitions, CPU):

| step in the chain | change | committed (teacher 0.449) | excess p95 | blobs per mille | edge MAE | PSNR |
|---|---|---|---|---|---|---|
| colour run | | 0.438 | 0.93 | 20.3 | 4.19 | 40.81 |
| A1 | data only | 0.441 | 0.88 | 19.1 | 4.01 | 41.17 |
| A2 | pooled 2, EMA | **0.433** | 0.86 | 18.6 | 4.13 | 41.06 |
| A3 | + band 6 | **0.425** | 0.80 | 15.7 | 4.12 | 41.12 |
| A4 | + 80k at 1e-4 | 0.426 | 0.74 | 13.0 | 4.04 | 41.33 |
| se | + ensemble fill targets, 20k | 0.427 | 0.73 | 12.4 | 4.04 | 41.35 |
| long | + 100k at 1e-4 | 0.429 | 0.71 | 11.2 | 3.96 | 41.53 |
| ens-last | 8-orientation targets everywhere | 0.429 | 0.76 | 12.2 | 4.29 | 40.87 |

Commitment dropped by 0.008 at the pooled term and 0.008 at the band term, and by at most 0.003 anywhere
else. Both terms are computed on the whole scored crop. A pixel-off edge is a residual a few pixels wide
with coherent sign, which is exactly what `avg_pool8(r)` and `avg7(r) - avg31(r)` charge; a hedged edge
halves that charge. So the student learned to hedge where it was unsure, and the data-only step (A1),
which had no such term, is the most committed model in the chain. The fill gains of those two terms are
also real (blobs 20.3 to 15.7, then 11.2 with time), and the blob metric lives inside the `fill` mask,
which is 5 px or more from any strong edge. Masking the two terms to that region keeps what they bought
and stops what they cost. This is one page, and A2 also added the EMA, so the first run of step B is the
confirmation.

Reach is not the lever for edges: `c48n8d` (reach 16, equal PSNR) lost commitment against its control,
`ps2` (reach 21) lost far more. Training length is not either: the chain's last 200k steps moved
commitment by 0.004. Width is the one shape change that learned edges faster (c64n8 at 40k: 0.361
against 0.357, +1.6 dB), at 1.69x the cost.

The ens-last row is a side finding: the ensemble-everywhere run was closed on PSNR and edge MAE measured
against the teacher, while its target was the ensemble, so those two numbers could only be worse. Its
tails equal long's. Worth one glance by the owner (section 7), not a run.

### 4.2 Fills: context helps, and the floor is far

Long's owner-page blobs are 8x the floor; grey 4.7x. Every run that saw more context (dilated, ps2) cut
blobs, and the masked terms in step B can only hold them, not cut them further. If the owner's eye still
finds marks after step B, the hybrid in step D is the fill experiment.

### 4.3 The open question: can c48n8 commit at all

The previous over-fit probe (`probe-se.log`) reached the teacher's fill texture on one page within 750
steps and stalled at edge MAE 3.0 to 3.3 by 2,250 steps at 43.9 dB; it did not report `committed`, and
teacher-ens's edge MAE against the teacher is 2.83, so edge MAE near 3 may be the teacher's own
orientation noise rather than a ceiling. A five-minute rerun that prints `committed` decides whether
step B can reach 0.449 or whether the shape question (c64n6 or the hybrid) comes back for edges. It is
step A2.

## 5. The plan

Reference values throughout (owner page / colour mean / grey mean): long committed 0.429 / 0.452 / 0.435,
teacher 0.449 / 0.480 / 0.445, teacher-ens 0.464 / 0.489 / 0.462; long blobs 11.2 / 2.04 / 27.3 per
mille, floor 1.4 / 0.66 / 5.8; long excess p95 0.71 / 0.29 / 0.85, floor 0.39; long PSNR 41.53 / 43.33 /
37.96; long pure-fill texture 0.422 (teacher 0.407), spots 7.09% (teacher 6.16%); texture_crease within
1% of the teacher's on every model so far. "Halfway" means halfway from long to the teacher (committed) or
to the floor (blobs, excess).

Every run below: `train_served.py`, long's sets, weights and `--swap`, EMA 0.999, `--slack 99` so every
run saves, `--clip 4`, `--drop-stop 3`, and a check-in by `logs/checkin.sh` generalised to take the step
list and the kill rule (section 5.6). No run is longer than 60k steps; most are 20k (about 17 minutes at
long's 20 it/s, longer for B5 and the GAN).

### Step A: diagnostics (tonight or first thing, 20 minutes of GPU)

- **A1, done.** The attribution table in 4.1. Re-run it on colour-1 and grey-1 when the GPU is free
  (`measure.py c48n8-a1 c48n8-a2 c48n8-a3 --no-sheets`, a few minutes) to see whether the two-step
  drop holds beyond the owner page.
- **A2, over-fit probe with commitment.** Fine-tune long on the owner page alone, plain L1, 1,500 steps,
  report `committed`, `blobs`, `fill_excess_p95` every 500 steps. Weights are discarded; the page stays a
  test page. Decision rule: `committed` at 0.449 or more means the target is inside the shape and step B
  is the right place to look; under 0.44 means the shape cannot render the teacher's edges even when
  memorising, and step D (the hybrid, or c64n6 at 21.5 ms) moves up to run beside step B rather than
  after it.
- **A3, read `c48n8e-ctl`** (`logs/measure-s1.log`). Base shape for step B is `c48n8e` if `committed`
  improves by 0.005 or more on the owner page with blobs and PSNR held; otherwise `c48n8`.

### Step B: the loss ladder on long (one evening)

All arms from `student-c48n8-long.pt` (or the ECB control, per A3), 20k steps, lr 1e-4 one-cycle, check-in
at 10k. B1 runs first and alone; the others are read against it.

| arm | tag | adds to long's recipe | question |
|---|---|---|---|
| B1 | `mask` | `--fill-mask`: the pooled and band terms multiplied by `fill_mask(target)` and averaged over it; plain L1 unchanged everywhere | does commitment return to A1's 0.441 with blobs held |
| B2 | `mask-grad` | B1 + `--grad 0.1` (Sobel-gradient L1, in `losses.py`) | cheapest commitment term |
| B2b | `mask-gv` | B1 + gradient variance loss (Abrahamyan et al. 2022: Sobel maps of prediction and target, 8x8 non-overlapping patches, per-patch variance, distance between the variance maps; 8x8 is their 2x setting) | the structural form of B2: charges a soft edge for low gradient variance, not for misplacement |
| B3 | `mask-topk` | B1 + the mean of the worst 20% of pixel residuals per crop, weight 1 | tails directly; closer to an L-infinity objective |
| B4 | `mask-mssim` | B1 + `--mssim 0.15` | the previous plan's non-adversarial control |
| B5 | `mask-feat` | B1 + feature distillation: the teacher run in bf16 with no gradient on the same low-res crop; the student's last body activation (48 channels, low-res resolution) through a trained 1x1 conv to 64 channels, L1 to the teacher's last body activation, both standardised per channel over the batch, inside the margin | a richer signal per step than one luma value per pixel |
| B6 | `mask-lp` | B1 with the L1 replaced by `(r^2 + eps^2)^(p/2)`, p = 0.7, eps half a level | a mode-seeking pixel loss (Barron 2019: alpha under 1 is redescending; the limit is mode finding), so the student commits to the most frequent teacher answer rather than the median; untested in SR, so last |

Weights for the new terms: log every term's value at step 200 and set the weight so the new term is a
quarter of the L1 term at the start. `--grad 0.1` and `--mssim 0.15` are the previous plan's values; check
them against the same rule. B5 drops throughput by about a third (one extra 64-channel, 16-conv forward);
budget 30 minutes. B2b and B6 need about fifteen lines each in `losses.py`; B3 is a `topk` on the flattened
residual; B5 needs the teacher loaded in the trainer and one 1x1 conv in the optimiser.

Expected: B1 lifts committed to about 0.44 with blobs at or under 11.2 and PSNR unchanged; B2 or B2b add
another few thousandths; B3 and B6 are the arms most likely to surprise in either direction; B4 is a
control; B5 is likely small (EMSR and DSCLoRA report hundredths of a dB on PSNR from affinity distillation,
and PSNR is not our target, so it stays as one arm).

**Check-in at 10k** (owner page only, CPU, about 90 seconds with two threads using measure.py's functions,
as `attribute_commit.py` does; or `measure.py --cpu --no-sheets` over all pages, five minutes at eight
threads): kill the arm if owner-page `committed` is under 0.425, or `blobs` over 13.4 (1.2x long), or PSNR
under 41.2, or `texture_crease` outside 5% of the teacher's. Note every check-in in `queue.log` as now.

**Pass (a candidate for the owner's eye):** committed at least 0.439 on the owner page and not under long
on colour (0.452) or grey (0.435); blobs not over long on any group (11.2 / 2.04 / 27.3), excess p95 not
over 0.71; PSNR within 0.25 dB of long on every group; texture_crease within 5%; pure-fill texture not
under the teacher's by more than 3% (over-smoothing); `phase` 0 and `seam` 148 (automatic for this shape,
checked anyway).

**Pick:** among passing arms, score each by the share of the long-to-teacher commitment gap closed plus
the share of the long-to-floor blob gap closed, owner page and colour mean averaged. The best two go to
the owner (section 6) and the best one is the base for step C. If nothing passes, B1's result still
decides the next move: committed up with blobs held means the masking is right and the remaining gap is
capacity (go to A2's reading and step D); committed unchanged means the attribution was wrong and the
GAN (step C) runs from long.

Combinations (for example B1 + grad + topk) are a second evening, only if two arms pass on different
tails.

### Step C: the GAN, from the best B arm (a second evening)

Base: the step B pick. `train_served.py --gan`, 20k steps, G lr 5e-5, `--ema 0.999`, `--ldl` at a weight set
by the quarter rule, four arms as before (0.005 and 0.02, each with and without LDL; the LDL arms first).
Discriminator protocol changes in `gan.py` and the trainer:

- `--d-mask`: the hinge terms and the generator term averaged over `fill_mask(target)` union the 11 px
  dilation of the target's strong edges (`sobel > 64`), computed per crop from the luma target. Pixels
  outside cannot differ and only dilute both the loss and its reading.
- instance noise: uniform noise in plus or minus half a level added to the real and the fake before D
  (not to the condition), every step, so quantisation of the stored target is not a shortcut.
- `--d-gain 16`, `--d-lr 2e-4`, spectral norm kept on the inner layers; betas (0.5, 0.999) as now.
- warm-up 2,000 D-only steps. Every 500 steps print `d`, the masked logit gap `mean D(real) - mean D(fake)`
  and the share of masked pixels with `D(real) > D(fake)`. Continue past warm-up only if the gap is at least
  0.05 at 2,000 and higher than at 1,000; otherwise extend the warm-up once to 4,000 with the same test;
  otherwise stop and record the configuration as inert. Do not sweep gain or lr on the loss value.

Check-ins every 5k on the owner page (CPU): kill at the first of pure-fill `texture` over 0.419 (teacher
+3%), `spots` over 6.8% (teacher +10%), owner PSNR down more than 0.3 dB from the base, `texture_crease`
outside 5%, `blobs` over the base's, or grey PSNR down more than 0.3 dB (grain). Pass: committed at least
0.449 on the owner page and within 0.01 of the teacher on colour and grey; blobs within 2x the floor
(2.8 / 1.3 / 11.6); the kill numbers all held. The owner sees the best GAN arm beside its base and the
teacher whether or not it passes, because a look that is crisper than the teacher is their call.

### Step D: the hybrid shape, on a trigger (an hour of GPU)

Trigger: step B and C leave owner-page blobs over 6.3 or committed under 0.439, or A2 says the shape
cannot commit. Run `hyb-c48n8-b64n4` (20.1 ms, 1.21x; section 1.5) initialised from long: trunk convs,
head and first conv copied, branch convs randomly initialised, the 1x1 projection at zero so step 0 is
long exactly (the trainer's `start` scores must equal long's, which is the assertion). 60k steps at 1e-4
with the step B pick's loss, `--probes` at the 20k and 40k check-ins (the branch's reach is 18 px, past
Maki's 12 px pad, so seams and phase are both live). Kill: phase over 0.5 levels on the owner page or
over 1.0 on grey, seam under 62.5 dB, committed under the base's minus 0.005 at 40k. Pass: owner blobs at
or under 6.3 with committed and PSNR not under the base's, phase under 0.3, seam within 3 dB of the
teacher's 65.5. If the 20k check-in shows phase between 0.3 and 0.5, swap the 2x2 average pool for a
depthwise 3x3 binomial blur with stride 2 (BlurPool) and restart; same cost in ORT.

`hyb-c48n6-b64n4` (16.1 ms, equal cost to today) is the fallback if the owner declines the 1.21x. It
cannot be initialised from long (a shorter trunk), so it is a from-scratch 200k run and a night; only on
the owner's say.

c64n6 (21.5 ms) is the width alternative for edges if A2 names capacity and the hybrid does not pass. It
cannot be initialised from long either; a night, on the owner's say.

### Step E: Maki

As in the previous plan: collapse the ECB branches if the `e` form shipped, export, catalogue entry
(model multiple 2 if the hybrid shipped), the `KeepsFlat` exact-flat check (`flat_train` is in every run),
NAS timing on a paged page and a tall webtoon slice, `UpscaleCache.Generation` raised, then the owner's eye
on the reference set plus two pages from series in no set.

### 5.6 Check-in protocol

`logs/checkin.sh` as it stands waits for a step line, copies the checkpoint, measures on the CPU, notes
`queue.log` and kills on phase and seam. For steps B and C it needs three generalisations: the step list
as an argument (10000 for B; 5000 10000 15000 for C; 20000 40000 for D), the kill rule as an expression
over the tails columns (`committed`, `fill_excess_p95`, `blobs`, `psnr`, `texture`, `spots`,
`texture_crease`) rather than only `phase` and `seam`, and an owner-page-only mode that imports
measure.py's functions (as `attribute_commit.py` does) so a check-in costs about 90 seconds instead of
five minutes and the GPU run loses nothing. Every check-in writes its line to `queue.log`; a kill names the
rule that fired. No run in this plan is longer than 60k steps or runs past its first check-in unjudged.

## 6. What to show the owner, when

- Nothing from step A or the first half of step B.
- After step B: if any arm passes, `show/index.html` with long, the best two arms and the teacher, plus
  the worst-window sheets from `measure/<model>/owner-webtoon.png` for the same three, so the owner sees
  the tails the numbers picked. If nothing passes, a two-line note with B1's committed and blobs and the
  reading from section 5, step B, last paragraph, no page.
- After step C: the best GAN arm beside its base and the teacher, pass or fail.
- After step D: only if it passes the phase and seam bars; a model with a 2 px pattern is on the owner's
  rejection list and should not be shown.
- One side request, any time: the ens-last crops already in `show/` (`*_c48n8-ens-last.png`) beside long's.
  The ensemble target was closed on numbers measured against the wrong reference (4.1); a thirty-second
  look either confirms the closure or re-opens a cheap direction (the `se8` label sets exist).

## 7. Owner decisions

1. **The order.** GAN after the loss ladder, which delays the GAN result by one evening. Recommended.
2. **Cost ceiling for the hybrid.** `hyb-c48n8-b64n4` is 1.21x c48n8 on this machine; it is still about 2.9x
   faster than the teacher Maki runs today. Whether 1.21x is inside "clearly faster" is the owner's call,
   and the NAS timing decides, not this machine's. If no, the equal-cost `hyb-c48n6-b64n4` is a night from
   scratch.
3. **Ens-last by eye** (section 6). Yes or no costs nothing.
4. **Ship from B or wait for C.** If a step B arm passes and looks right, it can go to Maki while the GAN
   runs; or the owner waits one evening for the GAN's answer on "refined lines". Recommended: wire the B
   pick behind the catalogue and NAS-time it while C runs, since the Maki steps are the same either way.
5. **Data like the owner's page** stays the owner's call and stays on hold, unchanged from the previous
   plan: nothing here says the data is the lever yet.

## 8. What is uncertain

- The attribution is one page and one chain. A2 added the EMA as well as the pooled term, and the colour
  run to A1 step changed the data. The pattern (0.008 at each of the two loss terms, 0.003 or less at the
  other five) is clear enough to act on; B1 is the confirmation, and A1's colour and grey rerun the check
  that it is not an owner-page accident.
- Masking may not recover all of the 0.016. The fill mask stops 5 px short of strong edges, so the terms
  no longer touch edge residuals, but the student may have learned a general softening that 20k steps do
  not undo; A4 and long recovered 0.004 in 200k steps, which argues the hedge is held in place by the
  terms rather than baked in.
- Whether 0.44 against 0.45 commitment is what the owner sees as "refined lines", or whether they see
  the near-line fill excess (p95 0.71 against the floor's 0.39), which is marks beside lines rather than
  the lines themselves. The masked band term works on that region too; measure both.
- The GAN protocol is reasoned, not measured. The logit-gap test is the right kind of instrument; its
  numbers (0.05 at 2,000 steps) are a guess to be replaced by what the first run logs.
- The hybrid's phase is unmeasured until it runs. The argument says it is small (no learned sub-pixel
  weights); the probe decides; BlurPool is the fallback.
- CPU timings transfer as ratios, not milliseconds, and the NAS is the only timing that counts.
- The owner's eye is the test, and the numbers have twice disagreed with it. Every pass above is a bar
  for showing, not for shipping.

## 9. Sources

Read tonight:

- Abrahamyan, Truong, Philips, Deligiannis, "Gradient Variance Loss for Structure-Enhanced Image
  Super-Resolution", ICASSP 2022, https://ar5iv.labs.arxiv.org/html/2202.00997 (Sobel maps on grey, 8x8
  non-overlapping patches at 2x, variance maps compared; gains of a few tenths of a dB on VDSR and EDSR,
  not uniform; no weight stated, no artifact discussion). Code: https://github.com/lusinlu/gradient-variance-loss.
- He et al., "FAKD: Feature-Affinity Based Knowledge Distillation for Efficient Image Super-Resolution",
  ICIP 2020, https://ieeexplore.ieee.org/abstract/document/9190917 (spatial affinity of flattened feature
  maps, L1 between student and teacher affinities).
- "Distillation-Supervised Convolutional Low-Rank Adaptation for Efficient Image Super-Resolution"
  (DSCLoRA), 2025, https://arxiv.org/html/2504.11271v1 (affinity loss weight 0.01 against pixel
  distillation 0.1 and reconstruction 1; gains 0.003 to 0.03 dB; a 52-channel teacher was worse than none).
- Ren et al., "The Tenth NTIRE 2025 Efficient Super-Resolution Challenge Report",
  https://arxiv.org/html/2504.10686v1 (EMSR: L1 pixel distillation plus spatial affinity distillation
  after each block plus L2 to ground truth, 30k steps at 1e-4, EMA; no ablation of the distillation
  terms; distillation and re-parameterisation named as the trends).
- Kaparinos, Mezaris, "SDAKD: Student Discriminator Assisted Knowledge Distillation for Super-Resolution
  GANs", 2025, https://arxiv.org/abs/2510.03870 (student-sized discriminator, three stages, feature map
  distillation in the last two).
- Barron, "A General and Adaptive Robust Loss Function", CVPR 2019, https://arxiv.org/abs/1701.03077
  (alpha under 1 is redescending; the alpha to minus infinity limit is local mode finding; alpha 1 is
  Charbonnier).
- Zhang, "Making Convolutional Networks Shift-Invariant Again", ICML 2019, https://arxiv.org/abs/1904.11486
  (BlurPool: low-pass before subsampling restores shift equivariance; binomial filters).
- ONNX Runtime, `onnxruntime/core/optimizer/nchwc_transformer.cc` (main),
  https://github.com/microsoft/onnxruntime/blob/main/onnxruntime/core/optimizer/nchwc_transformer.cc
  (converted ops: Conv including depthwise, MaxPool, AveragePool, Add and Sum between blocked tensors,
  Concat on axis 1 with block-aligned channels, Upsample and Resize nearest or linear with asymmetric,
  align_corners or half_pixel coordinates, BatchNormalization; LeakyRelu, PRelu and Clip force a
  reorder unless fused into the conv first).
- Real-ESRGAN x2 model notes, https://mintlify.com/xinntao/Real-ESRGAN/models/general-models (x2plus
  ranked under x4plus by the maintainers' own guide; indirect).

Carried over from `RESEARCH-PLAN.md` (not re-read tonight): Real-ESRGAN (Wang et al. 2021), LDL (Liang et
al. 2022), ECBSR (Zhang et al. 2021), RepSR (Wang et al. 2022), APISR (Wang et al. 2024), AnimeSR (Wu et
al. 2022), FBCNN (Jiang et al. 2021), Zhao et al. 2017 on MS-SSIM + L1.

Measured tonight (this machine, scripts in the session scratchpad): `bench_shapes.py` (section 1.5
table), `attribute_commit.py` (section 4.1 table; owner page, two CPU threads, measure.py's functions).
