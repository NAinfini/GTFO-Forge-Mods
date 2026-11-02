"""Builds the summoning ball (M01) from scratch: a compact hand-thrown call-in beacon about 7 cm across and 8 cm tall.

Run inside Blender (units are metres, +Z is the ball's up, the origin is its centre):
  blender --background --factory-startup --python make_ball.py -- --out M01.blend [--size 2048] [--segments 48]

The body is one lathe mesh, so its UV map is a clean cylinder wrap (u around, v along the profile) with even texel density,
and every detail (hazard stripes, panel seams, the amber ring's cells, knurled bands, rivets, wear) is painted into the
textures at --size: albedo, metal/roughness (glTF layout: G roughness, B metallic) and a tangent-space normal map taken from
a height field. Only free, self-made content. Material M01_mat, object M01; the amber ring's colour is what export_blender.py
--glow turns into emission.
"""
import argparse
import math
import os
import sys

import bmesh
import bpy
import numpy as np

A = 0.035        # equator radius of the shell (m)
C = 0.0425       # half height of the ellipsoid shell
RING_HALF = 0.0058   # half height of the amber ring
RING_BUMP = 0.0012   # how far the ring lens stands proud
Z_FOOT = -0.0405
Z_TOP = 0.0412


def arguments():
    argv = sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else []
    parser = argparse.ArgumentParser()
    parser.add_argument("--out", required=True)
    parser.add_argument("--size", type=int, default=2048)
    parser.add_argument("--segments", type=int, default=48)
    return parser.parse_args(argv)


def shell_radius(z):
    return A * math.sqrt(max(0.0, 1.0 - (z / C) ** 2))


def ring_bump(z):
    t = abs(z) / RING_HALF
    return RING_BUMP * (math.cos(min(t, 1.0) * math.pi / 2) ** 0.6) if t < 1.0 else 0.0


def profile():
    """(r, z) points from the bottom centre to the top centre."""
    pts = [(0.0, Z_FOOT), (0.006, Z_FOOT), (0.012, Z_FOOT)]
    z0, z1 = Z_FOOT + 0.00005, 0.0376
    zs = set(z0 + (z1 - z0) * (1 - math.cos(math.pi * i / 36)) / 2 for i in range(37))   # denser toward both ends
    zs.update(np.linspace(-RING_HALF, RING_HALF, 13).tolist())
    for z in sorted(zs):
        if z0 <= z <= z1:
            pts.append((shell_radius(z) + ring_bump(z), z))
    pts += [(0.0150, 0.0396), (0.0120, 0.0408), (0.0070, Z_TOP), (0.0, Z_TOP)]
    out = [pts[0]]
    for p in pts[1:]:
        if abs(p[0] - out[-1][0]) + abs(p[1] - out[-1][1]) > 1e-5:
            out.append(p)
    return out


def build_mesh(segments):
    prof = profile()
    lengths = [0.0]
    for (r0, z0), (r1, z1) in zip(prof, prof[1:]):
        lengths.append(lengths[-1] + math.hypot(r1 - r0, z1 - z0))
    total = lengths[-1]
    bm = bmesh.new()
    rings = []
    for r, z in prof:
        if r < 1e-9:
            rings.append([bm.verts.new((0.0, 0.0, z))])
        else:
            rings.append([bm.verts.new((r * math.cos(2 * math.pi * k / segments), r * math.sin(2 * math.pi * k / segments), z)) for k in range(segments)])
    uv = bm.loops.layers.uv.new("UVMap")
    faces = []
    for i in range(len(prof) - 1):
        lo, hi = rings[i], rings[i + 1]
        for k in range(segments):
            k2 = (k + 1) % segments
            if len(lo) == 1:
                vs = [lo[0], hi[k2], hi[k]]
            elif len(hi) == 1:
                vs = [lo[k], lo[k2], hi[0]]
            else:
                vs = [lo[k], lo[k2], hi[k2], hi[k]]
            try:
                faces.append((bm.faces.new(vs), i, k))
            except ValueError:
                pass
    for face, i, k in faces:
        for loop in face.loops:
            v = loop.vert
            ring = [idx for idx, rg in enumerate(rings) if v in rg][0]
            col = rings[ring].index(v)
            # the centre vertices take the u of the face's own corner so the fan keeps a clean wrap
            if len(rings[ring]) == 1:
                u = (k + 0.5) / segments
            else:
                u = col / segments
                if col == 0 and k == segments - 1:
                    u = 1.0
            loop[uv].uv = (u, lengths[ring] / total)
    bmesh.ops.recalc_face_normals(bm, faces=bm.faces[:])
    mesh = bpy.data.meshes.new("M01")
    bm.to_mesh(mesh)
    bm.free()
    for poly in mesh.polygons:
        poly.use_smooth = True
    return mesh, np.array(prof), np.array(lengths) / total


# ---------------------------------------------------------------- textures

def smooth(d, width):
    """Anti-aliased step: 1 where d > 0, with a transition of `width` (same unit as d)."""
    return np.clip(d / width + 0.5, 0.0, 1.0)


def noise(size, rng, cells, octaves=3):
    """Tileable (in x) value-like noise in 0..1 made of low-pass filtered random fields."""
    total = np.zeros((size, size), dtype=np.float32)
    amp = 1.0
    norm = 0.0
    for _ in range(octaves):
        n = max(2, cells)
        field = rng.random((n, n)).astype(np.float32)
        spectrum = np.fft.rfft2(field)
        padded = np.zeros((size, size // 2 + 1), dtype=np.complex64)
        h = min(n // 2 + 1, size // 2 + 1)
        padded[:n // 2, :h] = spectrum[:n // 2, :h]
        padded[-(n // 2):, :h] = spectrum[-(n // 2):, :h]
        layer = np.fft.irfft2(padded, s=(size, size))
        layer = (layer - layer.min()) / (np.ptp(layer) + 1e-9)
        total += amp * layer
        norm += amp
        amp *= 0.5
        cells *= 2
    return total / norm


def paint(size, prof, v_of_point):
    rng = np.random.default_rng(1950114)
    px_z = np.interp((np.arange(size) + 0.5) / size, v_of_point, prof[:, 1])
    px_r = np.interp((np.arange(size) + 0.5) / size, v_of_point, prof[:, 0])
    phi = (np.arange(size) + 0.5) / size * 2 * np.pi
    Z = px_z[:, None] * np.ones((1, size))
    R = np.maximum(px_r[:, None], 1e-4) * np.ones((1, size))
    PHI = np.ones((size, 1)) * phi[None, :]
    # arc-length coordinates in metres: s along the surface around the ring, z up
    S = PHI / (2 * np.pi) * 16 * 0.014   # 224 mm of seamless wrap = 16 stripe half-periods
    # metres per pixel along z (per row) and around (at the local radius)
    dz = np.gradient(px_z, axis=0)
    mz = np.abs(dz)[:, None] * np.ones((1, size)) + 1e-6
    px = np.maximum(mz, 7e-5)

    albedo = np.zeros((size, size, 3), dtype=np.float32)
    rough = np.zeros((size, size), dtype=np.float32)
    metal = np.zeros((size, size), dtype=np.float32)
    height = np.zeros((size, size), dtype=np.float32)   # metres, drives the normal map

    white = np.array([0.86, 0.85, 0.80], dtype=np.float32)
    orange = np.array([0.93, 0.36, 0.05], dtype=np.float32)
    black = np.array([0.045, 0.045, 0.05], dtype=np.float32)
    steel = np.array([0.30, 0.31, 0.33], dtype=np.float32)
    amber = np.array([1.0, 0.78, 0.04], dtype=np.float32)

    def lerp(base, colour, mask):
        return base * (1 - mask[..., None]) + colour * mask[..., None]

    # base paint: white shell
    albedo[:] = white
    rough[:] = 0.52
    metal[:] = 0.0

    # hazard stripes on the lower shell (diagonal, period 28 mm around, edges 1.2 px soft)
    lower = smooth(-0.0125 - Z, px) * smooth(Z + 0.0395, px)
    stripe_phase = ((S * 1.0 + Z * 1.0) / 0.014) % 2.0
    stripe_on = (stripe_phase < 1.0).astype(np.float32)
    # antialias the stripe edges
    d = np.minimum(np.abs(stripe_phase - 1.0), np.minimum(stripe_phase, 2.0 - stripe_phase)) * 0.014
    edge = smooth(d, 0.0003)
    stripe_mask = np.where(stripe_on > 0, edge, 1 - edge)
    stripe_mask = np.clip(stripe_mask, 0, 1)
    albedo = lerp(albedo, orange, lower * stripe_mask)
    rough = rough * (1 - lower * stripe_mask) + 0.62 * lower * stripe_mask

    # orange collar and chevron plates on the upper shell
    collar = smooth(Z - 0.0128, px) * smooth(0.0172 - Z, px)
    albedo = lerp(albedo, orange, collar)
    for centre in (0.0, 0.5 * math.pi, math.pi, 1.5 * math.pi):
        dphi = np.abs(((PHI - centre + math.pi) % (2 * math.pi)) - math.pi)
        plate = smooth(0.30 - dphi, 0.004) * smooth(Z - 0.021, px) * smooth(0.031 - Z, px)
        albedo = lerp(albedo, orange, plate)
        height += plate * 0.00035                       # a raised plate
        # slot window on each plate
        slot = smooth(0.09 - dphi, 0.004) * smooth(Z - 0.0235, px) * smooth(0.0285 - Z, px)
        albedo = lerp(albedo, black, slot)
        height -= slot * 0.0005
        metal = metal * (1 - slot) + 0.7 * slot
        rough = rough * (1 - slot) + 0.3 * slot

    # black knurled bands above and below the ring
    for sign in (-1, 1):
        zc = sign * 0.0100
        band = smooth(0.0025 - np.abs(Z - zc), px)
        albedo = lerp(albedo, black, band)
        metal = metal * (1 - band) + 0.55 * band
        rough = rough * (1 - band) + 0.42 * band
        knurl = 0.5 + 0.5 * np.cos(PHI * 100)   # 100 ribs around
        height += band * knurl * 0.00028
        albedo = albedo * (1 - band[..., None] * knurl[..., None] * 0.35 * 0.5)
    # seams: thin dark grooves at the band edges and the shell panel lines
    for zc in (-0.0075, 0.0075, -0.0128, 0.0128, -0.0395, 0.0220, 0.0345):
        g = smooth(0.00035 - np.abs(Z - zc), px)
        albedo = lerp(albedo, albedo * 0.35, g)
        height -= g * 0.0006
    # vertical panel lines every 60 degrees on the white upper shell and orange lower shell
    for k in range(6):
        dphi = np.abs(((PHI - k * math.pi / 3 + math.pi) % (2 * math.pi)) - math.pi)
        g = smooth(0.00030 - dphi * 0.035, 0.00022) * smooth(Z - 0.0075, px) * smooth(0.0376 - Z, px)
        g2 = smooth(0.00030 - dphi * 0.035, 0.00022) * smooth(-0.0075 - Z, px) * smooth(Z + 0.040, px)
        gg = np.maximum(g, g2)
        albedo = lerp(albedo, albedo * 0.4, gg)
        height -= gg * 0.0005

    # the amber ring: segmented glowing cells with dark separators
    ring = smooth(RING_HALF - 0.0007 - np.abs(Z), px)
    cells = 24
    cell_phase = (PHI / (2 * np.pi) * cells) % 1.0
    sep = smooth(0.045 - np.minimum(cell_phase, 1.0 - cell_phase), 0.012)
    glow = ring * (1 - sep)
    frame = smooth(RING_HALF + 0.0004 - np.abs(Z), px)
    albedo = lerp(albedo, black, frame)
    metal = metal * (1 - frame) + 0.65 * frame
    rough = rough * (1 - frame) + 0.38 * frame
    albedo = lerp(albedo, amber, glow)
    metal = metal * (1 - glow)
    rough = rough * (1 - glow) + 0.14 * glow
    height -= glow * 0.0003

    # rivets on the upper collar and on the lower rim
    for zc, count in ((0.0150, 24), (-0.0300, 16)):
        cp = (PHI / (2 * np.pi) * count) % 1.0
        dx = (cp - 0.5) * (2 * np.pi * 0.0352 / count)
        dist = np.hypot(dx, Z - zc)
        rivet = smooth(0.00075 - dist, px)
        albedo = lerp(albedo, steel * 1.4, rivet)
        metal = metal * (1 - rivet) + 0.9 * rivet
        rough = rough * (1 - rivet) + 0.3 * rivet
        height += rivet * 0.0004

    # bottom foot and top button, by profile radius
    foot = smooth(0.0124 - R, 1e-4) * smooth(-0.0395 - Z, px)
    albedo = lerp(albedo, steel * 0.7, foot)
    metal = metal * (1 - foot) + 0.9 * foot
    rough = rough * (1 - foot) + 0.35 * foot
    for rr in (0.004, 0.008):
        groove = smooth(0.00025 - np.abs(R - rr), 8e-5) * foot
        height -= groove * 0.0004
        albedo = lerp(albedo, albedo * 0.4, groove)
    top = smooth(Z - 0.0372, px)
    albedo = lerp(albedo, black, top)
    metal = metal * (1 - top) + 0.55 * top
    rough = rough * (1 - top) + 0.4 * top
    dot = smooth(0.0036 - R, 8e-5) * smooth(Z - 0.0405, px)
    albedo = lerp(albedo, orange, dot)
    rough = rough * (1 - dot) + 0.5 * dot
    metal = metal * (1 - dot)

    # wear: grime in the low-frequency noise, edge scuffs and fine scratches in the high one
    low = noise(size, rng, 6, 4)
    fine = noise(size, rng, 64, 3)
    grime = np.clip((low - 0.55) * 2.0, 0, 1) * 0.28
    albedo = albedo * (1 - grime[..., None] * 0.9)
    scuff = np.clip((fine - 0.78) * 5.0, 0, 1)
    painted = np.clip(1.0 - metal, 0, 1)
    albedo = lerp(albedo, steel * 1.2, scuff * painted * 0.55)
    metal = metal + scuff * painted * 0.6
    rough = rough + (fine - 0.5) * 0.12
    height += (fine - 0.5) * 0.00006
    albedo = np.clip(albedo * (0.96 + 0.08 * fine[..., None]), 0, 1)

    # normal map from the height field (x along u / around, y along v / up the profile)
    gx = np.gradient(height, axis=1) / 0.0352 * (size / (2 * np.pi))     # d height / d metre around
    gy = np.gradient(height, axis=0) / np.maximum(mz, 1e-5)
    strength = 1.0
    nx, ny, nz = -gx * strength, -gy * strength, np.ones_like(height)
    length = np.sqrt(nx * nx + ny * ny + nz * nz)
    normal = np.stack([nx / length * 0.5 + 0.5, ny / length * 0.5 + 0.5, nz / length * 0.5 + 0.5], axis=-1)

    metalrough = np.stack([np.zeros_like(metal), np.clip(rough, 0.05, 1), np.clip(metal, 0, 1)], axis=-1)   # glTF: G rough, B metal
    return albedo, metalrough, normal


def make_image(name, rgb, colour, folder):
    """Writes the texture as a PNG, loads it back as a file image and packs it, so the .blend carries real pixels."""
    size = rgb.shape[0]
    scratch = bpy.data.images.new(name + "_scratch", size, size, alpha=True)
    data = np.ones((size, size, 4), dtype=np.float32)
    data[..., :3] = rgb
    scratch.colorspace_settings.name = "sRGB" if colour else "Non-Color"   # before the pixels: changing it later clears them
    scratch.pixels.foreach_set(data.ravel())
    path = os.path.join(folder, name + ".png")
    scratch.filepath_raw = path
    scratch.file_format = "PNG"
    scratch.save()
    bpy.data.images.remove(scratch)
    image = bpy.data.images.load(path)
    image.name = name
    image.colorspace_settings.name = "sRGB" if colour else "Non-Color"
    image.pack()
    return image


def main():
    args = arguments()
    bpy.ops.wm.read_factory_settings(use_empty=True)
    mesh, prof, v_of_point = build_mesh(args.segments)
    obj = bpy.data.objects.new("M01", mesh)
    bpy.context.scene.collection.objects.link(obj)
    albedo, metalrough, normal = paint(args.size, prof, v_of_point)
    folder = os.path.dirname(os.path.abspath(args.out))
    images = (make_image("M01_basecolor", albedo, True, folder), make_image("M01_metalrough", metalrough, False, folder), make_image("M01_normal", normal, False, folder))
    material = bpy.data.materials.new("M01_mat")
    material.use_nodes = True
    nodes, links = material.node_tree.nodes, material.node_tree.links
    bsdf = next(n for n in nodes if n.type == "BSDF_PRINCIPLED")

    def image_node(image, x):
        node = nodes.new("ShaderNodeTexImage")
        node.image = image
        node.location = (x, 0)
        return node

    base = image_node(images[0], -700)
    mr = image_node(images[1], -700)
    mr.location = (-700, -300)
    nm = image_node(images[2], -700)
    nm.location = (-700, -600)
    links.new(base.outputs["Color"], bsdf.inputs["Base Color"])
    sep = nodes.new("ShaderNodeSeparateColor")
    links.new(mr.outputs["Color"], sep.inputs["Color"])
    links.new(sep.outputs["Green"], bsdf.inputs["Roughness"])
    links.new(sep.outputs["Blue"], bsdf.inputs["Metallic"])
    nmap = nodes.new("ShaderNodeNormalMap")
    links.new(nm.outputs["Color"], nmap.inputs["Color"])
    links.new(nmap.outputs["Normal"], bsdf.inputs["Normal"])
    mesh.materials.append(material)
    bpy.ops.wm.save_as_mainfile(filepath=args.out)
    tris = sum(len(p.vertices) - 2 for p in mesh.polygons)
    print("MAKE_BALL", args.out, "triangles", tris, "dims", tuple(round(x, 4) for x in obj.dimensions))


main()
