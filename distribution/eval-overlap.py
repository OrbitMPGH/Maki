"""Cross-reader overlap of recommendation pages, and the page-frequency table built from it.

Reads the files `eval-reco-labels.cs library --dump-picks <dir>` writes: one line per reader,
`position;ranked ids;held-out ids;seed ids;other list entries;whole shelf`. A page that is the same
for everybody shows up here as a high pairwise Jaccard and a few titles on a large share of all
pages, which no relevance column sees. A dump from before the last field reads its shelf as the
seeds plus the held-out titles.

  python distribution/eval-overlap.py stats <picks.tsv> [--first N] [--top K] [--titles DUMP]
  python distribution/eval-overlap.py table <picks.tsv> --skip N --out <file> [--top K]
  python distribution/eval-overlap.py compare <a.tsv> <b.tsv> [--first N] [--top K]

`stats`   mean pairwise Jaccard, how concentrated the slots are, and the most common titles.
`table`   id<TAB>share for every title on a page of the readers AFTER the first N, so the readers
          a penalty is graded on are not the ones it was measured from. The input for
          `commonpath` / PageFrequency. The share is conditional: pages of readers who had not
          already read the title that hold it, over those readers, because a title most readers
          own can only reach the few pages left and would look rare. A held-out title on its own
          reader's page is not counted. Writes `<out>.provenance.json` with the readers whose pages
          made the table (from the dump's `.json`), which eval-reco-labels.cs checks against the
          readers it grades.
`compare` paired difference in each reader's mean Jaccard against every other reader, with a
          bootstrap interval over readers.
"""
import argparse
import json
import random
import sqlite3
import sys
from collections import Counter
from pathlib import Path


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
            shelf = {int(x) for x in cells[5].split(",") if x} if len(cells) > 5 else seeds | held
            pages.append((ids, held, shelf))
    pages = pages[skip:]
    return pages[:first] if first else pages


def positions(path, skip=0):
    """The request position of each dumped page after --skip, in file order."""
    with open(path, encoding="utf-8") as handle:
        rows = [int(line.split(";", 1)[0]) for line in handle if ";" in line]
    return rows[skip:]


def provenance(picks, skip=0):
    """Which readers made the pages after --skip, from the `.json` the harness writes beside the dump."""
    sidecar = Path(picks).with_suffix(".json")
    about = json.loads(sidecar.read_text(encoding="utf-8")) if sidecar.exists() else {}
    out = {
        "picks": Path(picks).name,
        "skip": skip,
        "foldUsers": about.get("foldUsers"),
        "split": about.get("split"),
        "shelfStatus": about.get("shelfStatus"),
        "requestDigest": about.get("requestDigest"),
    }
    readers = about.get("readerIds")
    if readers is not None:
        out["readerIds"] = [readers[i] for i in positions(picks, skip)]
    return out


def write_provenance(table_path, about):
    path = Path(str(table_path) + ".provenance.json")
    path.write_text(json.dumps(about, indent=1) + "\n", encoding="utf-8")
    if "readerIds" not in about:
        print(f"warning: {Path(about['picks']).name} names no readers (a dump from an older harness), so {path.name} "
              "only records the fold, and the harness refuses this table on readers of that fold")
    return path


def frequency(pages):
    counts = Counter()
    for ids, _, _ in pages:
        counts.update(set(ids))
    return counts


def conditional(pages, min_open=30):
    """Of the readers who had not read a title, the share whose page held it."""
    owned = Counter()
    shown = Counter()
    for ids, _, shelf in pages:
        owned.update(shelf)
        shown.update(i for i in set(ids) if i not in shelf)
    n = len(pages)
    return {i: c / (n - owned[i]) for i, c in shown.items() if n - owned[i] >= min_open}


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
    open_share = conditional(pages)
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
    with_shelves = any(shelf for _, _, shelf in pages)
    share = conditional(pages) if with_shelves else {i: c / n for i, c in counts.items()}
    with open(args.out, "w", encoding="utf-8", newline="\n") as out:
        for i, value in sorted(share.items(), key=lambda kv: -kv[1]):
            out.write(f"{i}\t{value:.6f}\n")
    sidecar = write_provenance(args.out, provenance(args.picks, args.skip))
    print(f"{len(share)} titles from {n} readers written to {args.out} (readers in {sidecar.name})")


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


if __name__ == "__main__":
    main()
