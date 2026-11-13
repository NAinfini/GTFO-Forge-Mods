"""Wear and rust for the exosuits' armour maps (build_frame.py calls paint right after glow.paint, on the finished shell): Cycles bakes
the shell's ambient occlusion, its edges (a bevel against the true normal) and its up-facing mask on the armour's own UVs, and
procedural noise over those masks writes chipped edges, scratches, grime in the creases, dust on the tops, rust and oil into
albedo.png (raised to ATLAS px) and metallic.png (R metal, G and A smoothness, ATLAS_METAL px). Wear is the same for every part and
every peer: it lives in the textures the one gear-shader surface already reads (no second material, no shader change). Each suit
has its own paint (STYLES). The lights (emission.png) and the atlas's reserved strip (the cavity and trim tiles) are left alone.

Standalone, to judge the look on a finished build without rebuilding it:
  blender -b <model>_rigged.blend --python wear.py -- <model> <texture dir> <out dir>"""
import math
import os
import sys

import bpy
import numpy as np

ATLAS = 4096           # px: the armour albedo
ATLAS_METAL = 2048     # px: the metal-smoothness map
TILE_V = 0.965         # the atlas's reserved strip starts here (build_frame.py TILE_V)
BEVEL = 0.007          # m: the edge mask's bevel radius
AO_DISTANCE = 0.45     # m
SEED = 20261104

# Per suit: the bare-metal colour showing at chipped ridges, then amounts 0..1 of each layer. Wear is an accent: the painted detail of the
# source maps must stay readable, so every layer is thin, low in contrast and confined to where wear really happens (raised ridges for chips,
# creases and seams for grime and rust, top faces for dust).
STYLES = {
    # Sirius: blue-black paint, bright chips on ridges, a few fine scuffs.
    # grade: the paint's own tone before the wear. white: over this luminance (from .38, full at .7) albedo is multiplied by this colour (a source's
    # bright white undersuit reads as a hole in a dark suit); tint and saturation for all of it; gloss: the highest smoothness, keeping a dark gear shader from mirroring the room.
    "M15": dict(grade=dict(gloss=0.62), bare=(0.36, 0.37, 0.41), chip=0.8, scratch=0.5, scratch_n=700, grime=0.5, grime_col=(0.02, 0.025, 0.035), dust=0.1,
                dust_col=(0.34, 0.35, 0.38), rust=0.0, oil=0.0, soot=0.1, weld=0.0, rough=0.3),
    # Taishan: heavy plate, more scratches, weld beads in the seams.
    "M16": dict(grade=dict(white=(0.22, 0.23, 0.22), tint=(0.80, 0.83, 0.74), gloss=0.55), bare=(0.42, 0.42, 0.43), chip=0.9, scratch=0.7, scratch_n=1100, grime=0.5, grime_col=(0.03, 0.03, 0.03), dust=0.12,
                dust_col=(0.45, 0.44, 0.41), rust=0.25, oil=0.0, soot=0.1, weld=0.7, rough=0.35),
    # Thunderbird: soot in the creases and thin glossy oil runs; its plate shows metal at the ridges.
    "M17": dict(grade=dict(tint=(0.74, 0.70, 0.70), saturation=0.82, gloss=0.52), bare=(0.40, 0.36, 0.33), chip=0.7, scratch=0.4, scratch_n=600, grime=0.6, grime_col=(0.02, 0.012, 0.01), dust=0.05,
                dust_col=(0.3, 0.26, 0.22), rust=0.1, oil=0.8, soot=0.8, weld=0.0, rough=0.4),
    # Night Owl: sand on the tops, rust in the seams.
    "M18": dict(grade=dict(gloss=0.62), bare=(0.40, 0.38, 0.34), chip=0.7, scratch=0.45, scratch_n=650, grime=0.4, grime_col=(0.1, 0.075, 0.05), dust=0.7,
                dust_col=(0.66, 0.53, 0.35), rust=0.5, oil=0.0, soot=0.08, weld=0.0, rough=0.3),
}


# ---- noise -----------------------------------------------------------------------------------------------------------------

def smooth(t):
    return t * t * (3.0 - 2.0 * t)


def value_noise(size, cells, rng):
    """Smooth value noise on size x size texels with `cells` random cells across (0..1)."""
    g = rng.random((cells + 2, cells + 2), dtype=np.float32)
    pos = np.arange(size, dtype=np.float32) * (cells / size)
    i = pos.astype(np.int64)
    f = smooth(pos - i)
    rows = g[:, i] * (1 - f) + g[:, i + 1] * f
    return rows[i] * (1 - f)[:, None] + rows[i + 1] * f[:, None]


def fbm(size, cells, octaves, rng, gain=0.5):
    total = np.zeros((size, size), np.float32)
    amp, norm = 1.0, 0.0
    for _ in range(octaves):
        total += amp * value_noise(size, cells, rng)
        norm += amp
        amp *= gain
        cells *= 2
    total /= norm
    lo, hi = np.percentile(total[::8, ::8], (2, 98))
    return np.clip((total - lo) / max(hi - lo, 1e-6), 0.0, 1.0)


def step(x, lo, hi):
    return smooth(np.clip((x - lo) / max(hi - lo, 1e-6), 0.0, 1.0))


def upscale(a, size):
    """Bilinear resize of an (h, w, c) array to size x size."""
    h = a.shape[0]
    if size < h:
        f = h // size
        return a.reshape(size, f, size, f, a.shape[2]).mean(axis=(1, 3))
    pos = np.clip((np.arange(size, dtype=np.float32) + 0.5) * (h / size) - 0.5, 0, h - 1)
    i = np.minimum(pos.astype(np.int64), h - 2)
    f = (pos - i).astype(np.float32)
    out = np.empty((size, size, a.shape[2]), np.float32)
    for c in range(a.shape[2]):
        rows = a[i, :, c] * (1 - f)[:, None] + a[i + 1, :, c] * f[:, None]
        out[:, :, c] = rows[:, i] * (1 - f) + rows[:, i + 1] * f
    return out


def scratches(size, count, lengths, rng, strength):
    """Thin straight scuffs, mostly along two directions (a suit is scraped along its length and across), a few free."""
    mask = np.zeros((size, size), np.float32)
    base = rng.random() * math.pi
    for _ in range(count):
        x, y = rng.random() * size, rng.random() * size
        r = rng.random()
        angle = base + (0.0 if r < 0.6 else math.pi / 2 if r < 0.75 else rng.random() * math.pi) + rng.normal(0, 0.12)
        length = lengths[0] + (lengths[1] - lengths[0]) * rng.random() ** 2.2
        n = int(length)
        t = np.arange(n, dtype=np.float32)
        wobble = np.cumsum(rng.normal(0, 0.05, n)).astype(np.float32)
        px = np.clip((x + np.cos(angle) * t - np.sin(angle) * wobble).astype(np.int64), 0, size - 2)
        py = np.clip((y + np.sin(angle) * t + np.cos(angle) * wobble).astype(np.int64), 0, size - 2)
        fade = np.minimum(1.0, np.minimum(t, n - t) / 12.0) * (0.45 + 0.55 * rng.random())
        mask[py, px] = np.maximum(mask[py, px], fade)
        if rng.random() < 0.4:
            mask[py + 1, px] = np.maximum(mask[py + 1, px], fade * 0.7)
    return np.clip(mask * strength, 0.0, 1.0)


# ---- the baked masks --------------------------------------------------------------------------------------------------------

def bake_masks(body, size, log):
    """{coverage, edge, up, ao} as (size, size) arrays (rows from the bottom), baked on body's armour UVs (material 0)."""
    import bmesh
    scene = bpy.context.scene
    # The armour alone is baked; the lining (material 1 and up) stays in the scene as an occluder, so its UVs never reach the atlas.
    me = body.data.copy()
    me.uv_layers.active = me.uv_layers["UVMap"]
    me.uv_layers["UVMap"].active_render = True
    bm = bmesh.new()
    bm.from_mesh(me)
    inside = [f for f in bm.faces if f.material_index != 0]
    bmesh.ops.delete(bm, geom=inside, context="FACES")
    bm.to_mesh(me)
    bm.free()
    occluder_mesh = body.data.copy()
    bm = bmesh.new()
    bm.from_mesh(occluder_mesh)
    bmesh.ops.delete(bm, geom=[f for f in bm.faces if f.material_index == 0], context="FACES")
    bm.to_mesh(occluder_mesh)
    bm.free()
    occluder_mesh.materials.clear()
    occluder = bpy.data.objects.new("wear-occluder", occluder_mesh)
    scene.collection.objects.link(occluder)
    temp = bpy.data.objects.new("wear-bake", me)
    scene.collection.objects.link(temp)
    shown = body.hide_render
    body.hide_render = True
    prior = [(o, o.hide_render) for o in bpy.data.objects if o not in (temp, body, occluder)]
    for o, _ in prior:
        o.hide_render = True

    def material(name, image):
        mat = bpy.data.materials.new(name)
        mat.use_nodes = True
        nt = mat.node_tree
        nt.nodes.clear()
        out = nt.nodes.new("ShaderNodeOutputMaterial")
        target = nt.nodes.new("ShaderNodeTexImage")
        target.image = image
        nt.nodes.active = target
        return mat, nt, out

    # Armour: R edge, G up, B 1 (coverage). Everything else (the lining, the shell's inside) bakes into a throwaway image.
    img = bpy.data.images.new("wear-masks", size, size, alpha=False, float_buffer=True)
    img.colorspace_settings.name = "Non-Color"
    armour, nt, out = material("wear-armour", img)
    geo = nt.nodes.new("ShaderNodeNewGeometry")
    bevel = nt.nodes.new("ShaderNodeBevel")
    bevel.samples = 10
    bevel.inputs["Radius"].default_value = BEVEL
    dot = nt.nodes.new("ShaderNodeVectorMath")
    dot.operation = "DOT_PRODUCT"
    nt.links.new(bevel.outputs["Normal"], dot.inputs[0])
    nt.links.new(geo.outputs["Normal"], dot.inputs[1])
    flat = nt.nodes.new("ShaderNodeMath")
    flat.operation = "SUBTRACT"
    flat.inputs[0].default_value = 1.0
    nt.links.new(dot.outputs["Value"], flat.inputs[1])
    gain = nt.nodes.new("ShaderNodeMath")
    gain.operation = "MULTIPLY"
    gain.inputs[1].default_value = 5.0
    nt.links.new(flat.outputs["Value"], gain.inputs[0])
    sep = nt.nodes.new("ShaderNodeSeparateXYZ")
    nt.links.new(geo.outputs["Normal"], sep.inputs[0])
    up = nt.nodes.new("ShaderNodeMath")
    up.operation = "MULTIPLY_ADD"
    up.inputs[1].default_value = 0.5
    up.inputs[2].default_value = 0.5
    nt.links.new(sep.outputs["Z"], up.inputs[0])
    comb = nt.nodes.new("ShaderNodeCombineXYZ")
    nt.links.new(gain.outputs["Value"], comb.inputs["X"])
    nt.links.new(up.outputs["Value"], comb.inputs["Y"])
    comb.inputs["Z"].default_value = 1.0
    emit = nt.nodes.new("ShaderNodeEmission")
    nt.links.new(comb.outputs["Vector"], emit.inputs["Color"])
    nt.links.new(emit.outputs["Emission"], out.inputs["Surface"])
    me.materials.clear()
    me.materials.append(armour)
    scene.render.engine = "CYCLES"
    scene.cycles.device = "CPU"
    scene.cycles.use_denoising = False
    scene.render.bake.margin = 4
    scene.render.bake.use_selected_to_active = False
    if scene.world is None:
        scene.world = bpy.data.worlds.new("wear-world")
    scene.world.light_settings.distance = AO_DISTANCE
    bpy.context.view_layer.objects.active = temp
    for o in bpy.context.view_layer.objects:
        o.select_set(o is temp)

    def read(image):
        px = np.empty(image.size[0] * image.size[1] * 4, np.float32)
        image.pixels.foreach_get(px)
        return px.reshape(image.size[1], image.size[0], 4)

    scene.cycles.samples = 4
    bpy.ops.object.bake(type="EMIT")
    masks = read(img)
    edge, upfacing, cover = masks[..., 0].copy(), masks[..., 1].copy(), masks[..., 2] > 0.5
    # The AO bake rewrites the same image: pull the first bake out first.
    scene.cycles.samples = 40
    bpy.ops.object.bake(type="AO")
    ao = read(img)[..., 0].copy()
    if os.environ.get("WEAR_DEBUG"):
        dbg = bpy.data.images.new("dbg", size, size, alpha=False)
        dbg.colorspace_settings.name = "Non-Color"
        ch = np.stack([np.clip(edge, 0, 1), np.clip(upfacing, 0, 1), np.clip(ao, 0, 1), np.ones_like(ao)], axis=-1)
        dbg.pixels.foreach_set(ch.astype(np.float32).ravel())
        dbg.filepath_raw = os.path.join(os.environ["WEAR_DEBUG"], "masks.png")
        dbg.file_format = "PNG"
        dbg.save()
    # Soft parts (hands): the weights of the hand bones, baked through an attribute node into the same emission.
    soft = np.zeros_like(ao)
    hands = [g.name for g in body.vertex_groups if g.name.startswith("hand")]
    if hands:
        for g in body.vertex_groups:
            temp.vertex_groups.new(name=g.name)
        total = None
        for name in hands:
            attr = nt.nodes.new("ShaderNodeAttribute")
            attr.attribute_name = name
            if total is None:
                total = attr.outputs["Fac"]
            else:
                add = nt.nodes.new("ShaderNodeMath")
                nt.links.new(total, add.inputs[0])
                nt.links.new(attr.outputs["Fac"], add.inputs[1])
                total = add.outputs["Value"]
        nt.links.new(total, emit.inputs["Color"])
        scene.cycles.samples = 4
        bpy.ops.object.bake(type="EMIT")
        soft = np.clip(read(img)[..., 0].copy(), 0.0, 1.0)
    log("wear bake: %d armour texels covered, edge %.2f mean, ao %.2f mean" % (int(cover.sum()), float(edge[cover].mean()), float(ao[cover].mean())))
    bpy.data.objects.remove(temp, do_unlink=True)
    bpy.data.objects.remove(occluder, do_unlink=True)
    bpy.data.meshes.remove(me)
    bpy.data.meshes.remove(occluder_mesh)
    bpy.data.materials.remove(armour)
    bpy.data.images.remove(img)
    body.hide_render = shown
    for o, h in prior:
        o.hide_render = h
    return cover, np.clip(edge, 0.0, 1.0), np.clip(upfacing * 2.0 - 1.0, -1.0, 1.0), np.clip(ao, 0.0, 1.0), soft


# ---- the paint --------------------------------------------------------------------------------------------------------------

def mix(a, b, t):
    return a + (b - a) * t[..., None]


def weather(style, alb, metal, smooth_map, cover, edge, nz, ao, lit, soft, size):
    """alb (size, size, 3), metal and smooth_map (size, size): worn in place, weighed by the wear mask. Everything is fine-grained
    (no blotch bigger than a few millimetres) and low in contrast; soft parts (soft: hands) get no chipped paint."""
    s = STYLES[style]
    rng = np.random.default_rng(SEED + int(style[1:]))
    allow = cover & ~lit
    allow[int(TILE_V * size):, :] = False
    w = allow.astype(np.float32)
    hard = 1.0 - soft
    fine = fbm(size, 320, 3, rng)
    grain = fbm(size, 110, 3, rng)
    orig_alb, orig_m, orig_s = alb.copy(), metal.copy(), smooth_map.copy()
    grade = s.get("grade", {})
    if "white" in grade:
        lum = alb @ np.array([0.2126, 0.7152, 0.0722], np.float32)
        high = step(lum, 0.38, 0.7)
        alb[:] = mix(alb, alb * np.array(grade["white"], np.float32), high)
    if "saturation" in grade:
        lum = (alb @ np.array([0.2126, 0.7152, 0.0722], np.float32))[..., None]
        alb[:] = lum + (alb - lum) * grade["saturation"]
    if "tint" in grade:
        alb *= np.array(grade["tint"], np.float32)
    if "gloss" in grade:
        smooth_map[:] = np.minimum(smooth_map, grade["gloss"])
    exposure = np.clip(ao * 1.25 - 0.1, 0.0, 1.0)
    crease = (1.0 - ao) ** 2

    # A slightly uneven finish: fine breakup of the smoothness only (a flat gloss reads as plastic).
    smooth_map *= 1.0 - s["rough"] * 0.55 * (0.4 * fine + 0.6 * grain)
    # Grime: thin and dark, only in the creases.
    g = np.clip(crease * (0.5 + 0.5 * grain) * s["grime"] * 0.6, 0.0, 0.45)
    alb *= (1.0 - 0.5 * g)[..., None]
    alb[:] = mix(alb, np.array(s["grime_col"], np.float32) + alb * 0.2, g * 0.5)
    smooth_map *= 1.0 - 0.3 * g
    # Soot (creases only).
    if s["soot"] > 0:
        burn = np.clip(crease * (0.4 + 0.6 * fine) * s["soot"] * 0.5, 0.0, 0.4)
        alb *= (1.0 - 0.45 * burn)[..., None]
    # Oil: thin glossy runs on side faces, low contrast.
    if s["oil"] > 0:
        runs = step(fbm(size, 70, 2, rng), 0.66, 0.82) * (1.0 - step(nz, 0.3, 0.7)) * s["oil"] * 0.5
        alb *= (1.0 - 0.35 * runs)[..., None]
        smooth_map[:] = smooth_map * (1.0 - runs) + 0.92 * runs
    # Rust: only in the seams and deep creases, low contrast.
    if s["rust"] > 0:
        seed = crease * 0.8 + edge * 0.2 + (fine - 0.5) * 0.45
        r = step(seed, 0.55, 0.85) * s["rust"] * 0.45 * hard
        tint = np.stack([0.34 + 0.2 * fine, 0.14 + 0.1 * fine, 0.05 + 0.04 * fine], axis=-1)
        alb[:] = mix(alb, tint, r)
        metal *= 1.0 - r
        smooth_map[:] = smooth_map * (1.0 - r) + (0.15 + 0.2 * fine) * r
    # Dust: on top faces only, thin.
    if s["dust"] > 0:
        d = step(nz, 0.5, 0.9) * (0.35 + 0.65 * grain) * (0.4 + 0.6 * ao) * s["dust"] * 0.45
        alb[:] = mix(alb, np.array(s["dust_col"], np.float32), d)
        metal *= 1.0 - d
        smooth_map[:] = smooth_map * (1.0 - d) + 0.1 * d
    # Chipped paint: only on raised ridges (a high edge value), thin and broken (fine noise decides which stretches), not on soft parts.
    ridge = step(edge, 0.3, 0.7)
    chip = step(ridge + (fine - 0.5) * 1.1, 0.62, 0.7) * hard * s["chip"]
    bare = np.array(s["bare"], np.float32)
    alb[:] = mix(alb, bare * (0.8 + 0.4 * fine[..., None]), chip * 0.85)
    metal[:] = np.maximum(metal, chip * 0.9)
    smooth_map[:] = smooth_map * (1.0 - chip * 0.8) + (0.4 + 0.25 * fine) * chip * 0.8
    # Scratches: fine short lines on exposed hard surfaces, faint.
    sc = scratches(size, s["scratch_n"], (20, 120), rng, s["scratch"]) * (0.2 + 0.8 * exposure) * hard
    alb[:] = mix(alb, bare * 1.1, np.clip(sc * 0.4, 0.0, 1.0))
    metal[:] = np.maximum(metal, sc * 0.6)
    smooth_map[:] = smooth_map * (1.0 - sc * 0.4) + 0.5 * sc * 0.4
    # Weld beads: thin lumpy runs along the seams.
    if s["weld"] > 0:
        seam = step(edge, 0.15, 0.35) * (1.0 - step(ao, 0.35, 0.75))
        lumps = step(fbm(size, 260, 2, rng), 0.45, 0.65)
        wl = np.clip(seam * (0.4 + 0.6 * lumps) * s["weld"] * 0.8, 0.0, 0.7) * hard
        alb[:] = mix(alb, np.array([0.2, 0.19, 0.18], np.float32), wl)
        metal[:] = np.maximum(metal, wl * 0.8)
        smooth_map[:] = smooth_map * (1.0 - wl) + 0.35 * wl
    alb[:] = mix(orig_alb, np.clip(alb, 0.0, 1.0), w)
    metal[:] = orig_m + (np.clip(metal, 0.0, 1.0) - orig_m) * w
    smooth_map[:] = orig_s + (np.clip(smooth_map, 0.0, 1.0) - orig_s) * w


def paint(body, model, out_dir, log, size=ATLAS):
    """Weathers <out_dir>/<model>_albedo.png (to size px) and _metallic.png (ATLAS_METAL px) in place, from the shell's baked masks."""
    if model not in STYLES:
        raise SystemExit("wear.py has no paint for %s" % model)
    paths = {k: os.path.join(out_dir, "%s_%s.png" % (model, k)) for k in ("albedo", "metallic", "emission")}

    def read(path):
        img = bpy.data.images.load(path, check_existing=False)
        img.colorspace_settings.name = "Non-Color"
        w, h = img.size
        px = np.empty(w * h * 4, np.float32)
        img.pixels.foreach_get(px)
        bpy.data.images.remove(img)
        return px.reshape(h, w, 4)

    def write(path, pixels):
        h, w = pixels.shape[:2]
        img = bpy.data.images.new("wear-out", w, h, alpha=True)
        img.colorspace_settings.name = "Non-Color"
        img.pixels.foreach_set(np.clip(pixels, 0.0, 1.0).astype(np.float32).ravel())
        img.filepath_raw = path
        img.file_format = "PNG"
        img.save()
        bpy.data.images.remove(img)

    cover, edge, nz, ao, soft = bake_masks(body, size, log)
    # Texels just outside an island keep their paint bleed from the margin; wear them like the island.
    albedo = upscale(read(paths["albedo"])[..., :3], size)
    metal4 = read(paths["metallic"])
    metal = upscale(metal4[..., :1], size)[..., 0]
    smooth_map = upscale(metal4[..., 1:2], size)[..., 0]
    emission = read(paths["emission"])[..., :3].max(axis=2)
    lit = upscale(emission[..., None], size)[..., 0] > 0.04
    # Keep the glow clear of the paint around it.
    grown = lit.copy()
    for shift in (-3, 3):
        grown |= np.roll(lit, shift, axis=0)
        grown |= np.roll(lit, shift, axis=1)
    weather(model, albedo, metal, smooth_map, cover, edge, nz, ao, grown, soft, size)
    write(paths["albedo"], np.concatenate([albedo, np.ones((size, size, 1), np.float32)], axis=2))
    f = max(1, size // ATLAS_METAL)
    n = size // f
    m = metal.reshape(n, f, n, f).mean(axis=(1, 3))
    sm = smooth_map.reshape(n, f, n, f).mean(axis=(1, 3))
    write(paths["metallic"], np.stack([m, sm, np.zeros_like(m), sm], axis=-1))
    log("wear %s: albedo %d px, metallic %d px; metal %.2f mean, smoothness %.2f..%.2f" % (
        model, size, ATLAS_METAL, float(m.mean()), float(sm.min()), float(sm.max())))


if __name__ == "__main__" and "--" in sys.argv:
    import shutil
    model, textures, out = sys.argv[sys.argv.index("--") + 1:][:3]
    os.makedirs(out, exist_ok=True)
    for kind in ("albedo", "metallic", "emission", "normal"):
        shutil.copyfile(os.path.join(textures, "%s_%s.png" % (model, kind)), os.path.join(out, "%s_%s.png" % (model, kind)))
    mesh = max((o for o in bpy.data.objects if o.type == "MESH"), key=lambda o: len(o.data.polygons))
    paint(mesh, model, out, print, int(os.environ.get("WEAR_SIZE", ATLAS)))
