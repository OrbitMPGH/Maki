"""Judges (seed, candidate) pairs for the recommendation panel and stores the scores.

    python distribution/judge-pairs.py estimate
    python distribution/judge-pairs.py show 2409 61
    python distribution/judge-pairs.py run --sync --limit 20
    python distribution/judge-pairs.py run
    python distribution/judge-pairs.py resume
    python distribution/judge-pairs.py stats
    python distribution/judge-pairs.py calibrate --n 150
    python distribution/judge-pairs.py import-labels calibration-labels.json
    python distribution/judge-pairs.py agreement
    python distribution/judge-pairs.py known
    python distribution/judge-pairs.py known-report

WHY THIS EXISTS
Every label set eval-reco-labels.cs grades against is a crowd's list of pairs. None of them says
whether two series tell a similar story, each covers a few percent of the index, and a pick with
no label counts as wrong. This tool grades what the variants actually returned: the eval writes
the unjudged top picks to .artifacts/eval/judge-queue.tsv, this judges them against a written
rubric and stores the result in .artifacts/judgements.db, and the next eval run reads it back.
A pair is judged once and reused by every later variant that returns it.

WHAT THE JUDGE SEES
Catalogue text only, the same for a famous title and an obscure one: title, type, year, status,
length, creators, genres, non-spoiler tags and a cleaned synopsis. No popularity, no rating, no
rank, no variant name and no engine reason, so it cannot reward fame or learn which variant it is
grading. It reports separately whether it recognises each title, so scores can be split by that.

TWO SCALES, NEVER MERGED
feel   similar story, tone and feel
enjoy  a reader who liked the seed would enjoy this
They answer different questions and the recommender's two kinds of evidence (what a series says
about itself, what readers did) are expected to disagree on them. Same-creator and same-franchise
picks can score 3 on enjoy and are scored on feel like anything else.

THE JUDGE IS PART OF THE KEY
A row is (seed, candidate, judge, rubric), with judge = "<model>/<effort>". Two judges or two
rubric versions are never averaged. Change RUBRIC and bump RUBRIC_VERSION together.

A refusal is stored as its own status and counts as "thin", not as a score. No fallback model is
configured: a second model answering in place of the first would put two judges under one name.

CHECKING THE JUDGE
Two checks need nobody to score anything, and they gate the pool:
  known     takes pairs MangaUpdates readers recommended by hand and gives each seed two controls
            of the candidate's type and popularity: `near` shares the seed's genres, `far` is a
            random series. No control is a pair any crowd links. `known-report` says how often
            the judge scores the human pick above each control, also for the pairs where it does
            not recognise the titles, which is where it has only the text to go on.
  repeat    `run --tag repeat` judges pairs a second time as its own judge, and
            `agreement --against <judge>+repeat` says whether the judge agrees with itself.
Neither says whether a 2 should have been a 3. For that a person who has read both series has to
score pairs: `calibrate` writes a page of them, `import-labels` stores the scores and `agreement`
reports weighted kappa and rank correlation. That is only worth doing for series the scorer knows.

Credentials come from the environment (ANTHROPIC_API_KEY, or an `ant auth login` profile). Only
public catalogue text is sent. Nothing from a library or a reading list goes into a request.
"""

import argparse
import datetime
import html
import json
import os
import random
import re
import sqlite3
import sys
import time
from pathlib import Path

RUBRIC_VERSION = "v1"
DEFAULT_MODEL = "claude-opus-5-5"
DEFAULT_EFFORT = "low"
BATCH_LIMIT = 10_000

# List prices per million tokens (input, output), as cached on 2026-11-03. Batches are half price.
PRICES = {
    "claude-opus-5-5": (4.00, 20.00),
    "claude-sonnet-5-5": (2.00, 10.00),
    "claude-haiku-5-5": (0.10, 0.50),
}

RUBRIC = """You are grading recommendations for a manga, manhwa and manhua reading app.

You are given a SEED series a reader liked and one CANDIDATE that was recommended because of it. Score the candidate on two separate scales. Judge only from the information given for each series. Do not use how famous a series is, and do not assume facts about a plot that are not in the text you were given.

Scale "feel": is this a similar story, told in a similar way?
3: The same kind of story told in a similar way. A reader would describe the two in the same breath: the premise or central situation is alike, and so are tone, pacing and audience.
2: Clearly related. Shares the central genre and either the premise or the tone, but not both.
1: Loosely related. Shares only a broad genre or setting; the reading experience is different.
0: Unrelated, or a mismatch a reader would object to: a different audience or content type, for example explicit adult content for a general-audience seed, or a romance between men for a seed with none.

Scale "enjoy": would a reader who liked the seed enjoy this?
3: Very likely. An obvious suggestion to a fan of the seed. A work by the same creator or in the same franchise can score 3 here.
2: Likely for many fans of the seed.
1: Possible for some fans; a stretch.
0: Unlikely, or it would put off a fan of the seed.

Rules:
- The two scales are independent. A sequel or another work by the same creator can score 3 on "enjoy" and still be scored on "feel" for story and tone like anything else.
- Shared words in the titles, or a shared tag on its own, are not evidence of similarity. Two series with "witch" in the title are not alike for that reason.
- Type and format matter to readers: a full-colour vertical-scroll manhwa and a black-and-white manga can still be alike, so do not mark down for format alone, but do weigh audience and content rating.
- If either synopsis and tag list is too thin to judge the pair, set "insufficient" to true and give your best guess on both scales anyway.
- "same_franchise" is true when the candidate is a sequel, prequel, spin-off, side story, alternate version, art book or guide of the seed.
- "recognise_seed" and "recognise_candidate" say whether you know the series from outside the text given. They do not change the scores.
- "reason" is one sentence of at most 25 words naming what decided the scores."""

SCHEMA = {
    "type": "object",
    "properties": {
        "feel": {"type": "integer", "enum": [0, 1, 2, 3]},
        "enjoy": {"type": "integer", "enum": [0, 1, 2, 3]},
        "insufficient": {"type": "boolean"},
        "same_franchise": {"type": "boolean"},
        "recognise_seed": {"type": "boolean"},
        "recognise_candidate": {"type": "boolean"},
        "reason": {"type": "string"},
    },
    "required": [
        "feel", "enjoy", "insufficient", "same_franchise", "recognise_seed", "recognise_candidate", "reason",
    ],
    "additionalProperties": False,
}

LINK = re.compile(r"\[([^\]]*)\]\([^)]*\)")
URL = re.compile(r"https?://\S+")
TAG = re.compile(r"<[^>]+>")
BOILERPLATE = re.compile(
    r"^\s*(\*+\s*)?(note|source|original (web)?(toon|novel|comic)|published in|pairing|"
    r"official (english|translation)|links?|alternative|read (it )?(on|at))\b.*$",
    re.IGNORECASE | re.MULTILINE,
)
LANGUAGES = {"ja": "Japan", "ko": "Korea", "zh": "China", "zh-hk": "Hong Kong", "zh-tw": "Taiwan", "en": "English"}
CENTRAL = {"core": 0, "defining": 1}
MINOR = {"recurrent": 0, "unweighted": 1}


def artifacts_dir() -> Path:
    return Path(os.environ.get("MAKI_ARTIFACTS_DIR", ".artifacts"))


def config_dir() -> Path:
    return Path(os.environ.get("MAKI_CONFIG_DIR", ".simulated"))


def now() -> str:
    return datetime.datetime.now(datetime.timezone.utc).isoformat(timespec="seconds")


# --------------------------------------------------------------------------------------------------
# Store
# --------------------------------------------------------------------------------------------------

def open_store() -> sqlite3.Connection:
    path = artifacts_dir() / "judgements.db"
    path.parent.mkdir(parents=True, exist_ok=True)
    conn = sqlite3.connect(path)
    conn.executescript(
        """
        CREATE TABLE IF NOT EXISTS judgement (
            seed_id        INTEGER NOT NULL,
            cand_id        INTEGER NOT NULL,
            judge          TEXT    NOT NULL,
            rubric         TEXT    NOT NULL,
            feel           INTEGER,
            enjoy          INTEGER,
            insufficient   INTEGER NOT NULL DEFAULT 0,
            same_franchise INTEGER,
            recognise_seed INTEGER,
            recognise_cand INTEGER,
            reason         TEXT,
            status         TEXT    NOT NULL,
            input_tokens   INTEGER,
            output_tokens  INTEGER,
            created_at     TEXT    NOT NULL,
            PRIMARY KEY (seed_id, cand_id, judge, rubric)
        ) WITHOUT ROWID;
        CREATE TABLE IF NOT EXISTS batch (
            id           TEXT PRIMARY KEY,
            judge        TEXT NOT NULL,
            rubric       TEXT NOT NULL,
            requests     INTEGER NOT NULL,
            submitted_at TEXT NOT NULL,
            ingested_at  TEXT
        );
        CREATE TABLE IF NOT EXISTS owner_label (
            seed_id      INTEGER NOT NULL,
            cand_id      INTEGER NOT NULL,
            rubric       TEXT    NOT NULL,
            feel         INTEGER,
            enjoy        INTEGER,
            insufficient INTEGER NOT NULL DEFAULT 0,
            created_at   TEXT    NOT NULL,
            PRIMARY KEY (seed_id, cand_id, rubric)
        ) WITHOUT ROWID;
        """
    )
    return conn


def read_queue(path: Path) -> list[tuple[int, int]]:
    if not path.exists():
        return []
    pairs = []
    with path.open(encoding="utf-8") as fh:
        next(fh, None)
        for line in fh:
            cells = line.rstrip("\n").split("\t")
            if len(cells) >= 2 and cells[0].isdigit() and cells[1].isdigit():
                pairs.append((int(cells[0]), int(cells[1])))
    return pairs


def write_queue(path: Path, pairs: list[tuple[int, int]]) -> None:
    with path.open("w", encoding="utf-8", newline="\n") as fh:
        fh.write("seed_id\tcand_id\n")
        for seed, cand in sorted(set(pairs)):
            fh.write(f"{seed}\t{cand}\n")


def judged_pairs(conn: sqlite3.Connection, judge: str) -> set[tuple[int, int]]:
    rows = conn.execute(
        "SELECT seed_id, cand_id FROM judgement WHERE judge = ? AND rubric = ?", (judge, RUBRIC_VERSION)
    )
    return {(seed, cand) for seed, cand in rows}


# --------------------------------------------------------------------------------------------------
# What the judge is shown
# --------------------------------------------------------------------------------------------------

def clean_synopsis(text: str | None, limit: int = 700) -> str:
    text = TAG.sub(" ", text or "")
    text = LINK.sub(lambda m: m.group(1), text)
    text = URL.sub("", text)
    text = re.sub(r"\(\s*source\s*:[^)]*\)", "", text, flags=re.IGNORECASE)
    # A bare "Notes:" heading starts the awards and edition trivia; nothing after it is plot.
    text = re.split(r"(?im)^\s*[*_]*\s*notes?\s*:?\s*[*_]*\s*$", text)[0]
    text = BOILERPLATE.sub("", text)
    text = re.sub(r"\\([-*_.!#\[\]()])", r"\1", text)
    text = re.sub(r"[*_#>`]+", "", text)
    text = re.sub(r"\s+", " ", text).strip()
    if len(text) > limit:
        text = text[:limit].rsplit(" ", 1)[0] + " ..."
    return text


def names(raw: str | None) -> list[str]:
    try:
        return [str(n) for n in json.loads(raw or "[]") if n]
    except json.JSONDecodeError:
        return []


def load_cards(ids: set[int]) -> dict[int, dict]:
    dump = sqlite3.connect(f"file:{config_dir() / 'mangabaka.db'}?mode=ro", uri=True)
    cards: dict[int, dict] = {}
    todo = sorted(ids)
    for start in range(0, len(todo), 500):
        chunk = todo[start:start + 500]
        rows = dump.execute(
            "SELECT id, title, type, year, status, total_chapters, content_rating, original_language, "
            "authors, artists, genres, tags_v2, description FROM series WHERE id IN (%s)"
            % ",".join("?" * len(chunk)),
            chunk,
        )
        for sid, title, kind, year, status, chapters, rating, language, authors, artists, genres, tags, text in rows:
            central, minor = [], []
            for tag in json.loads(tags or "[]"):
                if tag.get("is_spoiler"):
                    continue
                weight = tag.get("weight")
                if weight in CENTRAL:
                    central.append((CENTRAL[weight], tag["name"]))
                elif weight in MINOR:
                    minor.append((MINOR[weight], tag["name"]))
            creators = list(dict.fromkeys(names(authors) + names(artists)))
            cards[sid] = {
                "title": title or "",
                "type": kind or "",
                "origin": LANGUAGES.get((language or "").lower(), ""),
                "year": year,
                "status": status or "",
                "chapters": chapters,
                "rating": rating or "",
                "creators": creators[:4],
                "genres": names(genres),
                "central": [name for _, name in sorted(central)][:20],
                "minor": [name for _, name in sorted(minor)][:12],
                "synopsis": clean_synopsis(text),
            }
    return cards


def render_card(card: dict) -> str:
    facts = [card["type"] + (f" ({card['origin']})" if card["origin"] else "")]
    if card["year"]:
        facts.append(f"first published {card['year']}")
    if card["status"]:
        facts.append(card["status"])
    if card["chapters"]:
        facts.append(f"{card['chapters']} chapters")
    if card["rating"]:
        facts.append(f"content rating: {card['rating']}")
    lines = [
        f"Title: {card['title']}",
        "Format: " + ", ".join(facts),
        "Creators: " + (", ".join(card["creators"]) or "unknown"),
        "Genres: " + (", ".join(card["genres"]) or "none listed"),
        "Central tags: " + (", ".join(card["central"]) or "none listed"),
    ]
    if card["minor"]:
        lines.append("Other tags: " + ", ".join(card["minor"]))
    lines.append("Synopsis: " + (card["synopsis"] or "none"))
    return "\n".join(lines)


def render_pair(seed: dict, cand: dict) -> str:
    return f"<seed>\n{render_card(seed)}\n</seed>\n\n<candidate>\n{render_card(cand)}\n</candidate>"


def request_params(model: str, effort: str, prompt: str) -> dict:
    # The rubric is the stable prefix and carries the cache breakpoint; the pair comes after it.
    # Thinking counts toward max_tokens, so the ceiling is well above the few hundred the JSON needs.
    return {
        "model": model,
        "max_tokens": 4000,
        "system": [{"type": "text", "text": RUBRIC, "cache_control": {"type": "ephemeral"}}],
        "output_config": {"effort": effort, "format": {"type": "json_schema", "schema": SCHEMA}},
        "messages": [{"role": "user", "content": prompt}],
    }


# --------------------------------------------------------------------------------------------------
# Judging
# --------------------------------------------------------------------------------------------------

def store_message(conn: sqlite3.Connection, seed: int, cand: int, judge: str, message) -> str:
    """Stores one response. Returns the status, or 'retry' when nothing usable came back."""
    usage = (message.usage.input_tokens, message.usage.output_tokens)
    if message.stop_reason == "refusal":
        category = message.stop_details.category if message.stop_details else None
        conn.execute(
            "INSERT OR REPLACE INTO judgement VALUES (?,?,?,?,NULL,NULL,1,NULL,NULL,NULL,?,?,?,?,?)",
            (seed, cand, judge, RUBRIC_VERSION, f"refused: {category}", "refused", *usage, now()),
        )
        return "refused"
    if message.stop_reason != "end_turn":
        return "retry"
    text = next((block.text for block in message.content if block.type == "text"), None)
    try:
        data = json.loads(text or "")
        feel, enjoy = int(data["feel"]), int(data["enjoy"])
    except (json.JSONDecodeError, KeyError, TypeError, ValueError):
        return "retry"
    if not (0 <= feel <= 3 and 0 <= enjoy <= 3):
        return "retry"
    conn.execute(
        "INSERT OR REPLACE INTO judgement VALUES (?,?,?,?,?,?,?,?,?,?,?,?,?,?,?)",
        (
            seed, cand, judge, RUBRIC_VERSION, feel, enjoy, int(bool(data.get("insufficient"))),
            int(bool(data.get("same_franchise"))), int(bool(data.get("recognise_seed"))),
            int(bool(data.get("recognise_candidate"))), str(data.get("reason", ""))[:400], "ok", *usage, now(),
        ),
    )
    return "ok"


def pending(conn: sqlite3.Connection, judge: str, queue_path: Path, limit: int | None):
    done = judged_pairs(conn, judge)
    pairs = [pair for pair in dict.fromkeys(read_queue(queue_path)) if pair not in done]
    if limit:
        pairs = pairs[:limit]
    cards = load_cards({sid for pair in pairs for sid in pair})
    usable = [pair for pair in pairs if pair[0] in cards and pair[1] in cards]
    if len(usable) < len(pairs):
        print(f"  {len(pairs) - len(usable)} pairs skipped: a series is missing from the dump")
    return usable, cards


def ingest_batch(client, conn: sqlite3.Connection, batch_id: str, judge: str) -> dict[str, int]:
    counts: dict[str, int] = {}
    for entry in client.messages.batches.results(batch_id):
        seed, cand = (int(part) for part in entry.custom_id.split("-"))
        if entry.result.type == "succeeded":
            status = store_message(conn, seed, cand, judge, entry.result.message)
        else:
            status = entry.result.type
        counts[status] = counts.get(status, 0) + 1
    conn.execute("UPDATE batch SET ingested_at = ? WHERE id = ?", (now(), batch_id))
    conn.commit()
    return counts


def wait_for(client, batch_id: str):
    while True:
        batch = client.messages.batches.retrieve(batch_id)
        if batch.processing_status == "ended":
            return batch
        counts = batch.request_counts
        print(f"  {batch_id}: {counts.succeeded} done, {counts.processing} processing, {counts.errored} errored")
        time.sleep(60)


def cmd_run(args) -> int:
    import anthropic
    from anthropic.types.message_create_params import MessageCreateParamsNonStreaming
    from anthropic.types.messages.batch_create_params import Request

    # A tag makes a second pass by the same model its own judge, which is how a repeat is stored.
    judge = f"{args.model}/{args.effort}" + (f"+{args.tag}" if args.tag else "")
    conn = open_store()
    queue_path = Path(args.queue) if args.queue else artifacts_dir() / "eval" / "judge-queue.tsv"
    pairs, cards = pending(conn, judge, queue_path, args.limit)
    if not pairs:
        print(f"nothing to judge for {judge} at rubric {RUBRIC_VERSION}")
        return 0

    print(f"judge  : {judge}, rubric {RUBRIC_VERSION}")
    print(f"pairs  : {len(pairs)} ({'one request each' if args.sync else 'Message Batches, half price'})")
    client = anthropic.Anthropic()

    if args.sync:
        counts: dict[str, int] = {}
        for number, (seed, cand) in enumerate(pairs, 1):
            prompt = render_pair(cards[seed], cards[cand])
            try:
                message = client.messages.create(**request_params(args.model, args.effort, prompt))
            except anthropic.AuthenticationError:
                print("error: no valid credentials. Set ANTHROPIC_API_KEY or run `ant auth login`.")
                return 1
            except anthropic.BadRequestError as error:
                print(f"error: the request was rejected, nothing more was sent: {error.message}")
                return 1
            except anthropic.RateLimitError as error:
                delay = int(error.response.headers.get("retry-after", "60"))
                print(f"  rate limited, waiting {delay}s")
                time.sleep(delay)
                continue
            except anthropic.APIStatusError as error:
                print(f"  {seed}-{cand}: API error {error.status_code}, left in the queue")
                continue
            except anthropic.APIConnectionError:
                print(f"  {seed}-{cand}: network error, left in the queue")
                continue
            status = store_message(conn, seed, cand, judge, message)
            conn.commit()
            counts[status] = counts.get(status, 0) + 1
            if number == 1:
                usage = message.usage
                print(
                    f"  first request: {usage.input_tokens} input, {usage.output_tokens} output, "
                    f"{usage.cache_creation_input_tokens or 0} cache write"
                )
            if number % 10 == 0 or number == len(pairs):
                print(f"  {number}/{len(pairs)} {counts}")
    else:
        for start in range(0, len(pairs), BATCH_LIMIT):
            chunk = pairs[start:start + BATCH_LIMIT]
            requests = [
                Request(
                    custom_id=f"{seed}-{cand}",
                    params=MessageCreateParamsNonStreaming(
                        **request_params(args.model, args.effort, render_pair(cards[seed], cards[cand]))
                    ),
                )
                for seed, cand in chunk
            ]
            try:
                batch = client.messages.batches.create(requests=requests)
            except anthropic.AuthenticationError:
                print("error: no valid credentials. Set ANTHROPIC_API_KEY or run `ant auth login`.")
                return 1
            except anthropic.BadRequestError as error:
                print(f"error: the batch was rejected: {error.message}")
                return 1
            conn.execute(
                "INSERT INTO batch VALUES (?,?,?,?,?,NULL)", (batch.id, judge, RUBRIC_VERSION, len(chunk), now())
            )
            conn.commit()
            print(f"  submitted {batch.id} with {len(chunk)} requests")

        print("  waiting for the batches. Safe to stop: `resume` picks them up later.")
        return cmd_resume(args)

    report_cost(conn, judge, batch_price=False)
    prune_queue(conn, queue_path)
    return 0


def cmd_resume(args) -> int:
    import anthropic

    conn = open_store()
    waiting = conn.execute("SELECT id, judge FROM batch WHERE ingested_at IS NULL ORDER BY submitted_at").fetchall()
    if not waiting:
        print("no batch is waiting")
        return 0
    client = anthropic.Anthropic()
    for batch_id, judge in waiting:
        wait_for(client, batch_id)
        print(f"  {batch_id}: {ingest_batch(client, conn, batch_id, judge)}")
        report_cost(conn, judge, batch_price=True)
    queue_path = Path(args.queue) if args.queue else artifacts_dir() / "eval" / "judge-queue.tsv"
    prune_queue(conn, queue_path)
    return 0


def prune_queue(conn: sqlite3.Connection, queue_path: Path) -> None:
    """Drops pairs any judge has scored at this rubric, so the queue is what is still unjudged."""
    done = {
        (seed, cand)
        for seed, cand in conn.execute(
            "SELECT DISTINCT seed_id, cand_id FROM judgement WHERE rubric = ?", (RUBRIC_VERSION,)
        )
    }
    left = [pair for pair in read_queue(queue_path) if pair not in done]
    write_queue(queue_path, left)
    print(f"  {len(left)} pairs left in {queue_path}")


def report_cost(conn: sqlite3.Connection, judge: str, batch_price: bool) -> None:
    rows, tokens_in, tokens_out = conn.execute(
        "SELECT COUNT(*), COALESCE(SUM(input_tokens), 0), COALESCE(SUM(output_tokens), 0) "
        "FROM judgement WHERE judge = ? AND rubric = ?",
        (judge, RUBRIC_VERSION),
    ).fetchone()
    price = PRICES.get(judge.split("/")[0])
    if not rows or not price:
        return
    cost = (tokens_in * price[0] + tokens_out * price[1]) / 1e6 * (0.5 if batch_price else 1.0)
    print(
        f"  {judge}: {rows} judgements stored, {tokens_in / rows:.0f} input and {tokens_out / rows:.0f} output "
        f"tokens each, about ${cost:.2f} at {'batch' if batch_price else 'list'} prices"
    )


def cmd_estimate(args) -> int:
    conn = open_store()
    queue_path = Path(args.queue) if args.queue else artifacts_dir() / "eval" / "judge-queue.tsv"
    pairs, cards = pending(conn, f"{args.model}/{args.effort}", queue_path, None)
    if not pairs:
        print("the queue is empty")
        return 0
    sample = random.Random(1).sample(pairs, min(300, len(pairs)))
    chars = sum(len(render_pair(cards[seed], cards[cand])) for seed, cand in sample) / len(sample)
    # About four characters a token for English prose. The output figure is a guess until a first
    # run reports real usage: the JSON is under 100 tokens and the rest is thinking at this effort.
    tokens_in = (chars + len(RUBRIC)) / 4
    tokens_out = 350
    print(f"pairs waiting : {len(pairs)}")
    print(f"per pair      : about {tokens_in:.0f} input tokens, {tokens_out} output assumed at effort {args.effort}")
    for model, (price_in, price_out) in PRICES.items():
        cost = len(pairs) * (tokens_in * price_in + tokens_out * price_out) / 1e6 / 2
        print(f"  {model:20} about ${cost:,.0f} as a batch")
    print("An estimate from character counts. Run `run --sync --limit 20` first for measured usage.")
    return 0


def cmd_show(args) -> int:
    cards = load_cards({args.seed, args.candidate})
    if args.seed not in cards or args.candidate not in cards:
        print("error: one of those ids is not in the dump")
        return 1
    print(RUBRIC)
    print()
    print(render_pair(cards[args.seed], cards[args.candidate]))
    return 0


def cmd_stats(args) -> int:
    conn = open_store()
    rows = conn.execute(
        "SELECT judge, rubric, status, COUNT(*), AVG(feel), AVG(enjoy), AVG(insufficient), "
        "AVG(recognise_seed AND recognise_cand) FROM judgement GROUP BY 1, 2, 3 ORDER BY 1, 2, 3"
    ).fetchall()
    if not rows:
        print("no judgements stored")
        return 0
    print(f"{'judge':34}{'rubric':8}{'status':10}{'n':>7}{'feel':>7}{'enjoy':>7}{'thin':>7}{'known':>7}")
    for judge, rubric, status, count, feel, enjoy, thin, known in rows:
        def cell(value, pct=False):
            return "-" if value is None else (f"{value:.0%}" if pct else f"{value:.2f}")

        print(
            f"{judge:34}{rubric:8}{status:10}{count:>7}{cell(feel):>7}{cell(enjoy):>7}"
            f"{cell(thin, True):>7}{cell(known, True):>7}"
        )
    for judge, rubric in sorted({(row[0], row[1]) for row in rows}):
        spread = conn.execute(
            "SELECT enjoy, COUNT(*) FROM judgement WHERE judge = ? AND rubric = ? AND status = 'ok' GROUP BY 1",
            (judge, rubric),
        ).fetchall()
        print(f"  {judge} enjoy scores: " + ", ".join(f"{score}: {count}" for score, count in spread))
    owner = conn.execute("SELECT COUNT(*) FROM owner_label WHERE rubric = ?", (RUBRIC_VERSION,)).fetchone()[0]
    print(f"owner labels at rubric {RUBRIC_VERSION}: {owner}")
    return 0


# --------------------------------------------------------------------------------------------------
# Calibration against the owner's own judgement
# --------------------------------------------------------------------------------------------------

def cmd_calibrate(args) -> int:
    conn = open_store()
    queue_path = Path(args.queue) if args.queue else artifacts_dir() / "eval" / "judge-queue.tsv"
    pool = set(read_queue(queue_path)) | {
        (seed, cand) for seed, cand in conn.execute("SELECT DISTINCT seed_id, cand_id FROM judgement")
    }
    labelled = {(s, c) for s, c in conn.execute("SELECT seed_id, cand_id FROM owner_label")}
    pool -= labelled
    if not pool:
        print("error: no pairs to sample. Run the eval with --panel first so it writes a queue.")
        return 1

    # An even draw across the panel's popularity bands, so the sample is not mostly famous seeds.
    bands: dict[int, str] = {}
    panel = Path(args.panel)
    if panel.exists():
        with panel.open(encoding="utf-8") as fh:
            header = next(fh).rstrip("\n").split("\t")
            for line in fh:
                cells = dict(zip(header, line.rstrip("\n").split("\t")))
                bands[int(cells["id"])] = cells.get("band", "")
    by_band: dict[str, list] = {}
    for pair in sorted(pool):
        by_band.setdefault(bands.get(pair[0], ""), []).append(pair)
    rng = random.Random(args.seed)
    picked: list[tuple[int, int]] = []
    for band in sorted(by_band):
        rng.shuffle(by_band[band])
    while len(picked) < args.n and any(by_band.values()):
        for band in sorted(by_band):
            if by_band[band] and len(picked) < args.n:
                picked.append(by_band[band].pop())
    rng.shuffle(picked)

    cards = load_cards({sid for pair in picked for sid in pair})
    items = [
        {"seed": seed, "cand": cand, "s": cards[seed], "c": cards[cand]}
        for seed, cand in picked
        if seed in cards and cand in cards
    ]
    out = Path(args.out) if args.out else artifacts_dir() / "eval" / "calibration.html"
    out.write_text(
        PAGE.replace("__RUBRIC__", html.escape(RUBRIC))
        .replace("__VERSION__", RUBRIC_VERSION)
        .replace("__ITEMS__", json.dumps(items, ensure_ascii=False).replace("</", "<\\/")),
        encoding="utf-8",
    )
    print(f"{len(items)} pairs written to {out}")
    print("Open it in a browser, score the pairs, press Download, then:")
    print("  python distribution/judge-pairs.py import-labels <downloaded file>")
    return 0


def cmd_import(args) -> int:
    conn = open_store()
    data = json.loads(Path(args.file).read_text(encoding="utf-8"))
    if data.get("rubric") != RUBRIC_VERSION:
        print(f"error: these labels are for rubric {data.get('rubric')}, the tool is at {RUBRIC_VERSION}")
        return 1
    count = 0
    for label in data.get("labels", []):
        thin = bool(label.get("insufficient"))
        if not thin and (label.get("feel") is None or label.get("enjoy") is None):
            continue
        conn.execute(
            "INSERT OR REPLACE INTO owner_label VALUES (?,?,?,?,?,?,?)",
            (label["seed"], label["cand"], RUBRIC_VERSION, label.get("feel"), label.get("enjoy"), int(thin), now()),
        )
        count += 1
    conn.commit()
    print(f"{count} owner labels stored")
    return 0


def weighted_kappa(pairs: list[tuple[int, int]], levels: int = 4) -> float:
    """Cohen's kappa with quadratic weights: near misses cost less than opposite ends."""
    n = len(pairs)
    observed = [[0.0] * levels for _ in range(levels)]
    for a, b in pairs:
        observed[a][b] += 1 / n
    row = [sum(observed[i]) for i in range(levels)]
    col = [sum(observed[i][j] for i in range(levels)) for j in range(levels)]
    disagree = expected = 0.0
    for i in range(levels):
        for j in range(levels):
            weight = (i - j) ** 2 / (levels - 1) ** 2
            disagree += weight * observed[i][j]
            expected += weight * row[i] * col[j]
    return 1 - disagree / expected if expected else float("nan")


def spearman(pairs: list[tuple[int, int]]) -> float:
    def ranks(values):
        order = sorted(range(len(values)), key=lambda i: values[i])
        out = [0.0] * len(values)
        i = 0
        while i < len(order):
            j = i
            while j + 1 < len(order) and values[order[j + 1]] == values[order[i]]:
                j += 1
            for k in range(i, j + 1):
                out[order[k]] = (i + j) / 2 + 1
            i = j + 1
        return out

    a, b = ranks([p[0] for p in pairs]), ranks([p[1] for p in pairs])
    mean_a, mean_b = sum(a) / len(a), sum(b) / len(b)
    cov = sum((x - mean_a) * (y - mean_b) for x, y in zip(a, b))
    var = (sum((x - mean_a) ** 2 for x in a) * sum((y - mean_b) ** 2 for y in b)) ** 0.5
    return cov / var if var else float("nan")


def cmd_agreement(args) -> int:
    conn = open_store()
    judges = [row[0] for row in conn.execute(
        "SELECT judge FROM judgement WHERE rubric = ? GROUP BY judge ORDER BY COUNT(*) DESC", (RUBRIC_VERSION,)
    )]
    if args.judge:
        judges = [args.judge]
    if not judges:
        print("no judgements stored")
        return 0
    if args.against:
        rows = conn.execute(
            "SELECT a.feel, b.feel, a.enjoy, b.enjoy FROM judgement a JOIN judgement b "
            "ON b.seed_id = a.seed_id AND b.cand_id = a.cand_id AND b.rubric = a.rubric "
            "WHERE a.rubric = ? AND a.judge = ? AND b.judge = ? AND a.status = 'ok' AND b.status = 'ok' "
            "AND NOT a.insufficient AND NOT b.insufficient",
            (RUBRIC_VERSION, judges[0], args.against),
        ).fetchall()
        print(f"{judges[0]} against {args.against}: {len(rows)} pairs scored by both")
        if len(rows) < 20:
            print("  too few to report agreement")
            return 0
        for name, a, b in (("feel", 0, 1), ("enjoy", 2, 3)):
            pairs = [(row[a], row[b]) for row in rows]
            print(
                f"  {name:6} weighted kappa {weighted_kappa(pairs):.2f}, "
                f"same score {sum(x == y for x, y in pairs) / len(pairs):.0%}, "
                f"within one {sum(abs(x - y) <= 1 for x, y in pairs) / len(pairs):.0%}, "
                f"same side of the 1|2 line {sum((x >= 2) == (y >= 2) for x, y in pairs) / len(pairs):.0%}"
            )
        return 0
    for judge in judges:
        rows = conn.execute(
            "SELECT o.feel, j.feel, o.enjoy, j.enjoy, o.insufficient, j.insufficient "
            "FROM owner_label o JOIN judgement j ON j.seed_id = o.seed_id AND j.cand_id = o.cand_id "
            "AND j.rubric = o.rubric WHERE o.rubric = ? AND j.judge = ? AND j.status = 'ok'",
            (RUBRIC_VERSION, judge),
        ).fetchall()
        usable = [row for row in rows if not row[4] and not row[5]]
        print(f"{judge}: {len(rows)} pairs judged by both, {len(usable)} with a score from both")
        if len(usable) < 20:
            print("  too few to report agreement")
            continue
        for name, a, b in (("feel", 0, 1), ("enjoy", 2, 3)):
            pairs = [(row[a], row[b]) for row in usable]
            same_side = sum((x >= 2) == (y >= 2) for x, y in pairs) / len(pairs)
            lenient = sum(y - x for x, y in pairs) / len(pairs)
            print(
                f"  {name:6} weighted kappa {weighted_kappa(pairs):.2f}, Spearman {spearman(pairs):.2f}, "
                f"same side of the 1|2 line {same_side:.0%}, judge minus owner {lenient:+.2f}"
            )
        thin_owner = sum(1 for row in rows if row[4])
        thin_judge = sum(1 for row in rows if row[5])
        print(f"  could not judge: owner {thin_owner}, judge {thin_judge}")
    print("Target before judging the whole pool: weighted kappa of 0.5 or more on enjoy.")
    return 0


# --------------------------------------------------------------------------------------------------
# Checks that need no human scorer
# --------------------------------------------------------------------------------------------------

BANDS = [("top1k", 1_000), ("1k-5k", 5_000), ("5k-20k", 20_000), ("20k-60k", 60_000)]
KINDS = ("human", "near", "far")


def load_judgeable() -> dict[int, dict]:
    """Series the judge has enough text for: the same bar the panel's seeds have to clear."""
    dump = sqlite3.connect(f"file:{config_dir() / 'mangabaka.db'}?mode=ro", uri=True)
    rows = dump.execute(
        "SELECT id, type, popularity_global_current, content_rating, description, tags_v2, genres, authors, artists "
        "FROM series WHERE state = 'active' AND merged_with IS NULL "
        "AND type IN ('manga', 'manhwa', 'manhua', 'oel') AND popularity_global_current BETWEEN 1 AND 60000"
    )
    series: dict[int, dict] = {}
    for sid, kind, rank, rating, text, tags, genres, authors, artists in rows:
        if rating == "pornographic" or len(clean_synopsis(text, limit=10**6)) < 200:
            continue
        if sum(1 for tag in json.loads(tags or "[]") if not tag.get("is_spoiler")) < 3:
            continue
        series[sid] = {
            "type": kind if kind in ("manga", "manhwa") else "manhua",
            "band": next(name for name, upper in BANDS if rank <= upper),
            "genres": set(names(genres)),
            "creators": set(names(authors) + names(artists)),
        }
    return series


def cmd_known(args) -> int:
    """Writes pairs with a known answer: human recommendations, each with two controls."""
    series = load_judgeable()
    rng = random.Random(args.seed)
    mu = sqlite3.connect(f"file:{artifacts_dir() / 'mu-edges.db'}?mode=ro", uri=True)
    candidates = [
        row for row in mu.execute("SELECT a_id, b_id, human_weight FROM pair WHERE human_weight > 0 ORDER BY 1, 2")
        if row[0] in series and row[1] in series and not series[row[0]]["creators"] & series[row[1]]["creators"]
    ]
    rng.shuffle(candidates)
    quota = {name: args.n // len(BANDS) for name, _ in BANDS}
    positives, seeds = [], set()
    for a, b, weight in candidates:
        seed, cand = (a, b) if rng.random() < 0.5 else (b, a)
        band = series[seed]["band"]
        if seed in seeds or not quota[band]:
            continue
        quota[band] -= 1
        seeds.add(seed)
        positives.append((seed, cand, weight))

    # A control must not be a pair any crowd already links, or it is not a control.
    linked: dict[int, set[int]] = {seed: set() for seed in seeds}
    for path in (artifacts_dir() / "mu-edges.db", config_dir() / "reco-edges.db", config_dir() / "coread-edges.db"):
        if not path.exists():
            continue
        for a, b in sqlite3.connect(f"file:{path}?mode=ro", uri=True).execute("SELECT a_id, b_id FROM pair"):
            if a in linked:
                linked[a].add(b)
            if b in linked:
                linked[b].add(a)

    pools: dict[tuple, list[int]] = {}
    for sid in sorted(series):
        pools.setdefault((series[sid]["type"], series[sid]["band"]), []).append(sid)

    rows = []
    for group, (seed, cand, weight) in enumerate(positives):
        info = series[seed]
        pool = [
            sid for sid in pools[(series[cand]["type"], series[cand]["band"])]
            if sid not in (seed, cand) and sid not in linked[seed] and not series[sid]["creators"] & info["creators"]
        ]
        need = min(2, len(info["genres"]))
        near = [sid for sid in pool if len(series[sid]["genres"] & info["genres"]) >= need]
        if not pool or not near:
            continue
        rows.append((seed, cand, "human", weight, group))
        rows.append((seed, rng.choice(near), "near", 0, group))
        rows.append((seed, rng.choice(pool), "far", 0, group))

    out = artifacts_dir() / "eval" / "known-pairs.tsv"
    with out.open("w", encoding="utf-8", newline="\n") as fh:
        fh.write("seed_id\tcand_id\tkind\tweight\tgroup\n")
        for row in rows:
            fh.write("\t".join(str(cell) for cell in row) + "\n")
    queue = artifacts_dir() / "eval" / "known-queue.tsv"
    write_queue(queue, [(row[0], row[1]) for row in rows])
    print(f"{len(rows) // 3} human pairs, each with a near and a far control, written to {out}")
    print(f"Judge them with: python distribution/judge-pairs.py run --queue {queue}")
    return 0


def auc(high: list[int], low: list[int]) -> float:
    """Chance that a value from `high` beats one from `low`, a tie counting half."""
    if not high or not low:
        return float("nan")
    wins = sum((h > l) + 0.5 * (h == l) for h in high for l in low)
    return wins / (len(high) * len(low))


def cmd_known_report(args) -> int:
    conn = open_store()
    judge = args.judge or f"{DEFAULT_MODEL}/{DEFAULT_EFFORT}"
    path = artifacts_dir() / "eval" / "known-pairs.tsv"
    if not path.exists():
        print("error: no known-pairs.tsv. Run `known` first.")
        return 1
    stored = {
        (seed, cand): (feel, enjoy, thin, franchise, known_seed, known_cand)
        for seed, cand, feel, enjoy, thin, franchise, known_seed, known_cand in conn.execute(
            "SELECT seed_id, cand_id, feel, enjoy, insufficient, same_franchise, recognise_seed, recognise_cand "
            "FROM judgement WHERE judge = ? AND rubric = ? AND status = 'ok'",
            (judge, RUBRIC_VERSION),
        )
    }
    by_kind: dict[str, list] = {kind: [] for kind in KINDS}
    groups: dict[int, dict] = {}
    total = 0
    with path.open(encoding="utf-8") as fh:
        next(fh)
        for line in fh:
            seed, cand, kind, weight, group = line.rstrip("\n").split("\t")
            total += 1
            row = stored.get((int(seed), int(cand)))
            if row is None or row[2]:
                continue
            by_kind[kind].append((*row, int(weight)))
            groups.setdefault(int(group), {})[kind] = row

    judged = sum(len(rows) for rows in by_kind.values())
    print(f"{judge}, rubric {RUBRIC_VERSION}: {judged} of {total} pairs have a usable score")
    if judged < 60:
        print("  too few to report")
        return 0
    print(f"  {'kind':8}{'n':>5}{'feel':>7}{'enjoy':>7}{'enjoy>=2':>10}{'franchise':>11}")
    for kind in KINDS:
        rows = by_kind[kind]
        if not rows:
            continue
        print(
            f"  {kind:8}{len(rows):>5}{sum(r[0] for r in rows) / len(rows):>7.2f}"
            f"{sum(r[1] for r in rows) / len(rows):>7.2f}{sum(r[1] >= 2 for r in rows) / len(rows):>10.0%}"
            f"{sum(bool(r[3]) for r in rows) / len(rows):>11.0%}"
        )

    def separation(label: str, keep) -> None:
        kept = {kind: [row for row in by_kind[kind] if keep(row)] for kind in KINDS}
        sizes = "/".join(str(len(kept[kind])) for kind in KINDS)
        for name, column in (("enjoy", 1), ("feel", 0)):
            values = {kind: [row[column] for row in kept[kind]] for kind in KINDS}
            print(
                f"  {label:26}{name:6} human over far {auc(values['human'], values['far']):.2f}, "
                f"human over near {auc(values['human'], values['near']):.2f}, "
                f"near over far {auc(values['near'], values['far']):.2f}  (n {sizes})"
            )

    print("Separation, as the chance the first kind outscores the second (0.5 is a coin flip):")
    separation("all pairs", lambda row: True)
    separation("candidate not recognised", lambda row: not row[5])
    separation("neither title recognised", lambda row: not row[4] and not row[5])

    for other in ("near", "far"):
        full = [group for group in groups.values() if "human" in group and other in group]
        if not full:
            continue
        above = sum(group["human"][1] > group[other][1] for group in full)
        level = sum(group["human"][1] == group[other][1] for group in full)
        print(
            f"  same seed, enjoy: human above {other} in {above / len(full):.0%}, "
            f"level in {level / len(full):.0%}, below in {(len(full) - above - level) / len(full):.0%} of {len(full)}"
        )
    human = by_kind["human"]
    print(f"  enjoy against the number of people who made the recommendation: Spearman "
          f"{spearman([(row[6], row[1]) for row in human]):.2f}")
    return 0


PAGE = r"""<!doctype html>
<html lang="en">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>Recommendation calibration</title>
<style>
  :root { --bg: #f6f5f2; --card: #fff; --ink: #1c1b19; --dim: #6b675f; --line: #dcd8cf; --accent: #b5476a; }
  @media (prefers-color-scheme: dark) {
    :root { --bg: #161513; --card: #211f1c; --ink: #ece9e2; --dim: #a39e93; --line: #36322c; --accent: #e07a9c; }
  }
  * { box-sizing: border-box; }
  body { margin: 0; background: var(--bg); color: var(--ink); font: 15px/1.5 system-ui, sans-serif; }
  main { max-width: 1100px; margin: 0 auto; padding: 16px; }
  header { display: flex; flex-wrap: wrap; gap: 12px; align-items: center; justify-content: space-between; }
  h1 { font-size: 18px; margin: 0; }
  details { margin: 12px 0; }
  pre { white-space: pre-wrap; font: inherit; color: var(--dim); margin: 8px 0 0; }
  .pair { display: grid; grid-template-columns: 1fr 1fr; gap: 12px; margin-top: 12px; }
  @media (max-width: 760px) { .pair { grid-template-columns: 1fr; } }
  .card { background: var(--card); border: 1px solid var(--line); border-radius: 10px; padding: 14px; }
  .role { font-size: 12px; letter-spacing: .06em; text-transform: uppercase; color: var(--accent); }
  .card h2 { font-size: 17px; margin: 2px 0 6px; }
  .facts, .tags { color: var(--dim); font-size: 13px; margin: 4px 0; }
  .scales { background: var(--card); border: 1px solid var(--line); border-radius: 10px; padding: 14px; margin-top: 12px; }
  .scale { display: flex; flex-wrap: wrap; align-items: center; gap: 8px; margin: 6px 0; }
  .scale b { width: 220px; }
  button { font: inherit; color: var(--ink); background: transparent; border: 1px solid var(--line);
           border-radius: 8px; padding: 6px 14px; cursor: pointer; }
  button.on { background: var(--accent); border-color: var(--accent); color: #fff; }
  button:focus-visible { outline: 2px solid var(--accent); outline-offset: 2px; }
  nav { display: flex; gap: 8px; align-items: center; margin-top: 12px; flex-wrap: wrap; }
  .grow { flex: 1; }
</style>
</head>
<body>
<main>
  <header>
    <h1>Recommendation calibration <span id="progress"></span></h1>
    <button id="download">Download labels</button>
  </header>
  <details>
    <summary>Rubric</summary>
    <pre>__RUBRIC__</pre>
  </details>
  <div class="pair">
    <div class="card" id="seed"></div>
    <div class="card" id="cand"></div>
  </div>
  <div class="scales">
    <div class="scale"><b>Similar story, tone and feel</b><span id="feel"></span></div>
    <div class="scale"><b>A fan of the seed would enjoy it</b><span id="enjoy"></span></div>
    <div class="scale"><b></b><button id="thin">Cannot tell from this text</button></div>
  </div>
  <nav>
    <button id="prev">Previous</button>
    <button id="next">Next</button>
    <span class="grow"></span>
    <span id="hint">Keys: 1 to 4 score feel, Q W E R score enjoy, arrows move.</span>
  </nav>
</main>
<script>
const ITEMS = __ITEMS__;
const RUBRIC = "__VERSION__";
const KEY = "maki-calibration-" + RUBRIC;
let labels = {};
try { labels = JSON.parse(localStorage.getItem(KEY) || "{}"); } catch (e) { labels = {}; }
let at = 0;
const id = i => ITEMS[i].seed + "-" + ITEMS[i].cand;
const esc = s => String(s).replace(/[&<>"]/g, c => ({"&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;"}[c]));

function card(role, c) {
  const facts = [c.type + (c.origin ? " (" + c.origin + ")" : ""), c.year, c.status,
                 c.chapters ? c.chapters + " chapters" : "", c.rating].filter(Boolean).join(" · ");
  return '<div class="role">' + role + '</div><h2>' + esc(c.title) + '</h2>' +
    '<div class="facts">' + esc(facts) + '</div>' +
    '<div class="facts">' + esc(c.creators.join(", ")) + '</div>' +
    '<div class="tags">' + esc(c.genres.join(", ")) + '</div>' +
    '<div class="tags">' + esc(c.central.join(", ")) + '</div>' +
    '<p>' + esc(c.synopsis || "No synopsis.") + '</p>';
}

function save() { try { localStorage.setItem(KEY, JSON.stringify(labels)); } catch (e) {} }

function set(field, value) {
  const label = labels[id(at)] || {};
  if (field === "insufficient") { label.insufficient = !label.insufficient; }
  else { label[field] = value; label.insufficient = false; }
  labels[id(at)] = label;
  save();
  draw();
  if (!label.insufficient && label.feel != null && label.enjoy != null && at < ITEMS.length - 1) { at++; draw(); }
}

function draw() {
  const item = ITEMS[at];
  const label = labels[id(at)] || {};
  document.getElementById("seed").innerHTML = card("Seed", item.s);
  document.getElementById("cand").innerHTML = card("Candidate", item.c);
  for (const field of ["feel", "enjoy"]) {
    const holder = document.getElementById(field);
    holder.innerHTML = "";
    for (let score = 0; score <= 3; score++) {
      const b = document.createElement("button");
      b.textContent = score;
      if (label[field] === score && !label.insufficient) b.className = "on";
      b.onclick = () => set(field, score);
      holder.appendChild(b);
    }
  }
  document.getElementById("thin").className = label.insufficient ? "on" : "";
  const done = ITEMS.filter((_, i) => {
    const l = labels[id(i)];
    return l && (l.insufficient || (l.feel != null && l.enjoy != null));
  }).length;
  document.getElementById("progress").textContent = "· pair " + (at + 1) + " of " + ITEMS.length + " · " + done + " scored";
}

document.getElementById("thin").onclick = () => set("insufficient");
document.getElementById("prev").onclick = () => { if (at > 0) { at--; draw(); } };
document.getElementById("next").onclick = () => { if (at < ITEMS.length - 1) { at++; draw(); } };
document.getElementById("download").onclick = () => {
  const rows = ITEMS.map((item, i) => Object.assign({seed: item.seed, cand: item.cand}, labels[id(i)] || {}))
    .filter(r => r.insufficient || (r.feel != null && r.enjoy != null));
  const blob = new Blob([JSON.stringify({rubric: RUBRIC, labels: rows}, null, 1)], {type: "application/json"});
  const a = document.createElement("a");
  a.href = URL.createObjectURL(blob);
  a.download = "calibration-labels.json";
  a.click();
};
document.addEventListener("keydown", e => {
  const feel = "1234".indexOf(e.key), enjoy = "qwer".indexOf(e.key.toLowerCase());
  if (feel >= 0) set("feel", feel);
  else if (enjoy >= 0) set("enjoy", enjoy);
  else if (e.key === "ArrowLeft") document.getElementById("prev").click();
  else if (e.key === "ArrowRight") document.getElementById("next").click();
});
draw();
</script>
</body>
</html>
"""


def main() -> int:
    sys.stdout.reconfigure(encoding="utf-8")
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    sub = parser.add_subparsers(dest="command", required=True)

    def common(p):
        p.add_argument("--model", default=DEFAULT_MODEL)
        p.add_argument("--effort", default=DEFAULT_EFFORT, choices=["low", "medium", "high", "xhigh", "max"])
        p.add_argument("--queue", help="pairs to judge; defaults to <artifacts>/eval/judge-queue.tsv")

    run = sub.add_parser("run", help="judge the queue")
    common(run)
    run.add_argument("--limit", type=int, help="judge only the first N pairs")
    run.add_argument("--sync", action="store_true", help="one request at a time at list price, for a small check")
    run.add_argument("--tag", help="store this pass as its own judge, for a repeat of pairs already judged")
    run.set_defaults(handler=cmd_run)

    resume = sub.add_parser("resume", help="collect batches that were submitted earlier")
    common(resume)
    resume.set_defaults(handler=cmd_resume)

    estimate = sub.add_parser("estimate", help="size and rough cost of the queue, no API call")
    common(estimate)
    estimate.set_defaults(handler=cmd_estimate)

    show = sub.add_parser("show", help="print the exact prompt for one pair, no API call")
    show.add_argument("seed", type=int)
    show.add_argument("candidate", type=int)
    show.set_defaults(handler=cmd_show)

    stats = sub.add_parser("stats", help="what is stored")
    stats.set_defaults(handler=cmd_stats)

    calibrate = sub.add_parser("calibrate", help="write a page of pairs for the owner to score")
    calibrate.add_argument("--n", type=int, default=150)
    calibrate.add_argument("--seed", type=int, default=20261115)
    calibrate.add_argument("--panel", default=str(Path(__file__).with_name("eval-panel.tsv")))
    calibrate.add_argument("--queue")
    calibrate.add_argument("--out")
    calibrate.set_defaults(handler=cmd_calibrate)

    imported = sub.add_parser("import-labels", help="store the owner's scores")
    imported.add_argument("file")
    imported.set_defaults(handler=cmd_import)

    agreement = sub.add_parser("agreement", help="owner against judge")
    agreement.add_argument("--judge")
    agreement.add_argument("--against", help="a second judge to compare with, instead of the owner")
    agreement.set_defaults(handler=cmd_agreement)

    known = sub.add_parser("known", help="write pairs with a known answer: human picks and controls")
    known.add_argument("--n", type=int, default=160)
    known.add_argument("--seed", type=int, default=20261115)
    known.set_defaults(handler=cmd_known)

    known_report = sub.add_parser("known-report", help="does the judge tell human picks from controls")
    known_report.add_argument("--judge")
    known_report.set_defaults(handler=cmd_known_report)

    args = parser.parse_args()
    return args.handler(args)


if __name__ == "__main__":
    sys.exit(main())
