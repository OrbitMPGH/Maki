"""
Flattens screentone in a clean page while leaving line art alone, so a training target carries one
answer for tone: the flat grey Real-ESRGAN gives on library sources.

1. Notch. Screentone is periodic, so in a window's spectrum it is a handful of sharp, isolated peaks
   away from the centre. A straight line (a panel border, a hatching stroke, a letter's stem) is a
   streak through the centre instead: its energy continues along the radial direction. A bin is
   notched only when it stands well above its neighbourhood, is a local maximum, sits at a period
   short enough to be tone, and its radial neighbours are weak. Dot tone is a 2D lattice, so a peak
   with a partner at the same radius 60, 90 or 120 degrees round is tone. A lone peak is a line
   pattern and counts only when its period is short and it is not a harmonic of a coarser pattern,
   so ruled backgrounds and speed lines are never half-notched.
2. The notch leaves grain, so the notched page is not the answer. It only says where tone is (the
   share of each window's power that was notched) and gives a page in which strokes stand out.
3. Strokes are found in the notched page with oriented segment filters, each direction measured
   against a robust estimate of that direction's own leftover grain, plus hysteresis to follow a
   stroke through the denser parts of its tone.
4. Tone pixels are replaced with a local mean that excludes stroke pixels (normalised convolution).
   Stroke pixels keep their ink: each is unmixed into the share of it the ink covers, averaged
   along the stroke, so dots it crosses do not show as bumps and the stroke keeps its width.
   Solid black, bare paper and everything outside tone stay exactly as they were.
5. Tone that explains only part of an area's detail (tone under drawn texture, dense hatching,
   irregular dash tone) is left whole rather than smeared, decided over a wide area so a region is
   not flattened in patches.

Built for crops of a few hundred pixels with real context around them. Larger inputs are processed in
overlapping tiles so memory stays at a few hundred MB whatever the size.
"""

import functools
import math

import torch
import torch.nn.functional as F

WINDOW = 128
STRIDE = 32
TILE = 512
TILE_MARGIN = 64
DIRECTIONS = 12


@functools.lru_cache(maxsize=8)
def _spectrum_constants(device: str, max_period: float, line_period: float, width: float):
    dev = torch.device(device)
    c = WINDOW // 2
    yy, xx = torch.meshgrid(torch.arange(WINDOW, device=dev), torch.arange(WINDOW, device=dev), indexing="ij")
    dy, dx = (yy - c).float(), (xx - c).float()
    radius = torch.hypot(dy, dx)

    def at(ny, nx):
        return (ny.round().long().clamp(0, WINDOW - 1) * WINDOW + nx.round().long().clamp(0, WINDOW - 1)).reshape(-1)

    r = radius.clamp(min=1e-6)
    outer = at(yy + dy / r * 3, xx + dx / r * 3)
    inner = at(yy - dy / r * 3, xx - dx / r * 3)
    rotations = []
    for deg in (60.0, 90.0, 120.0):
        t = math.radians(deg)
        rotations.append(at(c + dy * math.cos(t) + dx * math.sin(t), c - dy * math.sin(t) + dx * math.cos(t)))
    half, third = at(c + dy / 2, c + dx / 2), at(c + dy / 3, c + dx / 3)
    hann1 = torch.hann_window(WINDOW, periodic=False, device=dev)
    k = int(math.ceil(3 * width))
    g = torch.exp(-torch.arange(-k, k + 1, device=dev, dtype=torch.float32) ** 2 / (2 * width ** 2))
    return {
        "hann": torch.outer(hann1, hann1),
        "tone_band": radius >= WINDOW / max_period,
        "short_ok": (radius >= WINDOW / line_period).reshape(-1)[None],
        "outer": outer, "inner": inner, "rotations": rotations, "half": half, "third": third,
        "spread_kernel": (g[:, None] * g[None, :])[None, None], "k": k,
    }


@functools.lru_cache(maxsize=8)
def _segments(length: int, device: str) -> torch.Tensor:
    """(DIRECTIONS, 1, L, L) kernels that average along a centred segment at evenly spread angles."""
    r = length // 2
    ks = torch.zeros(DIRECTIONS, 2 * r + 1, 2 * r + 1)
    for i in range(DIRECTIONS):
        th = math.pi * i / DIRECTIONS
        for s in torch.linspace(-r, r, 4 * r + 1).tolist():
            y, x = r + s * math.sin(th), r + s * math.cos(th)
            y0, x0 = int(math.floor(y)), int(math.floor(x))
            fy, fx = y - y0, x - x0
            for oy, ox, wgt in ((0, 0, (1 - fy) * (1 - fx)), (1, 0, fy * (1 - fx)), (0, 1, (1 - fy) * fx), (1, 1, fy * fx)):
                if 0 <= y0 + oy <= 2 * r and 0 <= x0 + ox <= 2 * r:
                    ks[i, y0 + oy, x0 + ox] += wgt
    ks /= ks.sum(dim=(1, 2), keepdim=True)
    return ks[:, None].to(device)


def _notch(grey: torch.Tensor, max_period: float, ratio: float, streak: float, width: float,
           line_period: float) -> tuple[torch.Tensor, torch.Tensor]:
    """The page with tone peaks notched out, and per pixel the share of tone-band power notched."""
    cs = _spectrum_constants(str(grey.device), max_period, line_period, width)
    hann, tone_band = cs["hann"], cs["tone_band"]
    h, w = grey.shape
    pad = WINDOW
    padded = F.pad(grey[None, None], (pad, pad, pad, pad), mode="reflect")[0, 0]
    ph, pw = padded.shape
    ph -= (ph - WINDOW) % STRIDE
    pw -= (pw - WINDOW) % STRIDE
    padded = padded[:ph, :pw]

    nx = (pw - WINDOW) // STRIDE + 1
    hann_cols = (hann * hann).reshape(-1, 1)
    strip_weight = F.fold(hann_cols.expand(-1, nx)[None], (WINDOW, pw), WINDOW, stride=STRIDE)[0, 0]
    out = torch.zeros((ph, pw), device=grey.device)
    score_map = torch.zeros((ph, pw), device=grey.device)
    weight = torch.zeros((ph, pw), device=grey.device)
    for y in range(0, ph - WINDOW + 1, STRIDE):
        t = padded[y:y + WINDOW].unfold(1, WINDOW, STRIDE).permute(1, 0, 2)  # (nx, W, W)
        means = t.mean(dim=(1, 2), keepdim=True)
        spec = torch.fft.fftshift(torch.fft.fft2((t - means) * hann), dim=(1, 2))
        mag = spec.abs()
        background = F.avg_pool2d(mag[:, None], 9, stride=1, padding=4, count_include_pad=False)[:, 0]
        strong = mag > ratio * (background + 1e-3)
        local_max = F.max_pool2d(mag[:, None], 3, stride=1, padding=1)[:, 0] <= mag
        flat = mag.reshape(nx, -1)
        radial = torch.maximum(flat[:, cs["outer"]], flat[:, cs["inner"]]).reshape(mag.shape)
        peaks = strong & local_max & tone_band & (radial < streak * mag)
        near = F.max_pool2d(peaks[:, None].float(), 5, stride=1, padding=2)[:, 0].reshape(nx, -1)
        paired = torch.stack([near[:, idx] for idx in cs["rotations"]]).amax(0)
        strong_near = F.max_pool2d(strong[:, None].float(), 3, stride=1, padding=1)[:, 0].reshape(nx, -1)
        harmonic = torch.maximum(strong_near[:, cs["half"]], strong_near[:, cs["third"]])
        lone_ok = cs["short_ok"] & (harmonic < 0.5)
        peaks = peaks & ((paired > 0.5) | lone_ok).reshape(peaks.shape)
        notch = F.conv2d(peaks[:, None].float(), cs["spread_kernel"], padding=cs["k"])[:, 0].clamp(max=1)
        power = mag * mag * tone_band
        score = (power * notch).sum(dim=(1, 2)) / power.sum(dim=(1, 2)).clamp(min=1e-3)
        score_map[y:y + WINDOW] += F.fold((score[None] * hann_cols)[None], (WINDOW, pw), WINDOW, stride=STRIDE)[0, 0]
        cleaned = torch.fft.ifft2(torch.fft.ifftshift(spec * (1 - notch), dim=(1, 2))).real
        cols = ((cleaned + means * hann) * hann).reshape(nx, -1).T[None]
        out[y:y + WINDOW] += F.fold(cols, (WINDOW, pw), WINDOW, stride=STRIDE)[0, 0]
        weight[y:y + WINDOW] += strip_weight
    weight = weight.clamp(min=1e-6)
    return (out / weight)[pad:pad + h, pad:pad + w].clamp(0, 255), (score_map / weight)[pad:pad + h, pad:pad + w]


def _blur(x: torch.Tensor, sigma: float) -> torch.Tensor:
    """Separable Gaussian over each channel of (N, C, H, W), reflected edges."""
    k = max(1, int(math.ceil(3 * sigma)))
    g = torch.exp(-torch.arange(-k, k + 1, device=x.device, dtype=torch.float32) ** 2 / (2 * sigma * sigma))
    g = g / g.sum()
    n, c, h, w = x.shape
    x = F.pad(x.reshape(n * c, 1, h, w), (k, k, k, k), mode="reflect")
    return F.conv2d(F.conv2d(x, g.view(1, 1, 1, -1)), g.view(1, 1, -1, 1)).reshape(n, c, h, w)


def _nc(x: torch.Tensor, w: torch.Tensor, sigma: float) -> tuple[torch.Tensor, torch.Tensor]:
    """Normalised convolution: the w-weighted local mean of x, and the local weight."""
    both = _blur(torch.cat([x * w, w], 0), sigma)
    return both[:1] / both[1:].clamp(min=1e-4), both[1:]


def _smooth(x: torch.Tensor, lo, hi) -> torch.Tensor:
    t = ((x - lo) / (hi - lo)).clamp(0, 1)
    return t * t * (3 - 2 * t)


# Gaussian |x| quantile to standard deviation.
_QSCALE = {0.3: 2.598, 0.5: 1.4826}


def _local_scale(x: torch.Tensor, mask: torch.Tensor, span: int, q: float) -> torch.Tensor:
    """Robust spread of x per channel: a low quantile of |x| over masked pixels of a 9x9 grid of
    samples `span` apart, evaluated every 4 px. A low quantile ignores the strokes in a busy area."""
    n, c, h, w = x.shape
    v = torch.where(mask > 0.5, x.abs(), torch.full_like(x, float("nan")))
    r = 4 * span
    v = F.pad(v, (r, r, r, r), mode="replicate")
    cols = F.unfold(v.reshape(n * c, 1, *v.shape[-2:]), 9, dilation=span, stride=4)
    est = cols.nanmedian(dim=1).values if q == 0.5 else cols.nanquantile(q, dim=1)
    gh, gw = (h + 2 * r - 8 * span - 1) // 4 + 1, (w + 2 * r - 8 * span - 1) // 4 + 1
    est = torch.nan_to_num(est.reshape(n * c, 1, gh, gw), nan=1e4)
    est = F.interpolate(est, size=(h, w), mode="bilinear", align_corners=False)
    return _QSCALE[q] * est.reshape(n, c, h, w)


def _global_scale(x: torch.Tensor, mask: torch.Tensor, q: float = 0.3) -> torch.Tensor:
    """The same over the whole input, per channel, as (1, C, 1, 1): caps the local estimate where an
    area is so busy with strokes that even a low quantile lands on them."""
    v = x[..., ::2, ::2].abs().flatten(2)
    m = (mask[..., ::2, ::2] > 0.5).flatten(2).expand_as(v)
    v = torch.where(m, v, torch.full_like(v, float("nan")))
    return _QSCALE[q] * torch.nan_to_num(v.nanquantile(q, dim=2), nan=1e4)[..., None, None]


def _directional(x: torch.Tensor, length: int) -> torch.Tensor:
    kern = _segments(length, str(x.device))
    r = kern.shape[-1] // 2
    return F.conv2d(F.pad(x, (r, r, r, r), mode="reflect"), kern)


def _descreen_tile(grey, max_period, line_period, ratio, streak, width, flat_sigma, line_k, long_k, grow_k,
                   expl_lo, expl_hi, debug):
    g = grey[None, None].float()
    base, score = _notch(grey.float(), max_period, ratio, streak, width, line_period)
    base, score = base[None, None], score[None, None]
    p = g - base

    # Solid ink and bare paper: areas wider than any tone period that stay dark or light throughout.
    def opening(m):
        return F.max_pool2d(-F.max_pool2d(-m, 11, 1, 5), 11, 1, 5)
    solid = torch.maximum(opening((g < 40).float()), opening((g > 220).float()))
    rim = F.max_pool2d(solid, 3, 1, 1) - solid

    # Tone-like pixels: the notched part explains their detail, and they sit at the local tone level.
    ones = torch.ones_like(base)
    hp0 = g - _nc(base, ones, flat_sigma)[0]
    num, _ = _nc((hp0 - p) ** 2, ones, 2.0)
    den, _ = _nc(hp0 * hp0, ones, 2.0)
    like = _smooth(1 - num / den.clamp(min=1e-3), 0.2, 0.5) * _smooth(den.sqrt(), 5.0, 10.0) * (1 - solid)

    def level(wgt):
        near, wl = _nc(base, wgt, 3.0)
        return torch.where(wl > 0.05, near, _nc(base, wgt, 12.0)[0])

    resid = base - level(like)
    spread = torch.minimum(_local_scale(resid, like, 4, 0.5), _global_scale(resid, like)).clamp(max=200)
    pix = like * (1 - _smooth(resid.abs(), 2.5 * spread + 5, 4 * spread + 10))
    detail = base - level(pix)

    # Strokes: along some direction the notched page stays off the tone level by more than that
    # direction's own leftover grain. Seeds are strong short marks or anything that holds over a
    # long segment; hysteresis follows them through weaker stretches.
    floor = 30.0
    resp = _directional(detail, 9)
    noise = torch.minimum(_local_scale(resp, pix.expand_as(resp), 8, 0.3),
                          3 * _global_scale(resp, pix.expand_as(resp))).clamp(min=1.0, max=500)
    z = resp.abs() / torch.maximum(noise, torch.full_like(noise, floor / line_k))
    zs, best = z.max(1, keepdim=True)
    dark_stroke = resp.gather(1, best) < 0
    resp_l = _directional(detail, 17)
    noise_l = torch.minimum(_local_scale(resp_l, pix.expand_as(resp_l), 8, 0.3),
                            3 * _global_scale(resp_l, pix.expand_as(resp_l))).clamp(min=1.0, max=500)
    zl = (resp_l.abs() / torch.maximum(noise_l, torch.full_like(noise_l, 0.6 * floor / long_k))).amax(1, keepdim=True)
    seed = torch.maximum(_smooth(zs, line_k, line_k * 1.5), _smooth(zl, long_k, long_k * 1.5))
    weak = (zs > grow_k).float()
    grown = (seed > 0.5).float()
    for _ in range(4):
        grown = F.max_pool2d(grown, 3, 1, 1) * weak
    line = F.max_pool2d(torch.maximum(seed, grown), 3, 1, 1)
    keep = 1 - line

    # Tone mask: periodic windows with real detail, where the periodic part explains most of it.
    flat0, _ = _nc(base, keep, flat_sigma)
    energy, _ = _nc((g - flat0) ** 2, pix, 2.0)
    tone = _smooth(score, 0.15, 0.35) * _smooth(energy.sqrt(), 5.0, 10.0)
    flat, wsum = _nc(base, keep * tone * pix, flat_sigma)
    hp1 = g - flat
    num, _ = _nc((hp1 - p) ** 2, keep, 24.0)
    den, _ = _nc(hp1 * hp1, keep, 24.0)
    tone = tone * _smooth(1 - num / den.clamp(min=1e-3), expl_lo, expl_hi)
    wide, _ = _nc(base, keep * tone * pix, flat_sigma * 3)
    flat = torch.where(wsum > 0.05, flat, wide)
    tone = tone * _smooth(_blur(tone * pix, flat_sigma), 0.05, 0.2)

    # Ink: each stroke pixel covers a share of the tone predicted under it; that share is averaged
    # along the stroke so the dots it crosses take their neighbours' value.
    t = flat + p
    ink = torch.where(dark_stroke, torch.zeros_like(g), torch.full_like(g, 255.0))
    span = t - ink
    a_raw = ((t - g) / torch.where(span.abs() < 1, torch.ones_like(span), span)).clamp(-0.5, 1.5)
    conf = (span / 255) ** 2
    both = _directional(torch.cat([a_raw * conf, conf], 0), 5)
    num, den = both[:1].gather(1, best), both[1:].gather(1, best)
    a = torch.where(den > 0.01, num / den.clamp(min=1e-4), _nc(a_raw, conf, 0.8)[0])
    a = a.clamp(0, 1) * _smooth(a, 0.3, 0.55) * torch.maximum(line, 1 - _blur(like, 1.0))
    edge = ((flat - g) / torch.where((flat - ink).abs() < 1, torch.ones_like(g), flat - ink)).clamp(0, 1)
    a = torch.where(rim > 0, edge, a)
    inner = torch.where(solid > 0, g, flat + a * (ink - flat))
    out = (g + tone * (inner - g))[0, 0].clamp(0, 255)
    if debug:
        return out, tone[0, 0], line[0, 0], a[0, 0]
    return out


@torch.no_grad()
def descreen(grey: torch.Tensor, max_period: float = 7.0, line_period: float = 5.0, ratio: float = 4.0,
             streak: float = 0.5, width: float = 2.0, flat_sigma: float = 4.5, line_k: float = 6.0,
             long_k: float = 6.0, grow_k: float = 4.0, expl_lo: float = 0.5, expl_hi: float = 0.65,
             debug: bool = False):
    """`grey` (H, W) float 0-255 on any device → the same page with periodic tone flattened.
    With `debug`, also returns the tone mask, the stroke mask and the ink coverage."""
    args = (max_period, line_period, ratio, streak, width, flat_sigma, line_k, long_k, grow_k, expl_lo, expl_hi)
    h, w = grey.shape
    if h <= TILE + 2 * TILE_MARGIN and w <= TILE + 2 * TILE_MARGIN:
        return _descreen_tile(grey, *args, debug)
    outs = None
    for y in range(0, h, TILE):
        for x in range(0, w, TILE):
            y0, x0 = max(0, y - TILE_MARGIN), max(0, x - TILE_MARGIN)
            y1, x1 = min(h, y + TILE + TILE_MARGIN), min(w, x + TILE + TILE_MARGIN)
            r = _descreen_tile(grey[y0:y1, x0:x1], *args, debug)
            r = r if isinstance(r, tuple) else (r,)
            if outs is None:
                outs = [torch.zeros((h, w), device=grey.device) for _ in r]
            ty, tx = min(TILE, h - y), min(TILE, w - x)
            for o, part in zip(outs, r):
                o[y:y + ty, x:x + tx] = part[y - y0:y - y0 + ty, x - x0:x - x0 + tx]
    return tuple(outs) if debug else outs[0]
