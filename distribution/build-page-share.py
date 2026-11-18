"""Builds the page-share table (page-share.db) from the engine's own Recommended pages.

The penalty (RecommenderTuning.CommonWeight) takes weight x share off a pick's score, where share is
how often that series lands on a reader's page. Only the engine can say that, so the table is made
in two steps: the eval harness writes pages for many readers, and this turns them into the artifact.

  1. Pages, from the repository root, with MAKI_CONFIG_DIR naming a config dir that holds the
     PUBLISHED artifacts (taste vectors, co-read graph; EASE at the path you pass) and the dump:
       dotnet run distribution/eval-reco-labels.cs -- library --work .artifacts/coread-graph.db \\
         --ease <published ease-table.db> --shelf-status CURRENT --split all --requests 3000 \\
         --dump-picks .artifacts/page-share-pages "pages:commonweight=0"
     No --fold-users: a published table comes from every reader. commonweight=0, because the table
     measures what everybody gets before the penalty acts; built from penalised pages it would hold
     the hub titles lower and the next build would penalise them less. The cap stays at its default.
     CURRENT shelves, because a table from finished shelves holds the titles everybody gets at 1 to
     2% and moves nothing (distribution/CLAUDE.md, "v8: titles the reader already knows").
  2. The artifact:
       python distribution/build-page-share.py --picks .artifacts/page-share-pages/picks-pages-library.tsv \\
         --out .artifacts/page-share.db
  3. Pack and publish with `graph-artifact.cs pageshare` (publish-graph.ps1 -Kind pageshare).

The share is conditional, as in `eval-overlap.py table`: pages holding the title over readers who had
not already read it, kept only where at least --min-open readers had not. meta.trainingFold is `all`
only when the pages came from every reader and every reader-trained artifact behind them saw every
reader (the `.json` the harness writes beside the dump says which); anything else is an evaluation
build that the installer and graph-artifact.cs refuse. Rebuild the table whenever the ranking changes.

The pages are derived from AniList reading lists. The artifact holds only per-series shares.
"""
import argparse
import datetime
import importlib.util
import json
import os
import sqlite3
import sys
from pathlib import Path


def overlap_module():
    path = Path(__file__).with_name("eval-overlap.py")
    spec = importlib.util.spec_from_file_location("eval_overlap", path)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


def training_fold(about):
    folds = about.get("artifactFolds", {})
    if about.get("foldUsers") != "all":
        return f"readers {about.get('foldUsers')}"
    limited = {k: v for k, v in folds.items() if v not in ("all", "off")}
    if limited:
        return "; ".join(f"{k} {v}" for k, v in sorted(limited.items()))
    return "all"


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--picks", required=True, help="picks-<variant>-library.tsv from --dump-picks")
    parser.add_argument("--out", required=True)
    parser.add_argument("--skip", type=int, default=0, help="readers to leave out from the start of the dump")
    parser.add_argument("--top", type=int, default=40, help="the page: how many leading picks count")
    parser.add_argument("--min-open", type=int, default=30)
    parser.add_argument("--min-readers", type=int, default=1000)
    args = parser.parse_args()

    sidecar = Path(args.picks).with_suffix(".json")
    if not sidecar.exists():
        sys.exit(f"error: {sidecar} is missing, so nothing says which readers and artifacts made these pages")
    about = json.loads(sidecar.read_text(encoding="utf-8"))

    overlap = overlap_module()
    pages = overlap.load(args.picks, skip=args.skip, top=args.top)
    if len(pages) < args.min_readers:
        sys.exit(f"error: {len(pages)} pages, fewer than --min-readers {args.min_readers}")
    share = overlap.conditional(pages, overlap.frequency(pages), min_open=args.min_open)

    out = Path(args.out)
    if out.exists():
        out.unlink()
    conn = sqlite3.connect(out)
    conn.execute("CREATE TABLE share (series_id INTEGER PRIMARY KEY, share REAL NOT NULL)")
    conn.execute("CREATE TABLE meta (key TEXT PRIMARY KEY, value TEXT NOT NULL)")
    conn.executemany("INSERT INTO share VALUES (?, ?)", sorted((int(i), float(s)) for i, s in share.items()))
    fold = training_fold(about)
    meta = {
        "schemaVersion": "1",
        "generatedAt": datetime.datetime.now(datetime.timezone.utc).strftime("%Y-%m-%dT%H:%M:%S.%fZ"),
        "trainingFold": fold,
        "readers": str(len(pages)),
        "shelfStatus": str(about.get("shelfStatus")),
        "pageDepth": str(args.top),
        "minOpen": str(args.min_open),
        "engine": str(about.get("variant")),
        "split": str(about.get("split")),
    }
    conn.executemany("INSERT INTO meta VALUES (?, ?)", sorted(meta.items()))
    conn.commit()
    conn.execute("VACUUM")
    conn.close()

    top = sorted(share.items(), key=lambda kv: -kv[1])[:5]
    print(f"{len(share)} series from {len(pages)} pages written to {out}")
    print(f"trainingFold: {fold}" + ("" if fold == "all" else "  (evaluation build: not publishable)"))
    print("highest shares: " + ", ".join(f"{i} {s:.3f}" for i, s in top))


if __name__ == "__main__":
    main()
