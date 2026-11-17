"""Acceptance gates for a ranking change, read off the next-read grader's per-request files.

    python distribution/eval-next-gates.py default obs50 cap125 ...

The first name is the baseline. Every variant has to have been scored in one run of
`eval-reco-labels.cs library --next --csv ndcg` with it, so they share requests. Reads
`<artifacts>/eval/rr-<variant>-next-<column>.csv` and `next-readers.tsv` (MAKI_ARTIFACTS_DIR,
default `.artifacts`). Paired differences against the baseline, 20,000 bootstrap resamples, 95%
percentile interval, the same method as eval-compare.py.

A change passes when none of these is a certain move the wrong way (the interval wholly on the
wrong side of zero):

  wantN, wantT   must not fall, pooled. wantN again in each third of the readers by how
                 household their own shelf is (niche, middle, mainstream).
  passed         must not rise, pooled and in each third.
  runaway        share of readers whose page is 30 points more household than their shelf. Must not rise.
  overlap        must not rise.
  floor          the page must not be less household than the shelves it was built from: the mean of
                 (page household share minus shelf household share) must not be certainly below zero.
                 Past that a change is removing titles these readers go on to read.

`lowers fame` is the claim a fame fix has to earn on top: passed certainly falls, pooled.
Plain nDCG@40, want and plan@20 are printed by the grader and are not gates: a page of the most
popular titles earns 17%, 38% and 70% of the engine's score on them. The judged panel, the confirm
split and a real mixed shelf are the gates this file cannot check (distribution/CLAUDE.md, "v8").
"""
import csv
import os
import sys
from pathlib import Path

import numpy as np

EVAL = Path(os.environ.get("MAKI_ARTIFACTS_DIR", ".artifacts")) / "eval"
BOOTSTRAP = 20000
SEED = 20260803
RUNAWAY = 0.30
THIRDS = ("niche", "middle", "mainstream")


def column(variant: str, name: str) -> dict[int, float]:
    path = EVAL / f"rr-{variant}-next-{name}.csv"
    if not path.exists():
        sys.exit(f"{path} is missing. Score `{variant}` with --next --csv ndcg first.")
    with path.open() as fh:
        return {int(key): float(value) for key, value in csv.reader(fh)}


def interval(values: np.ndarray) -> tuple[float, float, float]:
    rng = np.random.default_rng(SEED)
    picks = rng.integers(0, len(values), size=(BOOTSTRAP, len(values)))
    means = np.sort(values[picks].mean(axis=1))
    return float(values.mean()), float(means[int(0.025 * BOOTSTRAP)]), float(means[int(0.975 * BOOTSTRAP)])


def main() -> None:
    names = sys.argv[1:]
    if len(names) < 2:
        sys.exit(__doc__)
    base, variants = names[0], names[1:]

    shelf = {}
    with (EVAL / "next-readers.tsv").open() as fh:
        next(fh)
        for line in fh:
            cells = line.split("\t")
            shelf[int(cells[0])] = float(cells[4])
    ordered = sorted(shelf, key=shelf.get)
    third = len(ordered) // 3
    groups = {
        "niche": set(ordered[:third]),
        "middle": set(ordered[third:len(ordered) - third]),
        "mainstream": set(ordered[len(ordered) - third:]),
    }

    def paired(variant: str, name: str, keys=None, transform=None):
        a, b = column(variant, name), column(base, name)
        shared = sorted(k for k in set(a) & set(b) if keys is None or k in keys)
        x = np.array([a[k] for k in shared])
        y = np.array([b[k] for k in shared])
        if transform is not None:
            own = np.array([shelf[k] for k in shared])
            x, y = transform(x, own), transform(y, own)
        return interval(x - y), float(y.mean()), float(x.mean())

    def runaway(house, own):
        return (house - own >= RUNAWAY).astype(float)

    print(f"baseline {base}, {len(shelf)} readers, thirds of {third} by the household share of their own shelf\n")
    for variant in variants:
        rows = []
        failed = []

        def gate(label, result, wrong, percent):
            (diff, lo, hi), before, after = result
            bad = hi < 0 if wrong == "fall" else lo > 0
            good = lo > 0 if wrong == "fall" else hi < 0
            if bad:
                failed.append(label)
            scale, unit = (100, "") if percent else (1, "")
            shown = (f"{before * scale:6.1f}% -> {after * scale:5.1f}%   {diff * scale:+5.1f} [{lo * scale:+5.1f}, {hi * scale:+5.1f}]"
                     if percent else f"{before:7.3f} -> {after:6.3f}   {diff:+.4f} [{lo:+.4f}, {hi:+.4f}]{unit}")
            rows.append(f"  {label:22} {shown:52} {'FAIL' if bad else 'better' if good else 'no certain change'}")
            return good

        gate("wantN", paired(variant, "wantn"), "fall", False)
        gate("wantT", paired(variant, "wantt"), "fall", False)
        for name in THIRDS:
            gate(f"wantN, {name}", paired(variant, "wantn", groups[name]), "fall", False)
        lowers = gate("passed", paired(variant, "passed"), "rise", True)
        for name in THIRDS:
            gate(f"passed, {name}", paired(variant, "passed", groups[name]), "rise", True)
        gate("runaway pages", paired(variant, "house", None, runaway), "rise", True)
        gate("overlap", paired(variant, "overlap"), "rise", True)

        house = column(variant, "house")
        keys = sorted(set(house) & set(shelf))
        excess, lo, hi = interval(np.array([house[k] - shelf[k] for k in keys]))
        under = hi < 0
        if under:
            failed.append("floor")
        rows.append(f"  {'floor (page - shelf)':22} {f'{excess * 100:+5.1f} [{lo * 100:+5.1f}, {hi * 100:+5.1f}] points':52} "
                    f"{'FAIL' if under else 'ok'}")

        verdict = "PASS" if not failed else "FAIL (" + ", ".join(failed) + ")"
        print(f"{variant}: {verdict}{', lowers fame' if lowers and not failed else ''}")
        print("\n".join(rows) + "\n")


if __name__ == "__main__":
    main()
