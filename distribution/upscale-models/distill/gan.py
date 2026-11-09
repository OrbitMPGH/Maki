"""
Real-ESRGAN's stage 2 for distillation: a discriminator that learns to tell the teacher's output from the
student's, so the student commits to a flat fill or a crisp edge where an L1 loss lets it hedge.

Ours, not theirs. A small U-Net with spectral norm and a per-pixel answer, like their UNetDiscriminatorSN,
but 32 features and two downsamples (theirs is 64 and three, sized for 4x photos), on luma only (all
Maki keeps) and conditioned on the input, so it judges "is this what the teacher makes of this source"
and cannot reward a sharp edge that the source does not have. Hinge loss.
"""

import torch
import torch.nn as nn
import torch.nn.functional as F
from torch.nn.utils import spectral_norm


class Discriminator(nn.Module):
    def __init__(self, feat: int = 32, gain: float = 8.0):
        super().__init__()
        # The student sits about 41 dB from the teacher, so their difference is a level or two. With spectral
        # norm on the inner layers a discriminator fed raw luma cannot grow that into a +-1 logit: the first
        # run sat at the hinge loss of a constant output (d 1.998) for 2,500 steps. So it sees the detail
        # over the bilinear source, amplified, beside the source itself.
        self.gain = gain
        sn = spectral_norm
        self.inp = nn.Conv2d(2, feat, 3, 1, 1)
        self.down1 = sn(nn.Conv2d(feat, feat * 2, 4, 2, 1, bias=False))
        self.down2 = sn(nn.Conv2d(feat * 2, feat * 4, 4, 2, 1, bias=False))
        self.up1 = sn(nn.Conv2d(feat * 4, feat * 2, 3, 1, 1, bias=False))
        self.up2 = sn(nn.Conv2d(feat * 2, feat, 3, 1, 1, bias=False))
        self.mid = sn(nn.Conv2d(feat, feat, 3, 1, 1, bias=False))
        self.out = nn.Conv2d(feat, 1, 3, 1, 1)

    def forward(self, y: torch.Tensor, condition: torch.Tensor) -> torch.Tensor:
        """`y` is 2x luma, N1HW in [0, 1]; `condition` the source luma bilinearly upscaled to match."""
        act = lambda t: F.leaky_relu(t, 0.2)
        x0 = act(self.inp(torch.cat([(y - condition) * self.gain, condition * 2 - 1], 1)))
        x1 = act(self.down1(x0))
        x2 = act(self.down2(x1))
        u = F.interpolate(x2, size=x1.shape[-2:], mode="bilinear", align_corners=False)
        u = act(self.up1(u)) + x1
        u = F.interpolate(u, size=x0.shape[-2:], mode="bilinear", align_corners=False)
        u = act(self.up2(u)) + x0
        return self.out(act(self.mid(u)))


def _mean(x: torch.Tensor, mask: torch.Tensor | None) -> torch.Tensor:
    return x.mean() if mask is None else (x * mask).sum() / mask.sum().clamp_min(1.0)


def _noisy(x: torch.Tensor, noise: float) -> torch.Tensor:
    """Instance noise of +-noise (in [0, 1] units): the real target is stored as whole levels and the fake is
    continuous, a difference a discriminator could learn instead of edges and fills."""
    return x + (torch.rand_like(x) * 2 - 1) * noise if noise else x


def d_loss(d: Discriminator, real: torch.Tensor, fake: torch.Tensor, condition: torch.Tensor,
           mask: torch.Tensor | None = None, noise: float = 0.0) -> torch.Tensor:
    """Hinge loss, averaged over `mask` (the pixels that can differ) when given."""
    return (_mean(F.relu(1 - d(_noisy(real, noise), condition)), mask)
            + _mean(F.relu(1 + d(_noisy(fake.detach(), noise), condition)), mask))


def g_loss(d: Discriminator, fake: torch.Tensor, condition: torch.Tensor,
           mask: torch.Tensor | None = None, noise: float = 0.0) -> torch.Tensor:
    return -_mean(d(_noisy(fake, noise), condition), mask)


@torch.no_grad()
def logit_gap(d: Discriminator, real: torch.Tensor, fake: torch.Tensor, condition: torch.Tensor,
              mask: torch.Tensor | None = None) -> tuple[float, float]:
    """RESEARCH-PLAN-2's learning test: mean D(real) - mean D(fake) over the mask, and the share of masked
    pixels where D(real) > D(fake). The hinge loss cannot show learning at 41 dB, these can."""
    a, b = d(real, condition), d(fake, condition)
    m = torch.ones_like(a) if mask is None else mask
    return float(_mean(a - b, m)), float(((a > b).float() * m).sum() / m.sum().clamp_min(1.0))
