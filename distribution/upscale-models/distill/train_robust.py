"""
Round four: round one's look, kept on worse sources.

Every target is round one's: what Real-ESRGAN makes of a real library crop. Half of each batch is
that crop as it is, half is the same crop made worse at the same size (degrade.damage). The student
learns to give Real-ESRGAN's result on the better source from the worse one. Nothing here is a
teacher output on a volume page, which is where round three's grid came from: Real-ESRGAN turns
screentone that a clean shrink has aliased into circles and mazes.

Starts from round one's weights. A checkpoint only counts if it stays as close to Real-ESRGAN on
the clean crops as round one, within --clean-slack.

    python train_robust.py c48n8 --init student-c48n8.pt
"""

import argparse
import collections
import math
import multiprocessing
import time

import numpy as np
import torch

from common import MARGIN, WORK, load_teacher, luma, student
from degrade import fixed_damage, worker_damage, worker_init
from label_clean import label


def gpu(array: np.ndarray, device) -> torch.Tensor:
    return torch.from_numpy(np.ascontiguousarray(array)).to(device).permute(0, 3, 1, 2).contiguous()


def psnr_y(pred: torch.Tensor, target: torch.Tensor) -> float:
    a = (luma(pred.clamp(0, 1)) * 255).round()
    b = luma(target) * 255
    return 10 * math.log10(255 ** 2 / max(((a - b) ** 2).mean().item(), 1e-10))


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("spec")
    parser.add_argument("--init", required=True)
    parser.add_argument("--tag", default="r4")
    parser.add_argument("--damaged-share", type=float, default=0.5)
    parser.add_argument("--iterations", type=int, default=40000)
    parser.add_argument("--batch", type=int, default=32)
    parser.add_argument("--lr", type=float, default=2e-4)
    parser.add_argument("--rgb-weight", type=float, default=0.1)
    parser.add_argument("--clean-slack", type=float, default=0.15, help="dB a checkpoint may lose on clean crops")
    parser.add_argument("--workers", type=int, default=6)
    parser.add_argument("--eval-every", type=int, default=2500)
    parser.add_argument("--served", action="store_true",
                        help="damaged inputs aim at Real-ESRGAN's result on that damaged input (make_served.py)")
    args = parser.parse_args()

    torch.manual_seed(0)
    device = torch.device("cuda")
    data = WORK / "data"
    train, val = np.load(data / "train.npz"), np.load(data / "val.npz")
    source_file = data / "_robust_sources.npy"
    np.save(source_file, train["lr"])
    lib_lr, lib_target = gpu(train["lr"], device), gpu(train["hr"], device)
    val_lr, val_target = gpu(val["lr"], device), gpu(val["hr"], device)

    held = {}
    if not args.served:
        teacher = load_teacher(device)
        for tier, seed in (("mild", 11), ("medium", 13), ("heavy", 17)):
            damaged = fixed_damage(val["lr"], tier, seed)
            held[tier] = (gpu(damaged, device), gpu(label(teacher, 4, torch.float32, damaged, device), device))
        del teacher
        torch.cuda.empty_cache()

    m2 = 2 * MARGIN

    def centre(t):
        return t[..., m2:-m2, m2:-m2]

    def score(fn, inputs, target):
        values = []
        with torch.no_grad():
            for i in range(0, len(inputs), 64):
                values.append(psnr_y(centre(fn(inputs[i:i + 64].float() / 255)), centre(target[i:i + 64].float() / 255)))
        return sum(values) / len(values)

    if args.served:
        served_train, served_val = np.load(data / "served_train.npz"), np.load(data / "served_val.npz")
        srv_lr, srv_target = gpu(served_train["lr"], device), gpu(served_train["hr"], device)
        held = {"served": (gpu(served_val["lr"], device), gpu(served_val["hr"], device))}
        bars = {"served": float("inf")}
        del served_train

    def against(key):
        return held[key][1] if args.served else val_target

    model = student(args.spec).to(device)
    model.load_state_dict(torch.load(WORK / args.init, map_location="cpu", weights_only=True))
    model.eval()
    start_clean = score(model, val_lr, val_target)
    start = {k: score(model, d, against(k)) for k, (d, _) in held.items()}
    if not args.served:
        bars = {k: score(lambda x: x, served, val_target) for k, (_, served) in held.items()}
        print("Real-ESRGAN on the damaged crop: " + ", ".join(f"{k} {v:.2f}" for k, v in bars.items()), flush=True)
    print(f"round one: clean {start_clean:.2f} dB, damaged " + ", ".join(f"{k} {v:.2f}" for k, v in start.items()), flush=True)
    model.train()

    optimizer = torch.optim.AdamW(model.parameters(), lr=args.lr, weight_decay=0)
    schedule = torch.optim.lr_scheduler.OneCycleLR(optimizer, max_lr=args.lr, total_steps=args.iterations,
                                                   pct_start=0.05, final_div_factor=100)
    n_dmg = round(args.batch * args.damaged_share)
    n_clean = args.batch - n_dmg

    def loss_on(p, y):
        p, y = centre(p), centre(y)
        return (luma(p) - luma(y)).abs().mean() + args.rgb_weight * (p - y).abs().mean()

    if not args.served:
        pool = multiprocessing.Pool(args.workers, initializer=worker_init, initargs=(str(source_file),))
        pending = collections.deque(pool.apply_async(worker_damage, ((k, n_dmg),)) for k in range(4 * args.workers))
        jobs = len(pending)

    best = -1.0
    started = time.time()
    for step in range(1, args.iterations + 1):
        i = torch.randint(0, len(lib_lr), (n_clean,), device=device)
        with torch.autocast("cuda", dtype=torch.bfloat16):
            p = model(lib_lr[i].float() / 255)
        loss = loss_on(p.float(), lib_target[i].float() / 255) * n_clean / args.batch

        if args.served:
            j = torch.randint(0, len(srv_lr), (n_dmg,), device=device)
            x, y = srv_lr[j], srv_target[j]
        else:
            picks, inputs = pending.popleft().get()
            pending.append(pool.apply_async(worker_damage, ((jobs, n_dmg),)))
            jobs += 1
            x = torch.from_numpy(inputs).to(device).permute(0, 3, 1, 2)
            y = lib_target[torch.from_numpy(picks).to(device)]
        with torch.autocast("cuda", dtype=torch.bfloat16):
            p = model(x.float() / 255)
        loss = loss + loss_on(p.float(), y.float() / 255) * n_dmg / args.batch

        optimizer.zero_grad(set_to_none=True)
        loss.backward()
        optimizer.step()
        schedule.step()
        if step % args.eval_every == 0 or step == args.iterations:
            model.eval()
            clean = score(model, val_lr, val_target)
            results = {k: score(model, d, against(k)) for k, (d, _) in held.items()}
            model.train()
            mean = sum(results.values()) / len(results)
            kept = clean >= start_clean - args.clean_slack
            if kept and mean > best:
                best = mean
                torch.save(model.state_dict(), WORK / f"student-{args.spec}-{args.tag}.pt")
            detail = "  ".join(f"{k} {v:.2f} ({v - start[k]:+.2f} vs r1, {v - bars[k]:+.2f} vs RE)" for k, v in results.items())
            print(f"step {step}: clean {clean:.2f} ({clean - start_clean:+.2f})  {detail}  "
                  f"{'kept' if kept else 'too far on clean'}  best {best:.2f}  {step / (time.time() - started):.0f} it/s", flush=True)
    if not args.served:
        pool.terminate()


if __name__ == "__main__":
    main()
