"""
Trains a small 2x student to reproduce what Maki serves from Real-ESRGAN. The loss is on Maki's luma
(BT.601, `UpscalePixels.Luma`) because that is all of the model output Maki keeps: colour is taken
from the resized source. A small RGB term keeps the colour output sane for anything reading it raw.

    python train.py c32n8 --iterations 60000
"""

import argparse
import math
import time

import numpy as np
import torch

from common import MARGIN, WORK, luma, student


def psnr_y(pred: torch.Tensor, target: torch.Tensor) -> float:
    a = (luma(pred.clamp(0, 1)) * 255).round()
    b = luma(target) * 255
    mse = ((a - b) ** 2).mean().item()
    return 10 * math.log10(255 ** 2 / max(mse, 1e-10))


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("spec")
    parser.add_argument("--iterations", type=int, default=60000)
    parser.add_argument("--batch", type=int, default=16)
    parser.add_argument("--lr", type=float, default=1e-3)
    parser.add_argument("--rgb-weight", type=float, default=0.1)
    parser.add_argument("--eval-every", type=int, default=2500)
    parser.add_argument("--seed", type=int, default=0)
    args = parser.parse_args()

    torch.manual_seed(args.seed)
    device = torch.device("cuda")
    data = WORK / "data"
    train = np.load(data / "train.npz")
    val = np.load(data / "val.npz")
    lr_train = torch.from_numpy(train["lr"]).to(device).permute(0, 3, 1, 2).contiguous()
    hr_train = torch.from_numpy(train["hr"]).to(device).permute(0, 3, 1, 2).contiguous()
    lr_val = torch.from_numpy(val["lr"]).to(device).permute(0, 3, 1, 2).contiguous()
    hr_val = torch.from_numpy(val["hr"]).to(device).permute(0, 3, 1, 2).contiguous()
    print(f"train {len(lr_train)} crops, val {len(lr_val)}", flush=True)

    model = student(args.spec).to(device)
    params = sum(p.numel() for p in model.parameters())
    optimizer = torch.optim.AdamW(model.parameters(), lr=args.lr, weight_decay=0)
    schedule = torch.optim.lr_scheduler.OneCycleLR(
        optimizer, max_lr=args.lr, total_steps=args.iterations, pct_start=0.03, final_div_factor=100)
    m2 = 2 * MARGIN

    def evaluate():
        model.eval()
        scores = []
        with torch.no_grad():
            for i in range(0, len(lr_val), 64):
                x = lr_val[i:i + 64].float() / 255
                y = hr_val[i:i + 64].float() / 255
                p = model(x)
                scores.append(psnr_y(p[..., m2:-m2, m2:-m2], y[..., m2:-m2, m2:-m2]))
        model.train()
        return sum(scores) / len(scores)

    best = -1.0
    started = time.time()
    model.train()
    for step in range(1, args.iterations + 1):
        index = torch.randint(0, len(lr_train), (args.batch,), device=device)
        x = lr_train[index].float() / 255
        y = hr_train[index].float() / 255
        with torch.autocast("cuda", dtype=torch.bfloat16):
            p = model(x)
        p = p.float()[..., m2:-m2, m2:-m2]
        y = y[..., m2:-m2, m2:-m2]
        loss = (luma(p) - luma(y)).abs().mean() + args.rgb_weight * (p - y).abs().mean()
        optimizer.zero_grad(set_to_none=True)
        loss.backward()
        optimizer.step()
        schedule.step()
        if step % args.eval_every == 0 or step == args.iterations:
            score = evaluate()
            if score > best:
                best = score
                torch.save(model.state_dict(), WORK / f"student-{args.spec}.pt")
            rate = step / (time.time() - started)
            print(f"{args.spec} step {step}: loss {loss.item():.5f}  val PSNR-Y {score:.2f} dB (best {best:.2f})  "
                  f"{rate:.0f} it/s", flush=True)
    print(f"{args.spec}: {params} parameters, best val PSNR-Y {best:.2f} dB against the teacher")


if __name__ == "__main__":
    main()
