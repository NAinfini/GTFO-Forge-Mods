#!/usr/bin/env python3
"""Draws the four effect textures of the Arsenal blast and trail effects (Native/StrikeFx.cs): ring, scorch, streak, glow.

White on alpha like the other effect textures of forge-ordnance-arsenal.bundle (the game tints them); drawn from nothing but
numpy noise and gradients, so they carry no third-party licence. Seeded: the same run gives the same files.

    python Mods/ForgeOrdnance/tools/Arsenal/make_fx_textures.py [--out Source/mods/ordnance-assets/vfx]
"""
import argparse
import os

import numpy as np
from PIL import Image


def smooth(t):
    t = np.clip(t, 0.0, 1.0)
    return t * t * (3.0 - 2.0 * t)


def value_noise(size, cells, rng):
    """Bilinear value noise, tileable in neither direction, in [0, 1]."""
    grid = rng.random((cells + 1, cells + 1))
    xs = np.linspace(0, cells, size, endpoint=False)
    i = xs.astype(int)
    f = smooth(xs - i)
    top = grid[np.ix_(i, i)] * (1 - f)[None, :] + grid[np.ix_(i, i + 1)] * f[None, :]
    bottom = grid[np.ix_(i + 1, i)] * (1 - f)[None, :] + grid[np.ix_(i + 1, i + 1)] * f[None, :]
    return top * (1 - f)[:, None] + bottom * f[:, None]


def fbm(size, rng, octaves=5, base=4):
    total = np.zeros((size, size))
    amplitude, norm = 1.0, 0.0
    for o in range(octaves):
        total += amplitude * value_noise(size, base * 2 ** o, rng)
        norm += amplitude
        amplitude *= 0.5
    return total / norm


def save(path, alpha):
    a = (np.clip(alpha, 0, 1) * 255).astype(np.uint8)
    rgba = np.zeros(alpha.shape + (4,), np.uint8)
    rgba[..., :3] = 255
    rgba[..., 3] = a
    Image.fromarray(rgba, 'RGBA').save(path, optimize=True)


def grid(size):
    c = (np.arange(size) + 0.5) / size * 2 - 1
    x, y = np.meshgrid(c, c)
    return x, y, np.hypot(x, y), np.arctan2(y, x)


def ring(size, rng):
    x, y, r, theta = grid(size)
    wobble = 1 + 0.18 * (value_noise(size, 6, rng) - 0.5) * np.cos(theta * 3)
    band = np.exp(-(((r - 0.84) / 0.075) ** 2)) * wobble
    inner = 0.10 * smooth((r - 0.2) / 0.6) * (1 - smooth((r - 0.8) / 0.12))
    edge = 1 - smooth((r - 0.93) / 0.07)
    return (band + inner) * edge


def scorch(size, rng):
    x, y, r, theta = grid(size)
    noise = fbm(size, rng, 5, 3)
    spokes = 0.5 + 0.5 * np.sin(theta * 11 + noise * 9)
    core = 1.25 * np.clip(1 - r, 0, 1) ** 0.7 + 0.5 * (noise - 0.5) + 0.12 * spokes * smooth((r - 0.2) / 0.5) - 0.5
    return smooth(core * 1.7) * (1 - smooth((r - 0.86) / 0.14))


def streak(width, length):
    """Head at the bottom row (v = 0), a soft tail narrowing toward the top."""
    u = (np.arange(width) + 0.5) / width * 2 - 1
    v = (np.arange(length) + 0.5) / length
    uu, vv = np.meshgrid(u, v)
    half = 0.9 * (1 - 0.8 * vv) + 0.1
    across = np.exp(-((uu / half) ** 2) * 2.2)
    along = (1 - vv) ** 1.5
    head = np.exp(-((vv / 0.1) ** 2)) * 0.6
    return np.clip(across * (along + head), 0, 1)[::-1]


def glow(size):
    _, _, r, _ = grid(size)
    return np.clip(1 - r, 0, 1) ** 2.2 * (1 - smooth((r - 0.85) / 0.15))


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--out', default='Source/mods/ordnance-assets/vfx')
    out = parser.parse_args().out
    os.makedirs(out, exist_ok=True)
    rng = np.random.default_rng(20261024)
    save(os.path.join(out, 'vfx-ring.png'), ring(256, rng))
    save(os.path.join(out, 'vfx-scorch.png'), scorch(256, rng))
    save(os.path.join(out, 'vfx-streak.png'), streak(64, 256))
    save(os.path.join(out, 'vfx-glow.png'), glow(128))
    print('wrote vfx-ring, vfx-scorch, vfx-streak and vfx-glow to', os.path.abspath(out))


if __name__ == '__main__':
    main()
