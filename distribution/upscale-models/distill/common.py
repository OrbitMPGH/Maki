"""Shared pieces of the Real-ESRGAN distillation: the teacher, the student and Maki's luma."""

import os
from pathlib import Path

import numpy as np
import torch
import torch.nn as nn
from PIL import Image

REPO = Path(__file__).resolve().parents[3]


def _main_checkout(repo: Path) -> Path:
    # In a worktree `.git` is a file naming <main>/.git/worktrees/<name>.
    git = repo / ".git"
    if git.is_file():
        return Path(git.read_text().split(":", 1)[1].strip()).resolve().parents[2]
    return repo


MAIN = _main_checkout(REPO)  # the main checkout, where .simulated lives
# Data, checkpoints, logs and the venv live on X: (94 GB; the owner moved them out of .artifacts on
# 2026-11-05). MAKI_DISTILL_WORK overrides.
WORK = Path(os.environ.get("MAKI_DISTILL_WORK", "X:/MakiModel"))
TEACHER_PTH = WORK / "teacher" / "realesr-animevideov3.pth"

# Context the loss ignores at a crop's edge. The teacher's 18 stacked 3x3 convs see 18 px of low-res
# context, and zero padding at a crop edge is not what Maki's reflected page context gives it.
MARGIN = 20


def _pad_with(y: torch.Tensor, bias: torch.Tensor) -> torch.Tensor:
    """Pad by 1 with each channel's bias rather than zero, so a 1x1 then 3x3 pair folds into one 3x3 exactly."""
    b = bias.view(1, -1, 1, 1)
    return nn.functional.pad(y - b, (1, 1, 1, 1)) + b


class ECB(nn.Module):
    """ECBSR's edge-oriented block (Zhang et al. 2021), BN-free: a 3x3, a 1x1 expand to 2C then 3x3, and
    scaled Sobel-x, Sobel-y and Laplacian behind 1x1s. Training-time capacity only: `fused()` folds it into
    one 3x3, which is all the exported model runs."""

    def __init__(self, cin: int, cout: int):
        super().__init__()
        self.conv3 = nn.Conv2d(cin, cout, 3, 1, 1)
        self.expand = nn.Conv2d(cin, 2 * cout, 1)
        self.squeeze = nn.Conv2d(2 * cout, cout, 3)
        self.edge_in = nn.Conv2d(cin, 3 * cout, 1)
        self.edge_scale = nn.Parameter(torch.randn(3, cout) * 1e-3)
        self.edge_bias = nn.Parameter(torch.zeros(3, cout))
        sobel_x = torch.tensor([[1., 0, -1], [2, 0, -2], [1, 0, -1]])
        laplace = torch.tensor([[0., 1, 0], [1, -4, 1], [0, 1, 0]])
        self.register_buffer("masks", torch.stack([sobel_x, sobel_x.t(), laplace]))
        self.cout = cout

    def zero_branches(self):
        """Start the extra branches at exactly zero, so a plain checkpoint loaded into conv3 is unchanged."""
        for t in (self.squeeze.weight, self.squeeze.bias, self.edge_scale, self.edge_bias):
            nn.init.zeros_(t)

    def branches(self):
        """The branched block run as itself (slow); kept to check that `weights()` folds it exactly."""
        def run(x):
            y = self.conv3(x) + self.squeeze(_pad_with(self.expand(x), self.expand.bias))
            z = _pad_with(self.edge_in(x), self.edge_in.bias)
            for k in range(3):
                w = (self.edge_scale[k].view(-1, 1, 1, 1) * self.masks[k]).expand(self.cout, 1, 3, 3)
                y = y + nn.functional.conv2d(z[:, k * self.cout:(k + 1) * self.cout], w, self.edge_bias[k], groups=self.cout)
            return y
        return run

    def weights(self) -> tuple[torch.Tensor, torch.Tensor]:
        """Every branch is linear, so the block is one 3x3 whose weight is a function of the branch
        parameters. Training through this fold is the same function and the same gradients as running the
        branches, at the cost of a plain conv. Folded in fp32 even under autocast."""
        if torch.is_autocast_enabled():
            with torch.autocast("cuda", enabled=False):
                return self.weights()
        w = self.conv3.weight + torch.einsum("odkl,di->oikl", self.squeeze.weight, self.expand.weight[:, :, 0, 0])
        b = self.conv3.bias + self.squeeze.bias + torch.einsum("odkl,d->o", self.squeeze.weight, self.expand.bias)
        m = self.edge_in.weight[:, :, 0, 0].view(3, self.cout, -1)
        mb = self.edge_in.bias.view(3, self.cout)
        w = w + torch.einsum("ko,kxy,koi->oixy", self.edge_scale, self.masks, m)
        b = b + self.edge_bias.sum(0) + (self.edge_scale * self.masks.sum((1, 2))[:, None] * mb).sum(0)
        return w, b

    def forward(self, x):
        w, b = self.weights()
        return nn.functional.conv2d(x, w, b, padding=1)

    @torch.no_grad()
    def fused(self) -> nn.Conv2d:
        w, b = self.weights()
        conv = nn.Conv2d(w.shape[1], w.shape[0], 3, 1, 1).to(w.device)
        conv.weight.copy_(w)
        conv.bias.copy_(b)
        return conv


class Compact(nn.Module):
    """SRVGGNetCompact: conv/activation stack, pixel shuffle, plus a nearest-upsampled copy of the input.

    `unshuffle=2` is Real-ESRGAN's own trick for its 2x models (section 3.4): pixel-unshuffle the input so
    the body runs at half resolution, with four times the channels per unit of cost and twice the reach
    per layer, then pixel-shuffle by 2 * scale back out."""

    def __init__(self, channels: int | list[int], convs: int, scale: int, act: str = "prelu",
                 dilations: list[int] | None = None, ecb: bool = False, unshuffle: int = 1):
        super().__init__()
        self.scale = scale
        dilations = dilations or [1] * convs
        # One width per conv before the head: the first conv, then each body conv. A list widens part of the
        # stack (`c64_4_48n8`: the first four at 64, the rest at 48).
        widths = [channels] * (convs + 1) if isinstance(channels, int) else list(channels)
        assert len(widths) == convs + 1, (widths, convs)
        self.widths = widths

        def activation(c):
            if act == "prelu":
                return nn.PReLU(c)
            if act == "leaky":
                return nn.LeakyReLU(0.1, inplace=True)
            raise ValueError(act)

        def conv(cin, cout, d=1):
            if ecb:
                assert d == 1, "ECB branches are not dilated"
                return ECB(cin, cout)
            return nn.Conv2d(cin, cout, 3, 1, d, dilation=d)

        u = unshuffle
        layers = ([nn.PixelUnshuffle(u)] if u > 1 else []) + [conv(3 * u * u, widths[0]), activation(widths[0])]
        for i, d in enumerate(dilations):
            layers += [conv(widths[i], widths[i + 1], d), activation(widths[i + 1])]
        out_scale = scale * u
        layers += [nn.Conv2d(widths[convs], 3 * out_scale * out_scale, 3, 1, 1), nn.PixelShuffle(out_scale)]
        self.body = nn.ModuleList(layers)

    def forward_features(self, x):
        """The output and the last body activation (the final conv's input), for feature distillation."""
        out = x
        for i, layer in enumerate(self.body):
            if i == len(self.body) - 2:
                feats = out
            out = layer(out)
        return out + nn.functional.interpolate(x, scale_factor=self.scale, mode="nearest"), feats

    def forward(self, x):
        return self.forward_features(x)[0]


class Hybrid(Compact):
    """RESEARCH-PLAN-2 step D: long's full-resolution trunk for edges, plus a half-resolution side branch for
    context in fills. After the trunk's third body conv the features are 2x2 average-pooled, run through
    `branch_convs` 3x3 convs of `branch_channels`, projected back by a 1x1 that starts at zero (so a
    trunk checkpoint loads and step 0 is that checkpoint exactly), bilinearly upsampled and added after
    the fourth. Bilinear upsampling of a smooth side signal has no sub-pixel weights, unlike a
    pixel-unshuffle body, and every op stays in ONNX Runtime's blocked layout (20.1 ms a tile, 1.21x)."""

    TAP, JOIN = 7, 9  # body indices: after body conv 3's and body conv 4's activations

    def __init__(self, channels: int | list[int], convs: int, branch_channels: int, branch_convs: int, ecb: bool = False):
        super().__init__(channels, convs, 2, "leaky", ecb=ecb)
        # The branch reads the trunk after body conv 3 and writes back after body conv 4.
        layers = [nn.AvgPool2d(2), nn.Conv2d(self.widths[3], branch_channels, 3, 1, 1), nn.LeakyReLU(0.1, inplace=True)]
        for _ in range(branch_convs - 1):
            layers += [nn.Conv2d(branch_channels, branch_channels, 3, 1, 1), nn.LeakyReLU(0.1, inplace=True)]
        project = nn.Conv2d(branch_channels, self.widths[4], 1)
        nn.init.zeros_(project.weight)
        nn.init.zeros_(project.bias)
        layers += [project, nn.Upsample(scale_factor=2, mode="bilinear", align_corners=False)]
        self.branch = nn.Sequential(*layers)

    def forward_features(self, x):
        out = x
        for i, layer in enumerate(self.body):
            if i == len(self.body) - 2:
                feats = out
            out = layer(out)
            if i == self.TAP:
                context = self.branch(out)
            elif i == self.JOIN:
                out = out + context
        return out + nn.functional.interpolate(x, scale_factor=self.scale, mode="nearest"), feats


def collapse(model: Compact) -> Compact:
    """A copy with every ECB folded into its single 3x3: the network Maki runs."""
    import copy
    plain = copy.deepcopy(model)
    for i, layer in enumerate(plain.body):
        if isinstance(layer, ECB):
            plain.body[i] = layer.fused()
    return plain


def load_student(model: Compact, state: dict) -> None:
    """Load a checkpoint; a plain one into an ECB model goes into each block's 3x3, branches at zero."""
    if isinstance(model, Hybrid) and not any(k.startswith("branch.") for k in state):
        # A trunk checkpoint: the branch keeps its init, whose zero projection adds nothing.
        trunk = {k: v for k, v in model.state_dict().items() if k.startswith("branch.")}
        state = dict(state) | trunk
    ecb = {i for i, layer in enumerate(model.body) if isinstance(layer, ECB)}
    if ecb and not any(".conv3." in k for k in state):
        for i in ecb:
            model.body[i].zero_branches()
        state = {(f"body.{k.split('.')[1]}.conv3.{k.split('.')[2]}"
                  if k.startswith("body.") and int(k.split(".")[1]) in ecb else k): v
                 for k, v in state.items()}
        missing, unexpected = model.load_state_dict(state, strict=False)
        assert not unexpected and all(".conv3." not in k for k in missing), (missing, unexpected)
        return
    model.load_state_dict(state)


def load_teacher(device) -> Compact:
    state = torch.load(TEACHER_PTH, map_location="cpu", weights_only=True)
    state = state.get("params", state)
    convs = sum(1 for k in state if k.endswith(".weight") and state[k].dim() == 4)
    model = Compact(64, convs - 2, 4, "prelu")
    model.load_state_dict(state)
    return model.eval().to(device)


ANIME6B_PTH = WORK / "teacher" / "RealESRGAN_x4plus_anime_6B.pth"


class DenseBlock(nn.Module):
    def __init__(self, feat: int = 64, grow: int = 32):
        super().__init__()
        for i in range(5):
            setattr(self, f"conv{i + 1}", nn.Conv2d(feat + i * grow, grow if i < 4 else feat, 3, 1, 1))
        self.act = nn.LeakyReLU(0.2, inplace=True)

    def forward(self, x):
        parts = [x]
        for i in range(4):
            parts.append(self.act(getattr(self, f"conv{i + 1}")(torch.cat(parts, 1))))
        return self.conv5(torch.cat(parts, 1)) * 0.2 + x


class RRDB(nn.Module):
    def __init__(self, feat: int = 64, grow: int = 32):
        super().__init__()
        self.rdb1, self.rdb2, self.rdb3 = DenseBlock(feat, grow), DenseBlock(feat, grow), DenseBlock(feat, grow)

    def forward(self, x):
        return self.rdb3(self.rdb2(self.rdb1(x))) * 0.2 + x


class RRDBNet(nn.Module):
    """ESRGAN's 4x generator, the layout RealESRGAN_x4plus_anime_6B's weights expect."""

    def __init__(self, blocks: int, feat: int = 64, grow: int = 32):
        super().__init__()
        self.conv_first = nn.Conv2d(3, feat, 3, 1, 1)
        self.body = nn.Sequential(*(RRDB(feat, grow) for _ in range(blocks)))
        self.conv_body = nn.Conv2d(feat, feat, 3, 1, 1)
        self.conv_up1 = nn.Conv2d(feat, feat, 3, 1, 1)
        self.conv_up2 = nn.Conv2d(feat, feat, 3, 1, 1)
        self.conv_hr = nn.Conv2d(feat, feat, 3, 1, 1)
        self.conv_last = nn.Conv2d(feat, 3, 3, 1, 1)
        self.act = nn.LeakyReLU(0.2, inplace=True)

    def forward(self, x):
        feat = self.conv_first(x)
        feat = feat + self.conv_body(self.body(feat))
        feat = self.act(self.conv_up1(nn.functional.interpolate(feat, scale_factor=2, mode="nearest")))
        feat = self.act(self.conv_up2(nn.functional.interpolate(feat, scale_factor=2, mode="nearest")))
        return self.conv_last(self.act(self.conv_hr(feat)))


def load_anime6b(device) -> RRDBNet:
    state = torch.load(ANIME6B_PTH, map_location="cpu", weights_only=True)
    state = state.get("params_ema", state.get("params", state))
    model = RRDBNet(6)
    model.load_state_dict(state)
    return model.eval().to(device)


def _widths(channels: str, convs: int) -> int | list[int]:
    """`48` is 48 everywhere; `64_4_48` is 64 for the first four convs (the first conv counted) and 48 after."""
    if "_" not in channels:
        return int(channels)
    wide, count, rest = (int(v) for v in channels.split("_"))
    return [wide] * count + [rest] * (convs + 1 - count)


def _parse(spec: str) -> tuple[int, int | list[int], int, str]:
    unshuffle = 1
    if spec.startswith("ps2-"):
        unshuffle, spec = 2, spec[4:]
    channels, rest = spec[1:].split("n")
    flags = rest.lstrip("0123456789")
    convs = int(rest[:len(rest) - len(flags)])
    return unshuffle, _widths(channels, convs), convs, flags


def _hybrid(spec: str) -> tuple[str, int, int]:
    _, trunk, branch = spec.split("-")
    bc, bn = branch[1:].split("n")
    return trunk, int(bc), int(bn)


def plain_spec(spec: str) -> str:
    """The spec Maki runs: the same network with ECB branches folded (`c48n8e` -> `c48n8`)."""
    if spec.startswith("hyb-"):
        trunk, bc, bn = _hybrid(spec)
        return f"hyb-{trunk.rstrip('e')}-b{bc}n{bn}"
    return spec[:-1] if spec.endswith("e") else spec


def student(spec: str) -> Compact:
    """`c32n8` is 32 channels and 8 body convs, LeakyReLU, 2x. A trailing `d` (`c48n8d`) dilates the middle
    body convs 2, 3, 3, 2: the same MACs, a 16 instead of 10 low-res pixel reach. Mixed rates so no
    pixel grid goes unseen (a single rate leaves a checkerboard the owner would see). A trailing `e`
    trains every 3x3 but the last as an ECB (collapse() before export). A `ps2-` prefix runs the body at
    half resolution behind a pixel-unshuffle (`ps2-c96n8e`)."""
    if spec.startswith("hyb-"):
        trunk, bc, bn = _hybrid(spec)
        _, channels, convs, flags = _parse(trunk)
        return Hybrid(channels, convs, bc, bn, ecb="e" in flags)
    unshuffle, channels, convs, flags = _parse(spec)
    dilations = None
    if "d" in flags:
        dilations = [1] * convs
        mid = convs // 2 - 2
        dilations[mid:mid + 4] = [2, 3, 3, 2]
    return Compact(channels, convs, 2, "leaky", dilations, ecb="e" in flags, unshuffle=unshuffle)


def reach(spec: str) -> int:
    """Low-res pixels of context either side that the student's stacked 3x3s see."""
    if spec.startswith("hyb-"):
        trunk, _, bn = _hybrid(spec)
        convs = _parse(trunk)[2]
        # 4 trunk convs to the tap, the pool, 2 px per half-res conv, the upsample, then the rest of the trunk.
        return convs + 2 * bn + 3
    unshuffle, _, convs, flags = _parse(spec)
    body = convs + 6 if "d" in flags else convs
    if unshuffle == 1:
        return body + 2
    return unshuffle * (body + 2) + unshuffle - 1


def margin(spec: str) -> int:
    """MARGIN, or the student's reach plus a little when that is wider: inside the margin the student's own
    context runs into the crop's zero padding, which Maki's reflected page context never shows it."""
    return max(MARGIN, reach(spec) + 3)


def split_name(name: str) -> tuple[str, str]:
    """`c48n8-long` -> (`c48n8`, `long`); `ps2-c96n8e-arch` -> (`ps2-c96n8e`, `arch`)."""
    parts = name.split("-")
    k = {"ps2": 2, "hyb": 3}.get(parts[0], 1)
    return "-".join(parts[:k]), "-".join(parts[k:])


def to_byte(x: torch.Tensor) -> torch.Tensor:
    """Maki's ToByte: clamp to [0, 1], scale, round half up."""
    return (x.clamp(0, 1) * 255 + 0.5).floor().to(torch.uint8)


def halve(rgb4x: np.ndarray) -> np.ndarray:
    """A 4x tile resized to 2x the way Maki's Plain4xThenHalve does it (vips Lanczos3; PIL's is the same kernel)."""
    h, w = rgb4x.shape[:2]
    return np.asarray(Image.fromarray(rgb4x).resize((w // 2, h // 2), Image.LANCZOS))


def luma(x: torch.Tensor) -> torch.Tensor:
    """UpscalePixels.Luma on NCHW floats. Maki keeps only this from the model, colour comes from the source."""
    return 0.299 * x[:, 0] + 0.587 * x[:, 1] + 0.114 * x[:, 2]


def teacher_2x(teacher: Compact, lr: torch.Tensor) -> np.ndarray:
    """What Maki serves from Real-ESRGAN for a uint8 HWC crop: 4x, rounded to bytes, halved."""
    with torch.no_grad():
        x = lr.permute(2, 0, 1)[None].float() / 255
        y = to_byte(teacher(x))[0].permute(1, 2, 0).cpu().numpy()
    return halve(y)
