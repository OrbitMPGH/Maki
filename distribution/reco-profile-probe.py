"""Held-out AniList reading lists as Maki libraries, for the real Recommended service.

`eval-reco-labels.cs library` calls SemanticRecommender directly. This tool feeds the real
RecommendationService instead (seed weighting, franchise spread, caption balance, the owned-edition
filter), through the gated test `tests/Maki.Api.Tests/RecommendationServiceProfileProbe.cs`.

  python distribution/reco-profile-probe.py select  --work <coread-graph.db> --dump <mangabaka.db>
         --embeddings <embeddings.db> --fold 0/4 --exclude-picks <picks.tsv> --out <dir>
  python distribution/reco-profile-probe.py prepare --out <dir> --cfg <fold cfg dir> --ease <table>
         --cohorts <reader-cohorts.db> --page-table <id<TAB>share file>
  python distribution/reco-profile-probe.py report  --out <dir> --dump <mangabaka.db>
         --listed <next-listed.tsv> [--baseline w1]

`select`  readers of the held-out fold only (the harness's FNV-1a fold hash), minus every reader a
          pick dump came from (the page-share table's builders), mapped to MangaBaka the way the
          harness maps them. Writes one library per reader with a seeded share of their in-index
          COMPLETED titles hidden. Two groups: `look` by default (a romance and comedy shelf with school
          or slice-of-life and few Korean webtoons; `--group` renames it, `--coverage-table` with
          `--max-coverage` also keeps only shelves an EASE table covers less) and `ctrl` (random).
`prepare` copies fold-limited artifacts into `<out>/cfg` and writes the provenance the probe
          checks before it boots. `--ease-extra name=path` adds tables a case picks with `easetable=name`.
`report`  held-out recovery with paired bootstrap intervals, page composition and caption mix.

Everything written under --out is third-party data (AniList ids and lists). Keep it git-ignored.
"""
import argparse
import json
import math
import random
import shutil
import sqlite3
import sys
from collections import Counter, defaultdict
from pathlib import Path

READ = ("COMPLETED", "REPEATING", "CURRENT", "PAUSED")
PRIORITY = {"COMPLETED": 0, "REPEATING": 1, "CURRENT": 2, "PAUSED": 3, "DROPPED": 4, "PLANNING": 5}
READER_TRAINED = ("coread-edges.db", "taste-vectors.db", "ease-table.db", "reader-cohorts.db")
DEPTH_CAP = 30
READ_SHARE = {"COMPLETED": 1.0, "REPEATING": 1.0, "CURRENT": 0.5, "PAUSED": 0.25, "DROPPED": 0.1, "PLANNING": 0.0}


def fold_of(user_id, folds):
    value = user_id & 0xFFFFFFFFFFFFFFFF
    h = 2166136261
    for i in range(8):
        h = ((h ^ ((value >> (i * 8)) & 0xFF)) * 16777619) & 0xFFFFFFFF
    return h % folds


def ro(path):
    return sqlite3.connect(f"file:{Path(path).as_posix()}?mode=ro", uri=True)


def load_browse(dump):
    conn = ro(dump)
    rows = {}
    cross = {}
    for mid, typ, genres, pop, total, anilist, state, title in conn.execute(
            "SELECT id, type, genres, popularity_global_current, total_chapters, source_anilist_id, state, title "
            "FROM maki_browse ORDER BY COALESCE(popularity_global_current, 2147483647)"):
        if state != "active":
            continue
        try:
            names = json.loads(genres) if genres else []
        except ValueError:
            names = []
        try:
            chapters = int(float(total)) if total not in (None, "") else None
        except ValueError:
            chapters = None
        rows[mid] = {"type": typ, "genres": names, "pop": pop, "chapters": chapters, "title": title}
        if anilist is not None and typ != "novel":
            cross.setdefault(anilist, mid)
    return rows, cross


def load_indexed(embeddings, browse):
    conn = ro(embeddings)
    return {r[0] for r in conn.execute("SELECT id FROM series_vectors") if r[0] in browse}


def load_users(work, cross, fold, folds):
    conn = ro(work)
    users = defaultdict(dict)
    folds_seen = {}
    for user, media, score, status in conn.execute("SELECT user_id, media_id, score, status FROM user_entry"):
        held = folds_seen.get(user)
        if held is None:
            held = folds_seen[user] = fold_of(user, folds) == fold
        if not held:
            continue
        mid = cross.get(media)
        if mid is None:
            continue
        entry = users[user].get(mid)
        if entry is None or PRIORITY.get(status, 9) < PRIORITY.get(entry[0], 9):
            users[user][mid] = (status, max(score, entry[1]) if entry else score)
        elif score > entry[1]:
            users[user][mid] = (entry[0], score)
    return users


def match_picks(paths, users, indexed):
    """Readers a pick dump was built from, matched by the CURRENT shelf each row holds."""
    current = {}
    by_title = defaultdict(set)
    for user, entries in users.items():
        ids = {mid for mid, (status, _) in entries.items() if status == "CURRENT" and mid in indexed}
        if ids:
            current[user] = ids
            for mid in ids:
                by_title[mid].add(user)
    matched, weak = set(), 0
    for path in paths:
        for line in open(path, encoding="utf-8"):
            cells = line.rstrip("\n").split(";")
            if len(cells) < 4:
                continue
            row = {int(x) for part in (cells[2], cells[3]) for x in part.split(",") if x}
            if not row:
                continue
            rare = sorted(row, key=lambda m: len(by_title.get(m, ())))[:3]
            candidates = set().union(*(by_title.get(m, set()) for m in rare))
            best, best_share = None, 0.0
            for user in candidates:
                share = len(row & current[user]) / len(row)
                if share > best_share:
                    best, best_share = user, share
            if best is not None and best_share >= 0.9:
                matched.add(best)
            else:
                weak += 1
    return matched, weak


def profile(entries, browse, indexed):
    read = [mid for mid, (status, _) in entries.items() if status in READ and mid in indexed]
    n = len(read)
    shares = Counter()
    for mid in read:
        for g in set(browse[mid]["genres"]):
            shares[g] += 1
    manhwa = sum(1 for mid in read if browse[mid]["type"] == "manhwa")
    action = sum(1 for mid in read if browse[mid]["type"] == "manhwa" and "Action" in browse[mid]["genres"])
    return (n, {g: c / n for g, c in shares.items()} if n else {}, manhwa / n if n else 0.0,
            action / n if n else 0.0)


def select(args):
    fold, folds = (int(x) for x in args.fold.split("/"))
    out = Path(args.out)
    print("loading the dump", file=sys.stderr)
    browse, cross = load_browse(args.dump)
    indexed = load_indexed(args.embeddings, browse)
    print(f"  {len(cross)} AniList ids map, {len(indexed)} indexed series", file=sys.stderr)
    print("loading reading lists", file=sys.stderr)
    users = load_users(args.work, cross, fold, folds)
    print(f"  {len(users)} readers in fold {fold}/{folds}", file=sys.stderr)
    excluded, weak = match_picks(args.exclude_picks or [], users, indexed)
    print(f"  {len(excluded)} readers matched to pick dumps ({weak} rows unmatched)", file=sys.stderr)
    if weak:
        sys.exit("error: some pick-dump rows match no reader of this fold; refusing to guess who built the table.")

    covered = None
    if args.coverage_table:
        covered = {r[0] for r in ro(args.coverage_table).execute("SELECT DISTINCT seed_id FROM weight")}
    eligible = {}
    coverage = {}
    for user, entries in users.items():
        if user in excluded:
            continue
        library = [m for m in entries if m in indexed]
        completed = [m for m in library if entries[m][0] == "COMPLETED"]
        if not (args.min_entries <= len(library) <= args.max_entries) or len(completed) < args.min_completed:
            continue
        n, shares, manhwa, action_manhwa = profile(entries, browse, indexed)
        if n < args.min_read:
            continue
        if covered is not None:
            read = [m for m in library if entries[m][0] in READ]
            coverage[user] = sum(1 for m in read if m in covered) / len(read)
        eligible[user] = (len(library), len(completed), n, shares, manhwa, action_manhwa)

    def lookalike(p):
        s, manhwa, action_manhwa = p[3], p[4], p[5]
        return (s.get("Romance", 0) >= args.romance and s.get("Comedy", 0) >= args.comedy
                and max(s.get("School Life", 0), s.get("Slice of Life", 0)) >= args.school
                and args.min_manhwa <= manhwa <= args.max_manhwa
                and action_manhwa >= args.min_action_manhwa)

    pool_look = sorted(u for u, p in eligible.items() if lookalike(p) and coverage.get(u, 0) < args.max_coverage)
    rng = random.Random(args.seed)
    look = sorted(rng.sample(pool_look, min(args.size, len(pool_look))))
    rest = sorted(set(eligible) - set(pool_look))
    ctrl = sorted(rng.sample(rest, min(args.size, len(rest))))
    print(f"  {len(eligible)} eligible, {len(pool_look)} pass the lookalike cut; "
          f"{len(look)} lookalike and {len(ctrl)} control chosen", file=sys.stderr)

    skip = set(args.skip_status or [])
    users_dir = out / "users"
    if users_dir.exists():
        shutil.rmtree(users_dir)
    users_dir.mkdir(parents=True)
    manifest = ["key\tuser_id\tgroup\tlibrary\tcompleted\tread\thidden\tromance\tcomedy\tschool\tslice\taction\tmanhwa"]
    for group, members in ((args.group, look), ("ctrl", ctrl)):
        for i, user in enumerate(members):
            key = f"{group}{i + 1:03d}"
            entries = users[user]
            library = sorted(m for m in entries if m in indexed)
            completed = [m for m in library if entries[m][0] == "COMPLETED"]
            hide_rng = random.Random(f"{args.seed}:{user}")
            hidden = set(hide_rng.sample(completed, max(1, round(len(completed) * args.hide))))
            lines = ["mangabaka_id\tstatus\tscore\tchapters\tread"]
            for mid in library:
                status, score = entries[mid]
                if mid in hidden or status in skip:
                    continue
                chapters = max(1, min(browse[mid]["chapters"] or DEPTH_CAP, DEPTH_CAP))
                read = math.ceil(chapters * READ_SHARE.get(status, 0.0))
                lines.append(f"{mid}\t{status}\t{score}\t{chapters if read else 0}\t{read}")
            (users_dir / f"{key}.library.tsv").write_text("\n".join(lines) + "\n", encoding="utf-8")
            (users_dir / f"{key}.hidden.tsv").write_text(
                "mangabaka_id\n" + "".join(f"{m}\n" for m in sorted(hidden)), encoding="utf-8")
            _, _, n, s, manhwa, _ = eligible[user]
            manifest.append("\t".join(str(x) for x in (
                key, user, group, len(lines) - 1, len(completed), n, len(hidden),
                f"{s.get('Romance', 0):.3f}", f"{s.get('Comedy', 0):.3f}", f"{s.get('School Life', 0):.3f}",
                f"{s.get('Slice of Life', 0):.3f}", f"{s.get('Action', 0):.3f}", f"{manhwa:.3f}")))
    (out / "users.tsv").write_text("\n".join(manifest) + "\n", encoding="utf-8")
    (out / "page-table-readers.txt").write_text("".join(f"{u}\n" for u in sorted(excluded)), encoding="utf-8")
    summary = {
        "fold": args.fold, "seed": args.seed, "hide": args.hide, "skipped_statuses": sorted(skip),
        "readers_in_fold": len(users),
        "excluded_builders": len(excluded), "eligible": len(eligible), "lookalike_pool": len(pool_look),
        "chosen": {args.group: len(look), "ctrl": len(ctrl)},
        "coverage_table": args.coverage_table, "max_coverage": args.max_coverage,
        "coverage_means": {name: sum(coverage[u] for u in members) / len(members)
                           for name, members in ((args.group, look), ("ctrl", ctrl), ("eligible", list(eligible)))
                           if covered is not None and members},
        "cuts": {k: getattr(args, k) for k in (
            "min_entries", "max_entries", "min_completed", "min_read", "romance", "comedy", "school", "min_manhwa", "max_manhwa", "min_action_manhwa")},
        "eligible_means": {
            name: sum(fn(p) for p in eligible.values()) / max(1, len(eligible)) for name, fn in (
                ("romance", lambda p: p[3].get("Romance", 0)), ("comedy", lambda p: p[3].get("Comedy", 0)),
                ("school", lambda p: p[3].get("School Life", 0)), ("slice", lambda p: p[3].get("Slice of Life", 0)),
                ("action", lambda p: p[3].get("Action", 0)), ("manhwa", lambda p: p[4]),
                ("action_manhwa", lambda p: p[5]))},
    }
    (out / "selection.json").write_text(json.dumps(summary, indent=2), encoding="utf-8")
    print(json.dumps(summary, indent=2))


def training_folds(path):
    try:
        conn = ro(path)
        row = conn.execute("SELECT value FROM meta WHERE key = 'trainingFold'").fetchone()
    except sqlite3.Error:
        return None
    return row[0] if row else None


def prepare(args):
    out = Path(args.out)
    selection = json.loads((out / "selection.json").read_text(encoding="utf-8"))
    fold, folds = (int(x) for x in selection["fold"].split("/"))
    want = ",".join(str(f) for f in range(folds) if f != fold)
    cfg = out / "cfg"
    cfg.mkdir(parents=True, exist_ok=True)
    source = Path(args.cfg)
    copies = {
        "coread-edges.db": source / "coread-edges.db",
        "taste-vectors.db": source / "taste-vectors.db",
        "reco-edges.db": source / "reco-edges.db",
        "embeddings.db": source / "embeddings.db",
        "ease-table.db": Path(args.ease),
        "reader-cohorts.db": Path(args.cohorts),
    }
    for name, path in copies.items():
        if name in READER_TRAINED:
            got = training_folds(path)
            if got is None or ",".join(sorted(got.split(","))) != want:
                sys.exit(f"error: {path} was trained on folds '{got}', not exactly {want}.")
        shutil.copyfile(path, cfg / name)
    for extra in args.ease_extra or []:
        name, path = extra.split("=", 1)
        got = training_folds(path)
        if got is None or ",".join(sorted(got.split(","))) != want:
            sys.exit(f"error: {path} was trained on folds '{got}', not exactly {want}.")
        (cfg / "ease").mkdir(exist_ok=True)
        shutil.copyfile(path, cfg / "ease" / f"{name}.db")
    shutil.copyfile(args.page_table, cfg / "page-share-fold.tsv")
    provenance = {
        "foldUsers": selection["fold"], "source": str(Path(args.page_table).resolve()),
        "builtFrom": "readers listed in page-table-readers.txt, all in the held-out fold, none probed",
    }
    (cfg / "page-share-fold.json").write_text(json.dumps(provenance, indent=2), encoding="utf-8")
    cases = out / "cases.tsv"
    if not cases.exists():
        cases.write_text("".join(f"w{w}\teaseweight={w}\n" for w in (0, 1, 4)), encoding="utf-8")
    print(f"prepared {cfg}")


def bootstrap(diffs, rounds=5000, seed=7):
    diffs = [d for d in diffs if not math.isnan(d)]
    if not diffs:
        return float("nan"), float("nan"), float("nan")
    rng = random.Random(seed)
    n = len(diffs)
    means = sorted(sum(rng.choices(diffs, k=n)) / n for _ in range(rounds))
    return sum(diffs) / n, means[int(rounds * 0.025)], means[int(rounds * 0.975) - 1]


def tv(a, b):
    keys = set(a) | set(b)
    return 0.5 * sum(abs(a.get(k, 0) - b.get(k, 0)) for k in keys)


def genre_mix(ids, browse):
    mass = Counter()
    for mid in ids:
        names = browse.get(mid, {}).get("genres") or []
        for g in names:
            mass[g] += 1 / len(names)
    total = sum(mass.values())
    return {g: v / total for g, v in mass.items()} if total else {}


def report(args):
    out = Path(args.out)
    browse, _ = load_browse(args.dump)
    listed = {}
    for line in open(args.listed, encoding="utf-8"):
        cells = line.rstrip("\n").split("\t")
        if cells and cells[0].isdigit():
            listed[int(cells[0])] = float(cells[1])
    users = [line.rstrip("\n").split("\t") for line in open(out / "users.tsv", encoding="utf-8")][1:]
    variants = [line.split("\t")[0] for line in open(out / "cases.tsv", encoding="utf-8")
                if line.strip() and not line.startswith("#")]
    pages_dir = out / "pages"
    long_cut = args.long_chapters

    def top1k(mid):
        pop = browse.get(mid, {}).get("pop")
        return pop is not None and 0 < pop <= 1000

    def household(mid):
        return listed.get(mid, 0.0) >= args.household

    def is_long(mid):
        ch = browse.get(mid, {}).get("chapters")
        return ch is not None and ch >= long_cut

    def manhwa(mid):
        return browse.get(mid, {}).get("type") == "manhwa"

    def action_webtoon(mid):
        return manhwa(mid) and "Action" in (browse.get(mid, {}).get("genres") or [])

    fields = [
        ("hit40", "hidden back @40"), ("hit20", "hidden back @20"), ("manhwa", "Korean webtoons @40"),
        ("action", "action webtoons @40"), ("top1k", "top 1k @40"), ("house", "household @20"),
        ("passed", "passed @20"), ("long", f"{long_cut}+ chapters @40"), ("tv", "genre TV vs shelf"),
    ]
    rows = defaultdict(dict)
    shelf = {}
    for cells in users:
        key, group = cells[0], cells[2]
        lib = [line.split("\t") for line in open(out / "users" / f"{key}.library.tsv", encoding="utf-8")][1:]
        read = [int(c[0]) for c in lib if c[1] in READ]
        hidden = {int(line.strip()) for line in open(out / "users" / f"{key}.hidden.tsv", encoding="utf-8")
                  if line.strip().isdigit()}
        n = max(1, len(read))
        shelf[key] = {
            "group": group, "n": len(lib), "hidden": len(hidden), "manhwa": sum(map(manhwa, read)) / n,
            "action": sum(map(action_webtoon, read)) / n, "top1k": sum(map(top1k, read)) / n,
            "house": sum(map(household, read)) / n, "long": sum(map(is_long, read)) / n,
            "mix": genre_mix(read, browse),
        }
        for variant in variants:
            path = pages_dir / variant / f"{key}.tsv"
            if not path.exists():
                continue
            page = [line.rstrip("\n").split("\t") for line in open(path, encoding="utf-8")][1:41]
            ids = [int(c[1]) for c in page]
            if not ids:
                continue
            top20 = ids[:20]
            rows[key][variant] = {
                "hit40": len(set(ids) & hidden) / len(hidden), "hit20": len(set(top20) & hidden) / len(hidden),
                "n40": len(set(ids) & hidden),
                "manhwa": sum(map(manhwa, ids)) / len(ids), "action": sum(map(action_webtoon, ids)) / len(ids),
                "top1k": sum(map(top1k, ids)) / len(ids), "house": sum(map(household, top20)) / len(top20),
                "passed": sum(1 for m in top20 if household(m) and m not in hidden) / len(top20),
                "long": sum(map(is_long, ids)) / len(ids), "tv": tv(genre_mix(ids, browse), shelf[key]["mix"]),
                "kinds": Counter(c[2] for c in page[:20]),
            }

    def mean(values):
        values = [v for v in values if not math.isnan(v)]
        return sum(values) / len(values) if values else float("nan")

    def cell(f, v):
        return f"{v:.3f}" if f == "tv" else f"{v * 100:.1f}%"

    def diff(f, d, lo, hi):
        if f == "tv":
            return f"{d:+.3f} [{lo:+.3f}, {hi:+.3f}]"
        return f"{d * 100:+.1f} [{lo * 100:+.1f}, {hi * 100:+.1f}]"

    lines = []
    base = args.baseline if args.baseline in variants else variants[0]
    labels = {"look": "Lookalike shelves", "ctrl": "Control shelves"}
    for group in dict.fromkeys(cells[2] for cells in users):
        label = labels.get(group, f"Group {group}")
        keys = [k for k, s in shelf.items() if s["group"] == group and all(v in rows[k] for v in variants)]
        lines.append(f"## {label} (n = {len(keys)})\n")
        lines.append("Shelf, over read titles: " + ", ".join(
            f"{name} {mean([shelf[k][name] for k in keys]) * 100:.1f}%"
            for name in ("manhwa", "action", "top1k", "house", "long"))
            + f"; mean library {mean([shelf[k]['n'] for k in keys]):.0f} titles, "
            f"mean hidden {mean([shelf[k]['hidden'] for k in keys]):.1f}.\n")
        lines.append("| variant | hits@40 per reader | " + " | ".join(name for _, name in fields) + " |")
        lines.append("|---|---|" + "---|" * len(fields))
        for v in variants:
            lines.append(f"| {v} | {mean([rows[k][v]['n40'] for k in keys]):.2f} | "
                         + " | ".join(cell(f, mean([rows[k][v][f] for k in keys])) for f, _ in fields) + " |")
        lines.append("")
        lines.append(f"Paired against {base}, mean difference in points (TV in its own units), "
                     "[95% bootstrap over readers]:\n")
        lines.append("| variant | " + " | ".join(name for _, name in fields) + " |")
        lines.append("|---|" + "---|" * len(fields))
        for v in variants:
            if v != base:
                lines.append(f"| {v} | " + " | ".join(
                    diff(f, *bootstrap([rows[k][v][f] - rows[k][base][f] for k in keys])) for f, _ in fields) + " |")
        lines.append("")
        lines.append("Captions in the top 20 (share of cards):\n")
        kinds = ["feels", "fans", "readers", "author", "recommended", "taste", "similar"]
        lines.append("| variant | " + " | ".join(kinds) + " |")
        lines.append("|---|" + "---|" * len(kinds))
        for v in variants:
            total = Counter()
            for k in keys:
                total.update(rows[k][v]["kinds"])
            n = sum(total.values()) or 1
            lines.append(f"| {v} | " + " | ".join(f"{total[c] / n * 100:.0f}%" for c in kinds) + " |")
        lines.append("")
        floods = {v: sum(1 for k in keys if rows[k][v]["manhwa"] - shelf[k]["manhwa"] >= args.flood) for v in variants}
        lines.append(f"Readers whose top 40 holds {args.flood * 100:.0f}+ points more Korean webtoons than their "
                     "read shelf: " + ", ".join(f"{v} {floods[v]}" for v in variants) + ".\n")
    text = "\n".join(lines)
    (out / "report.md").write_text(text, encoding="utf-8")
    print(text)


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    sub = parser.add_subparsers(dest="command", required=True)
    s = sub.add_parser("select")
    s.add_argument("--work", required=True)
    s.add_argument("--dump", required=True)
    s.add_argument("--embeddings", required=True)
    s.add_argument("--fold", default="0/4")
    s.add_argument("--exclude-picks", action="append")
    s.add_argument("--out", required=True)
    s.add_argument("--size", type=int, default=75)
    s.add_argument("--seed", type=int, default=20261119)
    s.add_argument("--hide", type=float, default=0.25)
    s.add_argument("--min-entries", type=int, default=60)
    s.add_argument("--max-entries", type=int, default=600)
    s.add_argument("--min-completed", type=int, default=20)
    s.add_argument("--min-read", type=int, default=30)
    s.add_argument("--romance", type=float, default=0.75)
    s.add_argument("--comedy", type=float, default=0.7)
    s.add_argument("--school", type=float, default=0.5)
    s.add_argument("--min-manhwa", type=float, default=0.0)
    s.add_argument("--max-manhwa", type=float, default=0.08)
    s.add_argument("--min-action-manhwa", type=float, default=0.0,
                   help="share of read titles that are manhwa carrying the Action genre")
    s.add_argument("--group", default="look", help="name of the group the cuts above select")
    s.add_argument("--coverage-table", help="an EASE table; with --max-coverage, keep readers whose read shelf it covers less")
    s.add_argument("--max-coverage", type=float, default=1.01,
                   help="largest share of read titles that may be seeds in --coverage-table")
    s.add_argument("--skip-status", action="append", help="leave this AniList status off the library, e.g. PLANNING")
    p = sub.add_parser("prepare")
    p.add_argument("--out", required=True)
    p.add_argument("--cfg", required=True)
    p.add_argument("--ease", required=True)
    p.add_argument("--cohorts", required=True)
    p.add_argument("--page-table", required=True)
    p.add_argument("--ease-extra", action="append",
                   help="name=path: another fold-limited EASE table, picked per case with easetable=name")
    r = sub.add_parser("report")
    r.add_argument("--out", required=True)
    r.add_argument("--dump", required=True)
    r.add_argument("--listed", required=True)
    r.add_argument("--baseline", default="w1")
    r.add_argument("--household", type=float, default=0.10)
    r.add_argument("--long-chapters", type=int, default=150)
    r.add_argument("--flood", type=float, default=0.15)
    args = parser.parse_args()
    {"select": select, "prepare": prepare, "report": report}[args.command](args)


if __name__ == "__main__":
    main()
