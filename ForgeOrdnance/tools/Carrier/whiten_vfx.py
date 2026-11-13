"""Turns a grey-on-alpha effect sprite into white-on-alpha so the runtime tint (orange flames, blue-white flare) is not darkened
by the source's mid-grey colour channel. Alpha is kept; --alpha-gain scales it (clamped to 255).

    python whiten_vfx.py <source.png> <destination.png> [--alpha-gain 1.5]
"""
import argparse

import numpy as np
from PIL import Image


def whiten(source: str, destination: str, gain: float) -> None:
    pixels = np.array(Image.open(source).convert("RGBA"), dtype=np.float32)
    pixels[..., :3] = 255.0
    pixels[..., 3] = np.clip(pixels[..., 3] * gain, 0, 255)
    Image.fromarray(pixels.astype(np.uint8), "RGBA").save(destination)


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("source")
    parser.add_argument("destination")
    parser.add_argument("--alpha-gain", type=float, default=1.0)
    arguments = parser.parse_args()
    whiten(arguments.source, arguments.destination, arguments.alpha_gain)
