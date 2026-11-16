"""Builds an EASE^R item-item weight table from the co-read fetcher's reading lists.

Run (from the repository root, with MAKI_CONFIG_DIR naming a config dir that holds the dump):
  python distribution/build-ease-table.py --work .artifacts/coread-graph.db --out .artifacts/ease-weights.db
  python distribution/build-ease-table.py --work ... --out .artifacts/ease-fold0.db --fold-out 0/4

EASE (Steck 2019) is the closed form B = I - P diag(1/diag(P)), P = (X^T X + lambda I)^-1, over a
binary reader x item matrix of COMPLETED rows. B[i, j] is how much having finished i predicts j,
and it is signed: a negative weight says readers who finished i tend not to finish j. A seed set
scores a candidate j as the sum of B[s, j] over its seeds.

The dense B is items x items, so the artifact keeps each seed row's top N weights by ABSOLUTE value.
Dropping the negatives instead costs three times as much on held-out readers and makes the picks
much more famous (research/recommender/2026-11-15-v7/exp-behaviour/report.md in the main checkout).

Reading lists are mapped to MangaBaka ids with the same query and collision rule as
build-taste-vectors.cs, and readers are split with the same FNV-1a UserFold, so --fold-out k/n holds
out exactly the readers eval-reco-labels.cs --fold-users k/n grades on. meta.trainingFold records
the folds the table learned from, and the eval refuses a table trained on the graded fold.

coread-graph.db holds real reading lists. The artifact holds only item-item weights.
"""
import argparse
import datetime
import os
import sqlite3
import sys
import time

import numpy as np
import scipy.linalg.lapack as lapack
import scipy.sparse as sp


def log(*a):
    print(time.strftime("%H:%M:%S"), *a, flush=True)


def user_fold(user_id, folds):
    # Byte-identical to UserFold.Of in build-taste-vectors.cs and eval-reco-labels.cs.
    h = 2166136261
    v = int(user_id) & 0xFFFFFFFFFFFFFFFF
    for i in range(8):
        h = ((h ^ ((v >> (i * 8)) & 0xFF)) * 16777619) & 0xFFFFFFFF
    return h % folds


def ro(path):
    return sqlite3.connect(f"file:{path}?mode=ro", uri=True)


def cross_reference(dump):
    xref = {}
    for aid, mid in ro(dump).execute(
        """SELECT source_anilist_id, id FROM series
           WHERE state = 'active' AND type != 'novel' AND source_anilist_id IS NOT NULL
           ORDER BY COALESCE(popularity_global_current, 2147483647)"""
    ):
        xref.setdefault(aid, mid)
    return xref


def main():
    p = argparse.ArgumentParser()
    p.add_argument("--work", required=True, help="the co-read fetcher's working database")
    p.add_argument("--dump", help="mangabaka.db; defaults to $MAKI_CONFIG_DIR/mangabaka.db")
    p.add_argument("--out", required=True)
    p.add_argument("--fold-out", help="k/n: leave reader fold k of n out of training")
    p.add_argument("--items", type=int, default=20000, help="most-completed items kept")
    p.add_argument("--lam", type=float, default=300.0)
    p.add_argument("--top", type=int, default=100, help="weights kept per seed, by absolute value")
    p.add_argument("--min-reader-entries", type=int, default=300,
                   help="floor on a reader's whole list, every status, as build-taste-vectors.cs")
    a = p.parse_args()

    dump = a.dump or os.path.join(os.environ.get("MAKI_CONFIG_DIR", ""), "mangabaka.db")
    # --out is deleted before it is written, and the working database is days of fetching.
    inputs = [os.path.realpath(p) for p in (a.work, dump)]
    if os.path.realpath(a.out) in inputs:
        sys.exit("error: --out names an input file; it would be deleted")
    if not os.path.exists(dump):
        sys.exit(f"error: no dump at {dump}")
    fold_k, fold_n = -1, 0
    if a.fold_out:
        fold_k, fold_n = (int(x) for x in a.fold_out.split("/"))
        if fold_n < 2 or not 0 <= fold_k < fold_n:
            sys.exit(f"error: --fold-out wants k/n with n >= 2 and 0 <= k < n, not '{a.fold_out}'")

    t = time.time()
    xref = cross_reference(dump)
    log(f"xref {len(xref):,} AniList ids")

    g = ro(a.work)
    size = dict(g.execute("SELECT user_id, COUNT(*) FROM user_entry GROUP BY user_id").fetchall())
    keep_user = {u for u, n in size.items()
                 if n >= a.min_reader_entries and (fold_n == 0 or user_fold(u, fold_n) != fold_k)}
    log(f"readers {len(size):,}, training on {len(keep_user):,}")

    uslot, mslot, U, I = {}, {}, [], []
    cur = g.execute("SELECT user_id, media_id FROM user_entry WHERE status = 'COMPLETED'")
    while rows := cur.fetchmany(200_000):
        for u, m in rows:
            if u not in keep_user:
                continue
            mb = xref.get(m)
            if mb is None:
                continue
            us = uslot.setdefault(u, len(uslot))
            ms = mslot.setdefault(mb, len(mslot))
            U.append(us)
            I.append(ms)
    g.close()
    U = np.asarray(U, np.int32)
    I = np.asarray(I, np.int32)
    ids = np.empty(len(mslot), np.int64)
    for mb, s in mslot.items():
        ids[s] = mb
    # Two AniList ids can map to one MangaBaka id; a reader finishing both counts once.
    X = sp.csr_matrix((np.ones(len(U), np.float32), (U, I)), shape=(len(uslot), len(mslot)))
    X.data[:] = 1.0
    log(f"completed rows {X.nnz:,} over {X.shape[1]:,} items ({time.time() - t:.0f}s)")

    counts = np.asarray(X.sum(0)).ravel()
    n = min(a.items, X.shape[1])
    top = np.sort(np.argsort(-counts, kind="stable")[:n])
    share = counts[top].sum() / counts.sum()
    Xd = X[:, top].astype(np.float32).toarray()
    del X
    log(f"universe {n:,} items holding {share:.1%} of completions; dense X {Xd.nbytes / 2**30:.1f} GB")

    G = Xd.T @ Xd
    del Xd
    G[np.diag_indices(n)] += a.lam
    log(f"gram done ({time.time() - t:.0f}s)")

    # Cholesky and its inverse in place: one n x n float32 buffer, the lower triangle valid after.
    # G is symmetric, so its transpose is the Fortran-ordered view LAPACK takes without a copy.
    c, info = lapack.spotrf(G.T, lower=1, overwrite_a=1, clean=0)
    if info != 0:
        sys.exit(f"error: spotrf info {info}")
    del G
    P, info = lapack.spotri(c, lower=1, overwrite_c=1)
    if info != 0:
        sys.exit(f"error: spotri info {info}")
    del c
    # Mirror the lower triangle up, a column block at a time: index arrays for the whole upper
    # triangle would cost twice the matrix.
    for s in range(0, n, 2048):
        e = min(n, s + 2048)
        P[:s, s:e] = P[s:e, :s].T
        blk = P[s:e, s:e]
        P[s:e, s:e] = np.tril(blk) + np.tril(blk, -1).T
    d = P.diagonal().copy()
    # B = -P / diag(P) column-wise, zero diagonal. Row i is seed i.
    P /= -d[None, :]
    P[np.diag_indices(n)] = 0.0
    P = np.ascontiguousarray(P)
    log(f"solve done ({time.time() - t:.0f}s)")

    k = min(a.top, n - 1)
    seeds = np.repeat(ids[top], k)
    targets = np.empty(n * k, np.int64)
    weights = np.empty(n * k, np.float32)
    for s in range(0, n, 2048):
        blk = P[s:s + 2048]
        idx = np.argpartition(-np.abs(blk), k, axis=1)[:, :k]
        targets[s * k:(s + len(blk)) * k] = ids[top][idx].ravel()
        weights[s * k:(s + len(blk)) * k] = np.take_along_axis(blk, idx, 1).ravel()
    del P
    nonzero = weights != 0
    seeds, targets, weights = seeds[nonzero], targets[nonzero], weights[nonzero]
    log(f"kept {len(weights):,} weights, {np.mean(weights < 0):.1%} negative")

    if os.path.exists(a.out):
        os.remove(a.out)
    os.makedirs(os.path.dirname(os.path.abspath(a.out)), exist_ok=True)
    o = sqlite3.connect(a.out)
    o.execute("PRAGMA journal_mode = OFF")
    o.execute("PRAGMA synchronous = OFF")
    o.execute("""CREATE TABLE weight (
                     seed_id INTEGER NOT NULL,
                     item_id INTEGER NOT NULL,
                     weight  REAL    NOT NULL,
                     PRIMARY KEY (seed_id, item_id)
                 ) WITHOUT ROWID""")
    o.execute("CREATE TABLE meta (key TEXT PRIMARY KEY, value TEXT NOT NULL)")
    order = np.lexsort((targets, seeds))
    o.executemany("INSERT INTO weight VALUES (?, ?, ?)",
                  zip(seeds[order].tolist(), targets[order].tolist(), weights[order].astype(float).tolist()))
    trained_on = "all" if fold_n == 0 else ",".join(str(f) for f in range(fold_n) if f != fold_k)
    meta = {
        "schemaVersion": "1",
        "generatedAt": datetime.datetime.now(datetime.timezone.utc).isoformat(),
        "itemCount": str(n),
        "weightCount": str(len(weights)),
        "trainedReaders": str(len(uslot)),
        "trainingFold": trained_on,
        "statuses": "COMPLETED",
        "lambda": f"{a.lam:g}",
        "topPerItem": str(k),
        "minReaderEntries": str(a.min_reader_entries),
        "source": "anilist-lists-ease",
    }
    o.executemany("INSERT INTO meta VALUES (?, ?)", meta.items())
    o.commit()
    o.execute("VACUUM")
    o.close()
    log(f"wrote {a.out}: {os.path.getsize(a.out) / 2**20:.1f} MB ({time.time() - t:.0f}s)")


if __name__ == "__main__":
    main()
