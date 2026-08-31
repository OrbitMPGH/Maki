"""Paired comparison of two variants scored by eval-embeddings.cs, eval-search.cs or
eval-reco-labels.cs.

    python distribution/eval-compare.py arctic-m base clean
    python distribution/eval-compare.py default baseline search-premise
    python distribution/eval-compare.py q24 q8 reco nDCG@40

Why paired: every candidate answers the identical queries over the identical pool, so most of the
spread in MRR is query difficulty, not model quality, and it cancels when the same query is compared
across two models. The independent intervals eval-embeddings.cs prints are the conservative view and
can overlap while the paired difference is unambiguous - which is exactly the situation a model
swap has to resolve.

Reports, on the per-query reciprocal ranks:
  * the paired mean difference with a bootstrap 95% interval (no normality assumption; the per-query
    reciprocal ranks are a lumpy distribution concentrated on 0, 1/2 and 1, so a t-interval is a
    poor fit)
  * a two-sided paired t-test as a cross-check
  * McNemar's exact test on the perfect scores, which counts only the queries where exactly one
    variant scored 1.0 - for reciprocal ranks that is "did this change which queries land at rank 1"

The fourth argument names the metric in the .csv, for the printed labels only; it changes no
arithmetic. It matters because eval-reco-labels.cs can emit nDCG or recall instead of reciprocal
rank (`--csv ndcg`), and a change can move recall without moving the rank of the FIRST hit - so a
table headed MRR would be describing the wrong column.
"""

import csv
import math
import random
import sys
from pathlib import Path

RESULTS = Path(".artifacts/eval")
BOOTSTRAP = 20000
SEED = 20260803


def load(candidate: str, mode: str) -> dict[int, float]:
    path = RESULTS / f"rr-{candidate}-{mode}.csv"
    if not path.exists():
        sys.exit(f"missing {path}. Run: dotnet run distribution/eval-embeddings.cs -- {candidate} 10000 2000")
    with path.open() as fh:
        return {int(qid): float(rr) for qid, rr in csv.reader(fh)}


def main() -> None:
    if len(sys.argv) < 3:
        sys.exit(__doc__)
    a_name, b_name = sys.argv[1], sys.argv[2]
    mode = sys.argv[3] if len(sys.argv) > 3 else "clean"
    metric = sys.argv[4] if len(sys.argv) > 4 else "MRR@10"

    a, b = load(a_name, mode), load(b_name, mode)
    shared = sorted(set(a) & set(b))
    if not shared:
        sys.exit("no queries in common - were both scored with the same pool and query count?")
    if len(shared) != len(a) or len(shared) != len(b):
        print(f"warning: {len(a)} vs {len(b)} queries, comparing the {len(shared)} in common")

    diffs = [a[q] - b[q] for q in shared]
    n = len(diffs)
    mean = sum(diffs) / n

    # Bootstrap over the paired differences.
    rng = random.Random(SEED)
    means = sorted(
        sum(rng.choices(diffs, k=n)) / n
        for _ in range(BOOTSTRAP)
    )
    lo, hi = means[int(0.025 * BOOTSTRAP)], means[int(0.975 * BOOTSTRAP)]

    # Paired t-test.
    var = sum((d - mean) ** 2 for d in diffs) / (n - 1)
    stderr = math.sqrt(var / n)
    # Two variants that answered every query identically give mean 0 and stderr 0. That is a real and
    # common outcome here (a tuning knob the data says is inert), and reporting it as t=inf reads as
    # an overwhelming result rather than as no difference at all.
    t = 0.0 if stderr == 0 and mean == 0 else (mean / stderr if stderr else float("inf"))

    # McNemar on recall@1: only the queries where exactly one model put the answer first.
    a_only = sum(1 for q in shared if a[q] == 1.0 and b[q] != 1.0)
    b_only = sum(1 for q in shared if b[q] == 1.0 and a[q] != 1.0)
    discordant = a_only + b_only
    # Exact two-sided binomial test against p=0.5 on the discordant pairs.
    if discordant:
        k = min(a_only, b_only)
        tail = sum(math.comb(discordant, i) for i in range(k + 1)) / (2 ** discordant)
        p_mcnemar = min(1.0, 2 * tail)
    else:
        p_mcnemar = 1.0

    print(f"mode          : {mode}   ({n} paired queries)")
    print(f"{metric:<14}: {a_name} {sum(a[q] for q in shared) / n:.4f}   {b_name} {sum(b[q] for q in shared) / n:.4f}")
    print(f"paired diff   : {mean:+.4f}   bootstrap 95% [{lo:+.4f}, {hi:+.4f}]")
    print(f"paired t      : t={t:.2f} on {n - 1} df")
    p_text = f"{p_mcnemar:.2e}" if p_mcnemar < 0.001 else f"{p_mcnemar:.4f}"
    print(f"perfect wins  : {a_name} {a_only}, {b_name} {b_only}  (McNemar exact p={p_text}, on {metric} == 1.0)")
    verdict = (
        f"{a_name} better" if lo > 0 else
        f"{b_name} better" if hi < 0 else
        "indistinguishable (interval spans zero)"
    )
    print(f"verdict       : {verdict}")


if __name__ == "__main__":
    main()
