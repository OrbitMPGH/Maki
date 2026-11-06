"""
RESEARCH-PLAN-2 step A2: can this shape commit an edge at all? Fine-tune a checkpoint on one reference
page only (plain L1 on luma, random crops), and every 500 steps print that page's `committed`, `blobs`,
`fill_excess_p95` and PSNR exactly as measure.py scores them. Memorising one page needs no
generalisation, so reaching the teacher's commitment here means the target is inside the shape; staying
short means the shape cannot render the teacher's edges. The weights are thrown away.

    python probe_page.py c48n8 student-c48n8-long.pt --page owner-webtoon
"""

import argparse
import math

import numpy as np
import torch

from bakeoff import tiled, torch_runner
from common import WORK, load_student, load_teacher, luma, margin, student
from evaluate import luma_u8, take_colour_from
from measure import PAD, REFERENCE, TILE, cached_teacher, fill_mask, lanczos_2x, load_page, tail


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("spec")
    parser.add_argument("init")
    parser.add_argument("--page", default="owner-webtoon")
    parser.add_argument("--steps", type=int, default=1500)
    parser.add_argument("--every", type=int, default=500)
    parser.add_argument("--lr", type=float, default=1e-4)
    parser.add_argument("--batch", type=int, default=16)
    args = parser.parse_args()

    device = torch.device("cuda")
    torch.manual_seed(0)
    page = load_page(REFERENCE / "pages" / f"{args.page}.png")
    teacher_rgb = cached_teacher(load_teacher(device), device, page, REFERENCE / "teacher" / f"{args.page}.png")
    s_rgb = lanczos_2x(page)
    t = torch.from_numpy(luma_u8(take_colour_from(teacher_rgb, s_rgb))).float().to(device)
    fill = fill_mask(t)
    target = torch.from_numpy(luma_u8(teacher_rgb)).float().to(device) / 255
    lr_page = torch.from_numpy(page).to(device).permute(2, 0, 1).float() / 255

    model = student(args.spec).to(device)
    load_student(model, torch.load(WORK / args.init, map_location="cpu", weights_only=True))
    optimizer = torch.optim.Adam(model.parameters(), lr=args.lr)
    m, crop = margin(args.spec), 160
    h, w = page.shape[:2]

    def report(step):
        model.eval()
        p_rgb = take_colour_from(tiled(torch_runner(model, device, torch.float32), page, 2, TILE, PAD), s_rgb)
        p = torch.from_numpy(luma_u8(p_rgb)).float().to(device)
        row = tail(p, t, fill)
        mse = ((p - t) ** 2).mean().item()
        print(f"step {step}: committed {row['committed']:.3f} (teacher {row['committed_teacher']:.3f})  "
              f"blobs {row['blobs'] * 1000:.2f}  excess p95 {row['fill_excess_p95']:.2f}  "
              f"psnr {10 * math.log10(255 ** 2 / max(mse, 1e-10)):.2f}", flush=True)
        model.train()

    report(0)
    model.train()
    for step in range(1, args.steps + 1):
        ys = torch.randint(0, h - crop + 1, (args.batch,)).tolist()
        xs = torch.randint(0, w - crop + 1, (args.batch,)).tolist()
        x = torch.stack([lr_page[:, y:y + crop, xx:xx + crop] for y, xx in zip(ys, xs)])
        y = torch.stack([target[2 * y + 2 * m:2 * (y + crop - m), 2 * xx + 2 * m:2 * (xx + crop - m)]
                         for y, xx in zip(ys, xs)])
        with torch.autocast("cuda", dtype=torch.bfloat16):
            out = model(x)
        loss = (luma(out.float())[:, 2 * m:-2 * m, 2 * m:-2 * m] - y).abs().mean()
        optimizer.zero_grad(set_to_none=True)
        loss.backward()
        optimizer.step()
        if step % args.every == 0:
            report(step)


if __name__ == "__main__":
    main()
