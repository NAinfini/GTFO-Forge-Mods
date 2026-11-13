"""Draws the 2 x 2 atlas of faceted chunks the landing throws out of the ground (staged as the bundle's vfx-rock texture).

    python make_rocks.py <destination.png>

Two concrete chunks and two earth chunks, each a jittered polygon split into flat-shaded facets lit from the upper left, in a clean
stylised look (no photographic detail). Deterministic: the same file every run. Cells are 256 x 256, drawn at 4x and reduced.
"""
import math
import random
import sys

from PIL import Image, ImageDraw

CELL = 256
SCALE = 4


def chunk(seed: int, base: tuple, sides: int, squash: float) -> Image.Image:
    rng = random.Random(seed)
    size = CELL * SCALE
    centre = size / 2
    image = Image.new("RGBA", (size, size), (0, 0, 0, 0))
    draw = ImageDraw.Draw(image)
    angles = [2 * math.pi * i / sides + rng.uniform(-0.28, 0.28) for i in range(sides)]
    points = []
    for angle in angles:
        radius = size * 0.34 * rng.uniform(0.78, 1.12)
        points.append((centre + math.cos(angle) * radius, centre + math.sin(angle) * radius * squash))
    hub = (centre + rng.uniform(-0.05, 0.05) * size, centre + rng.uniform(-0.05, 0.05) * size)
    light = math.radians(225)
    for i, a in enumerate(points):
        b = points[(i + 1) % len(points)]
        mid = math.atan2((a[1] + b[1]) / 2 - centre, (a[0] + b[0]) / 2 - centre)
        shade = 0.62 + 0.38 * math.cos(mid - light) + rng.uniform(-0.07, 0.07)
        shade = max(0.35, min(1.15, shade))
        draw.polygon([hub, a, b], fill=tuple(min(255, int(c * shade)) for c in base) + (255,))
    draw.line(points + [points[0]], fill=tuple(int(c * 0.38) for c in base) + (255,), width=SCALE * 3, joint="curve")
    return image.resize((CELL, CELL), Image.LANCZOS)


if __name__ == "__main__":
    concrete = (176, 172, 164)
    earth = (150, 118, 84)
    cells = [chunk(3, concrete, 7, 0.82), chunk(11, earth, 8, 0.9), chunk(19, concrete, 6, 0.7), chunk(27, earth, 7, 0.78)]
    atlas = Image.new("RGBA", (CELL * 2, CELL * 2), (0, 0, 0, 0))
    for i, cell in enumerate(cells):
        atlas.paste(cell, ((i % 2) * CELL, (i // 2) * CELL))
    atlas.save(sys.argv[1])
