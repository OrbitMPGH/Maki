"""
Loss terms beside train_served.py's luma L1, all on N1HW luma in [0, 1]. Reimplemented from the papers,
not imported.

- `ms_ssim`: Wang et al. 2003, three scales here because a scored crop is 96 to 120 low-res px.
  SSIM compares local mean, variance and covariance, so a blotch in a flat fill and a softened edge
  both cost it far more than per-pixel L1 does.
- `sobel_l1`: L1 between Sobel gradients, which charges an edge for being soft rather than misplaced.
- `ldl_map`: LDL's artifact map (Liang et al. 2022): local residual variance over 7x7, scaled per crop
  by the crop's residual variance to the power 1/5, zeroed where the live model already beats its EMA.
  Computed on residuals in levels (0-255), not [0, 1]: a distilled student sits a level or two from its
  target, and on a [0, 1] scale the map is around 1e-6 and the term does nothing.
"""

import torch
import torch.nn.functional as F


def _gauss(size: int = 11, sigma: float = 1.5, device=None) -> torch.Tensor:
    x = torch.arange(size, dtype=torch.float32, device=device) - (size - 1) / 2
    g = torch.exp(-x ** 2 / (2 * sigma ** 2))
    g = g / g.sum()
    return (g[:, None] * g[None, :])[None, None]


def _ssim_parts(x: torch.Tensor, y: torch.Tensor, window: torch.Tensor) -> tuple[torch.Tensor, torch.Tensor]:
    c1, c2 = 0.01 ** 2, 0.03 ** 2
    mx, my = F.conv2d(x, window), F.conv2d(y, window)
    sxx = F.conv2d(x * x, window) - mx * mx
    syy = F.conv2d(y * y, window) - my * my
    sxy = F.conv2d(x * y, window) - mx * my
    cs = (2 * sxy + c2) / (sxx + syy + c2)
    lum = (2 * mx * my + c1) / (mx * mx + my * my + c1)
    return lum, cs


def ms_ssim(x: torch.Tensor, y: torch.Tensor, scales: int = 3) -> torch.Tensor:
    """Mean MS-SSIM of two N1HW batches; weights are the first `scales` of Wang et al.'s five, renormalised."""
    weights = torch.tensor([0.0448, 0.2856, 0.3001, 0.2363, 0.1333][:scales], device=x.device)
    weights = weights / weights.sum()
    window = _gauss(device=x.device)
    value = torch.ones(x.shape[0], device=x.device)
    for i in range(scales):
        lum, cs = _ssim_parts(x, y, window)
        part = (lum * cs if i == scales - 1 else cs).mean(dim=(1, 2, 3)).clamp_min(1e-6)
        value = value * part ** weights[i]
        if i < scales - 1:
            x, y = F.avg_pool2d(x, 2), F.avg_pool2d(y, 2)
    return value.mean()


_SOBEL = torch.tensor([[1., 0, -1], [2, 0, -2], [1, 0, -1]])


def sobel(x: torch.Tensor) -> torch.Tensor:
    k = torch.stack([_SOBEL, _SOBEL.t()])[:, None].to(x.device, x.dtype)
    return F.conv2d(x, k)


def sobel_l1(x: torch.Tensor, y: torch.Tensor) -> torch.Tensor:
    return (sobel(x) - sobel(y)).abs().mean()


def ldl_map(r_live: torch.Tensor, r_ema: torch.Tensor, size: int = 7) -> torch.Tensor:
    """Per-pixel weight for |r_live|; both residuals N1HW in [0, 1] units, no gradient through the map."""
    with torch.no_grad():
        a = r_live.abs() * 255
        mean = F.avg_pool2d(a, size, 1, size // 2, count_include_pad=False)
        local = (F.avg_pool2d(a * a, size, 1, size // 2, count_include_pad=False) - mean * mean).clamp_min(0)
        patch = a.var(dim=(1, 2, 3), keepdim=True) ** 0.2
        weight = patch * local
        weight[r_live.abs() < r_ema.abs()] = 0
    return weight


def _pool(x: torch.Tensor, size: int, kind: str) -> torch.Tensor:
    """Same-size pool with reflect padding on N1HW, as measure.pool does on HW."""
    r = size // 2
    padded = F.pad(x, (r, r, r, r), mode="reflect")
    if kind == "max":
        return F.max_pool2d(padded, size, 1)
    if kind == "min":
        return -F.max_pool2d(-padded, size, 1)
    return F.avg_pool2d(padded, size, 1)


def fill_mask(t255: torch.Tensor, faint: float = 0.0) -> torch.Tensor:
    """measure.fill_mask on an N1HW batch of target luma in levels: not paper white or ink black, and more
    than 5 px from a step of 16 levels or more. 1.0 inside, 0.0 outside. With `faint` (a Sobel threshold,
    e.g. 24), pixels within 2 px of a fainter line are left out too: strand lines in dark hair are fill by
    the 64 rule, and the fill terms diffused them (owner, 2026-11-06)."""
    with torch.no_grad():
        k = _SOBEL.to(t255.device, t255.dtype)
        x = F.pad(t255, (1, 1, 1, 1), mode="reflect")
        g = F.conv2d(x, torch.stack([k, k.t()])[:, None])
        magnitude = (g * g).sum(1, keepdim=True).sqrt()
        ink = _pool((magnitude > 64).float(), 11, "max") > 0.5
        if faint:
            ink |= _pool((magnitude > faint).float(), 5, "max") > 0.5
        mean = _pool(t255, 15, "avg")
        return ((mean > 10) & (mean < 245) & ~ink).float()


def _masked_avg(r: torch.Tensor, m: torch.Tensor, size: int, stride: int) -> tuple[torch.Tensor, torch.Tensor]:
    share = F.avg_pool2d(m, size, stride)
    return F.avg_pool2d(r * m, size, stride) / share.clamp_min(0.1), share


def masked_pooled(r: torch.Tensor, m: torch.Tensor) -> torch.Tensor:
    """The pooled term inside fills only: each 8x8 block's mean residual over its fill pixels, weighted by
    its fill share, so an edge placed a pixel off is never charged as a blotch (RESEARCH-PLAN-2 4.1)."""
    p, share = _masked_avg(r, m, 8, 8)
    return (p.abs() * share).sum() / share.sum().clamp_min(1.0)


def masked_band(r: torch.Tensor, m: torch.Tensor) -> torch.Tensor:
    """The band term (7 px mean minus 31 px mean) over fill pixels only, from fill residuals only."""
    a7, _ = _masked_avg(r, m, 7, 1)
    a31, _ = _masked_avg(r, m, 31, 1)
    b = a7[..., 12:-12, 12:-12] - a31
    w = m[..., 15:-15, 15:-15]
    return (b.abs() * w).sum() / w.sum().clamp_min(1.0)


def gradient_variance(x: torch.Tensor, y: torch.Tensor, patch: int = 8) -> torch.Tensor:
    """Abrahamyan et al. 2022: per 8x8 patch, the variance of the Sobel gradients; a soft edge has too
    little, wherever it sits."""
    gx, gy = sobel(x), sobel(y)
    vx = F.avg_pool2d(gx * gx, patch) - F.avg_pool2d(gx, patch) ** 2
    vy = F.avg_pool2d(gy * gy, patch) - F.avg_pool2d(gy, patch) ** 2
    return ((vx - vy) ** 2).mean()


def top_share(r: torch.Tensor, share: float = 0.2) -> torch.Tensor:
    """Mean of each crop's worst `share` of |residual|: the tails, nearer an L-infinity objective."""
    flat = r.abs().flatten(1)
    return flat.topk(max(1, int(share * flat.shape[1])), dim=1).values.mean()


def coherent_error(r: torch.Tensor, threshold: float = 8 / 255) -> torch.Tensor:
    """Squared excess of the 3x3-averaged |residual| over `threshold`: marks at least a few pixels across that
    are well off the target (measure.py's `marks`), which the p=0.7 power loss prices cheaply because it is
    redescending in exactly the large residuals they are made of. Single-pixel misses average out under it."""
    e = F.avg_pool2d(r.abs(), 3, 1)
    return (F.relu(e - threshold) ** 2).mean()


def power_loss(r: torch.Tensor, p: float, eps: float = 0.5 / 255) -> torch.Tensor:
    """(r^2 + eps^2)^(p/2): for p under 1 it is redescending (Barron 2019), so the student is pulled to the
    most frequent teacher answer rather than the median of them."""
    return ((r * r + eps * eps) ** (p / 2)).mean()


def high_pass(x: torch.Tensor) -> torch.Tensor:
    """x minus its 3x3 mean (reflect padded): the pixel-scale part, static in a fill or a stair step on a line."""
    return x - _pool(x, 3, "avg")


def masked_fine(r: torch.Tensor, m: torch.Tensor) -> torch.Tensor:
    """Mean |high_pass(r)| over the mask. The band and pooled terms work at 3 to 15 px and the base loss is
    a median, so neither charges pixel-scale grain; the owner saw it as static in grey panels (2026-11-06)."""
    return (high_pass(r).abs() * m).sum() / m.sum().clamp_min(1.0)


def edge_band(t255: torch.Tensor, width: int = 5) -> torch.Tensor:
    """1.0 within width // 2 px of a strong edge (Sobel over 64 levels) of the target."""
    with torch.no_grad():
        k = _SOBEL.to(t255.device, t255.dtype)
        g = F.conv2d(F.pad(t255, (1, 1, 1, 1), mode="reflect"), torch.stack([k, k.t()])[:, None])
        strong = ((g * g).sum(1, keepdim=True).sqrt() > 64).float()
        return _pool(strong, width, "max")


def sharpness_deficit_relative(x: torch.Tensor, y: torch.Tensor, low: float = 16 / 255, high: float = 64 / 255) -> torch.Tensor:
    """sharpness_deficit as a share of the target's own gradient, on faint edges only (Sobel in (low, high],
    off the flanks of strong ones): a 30-level strand line then counts as much as a 200-level outline, which
    the absolute term leaves to the strong edges it has already matched (probe_anatomy: strong-edge Sobel
    ratio p50 0.99, faint-edge p25 0.61 to 0.76)."""
    gx = sobel(x)
    gy = sobel(y)
    mx = ((gx * gx).sum(1, keepdim=True) + 1e-8).sqrt()
    my = ((gy * gy).sum(1, keepdim=True) + 1e-8).sqrt()
    near = F.max_pool2d(mx, 3, 1, 1)
    flank = F.max_pool2d((my > high).float(), 7, 1, 3)
    mask = ((my > low) & (my <= high)).float() * (1 - flank)
    return (F.relu(my - near) / my.clamp_min(low) * mask).sum() / mask.sum().clamp_min(1.0)


def sharpness_deficit_line(x: torch.Tensor, y: torch.Tensor, low: float = 16 / 255, high: float = 64 / 255,
                           over: float = 1.15, coherence: float = 0.0) -> torch.Tensor:
    """sharpness_deficit_relative plus a charge when the model's own gradient at the pixel exceeds `over` times
    the target's. At half its quarter weight the one-sided relative term lifted the faint lower quartile from
    0.61 to 0.82 but took the mean to 1.14 to 1.27 of the teacher's and owner-page blobs up 45% (probe3
    relsharp): it is cheapest to satisfy by sharpening every faint gradient, and most faint pixels sit beside
    creases. The excess charge makes broad sharpening cost. `coherence` gates on structure-tensor coherence
    (5x5); measured on the reference pages it passes fill texture about as often as lines, so it is off."""
    gx = sobel(x)
    gy = sobel(y)
    mx = ((gx * gx).sum(1, keepdim=True) + 1e-8).sqrt()
    my = ((gy * gy).sum(1, keepdim=True) + 1e-8).sqrt()
    with torch.no_grad():
        jxx = _pool(gy[:, :1] * gy[:, :1], 5, "avg")
        jyy = _pool(gy[:, 1:] * gy[:, 1:], 5, "avg")
        jxy = _pool(gy[:, :1] * gy[:, 1:], 5, "avg")
        coh = ((jxx - jyy) ** 2 + 4 * jxy * jxy).sqrt() / (jxx + jyy + 1e-8)
        flank = F.max_pool2d((my > high).float(), 7, 1, 3)
        mask = ((my > low) & (my <= high) & (coh > coherence)).float() * (1 - flank)
    near = F.max_pool2d(mx, 3, 1, 1)
    scale = my.clamp_min(low)
    return ((F.relu(my - near) + F.relu(mx - over * my)) / scale * mask).sum() / mask.sum().clamp_min(1.0)


def thin_line_deficit(x: torch.Tensor, y: torch.Tensor, contrast: float = 24 / 255, max_count: int = 8,
                      over: float = 1.1) -> torch.Tensor:
    """On the target's thin dark lines (a 5x5 local minimum at least `contrast` under the 7x7 mean, with at most
    `max_count` such pixels in the 5x5), how much shallower the model's line is: the target's depth (7x7 mean
    minus the pixel) less the model's depth at its 3x3 minimum, as a share of the target's. A line drawn a
    pixel off costs nothing; a lighter one does (probe_strong: thin lines at any contrast are the learnable
    part of the strong-edge tail), and so does one deeper than `over` times the target's: one-sided, at half
    its quarter weight, the term drew lines 8 to 17% deeper than the teacher's (probe3 thin)."""
    with torch.no_grad():
        a7 = _pool(y, 7, "avg")
        dark = y < a7 - contrast
        local_min = y <= _pool(y, 5, "min") + 0.5 / 255
        count = _pool(dark.float(), 5, "avg") * 25
        mask = (dark & local_min & (count <= max_count)).float()
        depth_t = (a7 - y).clamp_min(contrast)
    depth_p = a7 - _pool(x, 3, "min")
    return ((F.relu(depth_t - depth_p) + F.relu(depth_p - over * depth_t)) / depth_t * mask).sum() / mask.sum().clamp_min(1.0)


def sharpness_deficit(x: torch.Tensor, y: torch.Tensor, threshold: float = 16 / 255) -> torch.Tensor:
    """On the target's edges (Sobel over `threshold`, faint ones included), how much less steep the model
    is than the target, against the model's steepest gradient within 1 px: a blurred line is charged, a
    sharp line a pixel off is not, so this cannot reward hedging the way an L1 on gradients does."""
    gx = sobel(x)
    gy = sobel(y)
    mx = ((gx * gx).sum(1, keepdim=True) + 1e-8).sqrt()  # the eps keeps sqrt's gradient finite at 0
    my = ((gy * gy).sum(1, keepdim=True) + 1e-8).sqrt()
    near = F.max_pool2d(mx, 3, 1, 1)
    mask = (my > threshold).float()
    return (F.relu(my - near) * mask).sum() / mask.sum().clamp_min(1.0)
