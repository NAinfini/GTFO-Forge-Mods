"""Stages the effect textures of forge-frames-fx for the Unity build (tools/fx/build_fx.ps1).

Reads the CC0 effect sheets from Source/mods/ordnance-assets/vfx (Kenney Smoke Particles, Smoke Aura) and the Unity Labs CC0
flipbooks from the unity-labs-vfx-cc0 library folder (FireBall01, Explosion01, Flame02; see Assets/Models/License.txt), shrinks them to
the sizes the effects need, and draws the five small shapes the sheets lack (ring, soft dot, hard disc, glow, ground crack) procedurally. Usage:
    python make_fx_textures.py <vfx source folder> <unity labs folder> <War FX flamethrower stream tga> <output folder>
"""
import os
import sys

import numpy as np
from PIL import Image


def save(array, path):
    Image.fromarray(np.clip(array * 255.0 + 0.5, 0, 255).astype(np.uint8), "RGBA").save(path)


def grid(size):
    axis = (np.arange(size) + 0.5) / size * 2.0 - 1.0
    x, y = np.meshgrid(axis, axis)
    return x, y, np.sqrt(x * x + y * y)


def smooth(edge0, edge1, value):
    t = np.clip((value - edge0) / (edge1 - edge0), 0.0, 1.0)
    return t * t * (3.0 - 2.0 * t)


def white(alpha):
    out = np.ones(alpha.shape + (4,), dtype=np.float32)
    out[..., 3] = alpha
    return out


def ring(size=256):
    _, _, r = grid(size)
    # A thin bright line at 0.86 of the radius with a soft inner trail, nothing outside it.
    line = np.exp(-(((r - 0.86) / 0.035) ** 2))
    trail = smooth(0.35, 0.86, r) * (1.0 - smooth(0.86, 0.9, r)) * 0.22
    return white(np.clip(line + trail, 0.0, 1.0) * (1.0 - smooth(0.96, 1.0, r)))


def dot(size=64):
    _, _, r = grid(size)
    return white(np.clip(1.0 - r, 0.0, 1.0) ** 2.2)


def glow(size=128):
    _, _, r = grid(size)
    return white(np.exp(-(r * 2.6) ** 2) * (1.0 - smooth(0.85, 1.0, r)))


def disc(size=64):
    rng = np.random.default_rng(7)
    x, y, r = grid(size)
    angle = np.arctan2(y, x)
    wobble = 0.72 + 0.16 * np.sin(angle * 3 + 1.0) + 0.08 * np.sin(angle * 5 + 2.0) + rng.uniform(-0.02, 0.02)
    return white(1.0 - smooth(wobble - 0.1, wobble, r))


def crack(size=256):
    rng = np.random.default_rng(11)
    image = np.zeros((size, size), dtype=np.float32)
    centre = (size - 1) / 2.0

    def draw(x0, y0, angle, length, width, depth):
        steps = int(length)
        x, y = x0, y0
        for step in range(steps):
            fade = 1.0 - step / steps
            half = max(0.6, width * fade)
            for dy in range(-int(half) - 1, int(half) + 2):
                for dx in range(-int(half) - 1, int(half) + 2):
                    ix, iy = int(round(x)) + dx, int(round(y)) + dy
                    if 0 <= ix < size and 0 <= iy < size:
                        d = (dx * dx + dy * dy) ** 0.5
                        image[iy, ix] = max(image[iy, ix], max(0.0, 1.0 - d / (half + 0.8)) * (0.35 + 0.65 * fade))
            angle += rng.normal(0.0, 0.16)
            x += np.cos(angle)
            y += np.sin(angle)
            if depth > 0 and step > 14 and rng.random() < 0.03:
                draw(x, y, angle + rng.choice([-1, 1]) * rng.uniform(0.5, 1.0), length * 0.5 * fade, width * 0.6, depth - 1)

    arms = 11
    for index in range(arms):
        angle = index / arms * 2.0 * np.pi + rng.uniform(-0.2, 0.2)
        draw(centre, centre, angle, rng.uniform(0.45, 0.62) * size / 2.0 * 1.55, 2.4, 2)
    _, _, r = grid(size)
    image *= 1.0 - smooth(0.9, 1.0, r)
    image = np.maximum(image, (1.0 - smooth(0.0, 0.16, r)) * 0.55)
    return white(image)


def atlas(source, path, size):
    image = Image.open(source).convert("RGBA")
    image = image.resize(size, Image.LANCZOS)
    image.save(path)


def load_sheet(labs, name, sheet):
    return Image.open(os.path.join(labs, name, name + "_" + sheet + ".tga"))


def unpremultiply(image):
    """The fire sheets are light on black: alpha = brightest channel, colour divided by it, so additive blending reproduces them."""
    rgb = np.asarray(image.convert("RGB"), dtype=np.float32) / 255.0
    alpha = rgb.max(axis=2)
    colour = rgb / np.maximum(alpha, 1e-3)[..., None]
    return np.dstack([np.clip(colour, 0.0, 1.0), alpha]).astype(np.float32)


def clear_borders(array, cells, margin):
    """Zeroes a margin inside every cell of a sheet so no frame bleeds into its neighbour under filtering."""
    size = array.shape[0] // cells
    for index in range(cells):
        for edge in range(margin):
            array[index * size + edge, :, 3] = 0.0
            array[index * size + size - 1 - edge, :, 3] = 0.0
            array[:, index * size + edge, 3] = 0.0
            array[:, index * size + size - 1 - edge, 3] = 0.0
    return array


def to_image(array):
    return Image.fromarray((np.clip(array, 0.0, 1.0) * 255.0 + 0.5).astype(np.uint8), "RGBA")


def fireball(labs, output):
    rgba = unpremultiply(load_sheet(labs, "FireBall01", "8x8"))
    to_image(rgba).save(os.path.join(output, "fx-fireball.png"))
    # The same frames in grey, so a tint (the blue thruster) is not multiplied into orange.
    grey = rgba[..., :3] @ np.array([0.3, 0.59, 0.11], dtype=np.float32)
    grey = grey / max(float(grey.max()), 1e-3)
    to_image(np.dstack([grey, grey, grey, rgba[..., 3]])).resize((512, 512), Image.LANCZOS).save(os.path.join(output, "fx-fireball-grey.png"))


def blast(labs, output):
    """Explosion01 keeps its fire and drops its smoke: the grey, unsaturated pixels are masked out, and what is left (the orange
    body and the white-hot core) is drawn additively. The effect plays only the first ten of its 25 frames."""
    rgb = np.asarray(Image.open(os.path.join(labs, "Explosion01", "Explosion01_5x5.tga")).convert("RGB"), dtype=np.float32) / 255.0
    luminance = rgb @ np.array([0.3, 0.59, 0.11], dtype=np.float32)
    saturation = rgb.max(axis=2) - rgb.min(axis=2)
    mask = np.clip(np.maximum(saturation * 2.5, (luminance - 0.55) * 3.0), 0.0, 1.0)
    to_image(np.dstack([rgb, mask])).save(os.path.join(output, "fx-blast.png"))


def flame(labs, output):
    """Flame02 is 16x4 cells of 128x256; repack as 8x8 square cells of 128 (the flame keeps its height, with empty sides)."""
    rgba = unpremultiply(load_sheet(labs, "Flame02", "16x4"))
    sheet = np.zeros((1024, 1024, 4), dtype=np.float32)
    for index in range(64):
        cx, cy = index % 16, index // 16
        cell = to_image(rgba[cy * 256:(cy + 1) * 256, cx * 128:(cx + 1) * 128])
        square = Image.new("RGBA", (256, 256), (0, 0, 0, 0))
        square.paste(cell, (64, 0))
        small = np.asarray(square.resize((128, 128), Image.LANCZOS), dtype=np.float32) / 255.0
        sheet[(index // 8) * 128:(index // 8 + 1) * 128, (index % 8) * 128:(index % 8 + 1) * 128] = small
    to_image(clear_borders(sheet, 8, 3)).save(os.path.join(output, "fx-flame.png"))


def jet(stream, output):
    """The thruster cone: War FX "FlameThrower Stream" (a narrow bright root widening and fading), as white with its brightness as
    alpha. A stretched billboard maps the texture's x axis against the flight, so the root (left in the file) is flipped to the right."""
    grey = np.asarray(Image.open(stream).convert("L").resize((256, 256), Image.LANCZOS), dtype=np.float32) / 255.0
    grey = (grey / max(float(grey.max()), 1e-3)) ** 0.7
    image = Image.fromarray((np.dstack([np.ones_like(grey)] * 3 + [grey]) * 255.0 + 0.5).astype(np.uint8), "RGBA")
    image.transpose(Image.FLIP_LEFT_RIGHT).save(os.path.join(output, "fx-jet.png"))


def main():
    source, labs, stream, output = sys.argv[1], sys.argv[2], sys.argv[3], sys.argv[4]
    os.makedirs(output, exist_ok=True)
    atlas(os.path.join(source, "vfx-smoke.png"), os.path.join(output, "fx-smoke.png"), (640, 384))
    fireball(labs, output)
    blast(labs, output)
    flame(labs, output)
    jet(stream, output)
    atlas(os.path.join(source, "vfx-mist.png"), os.path.join(output, "fx-mist.png"), (256, 256))
    save(ring(), os.path.join(output, "fx-ring.png"))
    save(dot(), os.path.join(output, "fx-dot.png"))
    save(glow(), os.path.join(output, "fx-glow.png"))
    save(disc(), os.path.join(output, "fx-disc.png"))
    save(crack(), os.path.join(output, "fx-crack.png"))
    print("staged textures in " + output)


if __name__ == "__main__":
    main()
