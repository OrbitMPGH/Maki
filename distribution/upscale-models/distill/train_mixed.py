"""
Round two: a student trained on two kinds of example at once.

- Library crops, whose target is a blend of teachers (Real-ESRGAN and DAT2 by default). These carry
  the look the owner picked, on exactly the sources Maki upscales.
- Ground-truth pairs from make_gt_dataset.py, whose target is the real high-quality page. These are
  what can take the student past its teachers.

Validation reports both: how close the student is to the teacher blend, and how close it is to the
real page against Real-ESRGAN's own score there, which is the bar to beat.

    python train_mixed.py c48n12 --blend 0.5 --tag mix
"""

import argparse
import collections
import math
import multiprocessing
import time

import numpy as np
import torch

from common import MARGIN, WORK, load_teacher, luma, student, teacher_2x
from degrade import fixed_inputs, worker_batch, worker_init


def gpu(array: np.ndarray, device) -> torch.Tensor:
    return torch.from_numpy(array).to(device).permute(0, 3, 1, 2).contiguous()


def blend(first: np.ndarray, second: np.ndarray, share: float) -> np.ndarray:
    """share * first + (1 - share) * second, rounded to bytes, in chunks so RAM stays bounded."""
    out = np.empty_like(first)
    for i in range(0, len(first), 512):
        mix = first[i:i + 512].astype(np.float32) * share + second[i:i + 512].astype(np.float32) * (1 - share)
        out[i:i + 512] = (mix + 0.5).astype(np.uint8)
    return out


def stacked(data, prefixes, name, key):
    """`name` from every prefixed set that has it, concatenated; the bare set is prefix ''."""
    parts = [np.load(data / f"{p}{name}.npz")[key] for p in prefixes if (data / f"{p}{name}.npz").exists()]
    return np.concatenate(parts) if len(parts) > 1 else parts[0]


def psnr_y(pred: torch.Tensor, target: torch.Tensor) -> float:
    a = (luma(pred.clamp(0, 1)) * 255).round()
    b = luma(target) * 255
    return 10 * math.log10(255 ** 2 / max(((a - b) ** 2).mean().item(), 1e-10))


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("spec")
    parser.add_argument("--tag", default="mix")
    parser.add_argument("--blend", type=float, default=0.5, help="Real-ESRGAN's share of the teacher target; DAT2 has the rest")
    parser.add_argument("--gt-share", type=float, default=0.5, help="share of each batch drawn from ground-truth pairs")
    parser.add_argument("--iterations", type=int, default=60000)
    parser.add_argument("--batch", type=int, default=32)
    parser.add_argument("--lr", type=float, default=4e-4)
    parser.add_argument("--rgb-weight", type=float, default=0.1)
    parser.add_argument("--init", help="checkpoint to start from")
    parser.add_argument("--extra", action="append", default=[], help="prefix of an added set (make_*_dataset.py --prefix)")
    parser.add_argument("--online", action="store_true", help="damage ground-truth inputs afresh every step (degrade.py tiers)")
    parser.add_argument("--workers", type=int, default=6)
    parser.add_argument("--eval-every", type=int, default=5000)
    args = parser.parse_args()

    torch.manual_seed(0)
    device = torch.device("cuda")
    data = WORK / "data"
    sets = [""] + args.extra

    def load(name, key):
        return stacked(data, sets, name, key)

    lib_lr = gpu(load("train", "lr"), device)
    lib_target = gpu(blend(load("train", "hr"), load("train_dat2", "hr"), args.blend), device)
    if args.online:
        # The builder wrote each clean target once per damaged variant, consecutively; keep it once.
        targets = load("gt_train", "hr")
        assert (targets[0] == targets[1]).all(), "expected consecutive variants of one target"
        targets = np.ascontiguousarray(targets[::2])
        target_file = data / "_online_targets.npy"
        np.save(target_file, targets)
        gt_hr = gpu(targets, device)
        gt_lr = None
        del targets
    else:
        gt_lr, gt_hr = gpu(load("gt_train", "lr"), device), gpu(load("gt_train", "hr"), device)
    val_lr = gpu(load("val", "lr"), device)
    val_target = gpu(blend(load("val", "hr"), load("val_dat2", "hr"), args.blend), device)
    # Held-out ground truth stays per set, so a colour set is not averaged away by a larger grey one.
    truth_sets = {}
    for prefix in sets:
        path = data / f"{prefix}gt_val.npz"
        if path.exists():
            held = np.load(path)
            truth_sets[prefix or "base"] = (gpu(held["lr"], device), gpu(held["hr"], device))
            clean = np.ascontiguousarray(held["hr"][::2])
            truth_sets[(prefix or "base") + "-heavy"] = (gpu(fixed_inputs(clean, "heavy", 17), device), gpu(clean, device))
    print(f"library {len(lib_lr)} crops, ground truth {len(gt_hr)} targets; val {len(val_lr)} crops, "
          + ", ".join(f"{k} {len(v[0])} pairs" for k, v in truth_sets.items()), flush=True)

    m2 = 2 * MARGIN

    def score(model_fn, lr, target):
        """Mean luma PSNR over crop centres. `model_fn` gets inputs scaled to [0, 1]."""
        values = []
        with torch.no_grad():
            for i in range(0, len(lr), 64):
                p = model_fn(lr[i:i + 64].float() / 255)
                values.append(psnr_y(p[..., m2:-m2, m2:-m2], target[i:i + 64].float()[..., m2:-m2, m2:-m2] / 255))
        return sum(values) / len(values)

    teacher = load_teacher(device)
    # Exactly what Maki serves: 4x, rounded to bytes, Lanczos-halved.
    bars = {}
    for name, (lr, hr) in truth_sets.items():
        served = gpu(np.stack([teacher_2x(teacher, t) for t in lr.permute(0, 2, 3, 1)]), device)
        bars[name] = score(lambda x: x, served, hr)
        plain = score(lambda x: torch.nn.functional.interpolate(x, scale_factor=2, mode="bicubic"), lr, hr)
        print(f"ground truth {name}: bicubic {plain:.2f} dB, Real-ESRGAN {bars[name]:.2f} dB (the bar)", flush=True)
        del served
    del teacher

    model = student(args.spec).to(device)
    if args.init:
        model.load_state_dict(torch.load(args.init, map_location="cpu", weights_only=True))
    optimizer = torch.optim.AdamW(model.parameters(), lr=args.lr, weight_decay=0)
    schedule = torch.optim.lr_scheduler.OneCycleLR(optimizer, max_lr=args.lr, total_steps=args.iterations,
                                                   pct_start=0.05, final_div_factor=100)
    n_gt = round(args.batch * args.gt_share)
    n_lib = args.batch - n_gt

    def loss_on(p, y):
        p, y = p[..., m2:-m2, m2:-m2], y[..., m2:-m2, m2:-m2]
        return (luma(p) - luma(y)).abs().mean() + args.rgb_weight * (p - y).abs().mean()

    pool = None
    pending = collections.deque()
    if args.online and n_gt:
        pool = multiprocessing.Pool(args.workers, initializer=worker_init, initargs=(str(target_file),))
        for k in range(4 * args.workers):
            pending.append(pool.apply_async(worker_batch, ((k, n_gt),)))
    jobs = 4 * args.workers

    best = -1.0
    started = time.time()
    for step in range(1, args.iterations + 1):
        loss = 0.0
        if n_lib:
            i = torch.randint(0, len(lib_lr), (n_lib,), device=device)
            with torch.autocast("cuda", dtype=torch.bfloat16):
                p = model(lib_lr[i].float() / 255)
            loss = loss + loss_on(p.float(), lib_target[i].float() / 255) * n_lib / args.batch
        if n_gt:
            if pool:
                picks, inputs = pending.popleft().get()
                pending.append(pool.apply_async(worker_batch, ((jobs, n_gt),)))
                jobs += 1
                i = torch.from_numpy(picks).to(device)
                x = torch.from_numpy(inputs).to(device).permute(0, 3, 1, 2)
            else:
                i = torch.randint(0, len(gt_lr), (n_gt,), device=device)
                x = gt_lr[i]
            with torch.autocast("cuda", dtype=torch.bfloat16):
                p = model(x.float() / 255)
            loss = loss + loss_on(p.float(), gt_hr[i].float() / 255) * n_gt / args.batch
        optimizer.zero_grad(set_to_none=True)
        loss.backward()
        optimizer.step()
        schedule.step()
        if step % args.eval_every == 0 or step == args.iterations:
            model.eval()
            to_blend = score(model, val_lr, val_target)
            truths = {name: score(model, lr, hr) for name, (lr, hr) in truth_sets.items()}
            model.train()
            # Kept on the real page, averaged over sets: that is the claim this round exists to make.
            mean = sum(truths.values()) / len(truths)
            if mean > best:
                best = mean
                torch.save(model.state_dict(), WORK / f"student-{args.spec}-{args.tag}.pt")
            pages = "  ".join(f"{n} {v:.2f} (Real-ESRGAN {bars[n]:.2f})" for n, v in truths.items())
            print(f"{args.spec}-{args.tag} step {step}: loss {loss.item():.5f}  teacher blend {to_blend:.2f} dB  "
                  f"real page {pages}  best mean {best:.2f}  {step / (time.time() - started):.0f} it/s", flush=True)


if __name__ == "__main__":
    main()
