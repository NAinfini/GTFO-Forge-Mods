"""Stages the call-in effect flipbooks from the owner's read-only effect library.

    python prepare_vfx.py <Unity Labs folder> <Asset Store folder> <stage textures folder>

Unity Labs (CC0, https://unity.com/blog/engine-platform/free-vfx-image-sequences-flipbooks), unpacked as <Package>/<Package>_<cols>x<rows>.tga:
  vfx-flame      Flame02 (16 x 4 cells of 128 x 256, 64 frames): every second frame, each cell centred on a 256 x 256 square so the
                 quad is not stretched, laid out 8 x 4 (32 frames, 2048 x 1024).
Unity Asset Store packs (standard Asset Store licence, unpacked under <Asset Store folder>/<pack>/unpacked*):
  vfx-smoke      "Particle Pack Starter Assets" (Unity Technologies) SmokePuff01, 5 x 5 frames of 410 px: white colour, alpha from the
                 source brightness (normalised to its 99th percentile, blurred and capped at 80 % so the puffs are soft and translucent, not photographic), so the runtime tint alone decides grey smoke or white steam.
  vfx-ring       "Free Quick Effects Vol 1" (Gabriel Aguiar Prod) Circle01_v1: white colour, source alpha kept, 512 x 512; the beacon's
                 shock ring lying on the ground.
"""
import os
import sys

import numpy as np
from PIL import Image, ImageFilter


def find(root: str, name: str) -> str:
    for folder, _, files in os.walk(root):
        if name in files:
            return os.path.join(folder, name)
    raise FileNotFoundError(f"{name} under {root}")


def flame(source: str, destination: str) -> None:
    sheet = Image.open(source).convert("RGBA")
    cols, rows = 16, 4
    cw, ch = sheet.width // cols, sheet.height // rows
    frames = [sheet.crop((c * cw, r * ch, (c + 1) * cw, (r + 1) * ch)) for r in range(rows) for c in range(cols)][::2]
    assert len(frames) == 32 and ch == 256 and cw == 128, (len(frames), cw, ch)
    out = Image.new("RGBA", (8 * 256, 4 * 256), (0, 0, 0, 0))
    for i, frame in enumerate(frames):
        out.paste(frame, ((i % 8) * 256 + (256 - cw) // 2, (i // 8) * 256))
    out.save(destination)


def smoke(source: str, destination: str) -> None:
    luminance = np.array(Image.open(source).convert("L"), dtype=np.float32)
    alpha = np.clip(luminance / np.percentile(luminance[luminance > 4], 99), 0, 1) * 255
    # Soft, flat and translucent rather than photographic: the volumetric detail is blurred away and the peak capped.
    mask = Image.fromarray(alpha.astype(np.uint8), "L").resize((1280, 1280), Image.LANCZOS).filter(ImageFilter.GaussianBlur(9))
    out = Image.new("RGBA", (1280, 1280), (255, 255, 255, 0))
    out.putalpha(mask.point(lambda v: int(v * 0.8)))
    out.save(destination)


def ring(source: str, destination: str) -> None:
    pixels = np.array(Image.open(source).convert("RGBA"), dtype=np.uint8)
    pixels[..., :3] = 255
    Image.fromarray(pixels, "RGBA").resize((512, 512), Image.LANCZOS).save(destination)


if __name__ == "__main__":
    labs, store, dst = sys.argv[1], sys.argv[2], sys.argv[3]
    flame(os.path.join(labs, "Flame02", "Flame02_16x4.tga"), os.path.join(dst, "vfx-flame.png"))
    starter = os.path.join(store, "Particle Pack Starter Assets", "unpacked")
    smoke(find(starter, "SmokePuff01.tif"), os.path.join(dst, "vfx-smoke.png"))
    ring(find(os.path.join(store, "Free Quick Effects Vol 1", "unpacked-birp"), "Circle01_v1.png"), os.path.join(dst, "vfx-ring.png"))
