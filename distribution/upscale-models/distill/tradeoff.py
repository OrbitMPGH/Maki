"""
The tails of every measured model of one family in one table, from measure.py's JSON, so the loss arms can
be read as points on a trade-off curve (sharper faint lines against more marks or grain) or off it.

    python tradeoff.py hyb-c48n8e-b64n4 c48n8e
"""

import json
import sys

from common import WORK


def main():
    prefixes = sys.argv[1:] or ["hyb-c48n8e-b64n4"]
    files = sorted(p for p in (WORK / "measure").glob("*.json") if any(p.stem.startswith(x) for x in prefixes)
                   and "at" not in p.stem.split("-")[-1])
    print(f"{'model':40} {'faint o/c/g':>19} {'marks12 o/c/g':>19} {'grain o/c/g':>19} {'psnr o/c/g':>21} {'commit o':>8} {'blobs o/g':>12} {'line_hf g':>9}")
    for path in files:
        s = json.loads(path.read_text())["summary"]
        o, c, g = s.get("owner", {}), s.get("colour", {}), s.get("grey", {})
        def tri(k, scale=1.0, d=3):
            return "/".join(f"{x.get(k, float('nan')) * scale:.{d}f}" for x in (o, c, g))
        print(f"{path.stem[-40:]:40} {tri('sharp_faint'):>19} {tri('marks12', 1000):>19} {tri('grain'):>19} "
              f"{tri('psnr', 1, 2):>21} {o.get('committed', float('nan')):8.3f} "
              f"{o.get('blobs', float('nan')) * 1000:5.2f}/{g.get('blobs', float('nan')) * 1000:5.2f} {g.get('line_hf', float('nan')):9.3f}")


if __name__ == "__main__":
    main()
