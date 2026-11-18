"""Card caption policies over the judged shelf panels (distribution/CLAUDE.md, "v8: card captions").

Reads eval-reco-labels.cs `library --shelf-judged --dump-captions <dir>` output: one file per variant
and shelf kind, `captions-<variant>-<kind>.tsv`. It needs `default` and, for the attribution-margin
rows, `am25`, `am0`, `amn25` and `amn50` (`attributionmargin=0.25` ... `-0.5`). Every policy rewrites
reason fields only; the picks and their order are the dump's. Each row is the caption mix in the top
--depth after a copy of RecommendationReasons.Balance, the judged feel of every named pair ("claim"),
the same for the cards whose kind the policy changed ("added"), and how often one series is named.
Pairs with no stored judgement are counted (`unj`) and written to --queue, never scored.

  python distribution/eval-captions.py <dump dir> <judgements.db> <completed|current> [--depth 10]
      [--curve] [--queue out.tsv] [--titles mangabaka.db --show "today,near65u"]
"""
import sqlite3
import sys
from collections import Counter, defaultdict

WINDOW, CAP = 20, 2


def load_judgements(path):
    conn = sqlite3.connect(f"file:{path}?mode=ro", uri=True)
    judge = conn.execute(
        "SELECT judge FROM judgement WHERE rubric='v1' GROUP BY judge ORDER BY COUNT(*) DESC LIMIT 1").fetchone()[0]
    out = {}
    for s, c, feel, ins in conn.execute(
            "SELECT seed_id, cand_id, feel, insufficient FROM judgement WHERE judge=? AND rubric='v1'", (judge,)):
        out[(int(s), int(c))] = None if ins else (feel or 0)
    return out


def load_dump(path):
    shelves = defaultdict(list)
    with open(path, encoding="utf-8") as f:
        head = f.readline().rstrip("\n").split("\t")
        for line in f:
            row = dict(zip(head, line.rstrip("\n").split("\t")))
            ids = lambda v: [int(x) for x in v.split(";") if x and x != "?"]
            near = []
            for part in row["nearest"].split(";"):
                if part:
                    sid, cos = part.split(":")
                    near.append((int(sid), float(cos)))
            shelves[row["shelf"]].append({
                "rank": int(row["rank"]), "pick": int(row["pick"]),
                "relation": row["relation"] == "1",
                "feel": int(row["feel"]) if row["feel"] not in ("", "?") else None,
                "feelSeeds": ids(row["feelSeeds"]),
                "author": row["author"] == "1", "coRead": row["coRead"] == "1",
                "fans": int(row["fans"]) if row["fans"] not in ("", "?") else None,
                "fansSeeds": ids(row["fansSeeds"]),
                "coRec": row["coRecommended"] == "1", "taste": row["taste"] == "1",
                "near": near,
            })
    for cards in shelves.values():
        cards.sort(key=lambda c: c["rank"])
    return shelves


def kind_of(card):
    if card["relation"]:
        return "relation"
    if card["feel"] is not None:
        return "feels"
    if card["author"]:
        return "author"
    if card["coRead"] and card["fans"] is not None:
        return "fans"
    if card["coRead"]:
        return "readers"
    return "other"


def apply_near(card, floor, scope, gap=None):
    """Policy 2: a card in scope names the shelf series nearest to it as "Feels like", when the
    cosine clears the floor. scope: readers | unnamed (readers + other) | crowd (readers + fans)."""
    k = kind_of(card)
    allowed = {"readers": {"readers"}, "unnamed": {"readers", "other"}, "crowd": {"readers", "fans"},
               "all": {"readers", "other", "fans"}}[scope]
    if k not in allowed or not card["near"]:
        return card
    best = card["near"][0][1]
    if best < floor:
        return card
    if gap is not None and len(card["near"]) > 1 and best - card["near"][1][1] < gap:
        return card
    seeds = [s for s, c in card["near"] if c >= floor]
    return {**card, "feel": seeds[0], "feelSeeds": seeds, "converted": True}


def gated(today, lowered, floor):
    """A lower attribution margin, taken only where today's card names nothing (readers, other)
    and the seed it would name sits at least `floor` from the pick by text cosine."""
    if kind_of(today) not in ("readers", "other") or kind_of(lowered) != "feels":
        return today
    cos = dict(lowered["near"]).get(lowered["feel"], -1)
    return lowered if cos >= floor else today


def balance(cards, cap=CAP):
    """RecommendationReasons.Balance: returns the named seed and kind per card."""
    counts = Counter()
    named = [None] * len(cards)
    out = []
    for i, card in enumerate(cards):
        if i >= WINDOW and named[i - WINDOW] is not None:
            counts[named[i - WINDOW]] -= 1
        k = kind_of(card)
        seeds = None
        if k == "feels":
            seeds = card["feelSeeds"] or None
            if seeds is None:
                out.append((k, card["feel"]))
                continue
        elif k == "fans":
            seeds = card["fansSeeds"] or [card["fans"]]
        if seeds is None:
            out.append((k, None))
            continue
        chosen = next((s for s in seeds if counts[s] < cap), seeds[0])
        named[i] = chosen
        counts[chosen] += 1
        out.append((k, chosen))
    return out


def evaluate(shelves, judgements, policy, queue, depth=10, cap=CAP, today=None):
    mix = Counter()
    added_missing = 0
    claims = defaultdict(list)
    added = []
    top_share = []
    top_count = []
    total = 0
    for key, cards in shelves.items():
        base_kinds = [kind_of(c) for c in (today or shelves)[key]]
        changed = [policy(c, i, key) for i, c in enumerate(cards)]
        named = balance(changed, cap)
        per_seed = Counter()
        for i, (k, seed) in enumerate(named[:depth]):
            mix[k] += 1
            total += 1
            if seed is None:
                continue
            per_seed[seed] += 1
            pick = changed[i]["pick"]
            j = judgements.get((seed, pick), "missing")
            if j == "missing":
                queue.add((seed, pick))
                if k != base_kinds[i]:
                    added_missing += 1
                continue
            if j is None:
                continue
            claims[k].append(j)
            if k != base_kinds[i]:
                added.append(j)
        if per_seed:
            top_count.append(max(per_seed.values()))
            top_share.append(max(per_seed.values()) / sum(per_seed.values()))
    return {
        "mix": {k: mix[k] / total for k in ("feels", "fans", "readers", "author", "other", "relation")},
        "claims": claims, "added": added, "added_missing": added_missing,
        "top": sum(top_count) / max(1, len(top_count)),
        "topshare": sum(top_share) / max(1, len(top_share)),
        "maxtop": max(top_count) if top_count else 0,
    }


def stats(values):
    if not values:
        return "   -       -     0"
    return f"{sum(values) / len(values):5.2f}  {sum(v >= 2 for v in values) / len(values):5.0%}  {len(values):4d}"


def curve(shelves, judgements):
    """Judged feel of each top pick against its nearest shelf series, by that cosine and by its gap
    to the second nearest, per card kind."""
    rows = []
    for cards in shelves.values():
        for c in cards[:10]:
            if not c["near"]:
                continue
            sid, cos = c["near"][0]
            gap = cos - c["near"][1][1] if len(c["near"]) > 1 else 1
            j = judgements.get((sid, c["pick"]))
            if j is not None:
                rows.append((kind_of(c), cos, gap, j, c["feel"] == sid))
    edges = [0, 0.50, 0.55, 0.60, 0.65, 0.70, 0.75, 0.80, 2]
    print("nearest-series feel by cosine (top 10):  bucket  readers | other | fans | feels | author")
    for lo, hi in zip(edges, edges[1:]):
        cells = []
        for k in ("readers", "other", "fans", "feels", "author"):
            v = [j for kk, cos, _, j, _ in rows if kk == k and lo <= cos < hi]
            cells.append(f"{sum(v) / len(v):4.2f} {sum(x >= 2 for x in v) / len(v):4.0%} {len(v):3d}" if v else "   -          ")
        print(f"  [{lo:.2f},{hi:.2f})  " + " | ".join(cells))
    print("nearest-series feel by gap to the second nearest (top 10): readers+other | feels")
    for lo, hi in zip([0, 0.01, 0.02, 0.04, 0.06, 0.10], [0.01, 0.02, 0.04, 0.06, 0.10, 2]):
        cells = []
        for ks in (("readers", "other"), ("feels",)):
            v = [j for kk, cos, gap, j, _ in rows if kk in ks and lo <= gap < hi]
            cells.append(f"{sum(v) / len(v):4.2f} {sum(x >= 2 for x in v) / len(v):4.0%} {len(v):3d}" if v else "   -          ")
        print(f"  [{lo:.2f},{hi:.2f})  " + " | ".join(cells))


if __name__ == "__main__":
    dump, db, kind = sys.argv[1], sys.argv[2], sys.argv[3]
    judgements = load_judgements(db)
    depth = int(sys.argv[sys.argv.index("--depth") + 1]) if "--depth" in sys.argv else 10
    titles = None
    if "--titles" in sys.argv:
        conn = sqlite3.connect(f"file:{sys.argv[sys.argv.index('--titles') + 1]}?mode=ro", uri=True)
        titles = dict(conn.execute("SELECT id, title FROM series"))
    show = sys.argv[sys.argv.index("--show") + 1].split(",") if "--show" in sys.argv else []
    shown = {}
    base = load_dump(f"{dump}/captions-default-{kind}.tsv")
    if "--curve" in sys.argv:
        curve(base, judgements)
        print()
    queue = set()
    policies = [("today", base, lambda c, i, k: c, CAP)]
    dumps = {v: load_dump(f"{dump}/captions-{v}-{kind}.tsv") for v in ("am25", "am0", "amn25", "amn50")}
    for v, shelves in dumps.items():
        policies.append((v, shelves, lambda c, i, k: c, CAP))
    for f in (0.55, 0.60, 0.65, 0.70, 0.75):
        policies.append((f"near{int(f * 100)}", base, lambda c, i, k, f=f: apply_near(c, f, "readers"), CAP))
    for f in (0.60, 0.65, 0.70):
        policies.append((f"near{int(f * 100)}u", base, lambda c, i, k, f=f: apply_near(c, f, "unnamed"), CAP))
        policies.append((f"near{int(f * 100)}a", base, lambda c, i, k, f=f: apply_near(c, f, "all"), CAP))
    for f in (0.65, 0.70):
        policies.append((f"near{int(f * 100)}u cap1", base, lambda c, i, k, f=f: apply_near(c, f, "unnamed"), 1))
    for g in (0.02, 0.04, 0.06):
        policies.append((f"near60 gap{int(g * 100)}", base, lambda c, i, k, g=g: apply_near(c, 0.60, "unnamed", g), CAP))
    # Policy 1 without eating author cards, and with a cosine floor on the seed it would name.
    for v in ("am25", "am0", "amn50"):
        for f in (0.0, 0.65, 0.70):
            policies.append((f"{v} noauth" + (f" cos{int(f * 100)}" if f else ""), dumps[v],
                             lambda c, i, k, v=v, f=f: gated(base[k][i], c, f), CAP))
    print(f"{'policy':22} feels  fans  readers author other |  claim: feels  2+    n | fans  2+   n | added  2+   n unj | top/10 share max")
    for name, shelves, policy, cap in policies:
        r = evaluate(shelves, judgements, policy, queue, depth=depth, cap=cap, today=base)
        if titles and name in show:
            shown[name] = (shelves, policy, cap)
        m = r["mix"]
        print(f"{name:22} {m['feels']:5.0%} {m['fans']:5.0%} {m['readers']:7.0%} {m['author']:6.0%} {m['other']:5.0%} | "
              f"{stats(r['claims']['feels'])} | {stats(r['claims']['fans'])} | {stats(r['added'])} {r['added_missing']:3d} | "
              f"{r['top']:5.2f} {r['topshare']:5.0%} {r['maxtop']:3d}")
    for name, (shelves, policy, cap) in shown.items():
        print(f"\n-- {name}, top {depth}")
        for key, cards in shelves.items():
            changed = [policy(c, i, key) for i, c in enumerate(cards)]
            for i, (k, seed) in enumerate(balance(changed, cap)[:depth]):
                pick = changed[i]["pick"]
                cos = dict(changed[i]["near"]).get(seed) if seed else None
                j = judgements.get((seed, pick), "?") if seed else ""
                label = {"feels": "Feels like", "fans": "Fans of"}.get(k, k)
                named = f" {titles.get(seed, seed)}" if seed else ""
                extra = f"  (cos {cos if cos is None else round(cos, 2)}, judged {j})" if seed else ""
                print(f"  {i + 1:2d}  {titles.get(pick, pick)}  |  {label}{named}{extra}")
    if "--queue" in sys.argv:
        out = sys.argv[sys.argv.index("--queue") + 1]
        with open(out, "w", encoding="utf-8") as f:
            f.write("seed_id\tcand_id\n")
            for s, c in sorted(queue):
                f.write(f"{s}\t{c}\n")
        print(f"\n{len(queue)} unjudged named pairs -> {out}")
    else:
        print(f"\n{len(queue)} unjudged named pairs")
