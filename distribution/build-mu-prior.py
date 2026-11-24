"""Builds per-series quality priors from MangaUpdates' own aggregates in the MangaBaka FULL dump.

Two signals, each written as `id<TAB>value` for the eval harness's `priorpath=` key (score += weight
x value), over the rows the embedding index holds:

  bayes  MangaUpdates' Bayesian rating minus the value it shrinks low-vote titles toward (about 6.22,
         measured here as the median of titles with one or two votes). A title with little evidence
         sits near 0 instead of below the catalogue median, so the prior does not mark obscure titles
         down for being obscure. Clipped to [-1.5, 2.5] rating points.
  drop   Minus the share of MangaUpdates lists that hold the title as "unfinished", out of reading,
         complete and unfinished, shrunk toward the pooled rate with --drop-pseudo pseudo-lists, in
         units of --drop-unit (4 points). Clipped to [-2, 1].

  bayes-res, drop-res  The same two after subtracting the mean of rows in the same log-popularity
         bin (and, for drop, the same publication status), so neither can stand in for fame or for
         a series still running. Written because the plain pair measured as exactly that.

Rows with no MangaUpdates data get no line (0). The population is MangaUpdates', which no other
artifact and no grader reads, so there is no fold to respect.

  python distribution/build-mu-prior.py --dump .artifacts/mangabaka.full.db \\
    --index .simulated/embeddings.db --out-dir .artifacts/mu-prior
"""
import argparse
import math
import os
import sqlite3
import statistics

P = "source_manga_updates_response_"


def num(x):
    try:
        return float(x)
    except (TypeError, ValueError):
        return None


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--dump", required=True)
    ap.add_argument("--index", required=True, help="embeddings.db whose series_vectors ids bound the output")
    ap.add_argument("--out-dir", required=True)
    ap.add_argument("--drop-pseudo", type=float, default=50)
    ap.add_argument("--drop-unit", type=float, default=0.04)
    args = ap.parse_args()

    con = sqlite3.connect(f"file:{args.dump}?mode=ro", uri=True)
    cols = {r[1] for r in con.execute("pragma table_info(series)")}
    if P + "bayesian_rating" not in cols:
        raise SystemExit("no MangaUpdates response columns: this needs the FULL dump (series.full.sqlite.zst)")
    con.execute("attach ? as e", (f"file:{args.index}?mode=ro",))
    rows = con.execute(
        f"select s.id, {P}rating_votes, {P}bayesian_rating, {P}rank_lists_reading, "
        f"{P}rank_lists_complete, {P}rank_lists_unfinished, s.popularity_global_current, s.status "
        f"from series s join e.series_vectors v on v.id = s.id"
    ).fetchall()
    filled = sum(1 for r in rows if num(r[2]))
    if filled == 0:
        raise SystemExit("MangaUpdates columns are present but empty: that is the STANDARD dump")

    low = [num(r[2]) for r in rows if num(r[1]) in (1.0, 2.0) and num(r[2])]
    centre = statistics.median(low)

    lists = []
    for r in rows:
        rd, cp, un = (num(r[3]) or 0), (num(r[4]) or 0), (num(r[5]) or 0)
        lists.append((r[0], rd + cp + un, un))
    total_n = sum(n for _, n, _ in lists)
    pooled = sum(u for _, _, u in lists) / total_n if total_n else 0

    bayes, drop = {}, {}
    for r in rows:
        votes, b = num(r[1]), num(r[2])
        if votes and votes >= 1 and b:
            bayes[r[0]] = max(-1.5, min(2.5, b - centre))
    for sid, n, un in lists:
        if n <= 0:
            continue
        shrunk = (un + args.drop_pseudo * pooled) / (n + args.drop_pseudo)
        drop[sid] = max(-2.0, min(1.0, -(shrunk - pooled) / args.drop_unit))

    meta = {r[0]: (r[6], r[7]) for r in rows}

    def popbin(sid):
        rank = meta[sid][0]
        return -1 if not rank else int(math.log(max(1, rank)) * 2)

    def residual(table, key):
        groups = {}
        for sid, v in table.items():
            groups.setdefault(key(sid), []).append(v)
        means = {k: statistics.mean(v) for k, v in groups.items()}
        return {sid: v - means[key(sid)] for sid, v in table.items()}

    bayes_res = residual(bayes, popbin)
    drop_res = residual(drop, lambda sid: (popbin(sid), meta[sid][1]))

    os.makedirs(args.out_dir, exist_ok=True)
    for name, table in (("bayes", bayes), ("drop", drop), ("bayes-res", bayes_res), ("drop-res", drop_res)):
        path = os.path.join(args.out_dir, f"mu-{name}.tsv")
        with open(path, "w", encoding="utf-8", newline="\n") as f:
            for sid in sorted(table):
                f.write(f"{sid}\t{table[sid]:.4f}\n")
        values = list(table.values())
        q = statistics.quantiles(values, n=10)
        print(f"{name}: {len(values)} of {len(rows)} index rows, mean {statistics.mean(values):+.3f}, "
              f"sd {statistics.pstdev(values):.3f}, deciles {' '.join(f'{x:+.2f}' for x in q)} -> {path}")
    print(f"bayes centre {centre:.3f}; pooled drop rate {pooled:.4f}")


if __name__ == "__main__":
    main()
