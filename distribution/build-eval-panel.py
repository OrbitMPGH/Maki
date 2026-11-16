"""Builds the fixed seed panel that judged evaluation runs on.

    python distribution/build-eval-panel.py
    python distribution/build-eval-panel.py --config .simulated --out distribution/eval-panel.tsv

The panel is the request set for `eval-reco-labels.cs single --panel distribution/eval-panel.tsv`.
Every label set the recommender is graded on comes from a crowd, covers a few percent of the index
and counts an unlabelled pick as wrong. The panel replaces "which seeds happen to have labels" with
a chosen sample of the catalogue, and its picks are graded by judging what the variants return.

Two sets, drawn without overlap:
  dev      300 seeds. Every comparison during development runs on these.
  holdout  100 seeds. Judged once per release, never during a sweep.

Strata: four popularity bands (top 1k, 1k-5k, 5k-20k, 20k-60k), three types (manga, manhwa,
manhua with OEL) and three eras (before 2005, 2005-2017, 2018 on). Each popularity band gets the
same budget, which sits between the catalogue (63% of the index is past rank 50,000) and the
readership (which is mostly the head). Inside a band every populated cell gets a floor and the rest
goes out by the square root of the cell's size, so manhwa and older titles are present without the
panel being a copy of the catalogue's mix.

A seed has to be judgeable: in the vector index, not pornographic, a real synopsis (200 characters
once links are stripped) and at least three non-spoiler tags. Titles without that need their own
cold-start check and would only teach the judge to say "insufficient information".

The output is checked in. Rebuilding it changes the seeds, which orphans every stored judgement, so
do that deliberately: the draw is seeded and stable for a given dump, not across dumps.
"""

import argparse
import json
import math
import os
import random
import re
import sqlite3
import sys
from collections import defaultdict
from pathlib import Path

SEED = 20261115
BANDS = [("top1k", 1_000), ("1k-5k", 5_000), ("5k-20k", 20_000), ("20k-60k", 60_000)]
LINKS = re.compile(r"https?://\S+|\[([^\]]*)\]\([^)]*\)")


def band_of(rank: int) -> str | None:
    for name, upper in BANDS:
        if rank <= upper:
            return name
    return None


def era_of(year: int) -> str:
    return "pre2005" if year < 2005 else "2005-17" if year < 2018 else "2018+"


def allocate(cells: dict[tuple, int], budget: int, floor: int) -> dict[tuple, int]:
    """A floor per populated cell, the rest by sqrt(size), largest remainder first."""
    quota = {cell: min(floor, size) for cell, size in cells.items()}
    left = budget - sum(quota.values())
    weights = {cell: math.sqrt(size) for cell, size in cells.items() if size > quota[cell]}
    total = sum(weights.values())
    shares = {cell: left * w / total for cell, w in weights.items()}
    for cell, share in shares.items():
        quota[cell] += min(int(share), cells[cell] - quota[cell])
    for cell in sorted(shares, key=lambda c: shares[c] - int(shares[c]), reverse=True):
        if sum(quota.values()) >= budget:
            break
        if quota[cell] < cells[cell]:
            quota[cell] += 1
    return quota


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--config", default=os.environ.get("MAKI_CONFIG_DIR", ".simulated"))
    parser.add_argument("--out", default=str(Path(__file__).with_name("eval-panel.tsv")))
    parser.add_argument("--dev", type=int, default=300)
    parser.add_argument("--holdout", type=int, default=100)
    args = parser.parse_args()

    config = Path(args.config)
    index = sqlite3.connect(f"file:{config / 'embeddings.db'}?mode=ro", uri=True)
    indexed = {row[0] for row in index.execute("SELECT id FROM series_vectors")}
    dump = sqlite3.connect(f"file:{config / 'mangabaka.db'}?mode=ro", uri=True)

    cells: dict[tuple, list] = defaultdict(list)
    rows = dump.execute(
        """
        SELECT id, title, type, year, popularity_global_current, content_rating, description, tags_v2
        FROM series
        WHERE state = 'active' AND merged_with IS NULL AND year IS NOT NULL
          AND type IN ('manga', 'manhwa', 'manhua', 'oel')
          AND popularity_global_current BETWEEN 1 AND 60000
        ORDER BY id
        """
    )
    for sid, title, kind, year, rank, rating, description, tags in rows:
        if sid not in indexed or rating == "pornographic":
            continue
        synopsis = LINKS.sub(lambda m: m.group(1) or "", description or "").strip()
        if len(synopsis) < 200:
            continue
        if sum(1 for tag in json.loads(tags or "[]") if not tag.get("is_spoiler")) < 3:
            continue
        kind = kind if kind in ("manga", "manhwa") else "manhua"
        cells[(band_of(rank), kind, era_of(year))].append((sid, rank, year, title))

    rng = random.Random(SEED)
    picked: list[tuple] = []
    for set_name, total, floor in (("dev", args.dev, 4), ("holdout", args.holdout, 1)):
        for band, _ in BANDS:
            in_band = {cell: len(members) for cell, members in cells.items() if cell[0] == band and members}
            for cell, count in sorted(allocate(in_band, total // len(BANDS), floor).items()):
                members = cells[cell]
                rng.shuffle(members)
                for sid, rank, year, title in members[:count]:
                    picked.append((sid, set_name, cell[0], cell[1], cell[2], rank, year, title))
                del members[:count]

    picked.sort(key=lambda row: (row[1], [name for name, _ in BANDS].index(row[2]), row[3], row[4], row[0]))
    with open(args.out, "w", encoding="utf-8", newline="\n") as out:
        out.write("id\tset\tband\ttype\tera\trank\tyear\ttitle\n")
        for row in picked:
            out.write("\t".join(str(cell).replace("\t", " ") for cell in row) + "\n")

    for set_name in ("dev", "holdout"):
        members = [row for row in picked if row[1] == set_name]
        print(f"{set_name}: {len(members)} seeds")
        for band, _ in BANDS:
            in_band = [row for row in members if row[2] == band]
            by_type = defaultdict(int)
            by_era = defaultdict(int)
            for row in in_band:
                by_type[row[3]] += 1
                by_era[row[4]] += 1
            print(f"  {band:8} {len(in_band):4}  {dict(by_type)}  {dict(by_era)}")
    print(f"written to {args.out}")


if __name__ == "__main__":
    sys.exit(main())
