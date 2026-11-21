"""Breadth gate: does a page keep a reader's second taste?

For each held-out reader and each facet (type manhwa or manhua, or one genre), s is the share of
the reader's seeds with it, n the share of their held-out next reads, p the share of the top 40.
A facet is a second taste when 15% <= s <= 50%. Per reader:

  kept    mean over their second tastes of min(p / s, 1)
  lost    share of their second tastes with p < s / 3 (the taste all but gone from the page)
  drift   mean |p - s| over facets either side holds at 5% or more

Also kept/lost against next reads (facets with 15% <= n <= 50%, readers with 5+ next reads), which
is what the reader actually went on to read. Paired bootstrap over readers, variant minus base.

Pages come from `eval-reco-labels.cs library --next --dump-picks <dir>`, with both variants in one
run so they share requests.

  python distribution/eval-breadth.py <picks dir> <base variant> <variant> --dump <mangabaka.db> [--depth 40]
"""
import argparse
import glob
import json
import os
import random
import sqlite3

GENRES = ["Action", "Adventure", "Comedy", "Drama", "Fantasy", "Romance", "Slice of Life", "Supernatural",
          "Mystery", "Psychological", "Sci-Fi", "Sports", "Horror", "Historical", "Ecchi", "Harem",
          "School Life", "Boys Love", "Girls Love", "Shoujo", "Seinen", "Josei", "Shounen", "Isekai"]
TYPES = ["manhwa", "manhua"]
FACETS = TYPES + GENRES


def load_meta(ids, dump):
    con = sqlite3.connect(f"file:{dump}?mode=ro&immutable=1", uri=True)
    meta = {}
    ids = list(ids)
    for k in range(0, len(ids), 900):
        chunk = ids[k:k + 900]
        q = f"select id, type, genres from series where id in ({','.join('?' * len(chunk))})"
        for i, t, g in con.execute(q, chunk):
            gs = set(json.loads(g)) if g else set()
            meta[i] = {f for f in FACETS if f == t or f in gs}
    return meta


def shares(ids, meta):
    ids = [i for i in ids if i in meta]
    if not ids:
        return None
    return {f: sum(f in meta[i] for i in ids) / len(ids) for f in FACETS}


def read_picks(path):
    rows = {}
    for line in open(path, encoding="utf-8"):
        parts = line.rstrip("\n").split(";")
        if len(parts) < 6:
            continue
        ints = lambda s: [int(x) for x in s.split(",") if x]
        rows[int(parts[0])] = {"page": ints(parts[1]), "next": ints(parts[2]), "seeds": ints(parts[3])}
    return rows


def reader_scores(page, ref):
    """kept and lost of a page against a reference share profile (shelf or next reads)."""
    second = [f for f in FACETS if 0.15 <= ref[f] <= 0.5]
    if not second:
        return None
    kept = sum(min(page[f] / ref[f], 1) for f in second) / len(second)
    lost = sum(page[f] < ref[f] / 3 for f in second) / len(second)
    return kept, lost


def per_reader(rows, meta, depth):
    out = {}
    for i, r in rows.items():
        if len(r["seeds"]) < 10:
            continue
        s = shares(r["seeds"], meta)
        p = shares(r["page"][:depth], meta)
        if s is None or p is None:
            continue
        rec = {}
        sc = reader_scores(p, s)
        if sc:
            rec["kept"], rec["lost"] = sc
        both = [f for f in FACETS if s[f] >= 0.05 or p[f] >= 0.05]
        rec["drift"] = sum(abs(p[f] - s[f]) for f in both) / len(both)
        rec["web"] = (s["manhwa"], p["manhwa"])
        rec["action"] = (s["Action"], p["Action"])
        if len(r["next"]) >= 5:
            n = shares(r["next"], meta)
            if n:
                sc = reader_scores(p, n)
                if sc:
                    rec["kept_next"], rec["lost_next"] = sc
                # what the readers themselves do: next reads against the shelf
                sc = reader_scores(n, s)
                if sc:
                    rec["next_kept_shelf"], rec["next_lost_shelf"] = sc
        out[i] = rec
    return out


def boot(diffs, reps=2000):
    if not diffs:
        return float("nan"), float("nan"), float("nan")
    rnd = random.Random(20261120)
    m = sum(diffs) / len(diffs)
    means = sorted(sum(rnd.choice(diffs) for _ in diffs) / len(diffs) for _ in range(reps))
    return m, means[int(0.025 * reps)], means[int(0.975 * reps)]


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("picks")
    ap.add_argument("base")
    ap.add_argument("variant")
    ap.add_argument("--dump", required=True)
    ap.add_argument("--depth", type=int, default=40)
    a = ap.parse_args()
    def path(v):
        prefix = f"picks-{v}-"
        return next(p for p in glob.glob(os.path.join(a.picks, f"{prefix}*.tsv"))
                    if "-" not in os.path.basename(p)[len(prefix):])
    rb, rv = read_picks(path(a.base)), read_picks(path(a.variant))
    ids = set()
    for rows in (rb, rv):
        for r in rows.values():
            ids.update(r["page"][:a.depth], r["next"], r["seeds"])
    meta = load_meta(ids, a.dump)
    pb, pv = per_reader(rb, meta, a.depth), per_reader(rv, meta, a.depth)
    common = sorted(set(pb) & set(pv))
    print(f"readers {len(common)} (10+ seeds), depth {a.depth}")
    print(f"{'metric':22}{'n':>6}{a.base:>10}{a.variant:>10}   diff [95%]")
    for key in ("kept", "lost", "drift", "kept_next", "lost_next"):
        pairs = [(pb[i][key], pv[i][key]) for i in common if key in pb[i] and key in pv[i]]
        if not pairs:
            continue
        mb = sum(x for x, _ in pairs) / len(pairs)
        mv = sum(y for _, y in pairs) / len(pairs)
        m, lo, hi = boot([y - x for x, y in pairs])
        print(f"{key:22}{len(pairs):6}{mb:10.3f}{mv:10.3f}   {m:+.3f} [{lo:+.3f}, {hi:+.3f}]")
    ref = [pb[i]["next_kept_shelf"] for i in common if "next_kept_shelf" in pb[i]]
    refl = [pb[i]["next_lost_shelf"] for i in common if "next_lost_shelf" in pb[i]]
    if ref:
        print(f"{'next reads vs shelf':22}{len(ref):6}  kept {sum(ref) / len(ref):.3f}, lost {sum(refl) / len(refl):.3f}  (the readers themselves)")
    for facet, key in (("webtoons", "web"), ("Action", "action")):
        sub = [i for i in common if 0.15 <= pb[i][key][0] <= 0.5]
        if not sub:
            continue
        sh = sum(pb[i][key][0] for i in sub) / len(sub)
        b = sum(pb[i][key][1] for i in sub) / len(sub)
        v = sum(pv[i][key][1] for i in sub) / len(sub)
        m, lo, hi = boot([pv[i][key][1] - pb[i][key][1] for i in sub])
        print(f"{facet} as a second taste: {len(sub)} readers, shelf {sh:.1%}, page {b:.1%} -> {v:.1%}, "
              f"diff {m * 100:+.1f} [{lo * 100:+.1f}, {hi * 100:+.1f}] points")


if __name__ == "__main__":
    main()
