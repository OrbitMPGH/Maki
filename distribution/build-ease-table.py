"""Builds an EASE^R item-item weight table from the co-read fetcher's reading lists.

Run (from the repository root, with MAKI_CONFIG_DIR naming a config dir that holds the dump):
  python distribution/build-ease-table.py --work .artifacts/coread-graph.db --out .artifacts/ease-weights.db
  python distribution/build-ease-table.py --work ... --out .artifacts/ease-fold0.db --fold-out 0/4

EASE (Steck 2019) is the closed form B = I - P diag(1/diag(P)), P = (X^T X + lambda I)^-1, over a
binary reader x item matrix of COMPLETED rows. B[i, j] is how much having finished i predicts j,
and it is signed: a negative weight says readers who finished i tend not to finish j. A seed set
scores a candidate j as the sum of B[s, j] over its seeds.

Raw B favours whatever many readers finished, and inside the blend that turned a personal page
into a chart of hits. So each candidate's column is divided by (its completers / the median
completers) ** --pop-damp before anything is kept. 0.35 keeps the held-out gain (nDCG@40 0.277
against 0.283 undamped and 0.199 without the channel) with picks less mainstream than without the
channel; 0 is the raw table, 1 loses the gain (distribution/AGENTS.md, "v7: the EASE table is
popularity-damped").

The solve is an items x items inverse, so memory goes with the square of --items (20,000 items is
1.6 GB of float32). With fewer readers than items, --solve woodbury inverts readers x readers instead
((lam I + X^T X)^-1 = (I - X^T (lam I + X X^T)^-1 X) / lam) and forms B a block of rows at a time;
at 20,000 items it matches the direct table to 7e-7. --max-gb refuses a solve that would not fit.

--statuses picks which list rows are positives, each at an optional weight (COMPLETED,CURRENT=0.5),
and --drop-below-mean leaves out rows scored well under the reader's own mean; both default to the
shipped table (distribution/AGENTS.md, "v9: what EASE learns from").

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
import scipy.linalg.blas as blas
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

CHUNK = 4096


def dense(X, s, e):
    return X[:, s:e].toarray()


def direct_rows(X, lam, t):
    m, n = X.shape
    G = np.empty((n, n), np.float32)
    for s in range(0, n, CHUNK):
        Xs = dense(X, s, min(n, s + CHUNK))
        for u in range(0, s + 1, CHUNK):
            Xu = Xs if u == s else dense(X, u, min(n, u + CHUNK))
            G[s:s + Xs.shape[1], u:u + Xu.shape[1]] = Xs.T @ Xu
            if u != s:
                G[u:u + Xu.shape[1], s:s + Xs.shape[1]] = G[s:s + Xs.shape[1], u:u + Xu.shape[1]].T
    G[np.diag_indices(n)] += lam
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
    P = P.T
    d = P.diagonal().copy()
    for s in range(0, n, 2048):
        yield s, P[s:s + 2048], d


def woodbury_rows(X, lam, t):
    # (lam I + X^T X)^-1 = (I - X^T K X) / lam with K = (lam I + X X^T)^-1, readers x readers, so
    # the memory goes with the square of the readers instead of the items. Same P, row block by row block.
    m, n = X.shape
    K = np.zeros((m, m), np.float32)
    F = K.T
    for s in range(0, n, CHUNK):
        blas.ssyrk(1.0, dense(X, s, min(n, s + CHUNK)), beta=1.0, c=F, lower=1, overwrite_c=1)
    F[np.diag_indices(m)] += lam
    c, info = lapack.spotrf(F, lower=1, overwrite_a=1, clean=0)
    if info != 0:
        sys.exit(f"error: spotrf info {info}")
    c, info = lapack.spotri(c, lower=1, overwrite_c=1)
    if info != 0:
        sys.exit(f"error: spotri info {info}")
    del c
    for s in range(0, m, 2048):
        e = min(m, s + 2048)
        F[:s, s:e] = F[s:e, :s].T
        blk = F[s:e, s:e]
        F[s:e, s:e] = np.tril(blk) + np.tril(blk, -1).T
    log(f"reader inverse done ({time.time() - t:.0f}s)")
    d = np.empty(n, np.float32)
    for s in range(0, n, CHUNK):
        Xs = dense(X, s, min(n, s + CHUNK))
        d[s:s + Xs.shape[1]] = (1.0 - np.einsum("ij,ij->j", Xs, K @ Xs)) / lam
    for s in range(0, n, 2048):
        KXs = (K @ dense(X, s, min(n, s + 2048))).T
        P = np.empty((KXs.shape[0], n), np.float32)
        for u in range(0, n, CHUNK):
            P[:, u:u + CHUNK] = KXs @ dense(X, u, min(n, u + CHUNK))
        P *= -1.0 / lam
        P[np.arange(len(P)), np.arange(s, s + len(P))] += 1.0 / lam
        yield s, P, d


def main():
    p = argparse.ArgumentParser()
    p.add_argument("--work", required=True, help="the co-read fetcher's working database")
    p.add_argument("--dump", help="mangabaka.db; defaults to $MAKI_CONFIG_DIR/mangabaka.db")
    p.add_argument("--out", required=True)
    p.add_argument("--fold-out", help="k/n: leave reader fold k of n out of training")
    p.add_argument("--items", type=int, default=20000, help="most-completed items kept")
    p.add_argument("--lam", type=float, default=300.0)
    p.add_argument("--top", type=int, default=100, help="weights kept per seed, by absolute value")
    p.add_argument("--pop-damp", type=float, default=0.35,
                   help="divide each candidate's column by (completers / median completers) ** this")
    p.add_argument("--min-completers", type=int, default=0,
                   help="also drop items with fewer completers than this from the universe")
    p.add_argument("--solve", choices=("auto", "direct", "woodbury"), default="auto",
                   help="direct inverts items x items; woodbury inverts readers x readers, cheaper when readers < items")
    p.add_argument("--max-gb", type=float, default=10.0, help="refuse a solve whose estimated memory exceeds this")
    p.add_argument("--min-reader-entries", type=int, default=300,
                   help="floor on a reader's whole list, every status, as build-taste-vectors.cs")
    p.add_argument("--statuses", default="COMPLETED",
                   help="list statuses that count as a positive, comma-separated, each optionally STATUS=weight "
                        "(COMPLETED,CURRENT=0.5); a reader's cell keeps the largest weight it gets")
    p.add_argument("--drop-below-mean", type=float, default=None,
                   help="leave out scored rows this many points (of 100) under the reader's own mean score")
    a = p.parse_args()
    status_weight = {}
    for part in (x.strip() for x in a.statuses.split(",") if x.strip()):
        name, _, w = part.partition("=")
        status_weight[name.strip().upper()] = float(w) if w else 1.0
    statuses = list(status_weight)

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

    floor = {}
    if a.drop_below_mean is not None:
        for u, mean in g.execute("SELECT user_id, AVG(score) FROM user_entry WHERE score > 0 GROUP BY user_id"):
            floor[u] = mean - a.drop_below_mean
    dropped = 0
    uslot, mslot, U, I, V = {}, {}, [], [], []
    marks = ",".join("?" * len(statuses))
    cur = g.execute(f"SELECT user_id, media_id, score, status FROM user_entry WHERE status IN ({marks})", statuses)
    while rows := cur.fetchmany(200_000):
        for u, m, score, status in rows:
            if u not in keep_user:
                continue
            if score > 0 and u in floor and score < floor[u]:
                dropped += 1
                continue
            mb = xref.get(m)
            if mb is None:
                continue
            us = uslot.setdefault(u, len(uslot))
            ms = mslot.setdefault(mb, len(mslot))
            U.append(us)
            I.append(ms)
            V.append(status_weight[status])
    g.close()
    U = np.asarray(U, np.int32)
    I = np.asarray(I, np.int32)
    ids = np.empty(len(mslot), np.int64)
    for mb, s in mslot.items():
        ids[s] = mb
    # Two AniList ids can map to one MangaBaka id; a reader finishing both counts once, at the
    # larger of the two weights (COO duplicates are kept apart until the max-reduce below).
    V = np.asarray(V, np.float32)
    order = np.lexsort((-V, I, U))
    U, I, V = U[order], I[order], V[order]
    first = np.ones(len(U), bool)
    first[1:] = (U[1:] != U[:-1]) | (I[1:] != I[:-1])
    X = sp.csr_matrix((V[first], (U[first], I[first])), shape=(len(uslot), len(mslot)))
    log(f"{'+'.join(statuses)} rows {X.nnz:,} over {X.shape[1]:,} items ({time.time() - t:.0f}s)"
        + (f"; {dropped:,} scored under the reader's mean - {a.drop_below_mean:g} left out" if floor else ""))

    counts = np.asarray(X.sum(0)).ravel()
    n = min(a.items, X.shape[1])
    if a.min_completers > 0:
        n = min(n, int((counts >= a.min_completers).sum()))
    top = np.sort(np.argsort(-counts, kind="stable")[:n])
    share = counts[top].sum() / counts.sum()
    X = X[:, top].tocsc()
    m = X.shape[0]
    solver = a.solve if a.solve != "auto" else ("woodbury" if m < n else "direct")
    need = 4 * (m * m if solver == "woodbury" else n * n) / 2**30 + 1
    log(f"universe {n:,} items (fewest completers {int(counts[top].min())}) holding {share:.1%} of completions; "
        f"{solver} solve over {m:,} readers needs about {need:.1f} GB")
    if need > a.max_gb:
        sys.exit(f"error: the {solver} solve needs about {need:.1f} GB, over --max-gb {a.max_gb:g}")

    completers = counts[top].astype(np.float64)
    scale = None
    if a.pop_damp > 0:
        scale = ((completers / np.median(completers)) ** -a.pop_damp).astype(np.float32)
        log(f"pop damp {a.pop_damp:g}: column scale {scale.min():.3f} to {scale.max():.3f}")

    k = min(a.top, n - 1)
    tid = ids[top]
    seeds = np.repeat(tid, k)
    targets = np.empty(n * k, np.int64)
    weights = np.empty(n * k, np.float32)
    rows = direct_rows(X, a.lam, t) if solver == "direct" else woodbury_rows(X, a.lam, t)
    for s, P, d in rows:
        # B = -P / diag(P) column-wise, zero diagonal. Row i is seed i.
        for r in range(0, len(P), 512):
            blk = P[r:r + 512]
            blk /= -d[None, :]
            blk[np.arange(len(blk)), np.arange(s + r, s + r + len(blk))] = 0.0
            if scale is not None:
                blk *= scale[None, :]
            idx = np.argpartition(-np.abs(blk), k, axis=1)[:, :k]
            at = (s + r) * k
            targets[at:at + len(blk) * k] = tid[idx].ravel()
            weights[at:at + len(blk) * k] = np.take_along_axis(blk, idx, 1).ravel()
    log(f"solve done ({time.time() - t:.0f}s)")
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
        "statuses": ",".join(s if status_weight[s] == 1.0 else f"{s}={status_weight[s]:g}" for s in statuses),
        "lambda": f"{a.lam:g}",
        "popDamp": f"{a.pop_damp:g}",
        "topPerItem": str(k),
        "minReaderEntries": str(a.min_reader_entries),
        "source": "anilist-lists-ease",
    }
    if a.drop_below_mean is not None:
        meta["dropBelowMean"] = f"{a.drop_below_mean:g}"
    o.executemany("INSERT INTO meta VALUES (?, ?)", meta.items())
    o.commit()
    o.execute("VACUUM")
    o.close()
    peak = ""
    try:
        import psutil
        info = psutil.Process().memory_info()
        peak = f", peak memory {getattr(info, 'peak_wset', getattr(info, 'rss', 0)) / 2**30:.1f} GB"
    except ImportError:
        pass
    log(f"wrote {a.out}: {os.path.getsize(a.out) / 2**20:.1f} MB ({time.time() - t:.0f}s{peak})")


if __name__ == "__main__":
    main()
