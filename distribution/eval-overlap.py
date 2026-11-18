"""Cross-reader overlap of recommendation pages, and the page-frequency table built from it.

Reads the files `eval-reco-labels.cs library --dump-picks <dir>` writes: one line per reader,
`position;ranked ids;held-out ids;seed ids;other list entries`. A page that is the same for
everybody shows up here as a high pairwise Jaccard and a few titles on a large share of all pages,
which no relevance column sees.

  python distribution/eval-overlap.py stats <picks.tsv> [--first N] [--top K] [--titles DUMP]
  python distribution/eval-overlap.py table <picks.tsv> --skip N --out <file> [--top K]
  python distribution/eval-overlap.py compare <a.tsv> <b.tsv> [--first N] [--top K]

`stats`   mean pairwise Jaccard, how concentrated the slots are, and the most common titles.
`table`   id<TAB>share for every title on a page of the readers AFTER the first N, so the readers
          a penalty is graded on are not the ones it was measured from. The input for
          `commonpath` / PageFrequency. The share is conditional: pages holding the title over
          readers who had not already read it, because a title most readers own can only reach
          the few pages left and would look rare.
`compare` paired difference in each reader's mean Jaccard against every other reader, with a
          bootstrap interval over readers.
"""
import argparse
import random
import sqlite3
import sys
from collections import Counter


def load(path, first=None, skip=0, top=40):
    pages = []
    with open(path, encoding="utf-8") as handle:
        for line in handle:
            cells = line.rstrip("\n").split(";")
            if len(cells) < 2:
                continue
            ids = [int(x) for x in cells[1].split(",") if x][:top]
            held = {int(x) for x in cells[2].split(",") if x} if len(cells) > 2 else set()
            seeds = {int(x) for x in cells[3].split(",") if x} if len(cells) > 3 else set()
            pages.append((ids, held, seeds))
    pages = pages[skip:]
    return pages[:first] if first else pages


def frequency(pages):
    counts = Counter()
    for ids, _, _ in pages:
        counts.update(set(ids))
    return counts


def conditional(pages, counts, min_open=30):
    """Share of the readers who had not read a title whose page held it."""
    owned = Counter()
    for _, _, seeds in pages:
        owned.update(seeds)
    n = len(pages)
    return {i: c / (n - owned[i]) for i, c in counts.items() if n - owned[i] >= min_open}


def reader_jaccard(pages):
    """Each reader's mean Jaccard against every other reader's page."""
    sets = [set(ids) for ids, _, _ in pages]
    n = len(sets)
    out = [0.0] * n
    for a in range(n):
        for b in range(a + 1, n):
            union = len(sets[a] | sets[b])
            j = len(sets[a] & sets[b]) / union if union else 0.0
            out[a] += j
            out[b] += j
    return [x / (n - 1) for x in out] if n > 1 else out


def titles_for(dump, ids):
    if not dump or not ids:
        return {}
    conn = sqlite3.connect(f"file:{dump}?mode=ro&immutable=1", uri=True)
    q = ",".join(str(i) for i in ids)
    return {
        row[0]: (row[1], row[2])
        for row in conn.execute(f"select id, title, popularity_global_current from series where id in ({q})")
    }


def stats(args):
    pages = load(args.picks, args.first, top=args.top)
    n = len(pages)
    counts = frequency(pages)
    slots = sum(len(ids) for ids, _, _ in pages)
    per_reader = reader_jaccard(pages)
    print(f"readers           {n}")
    print(f"mean pairwise Jaccard@{args.top}   {sum(per_reader) / n:.4f}")
    print(f"distinct titles   {len(counts)} over {slots} slots ({len(counts) / slots:.3f} per slot)")
    for bar in (0.5, 0.25, 0.10, 0.05):
        common = {i for i, c in counts.items() if c / n >= bar}
        taken = sum(1 for ids, _, _ in pages for i in ids if i in common)
        print(f"titles on >= {bar:.0%} of pages: {len(common):>4}, taking {taken / slots:.1%} of all slots")
    # The share of other readers who got the same title, averaged over slots: what a reader sees.
    shared = sum(counts[i] - 1 for ids, _, _ in pages for i in ids) / slots / max(1, n - 1)
    print(f"mean share of other pages holding a slot's title   {shared:.3f}")
    open_share = conditional(pages, counts)
    if open_share:
        for bar in (0.5, 0.25):
            common = {i for i, v in open_share.items() if v >= bar}
            taken = sum(1 for ids, _, _ in pages for i in ids if i in common)
            print(f"titles on >= {bar:.0%} of the pages they could reach: {len(common):>4}, "
                  f"taking {taken / slots:.1%} of all slots")
    held_all = sum(len(h) for _, h, _ in pages)
    if held_all:
        common10 = {i for i, c in counts.items() if c / n >= 0.10}
        held_common = sum(1 for _, h, _ in pages for i in h if i in common10)
        hits = sum(1 for ids, h, _ in pages for i in ids if i in h)
        hits_common = sum(1 for ids, h, _ in pages for i in ids if i in h and i in common10)
        print(f"held-out titles that are >= 10% titles   {held_common / held_all:.1%}")
        print(f"hits@{args.top} that are >= 10% titles          {hits_common / max(1, hits):.1%} of {hits}")
    ranked = sorted(open_share.items(), key=lambda kv: -kv[1]) if open_share else counts.most_common()
    top = [i for i, _ in ranked[: args.show]]
    names = titles_for(args.titles, top)
    print()
    print("most common titles: share of pages, share of the pages of readers who had not read it,")
    print("mean rank when shown, popularity rank")
    for i in top:
        ranks = [ids.index(i) + 1 for ids, _, _ in pages if i in ids]
        title, pop = names.get(i, ("", ""))
        print(f"  {counts[i] / n:6.1%}  {open_share.get(i, 0):6.1%}  rank {sum(ranks) / len(ranks):5.1f}  "
              f"pop {pop!s:>6}  {i:>7}  {title}")


def table(args):
    pages = load(args.picks, skip=args.skip, top=args.top)
    if not pages:
        sys.exit("no readers left after --skip")
    counts = frequency(pages)
    n = len(pages)
    with_seeds = any(seeds for _, _, seeds in pages)
    share = conditional(pages, counts) if with_seeds else {i: c / n for i, c in counts.items()}
    with open(args.out, "w", encoding="utf-8", newline="\n") as out:
        for i, value in sorted(share.items(), key=lambda kv: -kv[1]):
            out.write(f"{i}\t{value:.6f}\n")
    print(f"{len(share)} titles from {n} readers written to {args.out}")


def compare(args):
    a = reader_jaccard(load(args.a, args.first, top=args.top))
    b = reader_jaccard(load(args.b, args.first, top=args.top))
    n = min(len(a), len(b))
    diffs = [b[i] - a[i] for i in range(n)]
    rng = random.Random(20260827)
    boots = sorted(sum(rng.choice(diffs) for _ in range(n)) / n for _ in range(2000))
    print(f"Jaccard@{args.top}: a {sum(a[:n]) / n:.4f}  b {sum(b[:n]) / n:.4f}  "
          f"b - a {sum(diffs) / n:+.4f} [{boots[50]:+.4f}, {boots[1949]:+.4f}]  n {n}")


def main():
    parser = argparse.ArgumentParser()
    sub = parser.add_subparsers(dest="mode", required=True)
    s = sub.add_parser("stats")
    s.add_argument("picks")
    s.add_argument("--first", type=int)
    s.add_argument("--top", type=int, default=40)
    s.add_argument("--show", type=int, default=25)
    s.add_argument("--titles", help="mangabaka.db, to print titles beside ids")
    s.set_defaults(run=stats)
    t = sub.add_parser("table")
    t.add_argument("picks")
    t.add_argument("--skip", type=int, required=True)
    t.add_argument("--out", required=True)
    t.add_argument("--top", type=int, default=40)
    t.set_defaults(run=table)
    c = sub.add_parser("compare")
    c.add_argument("a")
    c.add_argument("b")
    c.add_argument("--first", type=int)
    c.add_argument("--top", type=int, default=40)
    c.set_defaults(run=compare)
    args = parser.parse_args()
    args.run(args)


main()
