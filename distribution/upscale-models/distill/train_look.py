"""
Round three: one teacher's look per student, made robust to bad sources.

- Library crops: the teacher's output for the crop, as in round one.
- Volume crops: the input is a freshly damaged copy (degrade.py, mild/medium/heavy), the target is what
  the teacher makes of a clean half-size copy (label_clean.py). The student learns to give the
  teacher's best result from a source the teacher itself would do worse on.

Round two averaged two teachers and aimed at real pages, and taught the student a faint grid where
the targets disagree about screentone; every target here is one sharp teacher output.

Validation, per held-out volume set, at mild and at heavy damage: the student against the teacher's
clean-source result, beside the bar of the teacher itself run on the same damaged input.

    python train_look.py c48n12 --look realesrgan
"""

import argparse
import collections
import math
import multiprocessing
import time

import numpy as np
import torch

from common import MARGIN, WORK, luma, student
from degrade import fixed_inputs, worker_batch, worker_init
from label_clean import label, load as load_teacher_model

LIBRARY_TARGET = {"realesrgan": "{p}{split}.npz:hr", "dat2": "{p}{split}_dat2.npz:hr"}


def gpu(array: np.ndarray, device) -> torch.Tensor:
    return torch.from_numpy(np.ascontiguousarray(array)).to(device).permute(0, 3, 1, 2).contiguous()


def psnr_y(pred: torch.Tensor, target: torch.Tensor) -> float:
    a = (luma(pred.clamp(0, 1)) * 255).round()
    b = luma(target) * 255
    return 10 * math.log10(255 ** 2 / max(((a - b) ** 2).mean().item(), 1e-10))


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("spec")
    parser.add_argument("--look", choices=sorted(LIBRARY_TARGET), required=True)
    parser.add_argument("--tag", help="defaults to the look")
    parser.add_argument("--sets", nargs="+", default=["", "vol_"], help="dataset prefixes")
    parser.add_argument("--gt-share", type=float, default=0.5)
    parser.add_argument("--iterations", type=int, default=50000)
    parser.add_argument("--batch", type=int, default=32)
    parser.add_argument("--lr", type=float, default=6e-4)
    parser.add_argument("--rgb-weight", type=float, default=0.1)
    parser.add_argument("--init")
    parser.add_argument("--workers", type=int, default=6)
    parser.add_argument("--eval-every", type=int, default=5000)
    args = parser.parse_args()
    tag = args.tag or args.look

    torch.manual_seed(0)
    device = torch.device("cuda")
    data = WORK / "data"

    def library(split):
        file, key = LIBRARY_TARGET[args.look].split(":")
        lr, target = [], []
        for p in args.sets:
            if (data / f"{p}{split}.npz").exists():
                lr.append(np.load(data / f"{p}{split}.npz")["lr"])
                target.append(np.load(data / file.format(p=p, split=split))[key])
        return np.concatenate(lr), np.concatenate(target)

    lib_lr, lib_target = (gpu(a, device) for a in library("train"))
    val_lr, val_target = (gpu(a, device) for a in library("val"))

    clean, labels = [], []
    for p in args.sets:
        if (data / f"{p}gt_train.npz").exists():
            clean.append(np.load(data / f"{p}gt_train.npz")["hr"][::2])
            labels.append(np.load(data / f"{p}gt_train_clean_{args.look}.npz")["target"])
    clean = np.ascontiguousarray(np.concatenate(clean))
    target_file = data / "_online_targets.npy"
    np.save(target_file, clean)
    gt_target = gpu(np.concatenate(labels), device)
    print(f"{args.look}: library {len(lib_lr)} crops, volumes {len(clean)} clean targets", flush=True)
    del clean, labels

    teacher, scale, dtype = load_teacher_model(args.look, device)
    held = {}
    for p in args.sets:
        if not (data / f"{p}gt_val.npz").exists():
            continue
        targets = np.ascontiguousarray(np.load(data / f"{p}gt_val.npz")["hr"][::2])
        want = np.load(data / f"{p}gt_val_clean_{args.look}.npz")["target"]
        for tier, seed in (("mild", 11), ("heavy", 17)):
            damaged = fixed_inputs(targets, tier, seed)
            served = label(teacher, scale, dtype, damaged, device)
            held[f"{p or 'base_'}{tier}"] = (gpu(damaged, device), gpu(want, device), gpu(served, device))
    del teacher
    torch.cuda.empty_cache()

    m2 = 2 * MARGIN

    def centre(t):
        return t[..., m2:-m2, m2:-m2]

    def score(fn, inputs, target):
        values = []
        with torch.no_grad():
            for i in range(0, len(inputs), 64):
                p = fn(inputs[i:i + 64].float() / 255)
                values.append(psnr_y(centre(p), centre(target[i:i + 64].float() / 255)))
        return sum(values) / len(values)

    bars = {name: score(lambda x: x, served, want) for name, (_, want, served) in held.items()}
    print("bars (the teacher on the damaged input): " + ", ".join(f"{k} {v:.2f} dB" for k, v in bars.items()), flush=True)

    model = student(args.spec).to(device)
    if args.init:
        model.load_state_dict(torch.load(args.init, map_location="cpu", weights_only=True))
    optimizer = torch.optim.AdamW(model.parameters(), lr=args.lr, weight_decay=0)
    schedule = torch.optim.lr_scheduler.OneCycleLR(optimizer, max_lr=args.lr, total_steps=args.iterations,
                                                   pct_start=0.05, final_div_factor=100)
    n_gt = round(args.batch * args.gt_share)
    n_lib = args.batch - n_gt

    def loss_on(p, y):
        p, y = centre(p), centre(y)
        return (luma(p) - luma(y)).abs().mean() + args.rgb_weight * (p - y).abs().mean()

    pool = multiprocessing.Pool(args.workers, initializer=worker_init, initargs=(str(target_file),))
    pending = collections.deque(pool.apply_async(worker_batch, ((k, n_gt),)) for k in range(4 * args.workers))
    jobs = len(pending)

    best = -1.0
    started = time.time()
    for step in range(1, args.iterations + 1):
        i = torch.randint(0, len(lib_lr), (n_lib,), device=device)
        with torch.autocast("cuda", dtype=torch.bfloat16):
            p = model(lib_lr[i].float() / 255)
        loss = loss_on(p.float(), lib_target[i].float() / 255) * n_lib / args.batch

        picks, inputs = pending.popleft().get()
        pending.append(pool.apply_async(worker_batch, ((jobs, n_gt),)))
        jobs += 1
        x = torch.from_numpy(inputs).to(device).permute(0, 3, 1, 2)
        with torch.autocast("cuda", dtype=torch.bfloat16):
            p = model(x.float() / 255)
        loss = loss + loss_on(p.float(), gt_target[torch.from_numpy(picks).to(device)].float() / 255) * n_gt / args.batch

        optimizer.zero_grad(set_to_none=True)
        loss.backward()
        optimizer.step()
        schedule.step()
        if step % args.eval_every == 0 or step == args.iterations:
            model.eval()
            lib_score = score(model, val_lr, val_target)
            results = {name: score(model, damaged, want) for name, (damaged, want, _) in held.items()}
            model.train()
            mean = sum(results.values()) / len(results)
            if mean > best:
                best = mean
                torch.save(model.state_dict(), WORK / f"student-{args.spec}-{tag}.pt")
            detail = "  ".join(f"{k} {v:.2f} ({v - bars[k]:+.2f})" for k, v in results.items())
            print(f"{args.spec}-{tag} step {step}: library {lib_score:.2f} dB  volumes {detail}  best {best:.2f}  "
                  f"{step / (time.time() - started):.0f} it/s", flush=True)
    pool.terminate()


if __name__ == "__main__":
    main()
