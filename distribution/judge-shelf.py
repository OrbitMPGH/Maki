"""Whole-shelf rubric for the judged shelf panel: would a reader with this shelf want this next.

    python distribution/judge-shelf.py estimate
    python distribution/judge-shelf.py show <shelf> <candidate>
    python distribution/judge-shelf.py requests --out shelf-want-requests.jsonl

WHY THIS EXISTS
judge-pairs.py scores one seed against one pick. On a whole shelf the eval pairs each pick with the
reader's nearest series, and a shelf of 20 to 300 titles nearly always holds something close, so that
score is lenient: a hit picked up from one outlier title reads as a fit. This rubric shows the judge
a summary of the whole shelf, the three shelf series nearest the pick, and the pick, and asks whether
this reader would want it next and which part of the shelf it answers to.

WHAT IT READS
`eval-reco-labels.cs library --shelf-judged --next` writes two files under <artifacts>/eval:
  shelf-want-shelves.tsv   shelf key, seed ids
  shelf-want-queue.tsv     shelf key, candidate id, its three nearest shelf series

WHAT THE JUDGE SEES
Catalogue text only, as in judge-pairs.py: no popularity, rating, rank, variant or engine reason. A
shelf is reduced to counts over its series (format, era, content rating, genres, central tags) and
short lines for twelve of its titles. The shelf key is a hash of the seed list, never a reader id.
The shelves are held-out AniList readers' public finished lists. Do not point this at a private
library: unlike judge-pairs.py, a request here describes one person's reading.

WHAT IS NOT HERE
No `run`, no store and no ingest. This builds and sizes the requests; nothing has been sent, so the
rubric is unchecked against known answers. Before trusting a number from it, repeat the two checks
judge-pairs.py has (known pairs with controls, a repeat pass), and store results in their own table
keyed (shelf, candidate, judge, rubric): a want score is not a feel score and must not share a row.
"""

import argparse
import importlib.util
import json
import random
import statistics
import sys
from collections import Counter
from pathlib import Path

_spec = importlib.util.spec_from_file_location("judge_pairs", Path(__file__).with_name("judge-pairs.py"))
pairs = importlib.util.module_from_spec(_spec)
_spec.loader.exec_module(pairs)

RUBRIC_VERSION = "shelf-v1"
TYPICAL = 8
OUTLIERS = 4

RUBRIC = """You are grading recommendations for a manga, manhwa and manhua reading app.

You are given a summary of the SHELF of series one reader has finished, the three series on that shelf closest to the candidate, and one CANDIDATE the app recommended to that reader. The reader has not read the candidate. Judge only from the information given. Do not use how famous a series is, and do not assume facts about a plot that are not in the text you were given.

Scale "want": how likely is it that this reader would want to read the candidate next?
3: Squarely what this reader reads. It matches the main taste of the shelf in premise, tone and audience, and sits close to something they finished.
2: A good fit for a real strand of the shelf. Several of their series tell its kind of story, or it is very close to one they finished and at home beside the rest.
1: A stretch. It connects to the shelf only through a broad genre, a setting or a single unusual title, and the reading experience differs from most of what they read.
0: A mismatch this reader would skip or object to: an audience, content rating or kind of story the shelf does not contain.

Field "strand": which part of the shelf the candidate answers to. "main" when it matches what most of the shelf is, "side" when it matches a minority interest that is clearly present, "none" when nothing on the shelf supports it.

Rules:
- The shelf summary is the evidence of taste. A candidate that resembles one unusual title and nothing else on a large shelf scores at most 1.
- A sequel, spin-off or another work by a creator on the shelf can score 3.
- Shared words in titles, or a single shared tag, are not evidence.
- Do not mark down for format alone, but weigh audience and content rating: explicit adult content for a shelf with none is 0.
- If the candidate's synopsis and tag list are too thin to judge, set "insufficient" to true and give your best guess anyway.
- "recognise_candidate" says whether you know the series from outside the text given. It does not change the scores.
- "reason" is one sentence of at most 25 words naming what decided the score."""

SCHEMA = {
    "type": "object",
    "properties": {
        "want": {"type": "integer", "enum": [0, 1, 2, 3]},
        "strand": {"type": "string", "enum": ["main", "side", "none"]},
        "insufficient": {"type": "boolean"},
        "recognise_candidate": {"type": "boolean"},
        "reason": {"type": "string"},
    },
    "required": ["want", "strand", "insufficient", "recognise_candidate", "reason"],
    "additionalProperties": False,
}


def eval_dir() -> Path:
    return pairs.artifacts_dir() / "eval"


def read_shelves(path: Path) -> dict[int, list[int]]:
    shelves = {}
    with path.open(encoding="utf-8") as fh:
        next(fh, None)
        for line in fh:
            cells = line.rstrip("\n").split("\t")
            if len(cells) >= 2 and cells[1]:
                shelves[int(cells[0])] = [int(s) for s in cells[1].split(",")]
    return shelves


def read_queue(path: Path) -> list[tuple[int, int, list[int]]]:
    queue = []
    with path.open(encoding="utf-8") as fh:
        next(fh, None)
        for line in fh:
            cells = line.rstrip("\n").split("\t")
            if len(cells) >= 3:
                near = [int(s) for s in cells[2].split(",") if s]
                queue.append((int(cells[0]), int(cells[1]), near))
    return queue


def shares(counter: Counter, total: int, top: int) -> str:
    return ", ".join(f"{name} {count * 100 // total}%" for name, count in counter.most_common(top))


def render_shelf(seed_ids: list[int], cards: dict[int, dict]) -> str:
    shelf = [cards[sid] for sid in seed_ids if sid in cards]
    total = len(shelf)
    formats = Counter(card["type"] or "unknown" for card in shelf)
    ratings = Counter(card["rating"] or "unknown" for card in shelf)
    genres = Counter(genre for card in shelf for genre in card["genres"])
    tags = Counter(tag for card in shelf for tag in card["central"])
    years = sorted(card["year"] for card in shelf if card["year"])

    # How typical a title is of the shelf: the summed shelf share of its genres and central tags,
    # per facet it carries, so a long tag list does not win on length.
    def typical(card: dict) -> float:
        facets = card["genres"] + card["central"]
        if not facets:
            return 0.0
        return sum(genres[f] + tags[f] for f in facets) / total / len(facets)

    ranked = sorted(shelf, key=lambda card: (-typical(card), card["title"]))
    head = ranked[:TYPICAL]
    tail = ranked[max(TYPICAL, len(ranked) - OUTLIERS):]

    def line(card: dict) -> str:
        kind = card["type"] + (f", {card['year']}" if card["year"] else "")
        text = f"- {card['title']} ({kind}): {', '.join(card['genres'][:4]) or 'no genres'}"
        if card["central"]:
            text += "; tags: " + ", ".join(card["central"][:6])
        return text

    lines = [f"Finished series: {total}", "Format: " + shares(formats, total, 4)]
    if years:
        quart = statistics.quantiles(years, n=4) if len(years) >= 4 else [years[0], years[len(years) // 2], years[-1]]
        lines.append(f"First published: mostly {quart[0]:.0f} to {quart[2]:.0f}, median {quart[1]:.0f}")
    lines.append("Content rating: " + shares(ratings, total, 4))
    lines.append("Genres (share of the shelf): " + shares(genres, total, 10))
    lines.append("Central tags (share of the shelf): " + shares(tags, total, 18))
    lines.append("Typical of the shelf:")
    lines.extend(line(card) for card in head)
    if tail:
        lines.append("Less typical, also on the shelf:")
        lines.extend(line(card) for card in tail)
    return "\n".join(lines)


def render_request(seed_ids: list[int], near: list[int], cand: int, cards: dict[int, dict]) -> str:
    closest = "\n---\n".join(pairs.render_card(cards[sid]) for sid in near if sid in cards)
    return (
        f"<shelf>\n{render_shelf(seed_ids, cards)}\n</shelf>\n\n"
        f"<closest_on_shelf>\n{closest}\n</closest_on_shelf>\n\n"
        f"<candidate>\n{pairs.render_card(cards[cand])}\n</candidate>"
    )


def request_params(model: str, effort: str, prompt: str) -> dict:
    # Same shape as judge-pairs.py: the rubric is the cached prefix, the shelf and pick follow.
    return {
        "model": model,
        "max_tokens": 4000,
        "system": [{"type": "text", "text": RUBRIC, "cache_control": {"type": "ephemeral"}}],
        "output_config": {"effort": effort, "format": {"type": "json_schema", "schema": SCHEMA}},
        "messages": [{"role": "user", "content": prompt}],
    }


def load(args):
    shelves_path = Path(args.shelves) if args.shelves else eval_dir() / "shelf-want-shelves.tsv"
    queue_path = Path(args.queue) if args.queue else eval_dir() / "shelf-want-queue.tsv"
    if not shelves_path.exists() or not queue_path.exists():
        sys.exit(
            f"missing {shelves_path} or {queue_path}. Run: dotnet run distribution/eval-reco-labels.cs -- "
            "library --shelf-judged --next ..."
        )
    shelves = read_shelves(shelves_path)
    queue = [row for row in read_queue(queue_path) if row[0] in shelves]
    ids = {cand for _, cand, _ in queue} | {sid for _, _, near in queue for sid in near}
    ids |= {sid for shelf, _, _ in queue for sid in shelves[shelf]}
    cards = pairs.load_cards(ids)
    usable = [row for row in queue if row[1] in cards]
    if len(usable) < len(queue):
        print(f"  {len(queue) - len(usable)} pairs skipped: the candidate is missing from the dump")
    return shelves, usable, cards


def cmd_estimate(args) -> int:
    shelves, queue, cards = load(args)
    if not queue:
        print("the queue is empty")
        return 0
    sample = random.Random(1).sample(queue, min(300, len(queue)))
    chars = sum(len(render_request(shelves[shelf], near, cand, cards)) for shelf, cand, near in sample) / len(sample)
    # About four characters a token, and the output figure judge-pairs.py measured at this effort.
    tokens_in = (chars + len(RUBRIC)) / 4
    tokens_out = 350
    print(f"rubric        : {RUBRIC_VERSION}")
    print(f"pairs waiting : {len(queue)} over {len({shelf for shelf, _, _ in queue})} shelves")
    print(f"per pair      : about {tokens_in:.0f} input tokens, {tokens_out} output assumed at effort {args.effort}")
    for model, (price_in, price_out) in pairs.PRICES.items():
        cost = len(queue) * (tokens_in * price_in + tokens_out * price_out) / 1e6 / 2
        print(f"  {model:20} about ${cost:,.2f} as a batch")
    print("An estimate from character counts; nothing was sent.")
    return 0


def cmd_show(args) -> int:
    shelves, queue, cards = load(args)
    row = next((r for r in queue if r[0] == args.shelf and r[1] == args.candidate), None)
    if row is None:
        print("error: that (shelf, candidate) is not in the queue")
        return 1
    print(RUBRIC)
    print()
    print(render_request(shelves[row[0]], row[2], row[1], cards))
    return 0


def cmd_requests(args) -> int:
    shelves, queue, cards = load(args)
    out = Path(args.out)
    with out.open("w", encoding="utf-8", newline="\n") as fh:
        for shelf, cand, near in queue:
            prompt = render_request(shelves[shelf], near, cand, cards)
            fh.write(json.dumps({
                "custom_id": f"s{shelf}-c{cand}",
                "params": request_params(args.model, args.effort, prompt),
            }, ensure_ascii=False) + "\n")
    print(f"{len(queue)} Message Batches requests written to {out}. Nothing was sent.")
    return 0


def main() -> int:
    sys.stdout.reconfigure(encoding="utf-8")
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    sub = parser.add_subparsers(dest="command", required=True)

    def common(p):
        p.add_argument("--model", default=pairs.DEFAULT_MODEL)
        p.add_argument("--effort", default=pairs.DEFAULT_EFFORT, choices=["low", "medium", "high", "xhigh", "max"])
        p.add_argument("--queue", help="defaults to <artifacts>/eval/shelf-want-queue.tsv")
        p.add_argument("--shelves", help="defaults to <artifacts>/eval/shelf-want-shelves.tsv")

    estimate = sub.add_parser("estimate", help="size and rough cost of the queue, no API call")
    common(estimate)
    estimate.set_defaults(run=cmd_estimate)
    show = sub.add_parser("show", help="print the exact prompt for one pair, no API call")
    show.add_argument("shelf", type=int)
    show.add_argument("candidate", type=int)
    common(show)
    show.set_defaults(run=cmd_show)
    requests = sub.add_parser("requests", help="write the batch requests to a file, no API call")
    requests.add_argument("--out", required=True)
    common(requests)
    requests.set_defaults(run=cmd_requests)

    args = parser.parse_args()
    return args.run(args)


if __name__ == "__main__":
    sys.exit(main())
