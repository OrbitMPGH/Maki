"""
Check-ins beside a training run (RESEARCH-PLAN-2 section 5.6), so no run goes far before it is judged.
At each listed step it copies the run's checkpoint, scores it with measure.py on the CPU (the owner page
by default, about two minutes), writes one line to logs/queue.log, and stops the training process when
the kill rule holds. The rule is a Python expression over the owner page's summary from measure.py's
JSON (`committed`, `blobs`, `fill_excess_p95`, `psnr`, `texture`, `texture_teacher`, `spots`,
`texture_crease`, `texture_crease_teacher`, `phase`, `seam`, ...).

    python checkin.py c48n8e b1-mask --steps 10000 \
        --kill "committed < 0.425 or blobs > 0.0134 or psnr < 41.2 or abs(texture_crease / texture_crease_teacher - 1) > 0.05"
"""

import argparse
import json
import shutil
import subprocess
import sys
import time
from datetime import datetime

from common import WORK

LOG = WORK / "logs"


def note(text: str) -> None:
    line = f"{datetime.now():%H:%M:%S} {text}"
    print(line, flush=True)
    with open(LOG / "queue.log", "a", encoding="utf-8") as f:
        f.write(line + "\n")


def ended(spec: str, tag: str) -> bool:
    """The queue noted this run's exit after its latest start line."""
    state = False
    for line in open(LOG / "queue.log", encoding="utf-8"):
        if f"train {spec}-{tag} from" in line:
            state = False
        if f"train {spec}-{tag} exit" in line:
            state = True
    return state


def reached(path, step: int) -> bool:
    try:
        return any(line.startswith(f"step {step}:") for line in open(path, encoding="utf-8"))
    except FileNotFoundError:
        return False


def stop(spec: str, tag: str) -> None:
    script = ("Get-CimInstance Win32_Process | Where-Object { $_.CommandLine -match 'train_served' -and "
              f"$_.CommandLine -match '--tag {tag}( |$)' -and $_.CommandLine -match ' {spec} ' }} | "
              "ForEach-Object { Stop-Process -Id $_.ProcessId -Force }")
    subprocess.run(["powershell.exe", "-NoProfile", "-Command", script], check=False)


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("spec")
    parser.add_argument("tag")
    parser.add_argument("--steps", nargs="+", type=int, required=True)
    parser.add_argument("--kill", required=True, help="Python expression over the owner page summary")
    parser.add_argument("--probes", action="store_true")
    parser.add_argument("--log", help="the run's log under logs/ (default {tag}-{spec}.log)")
    parser.add_argument("--pages", nargs="+", default=["owner-webtoon"],
                        help="reference pages to score; a grey page adds grey_* names (grey_psnr) to the rule")
    args = parser.parse_args()
    run_log = LOG / (args.log or f"{args.tag}-{args.spec}.log")

    for step in args.steps:
        while not reached(run_log, step):
            if ended(args.spec, args.tag):
                note(f"checkin {args.spec}-{args.tag}: run ended before step {step}")
                return
            time.sleep(30)
        name = f"{args.spec}-{args.tag}at{step // 1000}k"
        # The weights at this eval, not the gated best (train_served.py writes -last at every eval).
        shutil.copy(WORK / f"student-{args.spec}-{args.tag}-last.pt", WORK / f"student-{name}.pt")
        cmd = [sys.executable, "-W", "ignore", "measure.py", name, "--cpu", "--no-sheets", "--pages", *args.pages]
        if args.probes:
            cmd.append("--probes")
        with open(LOG / f"checkin-{name}.log", "w", encoding="utf-8") as out:
            subprocess.run(cmd, stdout=out, stderr=subprocess.STDOUT, check=False)
        groups = json.loads((WORK / "measure" / f"{name}.json").read_text())["summary"]
        summary = dict(groups["owner"])
        for k, v in groups.get("grey", {}).items():
            summary[f"grey_{k}"] = v
        line = (f"committed {summary['committed']:.3f} blobs {summary['blobs'] * 1000:.2f} "
                f"excess p95 {summary['fill_excess_p95']:.2f} psnr {summary['psnr']:.2f} "
                f"crease {summary['texture_crease'] / summary['texture_crease_teacher']:.3f}x")
        if args.probes:
            line += f" phase {summary['phase']:.3f} seam {summary['seam']:.2f}"
        if "grey_psnr" in summary:
            line += f" grey psnr {summary['grey_psnr']:.2f} committed {summary['grey_committed']:.3f}"
        line += f" texture {summary['texture']:.3f} spots {summary['spots'] * 100:.2f}%"
        line += f" grain {summary['grain']:.3f}"
        line += f" sharp faint {summary['sharp_faint']:.3f} strong {summary['sharp_strong']:.3f}"
        if "marks" in summary:
            line += f" marks {summary['marks'] * 1000:.3f} marks12 {summary['marks12'] * 1000:.3f}"
        if "grey_grain" in summary:
            line += f" grey grain {summary['grey_grain']:.3f} line_hf {summary['grey_line_hf']:.3f}"
        killed = bool(eval(args.kill, {"abs": abs}, summary))
        note(f"checkin {name}: {line}" + ("; kill rule holds, stopping the run" if killed else ""))
        if killed:
            stop(args.spec, args.tag)
            return


if __name__ == "__main__":
    main()
