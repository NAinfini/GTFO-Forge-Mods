#!/usr/bin/env python3
"""Turns the Unity Labs VFX image sequences (CC0 1.0, https://unity.com/blog/engine-platform/free-vfx-image-sequences-flipbooks) into
the flipbook atlas of the Arsenal fireball, as PNG at a size the bundle can carry:

  vfx-fireball    FireBall02     8 x 8   1024 x 1024  orange fireball (black background, drawn additively)

Frames run left to right, top to bottom. The RGB-only fireballs get an opaque alpha channel; the shader scales their colour by
the tint's alpha when drawn additively.

    python Mods/ForgeOrdnance/tools/Arsenal/make_unitylabs_atlases.py [--src F:\\Unity\\特效\\unity-labs-vfx-cc0] [--out Source/mods/ordnance-assets/vfx]
"""
import argparse
import os

from PIL import Image

SETS = [
    ('vfx-fireball', 'FireBall02', 'FireBall02_8x8', (1024, 1024)),
]


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--src', default=r'F:\Unity\特效\unity-labs-vfx-cc0')
    parser.add_argument('--out', default='Source/mods/ordnance-assets/vfx')
    args = parser.parse_args()
    os.makedirs(args.out, exist_ok=True)
    for name, folder, stem, size in SETS:
        im = Image.open(os.path.join(args.src, folder, stem + '.tga')).convert('RGBA')
        if im.size != size:
            im = im.resize(size, Image.LANCZOS)
        im.save(os.path.join(args.out, name + '.png'), optimize=True)
        print('wrote', name, size)


if __name__ == '__main__':
    main()
