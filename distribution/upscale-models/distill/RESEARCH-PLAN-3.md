# Research plan 3: what limits m1, and how to close the last visible gap

Written 2026-11-06 by the research sub-agent (Fable 5.1), after m1 shipped as Maki 2x and the N round (more
steps, hard mining, hard mining plus sharpness 0.3) made no visible difference to the owner. `PLAN.md` is the
record; `RESEARCH-PLAN.md` and `RESEARCH-PLAN-2.md` are the earlier rounds and nothing from them is repeated
here without new evidence. Everything about our own data comes from `PLAN.md`, the logs, and the probes run
tonight (`probe_gap.py`, `probe_anatomy.py`, `probe_sensitivity.py`, `probe_strong.py`, `probe_widen.py`,
`tradeoff.py`, `bench3.py`, `faint.py`, plus seven 10k-step trainings in `X:/MakiModel/probe3/`). No library
page left the machine.

## 0. The short version

1. **What binds is the target, not the student.** Under one level of input noise, below the JPEG floor of
   any library page, the teacher's own faint lines change gradient by 20 to 27% on the owner and grey pages
   (a quarter of faint-edge pixels by more than 35%) while its strong edges move under 1%. On faint lines
   Real-ESRGAN is a chaotic function of its input. A student of any practical size fits the conditional
   mean of a placement it cannot predict and renders the strand soft; m1 under the same noise moves 5 to
   7%. The keep-or-erase decision beside a line is the same kind of decision, and the student's half-erased
   mark is its conditional mean too. That is why width (section 2.7: averages up 0.05 dB, every tail
   unchanged), data (2.1: no train-to-validation gap), hard mining and the loss ladder (2.3) could not move
   the circled pixels: they all ask the student to match what it cannot predict.
2. **The lever is commitment, not matching.** Reward a crisp line at the student's own placement: the
   one-sided sharpness term, extended to faint lines in relative form (2.9); a masked discriminator as the
   stronger form of the same thing; and the marks term, which already proved the principle (k1: marks down a
   third with PSNR up half a dB). The 8-orientation ensemble labels on every colour fill crop are softer
   than the teacher on faint lines (0.88 / 0.95 / 0.93 of its gradient, 2.4), but training without them for
   10k steps moves the student's faint lines by 0.005 (2.8): the softness is the student's own smoothing,
   not the labels'.
3. **One part of the tail is learnable and gets its own term.** Thin lines at high contrast are stable in
   the teacher (1 to 2% under noise) and soft in the student (Sobel ratio p10 0.67 to 0.77 against 0.83 to
   0.88 for thick edges of the same contrast; 2.6). Those are the thin grey-page lines in the green circles.
   A depth term aimed at them lifted their lower quartile from 0.92 to 1.00 in 10k steps and overshot (lines
   8 to 17% deeper, 2.11); like the faint term (2.9) it runs guarded. The two terms are the first things in
   two days to move a tail, and both moved only the tail they were aimed at.
4. **Ranked steps** (section 4): relative faint sharpness term; a thin-line depth term; a short masked GAN
   on the hybrid from the best of those; width as a long run only as polish, paid for by 192 px tiles on the
   Maki side. More data, hard mining, reach, pixel-unshuffle, flips, feature distillation, int8, tile
   routing, specialists and the ensemble swap stay closed (section 5).

## 1. The owner's circles as numbers

The three kinds of circle on `show/index.html`, with what the probes say about each (owner page / colour mean
/ grey mean; `probe3/anatomy-m1.log`, `measure-N.log`, `smudge-N.log`):

| circle | what it is on the page | measured on m1 |
|---|---|---|
| red: smudges, kept source marks, speckle, blotchy shading, a colour-edge fringe | marks 3 px and over that differ from the teacher by 12 levels or more: 0.106 / 0.089 / 0.503 per mille, 99 to 100% of them within 6 px of a line | pure flat fill is at the teacher's level (MAE 0.33 / 0.26 / 0.46 levels, blobs at the floor on colour pages); the marks are decisions beside lines, the same tail as the green circles seen from the fill side |
| green: strand tips, thin lines on grey, lettering, a gloss band, a band edge | the softest tenth of strong edges (Sobel ratio p10 0.77 / 0.82 / 0.82 against a median of 0.99) and the lower quartile of faint edges (p25 0.61 / 0.76 / 0.61) | thin dark lines are right on average (depth 0.98 / 1.01 / 1.01 of the teacher's, width 0.99 / 1.01 / 0.98), their lower quartile 8 to 10% shallow; 45% of the strong-edge error is placement within one pixel (MAE 3.9 against 2.2 when the model may be a pixel off), the rest is profile |
| blue: a hair stroke's dark-to-light gradient | fill beside creases: MAE 0.64 / 0.43 / 0.67 levels, band texture within 1% of the teacher's | a tail inside the crease region, not an average |

The three nitpicks are one problem: a few percent of edge and near-edge pixels where the student does not
resolve the teacher's decision. "Very good, now I only nitpick" is the median; the circles are the tail.

## 2. Diagnosis

### 2.1 Train against validation: no gap (`probe3/gap-m1.log`)

m1 scored on 1,024 crops of every training set (eight runs of 128, read from disk) and on the whole of every
validation set, with the trainer's own centre-luma PSNR, per-crop hardness (3x3-averaged |r| over 4 levels,
the `--hard` score), the residual on strong edges, and line sharpness pooled over the set. col2 on its
`@rest` rows only (the fill rows trained toward ensemble labels, not the teacher targets on disk).

| set | n | PSNR | p10 | hardness mean | p90 | edge MAE | rest MAE | error on edges | faint | strong |
|---|---|---|---|---|---|---|---|---|---|---|
| train (B&W clean) | 1024 | 40.81 | 31.03 | 0.543 | 1.566 | 4.15 | 0.40 | 88.5% | 0.948 | 0.992 |
| val | 766 | 41.37 | 33.01 | 0.339 | 0.884 | 3.70 | 0.40 | 85.8% | 0.968 | 0.995 |
| served_train (B&W damaged) | 1024 | 39.80 | 32.55 | 0.441 | 1.059 | 4.13 | 0.46 | 89.2% | 0.947 | 0.996 |
| served_val | 766 | 40.87 | 32.93 | 0.389 | 0.949 | 4.07 | 0.44 | 85.2% | 0.954 | 0.993 |
| col_train | 1024 | 47.62 | 37.52 | 0.109 | 0.298 | 2.95 | 0.39 | 73.2% | 0.927 | 0.996 |
| col_val | 264 | 45.04 | 38.21 | 0.096 | 0.259 | 2.89 | 0.55 | 65.3% | 0.971 | 0.997 |
| col_served_train | 1024 | 44.33 | 36.12 | 0.179 | 0.464 | 3.44 | 0.49 | 76.2% | 0.930 | 0.994 |
| col_served_val | 264 | 44.24 | 37.19 | 0.136 | 0.385 | 3.30 | 0.59 | 64.8% | 0.951 | 0.996 |
| col2_train@rest | 658 | 46.18 | 36.70 | 0.150 | 0.377 | 3.05 | 0.37 | 81.5% | 0.928 | 0.997 |
| col2_val@rest | 547 | 50.13 | 37.08 | 0.117 | 0.340 | 3.16 | 0.33 | 74.1% | 0.945 | 0.996 |
| col2_served_train@rest | 668 | 48.38 | 37.08 | 0.135 | 0.368 | 3.25 | 0.37 | 79.3% | 0.935 | 0.992 |
| col2_served_val@rest | 547 | 48.76 | 36.20 | 0.164 | 0.464 | 3.59 | 0.38 | 73.6% | 0.946 | 0.991 |

The training rows are not fit better than the validation rows on any column: B&W training crops score
lower than validation (the training series are harder), colour ones score higher on one set and lower on
another, hardness and its p90 move with set difficulty and not with membership, and pooled faint-line
sharpness is 0.93 to 0.95 on training crops against 0.95 to 0.97 on validation. After about 180 visits per
crop over the whole chain, a model that could memorise would show a gap. This one shows none: it cannot fit
the hard tail of its own training crops, so more crops like them cannot help, and hard mining (N1) could only
reweight crops it already could not fit. The one-page over-fit probe in `PLAN.md` succeeded because one page
needs far less capacity than the distribution. Section 2.5 says what the unfittable part is.

The edge columns say where the error goes: 65 to 89% of the squared error is on strong-edge pixels
(dilated by one), which are 8 to 23% of a page.

### 2.2 Anatomy of the residual on the reference pages (`probe3/anatomy-m1.log`)

Regions of the teacher's luma in priority order: strong edges (Sobel over 64, dilated 1), faint edges (16 to
64, off strong ones), paper and ink, pure flat fill, fill beside creases, the rest.

| group | pixels: strong / faint / paper / pure / crease / rest | squared error share | MAE strong / faint / pure / crease | strong, pixel-off tolerant |
|---|---|---|---|---|
| owner | 8.5 / 0.3 / 58.6 / 19.2 / 4.7 / 8.6% | 88.9 / 0.4 / 1.5 / 1.9 / 1.3 / 6.0% | 3.90 / 1.59 / 0.33 / 0.64 | 2.17 |
| colour (5) | 9.2 / 0.6 / 35.3 / 38.8 / 7.0 / 9.1% | 88.8 / 0.4 / 0.8 / 3.4 / 1.3 / 5.2% | 3.24 / 1.17 / 0.26 / 0.43 | 1.77 |
| grey (5) | 23.2 / 0.7 / 55.1 / 2.6 / 3.7 / 14.8% | 94.0 / 0.5 / 1.5 / 0.2 / 0.5 / 3.4% | 3.77 / 2.04 / 0.46 / 0.67 | 2.07 |

Line rendering, same log:

| group | thin dark lines: depth / p25 / width (teacher 1.0) | Sobel ratio on strong edges p10 / p25 / p50 | on faint edges p10 / p25 / p50 |
|---|---|---|---|
| owner | 0.979 / 0.919 / 0.992 | 0.77 / 0.91 / 0.99 | 0.37 / 0.61 / 0.87 |
| colour | 1.010 / 0.905 / 1.013 | 0.82 / 0.93 / 0.99 | 0.61 / 0.76 / 0.92 |
| grey | 1.005 / 0.930 / 0.983 | 0.82 / 0.93 / 0.99 | 0.36 / 0.61 / 0.90 |

Readings:
- Fills are solved to the teacher's level on average (the hybrid's branch did that); what the owner still
  circles in fills is beside lines.
- The median strong edge is the teacher's. The complaint lives in the bottom decile, and in faint edges
  generally, where a quarter of the pixels have under two thirds of the teacher's gradient. Those are the
  strand tips and thin grey-page lines in the green circles.
- Thin lines are not systematically wider or shallower; their lower quartile is 7 to 10% shallow.
- About 45% of the strong-edge error disappears when the model is allowed to be one pixel off, so nearly
  half of what PSNR counts on edges is placement the eye does not see; the other half is softness it does.

### 2.3 The loss arms sit on one surface (`tradeoff.py`)

Every measured arm of the hybrid family, owner / colour / grey (from `measure/*.json`):

| arm (from) | faint sharpness | marks12 per mille | grain (teacher .204 / .155 / .279) | PSNR owner | committed owner |
|---|---|---|---|---|---|
| f-hyb (e3 + branch, 60k) | .811 / .884 / .751 | .175 / .139 / .879 | .171 / .229 / .296 | 42.15 | .459 |
| final015 (+ sharp 0.15) | .898 / .917 / .995 | .136 / .103 / .732 | .170 / .148 / .297 | 42.11 | .478 |
| h2 (+ sharp 0.3) | .935 / .934 / 1.068 | | .155 / .166 / .307 | 41.85 | .486 |
| g2 (+ sharp at the quarter rule) | 1.014 / .956 / 1.222 | | .159 / .172 / .313 | 41.58 | .493 |
| k1 (final015 + marks) | .890 / .919 / .977 | .106 / .087 / .486 | .162 / .170 / .311 | 42.61 | .465 |
| l1 (k1 recipe, fine x2) | .879 / .911 / .952 | .103 / .090 / .485 | .169 / .150 / .300 | 42.58 | .463 |
| m1 (l1 + sharp 0.2), shipped | .899 / .919 / .989 | .106 / .089 / .503 | .156 / .139 / .301 | 42.55 | .467 |
| n0 (m1 + 20k) | .890 / .922 / .996 | .094 / .072 / .429 | .196 / .145 / .303 | 42.71 | .464 |
| n1 (m1 + hard mining) | .870 / .915 / .935 | .104 / .077 / .421 | .155 / .156 / .315 | 42.56 | .456 |
| n2 (n1 + sharp 0.3) | .894 / .924 / 1.009 | .088 / .067 / .386 | .163 / .216 / .311 | 42.60 | .461 |

The sharpness term buys faint sharpness with PSNR and commitment past the teacher's (g2, h2: the model
sharpens broadly because it cannot sharpen exactly); the fine term buys grain with faint sharpness; hard
mining buys marks with faint sharpness; sharpness on top buys them back with colour grain. The one arm that
moved a nitpick and PSNR together was the marks term (k1: marks down a third, PSNR +0.5 dB), because the
p = 0.7 power loss had left large coherent residuals cheap and the term priced a neglected region rather
than trading one for another. Everything after it is a trade.

### 2.4 The ensemble labels are softer than the teacher (`probe3/faint-teacher-ens.log`)

`faint.py` on Real-ESRGAN averaged over the page's 8 orientations (`teacher-ens`), the label every
col2 fill crop trains toward through `--swap` (3,185 clean and 6,370 damaged crops, 14 of 100 shares):

| | sharp_faint | snap_faint | sharp_strong |
|---|---|---|---|
| teacher-ens, owner page | 0.882 | 0.388 | 0.997 |
| teacher-ens, colour pages | 0.951 | 0.403 | 0.996 |
| teacher-ens, grey pages | 0.927 | 0.524 | 0.995 |
| m1, owner / colour / grey | 0.899 / 0.919 / 0.989 | | 0.993 / 0.991 / 0.993 |

Averaging eight orientations keeps strong edges (within 0.5%) and blurs faint ones by 5 to 12%, because
the teacher places a faint line differently per orientation. m1's faint lines on the owner page are as
soft as the ensemble's. The swap was adopted when fills were the problem (se against A4 in `PLAN.md`:
fill texture 0.426 against 0.431); the branch has since taken fills to the floor, so the swap's price is
now visible and its benefit is not. Section 2.8 measures removing it.

### 2.5 The teacher is deterministic, and chaotic on faint lines (`probe3/sensitivity.log`)

Real-ESRGAN is a plain stack with no stride and the Lanczos halve is linear, so for integer low-res shifts
its output is the same function of the same window: deterministic and shift-equivariant, and the student's
reach (19) covers the teacher's (18). The known inconsistencies are orientation (flips are out), the
ensemble swap (2.4) and Maki's 12 px pad against the 18 px reach, which the seam probe prices at 65.5 dB
(0.14 levels RMS, invisible). None of that is the limit.

What is: `probe_sensitivity.py` adds uniform noise of one level to every input pixel of every reference
page and scores each model's noisy-input output against its own clean-input output, on the clean output's
edge pixels (model(noisy) Sobel over model(clean) Sobel; PSNR between the two):

| model | group | PSNR noisy vs clean | faint-line ratio (p25) | strong-edge ratio | MAE on faint / strong pixels |
|---|---|---|---|---|---|
| teacher | owner | 49.17 | 1.271 (1.00) | 0.994 | 1.72 / 1.39 |
| teacher | colour (5) | 50.92 | 0.991 (0.90) | 0.994 | 0.71 / 0.80 |
| teacher | grey (5) | 51.24 | 1.204 (1.01) | 0.999 | 1.17 / 0.71 |
| m1 | owner | 53.38 | 1.074 (0.96) | 0.995 | 0.73 / 0.81 |
| m1 | colour (5) | 53.35 | 0.996 (0.93) | 0.996 | 0.46 / 0.57 |
| m1 | grey (5) | 53.53 | 1.052 (0.95) | 0.999 | 0.55 / 0.51 |

One level of noise, which no reader can see and which every JPEG page carries, moves the teacher's faint
lines by 20 to 27% on the owner and grey pages (on grey-4 and grey-5 the upper quartile of faint pixels
changes by over 35%), and its strong edges by under 1%. The teacher amplifies the noise into line crispness
(the mean ratio is over 1). m1 under the same noise moves its faint lines by 5 to 7% and is 4 dB more
stable. The student has learned a smoother function than the teacher, which is what a smaller model does
when the target varies faster than it can follow: it outputs the conditional mean over the input variations
it cannot resolve, and the mean of differently placed crisp strands is a soft strand. The same applies to a
speck beside a line that the teacher erases or keeps depending on noise: the student's mean is a half-erased
mark, the owner's smudge.

This is consistent with everything else measured tonight: no train-to-validation gap (the unfittable part
is in the training crops too), tails that no loss arm and no width could move, and an over-fit probe that
succeeds on one page (memorising one noise realisation needs no generalisation). It also says what will not
work: no amount of capacity short of reproducing the teacher's own sensitivity, which would mean reproducing
its noise amplification, gets the student to the teacher's placement, and the owner cannot tell the
teacher's placement from any other crisp one. They can tell crisp from soft. So the student should be made to
commit to a crisp rendering at its own placement, which is what the one-sided sharpness terms and a
discriminator do and what an L1 or power loss to the teacher does not.

### 2.6 Which strong edges are soft (`probe3/strong-m1.log`)

Strong-edge pixels of the teacher split by its 7x7 contrast range and by whether they lie on a thin line (at
most 8 of the 5x5 at least 24 levels under the 7x7 mean, dilated 1); m1's Sobel over the teacher's (mean,
p10) and the teacher's own change under one level of input noise as a share of its gradient:

| group | 64 to 128 thin / thick | 128 to 192 thin / thick | over 192 thin / thick |
|---|---|---|---|
| owner: p10 | 0.69 / 0.68 | 0.73 / 0.76 | 0.67 / 0.83 |
| owner: teacher noise | 6.0% / 6.8% | 3.5% / 3.4% | 1.9% / 1.4% |
| colour: p10 | 0.71 / 0.75 | 0.73 / 0.83 | 0.77 / 0.87 |
| colour: teacher noise | 4.4% / 4.1% | 1.9% / 1.7% | 1.1% / 0.7% |
| grey: p10 | 0.74 / 0.74 | 0.73 / 0.81 | 0.72 / 0.88 |
| grey: teacher noise | 2.7% / 2.7% | 1.8% / 1.5% | 1.3% / 0.7% |

The means are 0.95 to 1.01 in every bin; the tail is the p10. It has two parts:
- low-contrast strong edges (64 to 128 levels, 0.8 to 2.2% of a page), where the teacher is itself 3 to 7%
  noise-sensitive: the faint-line case continued;
- thin lines at any contrast, where the teacher is stable (1 to 2% at high contrast) and the student is
  soft (p10 0.67 to 0.77 against 0.83 to 0.88 for thick edges of the same contrast). This part is a
  learnable target the student does not fit, and it is the thin grey-page lines in the green circles. It
  gets its own term (section 4, step 3).

### 2.7 Width probe: a wider trunk from m1's own function (`probe3/measure-widen.log`, `anatomy-widen.log`)

`probe_widen.py` builds a wider hybrid whose new output channels are zero (new input channels small random,
so they carry a gradient), checked to a 0.0 difference against folded m1 at step 0, and `widen.sh` ran m1's
recipe for 10k steps from three starts of the same function: a control (ECB branches folded and restarted at
zero, 314k folded parameters), the trunk at 64 channels everywhere (`hyb-c64n8e-b64n4`, 456k), and the trunk
at 64 for its first four convs only (`hyb-c64_4_48n8e-b64n4`, 379k). The `c64_4_48` grammar is new in
`common.py` and changes nothing for existing specs. Owner / colour / grey:

| arm | validation mean at 10k | PSNR | blobs per mille | marks12 | faint sharpness | faint p25 (anatomy) | thin depth p25 | strong p10 |
|---|---|---|---|---|---|---|---|---|
| control (c48 trunk) | 40.95 | 42.61 / 44.00 / 38.78 | 2.33 / 0.75 / 10.51 | .100 / .084 / .458 | .895 / .916 / 1.000 | .60 / .75 / .63 | .919 / .899 / .923 | .78 / .82 / .81 |
| wide (c64 trunk) | 41.00 | 42.69 / 44.08 / 38.84 | 2.07 / 0.79 / 9.15 | .098 / .080 / .450 | .894 / .919 / .999 | .61 / .76 / .62 | .919 / .903 / .926 | .78 / .82 / .82 |
| front-wide (c64 first four) | 40.94 | 42.61 / 44.01 / 38.78 | 2.16 / 0.77 / 10.21 | .106 / .087 / .470 | .898 / .919 / .984 | .61 / .75 / .61 | .919 / .902 / .923 | .78 / .82 / .81 |

A third more channels at full resolution buys a uniform 0.05 to 0.08 dB on every set and page (the control
shape needed 20k steps, n0, for the same), 10 to 13% fewer blobs on the owner and grey pages, and leaves
every tail where it was to the third decimal: faint quartiles, thin-line depth, strong-edge p10, marks.
Front-wide buys nothing. The new capacity goes where the loss pulls, the averages, and the loss cannot pull
on a target the student cannot predict (2.5). Width is polish, at a cost (section 4, step 6).

### 2.8 Removing the ensemble swap (`probe3/measure-noswap.log`)

m1's recipe for 10k from the control start with `--swap` dropped, so every col2 fill crop trains toward the
teacher's own output. Owner / colour / grey, control in brackets:

| | validation mean | col2 fill sets | faint sharpness | faint p25 | thin depth p25 | owner texture (teacher .407) | owner grain (teacher .204) | blobs |
|---|---|---|---|---|---|---|---|---|
| no swap | 40.97 (40.95) | +0.12 / +0.11 (+0.07 / +0.06) | .898 / .921 / 1.001 (.895 / .916 / 1.000) | .61 / .76 / .64 (.60 / .75 / .63) | .918 / .899 / .923 (.919 / .899 / .923) | .404 (.398) | .186 (.166) | 2.39 / 0.76 / 9.83 (2.33 / 0.75 / 10.51) |

A clean negative. The model follows the new targets at once (the fill validation sets, whose targets are the
teacher's, gain twice what the control gains), yet faint lines move by 0.003 to 0.005 and their lower
quartile by 0.01, inside noise; the price is a little more texture and grain in the owner page's fills, which
is what the swap was bought for. The ensemble labels are softer than the teacher (2.4), but the student's
softness is not inherited from them: it is its own smoothing (2.5). Keep the swap as it is; it is not a lever
either way.

### 2.9 A relative sharpness term on faint edges (`probe3/measure-relsharp.log`)

`losses.sharpness_deficit_relative`: the one-sided deficit divided by the target's own gradient, on faint
edges (Sobel 16 to 64) off the flanks of strong ones, so a strand line counts as much as an outline; the
absolute term weighs a 30-level line at a fifth of a 200-level one and spends itself on strong edges that are
already matched (2.2). `train_served.py --sharp-rel`, run at half its quarter-rule weight beside m1's
absolute term, 10k from the control start. Weight 0.061 (the quarter rule gave 0.122). Owner / colour / grey,
control in brackets:

| | faint sharpness | faint Sobel ratio p10 / p25 / p50 | faint MAE | PSNR | blobs per mille | grain | validation (colour sets) |
|---|---|---|---|---|---|---|---|
| relative term | 1.137 / 1.006 / 1.271 (.895 / .916 / 1.000) | .56 / .82 / 1.09, .66 / .82 / .99, .59 / .87 / 1.17 (.60 / .75 / .87, .60 / .75 / .92, .38 / .63 / .91) | 1.83 / 1.28 / 2.34 (1.60 / 1.17 / 2.03) | 42.52 / 44.00 / 38.72 (42.61 / 44.00 / 38.78) | 3.38 / 0.85 / 12.63 (2.33 / 0.75 / 10.51) | .171 / .148 / .325 (.166 / .230 / .304) | -0.12 to -0.20 dB (+0.02 to +0.06) |

The first thing in two days to move the faint tail: the lower quartile of faint-edge pixels went from 0.61
to 0.82 of the teacher's gradient on the owner page and from 0.63 to 0.87 on grey pages, thin lines and
strong edges untouched, marks unchanged. And it overshot the way g2 did: the mean faint ratio is 1.14 and
1.27 where the teacher is 1.0, faint-pixel MAE rose, colour validation fell 0.12 to 0.20 dB, and owner-page
blobs rose 45%. Two reasons, both in the term's definition: every faint gradient inside a fill (JPEG texture
at 16 to 64 levels) counts as an edge, and a one-sided deficit is cheapest to satisfy by sharpening all of
it. The run would have been stopped by the standard check-in (blobs 3.38 against the 2.5 bar). So the
faint tail is loss-limited after all, in the sense that a term aimed at it moves it; what it needs is a
guard on how far. `losses.sharpness_deficit_line` (`--sharp-line`) is the relative term plus a charge when
the model's own gradient at the pixel exceeds 1.15 times the target's, so broad sharpening costs; on a
synthetic test it charges a blurred line, an over-sharpened one, and nothing for an exact or 10% sharper
one. A guard on where (structure-tensor coherence, to keep only line-like faint pixels) was measured and
dropped: on the owner page faint pixels in pure fill pass a coherence gate of 0.5 to 0.8 at 62 to 86%
against 69 to 91% for faint pixels on lines, and only 0 to 2% of faint pixels are in pure fill at all; the
overshoot came from faint structure beside creases, which coherence cannot tell from strands. So the
guarded term runs at a lower weight with the excess charge, and the check-in's blob bar does the rest. It is
step 1 in section 4.

### 2.10 Without the EMA (`probe3/measure-noema.log`)

Weight averaging over the last thousand steps could blur a placement successive iterates disagree on, and 2.5
says faint placement is exactly that. m1's recipe without `--ema` (live weights scored and saved), 10k from
the control start. Owner / colour / grey, control in brackets:

| | validation mean | PSNR | faint sharpness | faint p25 | blobs per mille | grain |
|---|---|---|---|---|---|---|
| no EMA | 40.94 (40.95) | 42.61 / 44.02 / 38.79 (42.61 / 44.00 / 38.78) | .886 / .919 / .964 (.895 / .916 / 1.000) | .60 / .75 / .61 (.60 / .75 / .63) | 2.04 / 0.75 / 9.20 (2.33 / 0.75 / 10.51) | .165 / .229 / .309 (.166 / .230 / .304) |

The live weights are no sharper than the averaged ones on faint lines (slightly softer on the owner and grey
pages), identical in every tail, and 0.21 to 0.26 dB under the control on every set at the learning-rate
peak (step 2,500), which the EMA is there to smooth. The EMA is not the blur. Keep it.

### 2.11 The thin-line depth term (`probe3/measure-thin.log`, `anatomy-thin.log`)

`losses.thin_line_deficit` (`--thin`): on the target's thin dark lines (a 5x5 minimum at least 24 levels
under the 7x7 mean, at most 8 such pixels in the 5x5), the one-sided depth deficit against the model's 3x3
minimum, as a share of the target's depth; 2.6's learnable part of the strong-edge tail. Run at half its
quarter-rule weight (0.087), 10k from the control start. Owner / colour / grey, control in brackets:

| | thin depth mean / p25 / width | strong Sobel p10 | strong sharpness | faint sharpness | PSNR | validation | blobs | grey line_hf | committed |
|---|---|---|---|---|---|---|---|---|---|
| thin term | 1.084 / 1.000 / 1.014, 1.167 / 1.031 / 1.036, 1.147 / 1.027 / 1.027 (0.980 / 0.919 / 0.993, 1.008 / 0.899 / 1.010, 0.999 / 0.923 / 0.980) | .79 / .83 / .84 (.78 / .82 / .81) | .995 / .998 / .998 (.992 / .991 / .992) | .939 / .935 / 1.226 (.895 / .916 / 1.000) | 42.46 / 43.77 / 38.45 (42.61 / 44.00 / 38.78) | -0.15 to -0.40 dB | 2.41 / 0.92 / 14.09 (2.33 / 0.75 / 10.51) | 2.40 (2.31) | .452 / .463 / .441 (.463 / .474 / .448) |

The term reaches its target: the thin-line lower quartile goes from 0.92 to 1.00 and over on every group,
and the thin part of the strong-edge tail rises 0.01 to 0.03, the first movement of that tail in two days.
And it overshoots the way the relative term did: lines 8 to 17% deeper than the teacher's and 1 to 4%
wider, grey faint lines at 1.23 (thin faint strokes are in both masks), jagged-line energy up, grey blobs
up a third, PSNR down 0.15 to 0.33 dB. The check-in would have stopped it on grey `line_hf` (2.40 against
the 2.4 bar). The remedy is the same as 2.9's: an excess charge above 1.1 times the teacher's depth (now in
the term as `over`; on a synthetic line it charges a line that is too deep and nothing for one 5% deeper)
and a quarter of the weight. Width is not charged; the check-in's `thin_width` bar is the guard.

### 2.12 Other candidates, dismissed on numbers

- bf16 autocast during training adds about a tenth of a level of noise on the residual path; the circled
  errors are 5 to 15 levels.
- The luma-only output costs nothing in quality (the chroma outputs get no gradient and are ignored by Maki);
  it is a 2% cost lever at most.
- The training targets are the teacher on the crop with zero padding, scored inside a 22 px margin, past the
  teacher's 18 px reach: uncontaminated. Rounding to bytes at 4x before the halve is under half a level.
- The quarter-rule weights differ by up to 10% between arms started from the same function (`fine` 5.9 to
  6.4, `marks` 464 to 537), because they are set from ten random batches. Below the differences read above
  but worth fixing with a fixed seed for the weight batches if arms are ever compared at the 0.01 dB level.

## 3. Research since plans 1 and 2 (only what adds evidence)

- **NTIRE 2026 Efficient SR report** (Ren et al., arXiv 2604.03198, April 2026). Trends the organisers name
  (sections 3.1 and 3.3): "network pruning combined with knowledge distillation remains a practical and
  effective strategy", "progressive pruning leads to more stable training and smaller performance
  degradation"; "most teams adopted multi-phase training pipelines with progressively increasing patch
  sizes and decreasing learning rates"; FFT frequency losses and EMA "widely adopted"; re-parameterisation
  used "to improve inference speed without sacrificing model capacity during training". Second and third
  place (BOE_AIoT, PKDSR) pruned SPAN's channels 32 to 20 and 32 to 28 to 24 in stages and recovered with
  teacher distillation (sections 4.2, 4.3); one stage-2 recipe is MSE feature distillation at 0.5 from a
  28-channel teacher, cosine annealing to 5e-5. No CPU figures anywhere; every runtime is an RTX A6000. What
  transfers: the prune-from-wider recipe as the way to keep a wide run's gain at today's cost (section 4,
  step 7). The rest we already have (EMA, re-parameterisation, a frequency term equivalent to band and fine).
- **SDAKD** (Kaparinos, Mezaris, arXiv 2510.03870): a student-sized discriminator for distilling SR GANs,
  which `gan.py` already is; staged training, feature-map distillation through an MLP. No new evidence on
  the fill cost that stopped our GAN arms. Section 2.5 is the new argument for trying it again, masked.
- **APISR** (Wang et al., CVPR 2024): line sharpness comes from the pseudo-ground-truth (XDoG-extracted
  hand-drawn lines sharpened and merged back), not from a loss; the twin perceptual loss uses a
  Danbooru-trained ResNet. Our target is the teacher's own look, so the pseudo-GT route is a deviation the
  owner would have to want. The line-map idea survives as a loss on lines, which the relative faint term and
  the thin-line term do more cheaply.
- **EatGAN** (arXiv 2509.14550) and the **Gradient Variance loss** (Abrahamyan et al. 2022): Sobel-gradient
  MSE at weight 0.01, and gradient-map variance matching. Both are our B2 and B2b arms, which gave a few
  thousandths of commitment; no new lever, and both are matching terms, which 2.5 says is the wrong kind.
- **Content-adaptive routing** (ClassSR 2021, PCSR ECCV 2024, CAMixerSR 2024, Pure-Pass arXiv 2510.01997):
  patch or pixel classifiers that send easy regions to a cheaper path. None reports CPU numbers; all are
  single-network designs on GPU. For Maki a two-model tile router would put two looks on neighbouring
  tiles; section 5 rules it out.
- **Net2Net** (Chen, Goodfellow, Shlens, ICLR 2016): function-preserving widening by unit replication; the
  variant used here (zero new output weights, small random new input weights) preserves the function exactly
  and breaks symmetry without replication, checked to 0.0 difference at step 0.
- **ERNCSR** (IEEE Access 2025) and the large-kernel re-parameterisation line (LKDN, RepLKASR): multi-branch
  training folded into one 3x3 for CPU, which is `common.ECB`. Large-kernel attention variants pay ORT
  reorders and stay out.
- **Community compact anime models** (Phhofm's 2x HFA2k Compact family): nothing on width against edge
  sharpness; 64 features is the community default at 2x.

## 4. Ranked next steps

Every run: `train_served.py`, m1's sets, shares and recipe unless stated, `--slack 99`, EMA 0.999, a
`checkin.py` at 10k on the owner page and grey-1 with `--probes`, kill rule relative to m1 unless stated:
`phase > 0.5 or grey_phase > 1.0 or seam < 62.5 or blobs > 0.0025 or psnr < 42.3 or abs(texture_crease /
texture_crease_teacher - 1) > 0.05 or grey_grain > 0.285 or marks12 > 0.000110 or grey_line_hf > 2.4`.
Cost per tile is one ONNX Runtime thread on a 120 px context tile on this machine, m1 at 19.6 ms and
c48n8 at 16.4 ms (`export_onnx.py`); the teacher is 58.7 ms. "By eye" means `show/index.html` rebuilt with
the candidate beside m1 and the teacher, plus the smudge sheets.

Expected effects are on the three circle kinds: red (marks beside lines), green (line sharpness), blue (the
gradient beside a stroke).

### Step 1: the guarded relative sharpness term on faint edges (two arms, 20k each from m1)

`--sharp-line 1 --quarter fine marks sharp_line --gain fine=2 sharp_line=0.25`, and a second arm at
`sharp_line=0.5`, both with m1's `--sharp 0.2` kept. Check-in at 10k with the standard kill plus
`sharp_faint > 1.03` on the owner page and `grey_sharp_faint > 1.03`.
- Expected: green, the main effect: faint lower quartile from 0.61 to about 0.8 on the owner page and from
  0.63 to about 0.85 on grey (2.9 reached that at the unguarded weight), mean faint sharpness held between
  0.97 and 1.03 by the excess charge, thin lines and strong edges unchanged; red, none or slightly fewer
  (a committed strand leaves no half-strand beside it); blue, none.
- Cost per tile: none. Training: 2 x 12 minutes.
- Risks against the reject list: blotches beside creases (2.9 saw +45% blobs on the owner page at the
  unguarded weight) and halos if the excess charge is too loose. Caught by `blobs > 0.0025`, the faint mean
  bar, `texture_crease` within 5%, and by eye on the owner page's hair and the grey-2 thin lines.
- Kill: either bar at 10k; if both arms kill on blobs, lower `over` to 1.05 before trying a third weight.
  If neither arm lifts the faint p25 (`probe_anatomy.py`) by at least 0.1, the term is closed and the GAN
  (step 3) is the only commitment lever left.

### Step 2: the thin-line depth term (one arm, 20k from the step 1 pick)

`--thin 1 --quarter fine marks sharp_line thin --gain fine=2 sharp_line=<pick> thin=0.25` (`losses.thin_line_deficit`
with its excess charge at 1.1: depth deficit on the target's thin dark lines, placement-tolerant within a
pixel, charged both for lighter and for over-deep lines). 2.11 has the 10k probe of the one-sided term at
twice this weight.
- Expected: green, thin-line depth p25 from 0.92 to about 0.97 with the mean held under 1.04, and the
  strong-edge p10 on thin lines from 0.67 to 0.77 toward the thick edges' 0.83 to 0.88 (2.6: the teacher is
  stable there; 2.11 moved the whole strong p10 by 0.01 to 0.03 at the unguarded weight); the grey-page thin
  lines in the owner's circles. Red and blue none.
- Cost per tile: none. Training: 12 minutes.
- Risks: lines darker or wider than the teacher's, which reads as heavier line art, and jagged lines (2.11:
  depth 1.08 to 1.17, width 1.01 to 1.04, grey line_hf 2.40, grey blobs up a third at the unguarded weight);
  caught by `thin_width > 1.02` and `thin_depth > 1.04` in `probe_anatomy.py`, `grey_line_hf > 2.4`, the
  standard blob bar, and by eye on grey-1 and grey-2.
- Kill: the standard rule, or width over 1.02 or depth over 1.04 at 10k; if it still overshoots, `over` 1.05.

### Step 3: a short masked GAN on the hybrid (from the step 2 pick, 10k at 5e-5)

`--gan 0.005 --d-mask --d-noise 0.5 --d-gain 16 --d-lr 2e-4 --gan-warmup 2000 --gap-min 0.05 --lr 5e-5
--iterations 10000`, check-ins at 2.5k, 5k and 7.5k on the owner page and grey-1, kill relative to the base:
`blobs > base x 1.1 or grain > base x 1.1 or grey_grain > base x 1.1 or psnr < base - 0.3 or
abs(texture_crease / texture_crease_teacher - 1) > 0.05 or marks12 > base`.
- Why now: 2.5 says the faint and near-line target is noise-driven, so matching cannot finish the job and
  commitment can; a discriminator is the strongest commitment term there is. The earlier arms (step C in
  `PLAN.md`) ran on c48n8e-b6, whose fills were 6x the floor, and paid in blobs; the hybrid's fills are at the
  floor and the branch holds them, so the price may be lower now. c3 at 5k reached the teacher's commitment
  for a few percent more blobs.
- Expected: green, committed and faint sharpness up without the mean overshoot a one-sided term produces
  (the discriminator judges the look, not the gradient); red, the half-erased marks beside lines resolved one
  way or the other (fewer marks12); blue, risk rather than gain.
- Cost per tile: none. Training: about 35 minutes at 5 it/s, plus the warm-up.
- Risks: every item on the reject list, which is why it is third and short; the owner sees the result beside
  its base and the teacher whether it passes or not, because a look crisper than the teacher is their call.
- Kill: any tripwire at a check-in; the warm-up stop if the logit gap is under 0.05.

### Step 4: assemble and show

The best of steps 1 to 3 (or step 2's pick if the GAN fails): `export_onnx.py` (plain hybrid layout, 19.6 ms,
no Maki change), `measure.py --probes`, `smudge.py --sheet --extra` with the owner's hair page, `faint.py`,
`probe_anatomy.py`, and `show.py` with Lanczos, m1, the candidate and the teacher, smudge windows from m1.
The owner's eye decides; the three circle kinds are the checklist.

### Step 5: Maki-side, when the owner wants more speed or a wider model

192 px tiles for Maki 2x (context 216): the pad overhead falls from 1.56x to 1.27x, which `bench3.py`
measures as 0.80 to 0.83 of the per-output-pixel cost of the 120 px tile for every shape in the table
(`probe3/bench3-clean.log`, one ORT thread, random weights, this machine):

| shape | ms per 120 px tile | x c48n8 | x m1 | ms per 216 px tile | per output pixel against the 120 px tile |
|---|---|---|---|---|---|
| c48n8 | 16.3 | 1.00 | 0.80 | 54.1 | 0.83 |
| hyb-c48n8-b64n4 (m1) | 20.4 | 1.25 | 1.00 | 65.7 | 0.81 |
| hyb-c48n8-b64n6 | 22.0 | 1.35 | 1.08 | 70.8 | 0.80 |
| hyb-c48n8-b96n4 | 23.9 | 1.47 | 1.17 | 76.7 | 0.80 |
| hyb-c48n10-b64n4 | 24.3 | 1.49 | 1.19 | 78.0 | 0.80 |
| hyb-c64_4_48n8-b64n4 | 25.7 | 1.58 | 1.26 | 82.5 | 0.80 |
| hyb-c64n6-b64n4 | 25.8 | 1.58 | 1.26 | 83.5 | 0.81 |
| hyb-c48n12-b64n4 | 28.1 | 1.73 | 1.38 | 90.4 | 0.80 |
| c64n8 | 28.9 | 1.77 | 1.42 | 93.0 | 0.81 |
| hyb-c64n8-b64n4 | 32.6 | 2.00 | 1.60 | 105.5 | 0.81 |
| teacher (4x, before the halve) | 58.7 | 3.60 | 2.88 | | |

Needs the seam check (reach 19 against the 12 px pad is unchanged), worker memory about 3.2x per tile under
`WorkerBudgetBytes`, and fewer one-value tiles skipped on paged B&W pages. Pure cost recovery; no quality
change, so no owner look. Pairs with step 6.

### Step 6: width as polish (60k from m1's function, only with step 5)

`probe_widen.py hyb-c48n8e-b64n4-<best>-last hyb-c64n8e-b64n4 --tag wide0`, then the recipe for 60k at
1e-4 with check-ins at 20k and 40k (probes on, since the branch's reach is unchanged this is a formality).
- Expected: +0.1 dB on every set, 10 to 15% fewer blobs on the owner and grey pages, no change in any tail
  (2.7). Red slightly, green none, blue slightly.
- Cost per tile: `hyb-c64n8` is 32.6 ms, 1.60x m1 on this machine and 1.8x faster than the teacher; on
  192 px tiles it is 1.29x m1's cost today per output pixel. The branch-only widenings (`b64n6` at 1.08x,
  `b96n4` at 1.17x) are cheaper but untested and act on fills, which are at the floor.
- Risks: none on the reject list beyond the standard rule; the risk is to speed, which the NAS timing decides.
- Kill: at 40k, faint p25, thin depth p25 and marks12 not better than a same-length control by 0.02 / 0.02 /
  10%: stop, keep the control. The 10k probe predicts exactly that outcome, which is why this is sixth.

### Step 7: prune back (only if step 6 ran and the owner wants today's cost)

NTIRE 2026's second and third place recipe: prune the c64 trunk to c48 in two stages (64 to 56 to 48, by
channel importance), 20k steps each with pixel distillation from the unpruned c64 model beside the teacher
targets, EMA. Expected: most of step 6's 0.1 dB kept at m1's cost. Risks and kill as step 6.

### Not queued, with their triggers

- Specialists: no trigger from this round (section 5).
- Online degradation with teacher labels on the fly (infinite coverage): only if a train-to-validation gap
  opens (2.1's rule).
- A less chaotic teacher: the owner's call, and only if steps 1 to 3 leave the faint tail where it is.

## 5. Ruled out, and why

- **More data of the current kind, and data like the owner's pages.** No memorisation (2.1); the unfittable
  part is noise in the target (2.5), which more examples of the same teacher do not remove. Re-open only if
  a change opens a train-to-validation gap (train PSNR more than 0.3 dB over validation on a set).
- **Hard-example mining.** Reweights crops the model cannot fit; N1 softened faint lines and N2 bought them
  back with colour grain at 1.4x the teacher. Same surface.
- **More reach, dilation, pixel-unshuffle.** Reach is 19 against the teacher's 18; the dilated and ps2 runs
  lost edges for fills; the hybrid already has the context branch. Closed.
- **Width as the lever for the circles.** 2.7: averages only. Kept as polish (step 6) with a cost.
- **Feature distillation from the teacher's body.** B5 scored 0.88 against the masked control's 0.92; the
  NTIRE reports put its value at hundredths of a dB. Not a lever for a tail of 5 to 15 levels, and it is a
  matching term.
- **Flips and orientation averaging everywhere.** A2 with flips lost 1.5 dB; ens-last had the same tails;
  and 2.4 shows what averaging does to faint lines.
- **int8.** 70 to 100 levels off on single pixels (`enhance.md`); the owner ruled it out.
- **Tile routing to two models.** Two looks on neighbouring tiles; a 1 to 2 level step along a tile line in
  a fill is a grid, which is on the reject list, and the seam probe cannot be run on a mixed page without
  the Maki pipeline. Not worth a run.
- **Specialist B&W and colour students.** Both kinds are under-fit the same way (2.1) and for the same
  reason (2.5); the lever is the objective, which specialists share.
- **Dropping the ensemble labels on colour fills.** Measured neutral on faint lines and slightly worse on
  the owner page's fills (2.8). Stays as it is.
- **Luma-only head, 4x head, depthwise inserts, 7x7 first conv.** Cost levers of 2 to 4% or reach levers;
  none touches the tail.
- **A different teacher or own-model training.** The owner's choice stands (`PLAN.md`); nothing here reopens
  it. One note for that day: a teacher that is 20 to 27% noise-sensitive on faint lines is a hard target for
  any student, and a less chaotic one (anime6B measured crisper and flatter but with its own outline) would
  distil more exactly.

## 6. What is uncertain

- **Every probe arm is 10k steps.** That is enough to see a term move a tail (2.9 did in 10k) and enough to
  see width move averages, but not enough to rule out that a wider trunk trained for 100k would eventually
  reach into the tail. Step 6's 40k kill rule is where that gets a fair test, if the owner wants the cost.
- **The noise probe uses one level of uniform noise as the stand-in for what varies between two scans of the
  same drawing.** Real page-to-page variation is JPEG quantisation and resampling, which is structured, not
  uniform. The ratio between the teacher's and the student's sensitivity (3 to 4x on faint lines, equal on
  strong ones) is the finding; the absolute 20 to 27% is specific to this noise.
- **The faint-line ratio under noise includes the noise's own gradient.** One level of input noise carries
  some Sobel energy of its own, which is why both models read over 1.0 on the owner page. The comparison
  between the two models on the same noise is still fair, and the MAE columns (1.72 against 0.73 levels on
  faint pixels) say the same thing without a ratio.
- **The owner set is one page, and the colour and grey means are five pages each.** The direction of every
  finding agrees across the three groups; the sizes differ (the owner page is the most noise-sensitive of all
  eleven for the teacher).
- **The quarter-rule weights move by up to 10% between arms** started from the same function, because they
  are set from ten random batches (2.12). Below the effects read here; fix the batch seed before comparing
  arms at the 0.01 dB level.
- **The cost table is this machine's**, one ORT thread, random weights, repeated c48n8 and hybrid rows within
  3%. The NAS is the only timing that decides, and nothing here was timed there.
- **The GAN's price on the hybrid is a prediction.** Section 2.5 is the reason to expect commitment to help
  and the floor-level fills are the reason to expect a smaller fill cost than step C paid; neither has been
  run. The tripwires are the protection, and it is third for that reason.
- **The owner's eye is the test**, and the numbers have disagreed with it before. Every bar above is a bar
  for showing, not for shipping.

## Files added tonight

| file | role |
|---|---|
| `probe_gap.py` | train against validation PSNR, hardness, edge residual and sharpness per set (2.1) |
| `probe_anatomy.py` | residual by region, pixel-off tolerant edge error, thin-line depth and width, Sobel ratio quantiles (2.2) |
| `probe_sensitivity.py` | a model's own faint and strong lines under one level of input noise (2.5) |
| `probe_strong.py` | the strong-edge tail by contrast and thinness, with the teacher's noise sensitivity per bin (2.6) |
| `probe_widen.py` | function-preserving widening of a checkpoint into a wider spec, checked at step 0 (2.7) |
| `tradeoff.py` | every measured arm of a family in one table (2.3) |
| `bench3.py` | one-thread ORT cost per 120 and 216 px tile for candidate shapes |
| `common.py` | `c64_4_48n8e` width grammar (a width per conv), `Hybrid` reads its tap and join widths, `load_student` maps only `body.` keys |
| `losses.py`, `train_served.py` | `sharpness_deficit_relative` (`--sharp-rel`, the unguarded probe term), `sharpness_deficit_line` (`--sharp-line`, step 1), `thin_line_deficit` (`--thin`, step 2) |
| `X:/MakiModel/probe3/` | logs, the seven 10k arms (`widen.sh`, `after.sh` to `after4.sh`), `measure-*.log`, `anatomy-*.log`, `bench3-clean.log`, `onnx/` for the bench |

## Sources

- Ren et al., "The Eleventh NTIRE 2026 Efficient Super-Resolution Challenge Report", CVPRW 2026,
  https://arxiv.org/abs/2604.03198 (sections 3.1, 3.3, 4.2, 4.3, 4.11, 4.14).
- Kaparinos, Mezaris, "SDAKD: Student Discriminator Assisted Knowledge Distillation for Super-Resolution
  GANs", 2025, https://arxiv.org/abs/2510.03870.
- Wang et al., "APISR: Anime Production Inspired Real-World Anime Super-Resolution", CVPR 2024,
  https://arxiv.org/html/2403.01598v2.
- "EatGAN: An Edge-Attention Guided Generative Adversarial Network for Single Image Super-Resolution", 2025,
  https://arxiv.org/abs/2509.14550.
- Abrahamyan et al., "Gradient Variance Loss for Structure-Enhanced Image Super-Resolution", ICASSP 2022,
  https://arxiv.org/abs/2202.00997.
- Kong et al., "ClassSR", CVPR 2021; "PCSR", ECCV 2024,
  https://www.ecva.net/papers/eccv_2024/papers_ECCV/papers/00370.pdf; "CAMixerSR", 2024,
  https://arxiv.org/abs/2402.19289; "Pure-Pass", 2025, https://arxiv.org/abs/2510.01997.
- Chen, Goodfellow, Shlens, "Net2Net: Accelerating Learning via Knowledge Transfer", ICLR 2016,
  https://arxiv.org/abs/1511.05641.
- Xie et al., "Large Kernel Distillation Network for Efficient Single Image Super-Resolution", CVPRW 2023,
  https://arxiv.org/abs/2407.14340; ERNCSR, IEEE Access 2025 (edge-aware re-parameterisable network for
  compressed-image SR, fused to one 3x3, about 20x faster on a consumer CPU).
- Phhofm, 2xHFA2kCompact and related releases, https://github.com/Phhofm/models/releases/tag/2xHFA2kCompact.
