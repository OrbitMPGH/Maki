"""
RESEARCH-PLAN-3 capacity probe: a shipped student widened with every new weight at zero on the output side,
so step 0 is that student exactly, as a starting checkpoint for a short run of the same recipe. Read beside
a control re-parametrised the same way (the ECB branches folded and restarted at zero), the run says whether
more width at full resolution moves the tails the loss terms could not.

New input channels of the next layer get a small random weight: their activations are zero at step 0, so
the function is unchanged, and they carry a gradient from the first step.

    python probe_widen.py hyb-c48n8e-b64n4-m1-sharp02-last hyb-c64n8e-b64n4 --tag p3wide0
    python probe_widen.py hyb-c48n8e-b64n4-m1-sharp02-last hyb-c48n8e-b64n4 --tag p3ctl0
"""

import argparse

import torch

from common import WORK, collapse, load_student, plain_spec, split_name, student


def widen(source: dict, target_plain: str) -> dict:
    target = student(target_plain)
    g = torch.Generator().manual_seed(0)
    out = {}
    for name, p in target.state_dict().items():
        s = source[name]
        new = torch.zeros_like(p)
        new[tuple(slice(0, n) for n in s.shape)] = s
        if p.dim() == 4 and p.shape[1] > s.shape[1]:
            fan = p.shape[1] * p.shape[2] * p.shape[3]
            new[:s.shape[0], s.shape[1]:] = torch.randn(s.shape[0], p.shape[1] - s.shape[1], p.shape[2], p.shape[3],
                                                        generator=g) * (2 / fan) ** 0.5 * 0.1
        out[name] = new
    return out


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("checkpoint", help="name under WORK without student- and .pt")
    parser.add_argument("target", help="the wider spec, e.g. hyb-c64n8e-b64n4")
    parser.add_argument("--tag", required=True)
    args = parser.parse_args()
    torch.set_num_threads(4)

    spec, _ = split_name(args.checkpoint)
    model = student(spec)
    load_student(model, torch.load(WORK / f"student-{args.checkpoint}.pt", map_location="cpu", weights_only=True))
    folded = collapse(model.eval())
    state = widen(folded.state_dict(), plain_spec(args.target))
    wide = student(args.target)
    load_student(wide, state)
    wide.eval()
    x = torch.rand(2, 3, 64, 64)
    with torch.no_grad():
        diff = (wide(x) - folded(x)).abs().max().item()
    params = sum(p.numel() for p in collapse(wide).parameters())
    print(f"{args.target} from {args.checkpoint}: max |diff| at step 0 {diff:.2e}, {params / 1000:.0f}k folded params")
    assert diff < 1e-4, "the widened model is not the same function"
    path = WORK / f"student-{args.target}-{args.tag}.pt"
    torch.save(wide.state_dict(), path)
    print(f"wrote {path}")


if __name__ == "__main__":
    main()
