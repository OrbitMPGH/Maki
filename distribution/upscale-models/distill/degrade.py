"""
How a clean page crop becomes a reading source: a random history of resizes and lossy saves.

The library's JPEGs were last saved at quality 73 to 99 (median 87, 433 pages sampled), yet many look
far worse, because the visible damage was inherited: a page saved small at low quality, enlarged
(8 px blocks become soft 12 to 16 px smears) and saved again at a decent quality. A file's own
quantisation table only records that last save. The tiers model that history:

  mild    blur, one resize, one save at 55-95 (round two's only tier)
  medium  one earlier generation: smaller, quality 45-75, enlarged back, then the final save
  heavy   one (sometimes two) earlier generations at quality 25-55 with 4:2:0 colour, enlarged back with a
          cheap filter, sometimes sharpened (which deepens the halos), then a final save at 70-95
"""

import io
import random

import numpy as np
from PIL import Image, ImageFilter

FILTERS = [Image.Resampling.BOX, Image.Resampling.BILINEAR, Image.Resampling.BICUBIC, Image.Resampling.LANCZOS]
CHEAP = [Image.Resampling.BILINEAR, Image.Resampling.BICUBIC]
TIERS = (("mild", 0.40), ("medium", 0.35), ("heavy", 0.25))


def _jpeg(image: Image.Image, quality: int, subsampling: int) -> Image.Image:
    buffer = io.BytesIO()
    image.save(buffer, "JPEG", quality=quality, subsampling=subsampling)
    return Image.open(buffer).convert("RGB")


def _webp(image: Image.Image, quality: int) -> Image.Image:
    buffer = io.BytesIO()
    image.save(buffer, "WEBP", quality=quality)
    return Image.open(buffer).convert("RGB")


def _final_save(image: Image.Image, rng: random.Random, low: int) -> Image.Image:
    roll = rng.random()
    if roll < 0.75:
        return _jpeg(image, rng.randint(low, 95), rng.choice([0, 2]))
    if roll < 0.95:
        return _webp(image, rng.randint(max(low, 60), 95))
    return image


def _generation(image: Image.Image, size: tuple[int, int], rng: random.Random, q_low: int, q_high: int,
                shrink: tuple[float, float]) -> Image.Image:
    """One earlier life of the page: saved smaller at a low quality, then enlarged back to `size`."""
    factor = rng.uniform(*shrink)
    small = image.resize((max(8, round(size[0] * factor)), max(8, round(size[1] * factor))), rng.choice(FILTERS))
    small = _jpeg(small, rng.randint(q_low, q_high), 2)
    return small.resize(size, rng.choice(CHEAP))


def pick_tier(rng: random.Random) -> str:
    roll = rng.random()
    for name, share in TIERS:
        if roll < share:
            return name
        roll -= share
    return TIERS[-1][0]


def degrade(hr: Image.Image, rng: random.Random, tier: str | None = None) -> Image.Image:
    """`hr` (RGB) becomes an input half its size with one plausible history of damage."""
    tier = tier or pick_tier(rng)
    w, h = hr.size
    size = (w // 2, h // 2)
    image = hr
    if rng.random() < 0.3:
        image = image.filter(ImageFilter.GaussianBlur(rng.uniform(0.3, 0.9)))
    if tier == "mild":
        if rng.random() < 0.3:
            step = rng.uniform(0.55, 0.85)
            image = image.resize((round(w * step), round(h * step)), rng.choice(FILTERS))
        image = image.resize(size, rng.choice(FILTERS))
        if rng.random() < 0.2:
            image = image.filter(ImageFilter.UnsharpMask(radius=rng.uniform(0.6, 1.5), percent=rng.randint(30, 90), threshold=1))
        roll = rng.random()
        if roll < 0.75:
            return _jpeg(image, rng.randint(55, 95), rng.choice([0, 2]))
        return _webp(image, rng.randint(60, 95)) if roll < 0.95 else image
    image = image.resize(size, rng.choice(FILTERS))
    if tier == "medium":
        image = _generation(image, size, rng, 45, 75, (0.7, 0.95))
    else:
        for _ in range(2 if rng.random() < 0.2 else 1):
            image = _generation(image, size, rng, 25, 55, (0.55, 0.85))
        if rng.random() < 0.4:
            image = image.filter(ImageFilter.UnsharpMask(radius=rng.uniform(0.8, 2.0), percent=rng.randint(40, 120), threshold=1))
        if rng.random() < 0.2:
            noise = np.random.default_rng(rng.randrange(1 << 30)).normal(0, rng.uniform(1, 3), (size[1], size[0], 1))
            image = Image.fromarray(np.clip(np.asarray(image, np.float32) + noise, 0, 255).astype(np.uint8))
    return _final_save(image, rng, 70)


def damage(source: Image.Image, rng: random.Random, tier: str | None = None) -> Image.Image:
    """`source` (a crop already at reading size) made worse at the same size, along the same tiers."""
    tier = tier or pick_tier(rng)
    size = source.size
    image = source
    if tier == "mild":
        if rng.random() < 0.3:
            image = image.filter(ImageFilter.GaussianBlur(rng.uniform(0.3, 0.7)))
        roll = rng.random()
        if roll < 0.8:
            return _jpeg(image, rng.randint(55, 92), rng.choice([0, 2]))
        return _webp(image, rng.randint(60, 92))
    if tier == "medium":
        image = _generation(image, size, rng, 45, 75, (0.7, 0.95))
    else:
        for _ in range(2 if rng.random() < 0.2 else 1):
            image = _generation(image, size, rng, 25, 55, (0.55, 0.85))
        if rng.random() < 0.4:
            image = image.filter(ImageFilter.UnsharpMask(radius=rng.uniform(0.8, 2.0), percent=rng.randint(40, 120), threshold=1))
        if rng.random() < 0.2:
            noise = np.random.default_rng(rng.randrange(1 << 30)).normal(0, rng.uniform(1, 3), (size[1], size[0], 1))
            image = Image.fromarray(np.clip(np.asarray(image, np.float32) + noise, 0, 255).astype(np.uint8))
    return _final_save(image, rng, 70)


# Background workers for training: each draws targets from a memory-mapped array and returns freshly
# damaged inputs, so every use of a clean crop sees a new history.
_targets = None


def worker_init(path: str) -> None:
    global _targets
    _targets = np.load(path, mmap_mode="r")


def worker_batch(job: tuple[int, int]) -> tuple[np.ndarray, np.ndarray]:
    seed, count = job
    rng = random.Random(seed)
    picks = np.array([rng.randrange(len(_targets)) for _ in range(count)])
    inputs = np.stack([np.asarray(degrade(Image.fromarray(np.asarray(_targets[i])), rng)) for i in picks])
    return picks, inputs


def worker_damage(job: tuple[int, int]) -> tuple[np.ndarray, np.ndarray]:
    seed, count = job
    rng = random.Random(seed)
    picks = np.array([rng.randrange(len(_targets)) for _ in range(count)])
    inputs = np.stack([np.asarray(damage(Image.fromarray(np.asarray(_targets[i])), rng)) for i in picks])
    return picks, inputs


def fixed_damage(sources: np.ndarray, tier: str, seed: int) -> np.ndarray:
    rng = random.Random(seed)
    return np.stack([np.asarray(damage(Image.fromarray(s), rng, tier)) for s in sources])


def fixed_inputs(targets: np.ndarray, tier: str, seed: int) -> np.ndarray:
    """A deterministic damaged copy of a validation set, so scores compare across runs."""
    rng = random.Random(seed)
    return np.stack([np.asarray(degrade(Image.fromarray(t), rng, tier)) for t in targets])
