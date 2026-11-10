# Renders one call-in icon (a transparent PNG of the model, three-quarter view, orthographic) for the ForgeOrdnance lobby picker.
# Run by tools/build_icons.ps1:
#   blender -b <model.blend> --factory-startup --python-exit-code 1 --python render_icon.py -- --out <png> [--layout L] [--glow r,g,b] [--tint r,g,b] [--mark M] [--supply kinds --pickups dir] [--exposure E] [--size 256]
# Layouts: single, scatter (five copies spread out), row (three copies stepping forward), line (five copies in a line),
# beam (no model: a beacon pole under a column of light), supply (no model file: the game's own pickups, one per name in --supply,
# read from --pickups <dir>/<name>.obj + <name>.json as tools/extract_pickups.py wrote them from the game files: ammo, medi, tool,
# disinfect, cfoam, syringe, meleesyringe, tripmine, foammine, fogrepeller, lockmelter). --tint r,g,b turns the finished picture into one colour (its own light and dark kept, drawn in that
# colour). --glow adds a coloured light under the model (fire, target, freeze) or,
# with --glow-at front, in front of it (tints the whole model). --exposure E brightens a dark model in its own colours (stops of light, with a matching
# front fill) so it reads on the picker's dark rows.
import argparse
import math
import sys

import bpy
from mathutils import Vector

args = argparse.ArgumentParser()
args.add_argument("--out", required=True)
args.add_argument("--layout", default="single", choices=["single", "scatter", "row", "line", "beam", "supply"])
args.add_argument("--supply", default="")
args.add_argument("--pickups", default="")
args.add_argument("--tint", default="")
args.add_argument("--mark", default="", choices=["", "crosshair", "flame", "lock"])
args.add_argument("--view", default="0.85,-1,0.65")
args.add_argument("--glow", default="")
args.add_argument("--glow-at", default="below", choices=["below", "front"])
args.add_argument("--also", default="")  # <blend>[,fit[,count]]: a second model (a munition) standing in front, fit = its size as a share of the first model's, count copies fanned out across the front (thrown)
args.add_argument("--foam", action="store_true")  # lumps of C-Foam heaped beside the model and flung around it
args.add_argument("--exposure", type=float, default=0.0)
args.add_argument("--size", type=int, default=256)
a = args.parse_args(sys.argv[sys.argv.index("--") + 1:])

scene = bpy.context.scene
for obj in list(scene.objects):
    if obj.type in {"CAMERA", "LIGHT"}:
        bpy.data.objects.remove(obj, do_unlink=True)
meshes = [o for o in scene.objects if o.type == "MESH" and o.visible_get() and not o.hide_render]


def emissive(name, colour, strength):
    mat = bpy.data.materials.new(name)
    mat.use_nodes = True
    nodes = mat.node_tree.nodes
    bsdf = next(n for n in nodes if n.type == "BSDF_PRINCIPLED")
    bsdf.inputs["Base Color"].default_value = (0, 0, 0, 1)  # emission only: the lights would wash the colour out
    bsdf.inputs["Emission Color"].default_value = (*colour, 1)
    bsdf.inputs["Emission Strength"].default_value = strength
    return mat


def solid(name, colour, strength=0.0, roughness=0.55):
    mat = bpy.data.materials.new(name)
    mat.use_nodes = True
    bsdf = next(n for n in mat.node_tree.nodes if n.type == "BSDF_PRINCIPLED")
    bsdf.inputs["Base Color"].default_value = (*colour, 1)
    bsdf.inputs["Roughness"].default_value = roughness
    if strength:
        bsdf.inputs["Emission Color"].default_value = (*colour, 1)
        bsdf.inputs["Emission Strength"].default_value = strength
    return mat


def part(kind, location, material, **size):
    """One primitive (box: dims, cyl: r and h, sphere: r, torus: r and tube) at location; returns the object."""
    if kind == "box":
        bpy.ops.mesh.primitive_cube_add(size=1, location=location)
        bpy.context.active_object.dimensions = size["dims"]
    elif kind == "cyl":
        bpy.ops.mesh.primitive_cylinder_add(radius=size["r"], depth=size["h"], location=location, vertices=24)
    elif kind == "sphere":
        bpy.ops.mesh.primitive_uv_sphere_add(radius=size["r"], location=location, segments=24, ring_count=12)
    else:
        bpy.ops.mesh.primitive_torus_add(major_radius=size["r"], minor_radius=size["tube"], location=location)
    obj = bpy.context.active_object
    obj.data.materials.append(material)
    return obj


def place_pickups(kinds, directory):
    """One game pickup per name in kinds, as the game stands it (Z up in the OBJ), laid out left to right in rows of two, the back row
    centred behind the front one; every piece keeps its true size so a grenade is smaller than a pack."""
    import json
    import os
    cache = {}

    def load(kind):
        if kind in cache:
            return cache[kind]
        verts, uvs, faces, group_of_face, group = [], [], [], [], 0
        with open(os.path.join(directory, kind + ".obj")) as handle:
            for line in handle:
                parts = line.split()
                if not parts:
                    continue
                if parts[0] == "usemtl":
                    group = int(parts[1])
                elif parts[0] == "v":
                    verts.append(tuple(float(v) for v in parts[1:4]))
                elif parts[0] == "vt":
                    uvs.append(tuple(float(v) for v in parts[1:3]))
                elif parts[0] == "f":
                    faces.append(tuple(int(v.split("/")[0]) - 1 for v in parts[1:4]))
                    group_of_face.append(group)
        mats = []
        for texture in json.load(open(os.path.join(directory, kind + ".json")))["textures"]:
            mat = bpy.data.materials.new(texture)
            mat.use_nodes = True
            bsdf = next(n for n in mat.node_tree.nodes if n.type == "BSDF_PRINCIPLED")
            image = mat.node_tree.nodes.new("ShaderNodeTexImage")
            image.image = bpy.data.images.load(os.path.join(directory, texture))
            mat.node_tree.links.new(image.outputs["Color"], bsdf.inputs["Base Color"])
            bsdf.inputs["Roughness"].default_value = 0.5
            mats.append(mat)
        cache[kind] = (verts, uvs, faces, group_of_face, mats)
        return cache[kind]

    objects = []
    for kind in kinds:
        verts, uvs, faces, group_of_face, mats = load(kind)
        mesh = bpy.data.meshes.new(kind)
        mesh.from_pydata(verts, [], faces)
        layer = mesh.uv_layers.new(name="UV")
        for loop in mesh.loops:
            layer.data[loop.index].uv = uvs[loop.vertex_index]
        for mat in mats:
            mesh.materials.append(mat)
        for polygon, group in zip(mesh.polygons, group_of_face):
            polygon.material_index = group
        mesh.update()
        obj = bpy.data.objects.new(kind, mesh)
        scene.collection.objects.link(obj)
        objects.append(obj)
    bpy.context.view_layer.update()
    sizes = [(o.dimensions.x, o.dimensions.y) for o in objects]
    pitch = max(max(s) for s in sizes) * 1.15
    rows = [objects[:2], objects[2:]] if len(objects) > 2 else [objects]
    if len(objects) == 3:
        rows = [objects[1:], objects[:1]]  # the odd one stands behind, centred
    for r, row in enumerate(rows):
        for c, obj in enumerate(row):
            lo = Vector(min(v[i] for v in (obj.matrix_world @ Vector(co) for co in obj.bound_box)) for i in range(3))
            hi = Vector(max(v[i] for v in (obj.matrix_world @ Vector(co) for co in obj.bound_box)) for i in range(3))
            centre = (lo + hi) / 2
            obj.location = Vector(((c - (len(row) - 1) / 2) * pitch - centre.x, r * pitch - centre.y, -lo.z))
    bpy.context.view_layer.update()
    return objects

if a.layout == "beam":
    for obj in meshes:
        bpy.data.objects.remove(obj, do_unlink=True)
    colour = (1.0, 0.35, 0.12)
    base = part("cyl", (0, 0, 0.02), solid("base", (0.12, 0.12, 0.14)), r=0.5, h=0.04)
    pole = part("cyl", (0, 0, 0.45), solid("pole", (0.2, 0.2, 0.23), 0.0, 0.4), r=0.06, h=0.9)
    lamp = part("sphere", (0, 0, 0.93), emissive("lamp", colour, 4), r=0.12)
    column = part("cyl", (0, 0, 1.8), emissive("column", colour, 2.0), r=0.13, h=1.6)
    core = part("cyl", (0, 0, 1.8), emissive("core", (1.0, 0.9, 0.8), 6.0), r=0.05, h=1.6)
    meshes = [base, pole, lamp, column, core]
elif a.layout == "supply":
    for obj in meshes:
        bpy.data.objects.remove(obj, do_unlink=True)
    meshes = place_pickups([k for k in a.supply.split(",") if k], a.pickups)
if a.also and meshes:
    also_path, _, also_rest = a.also.partition(",")
    also_fit, _, also_count = also_rest.partition(",")
    with bpy.data.libraries.load(also_path) as (source, loaded):
        loaded.objects = list(source.objects)
    extra = [o for o in loaded.objects if o is not None and o.type == "MESH"]
    for o in extra:
        scene.collection.objects.link(o)
    bpy.context.view_layer.update()
    lo, hi = Vector((math.inf,) * 3), Vector((-math.inf,) * 3)
    for o in meshes:
        for corner in o.bound_box:
            w = o.matrix_world @ Vector(corner)
            lo, hi = Vector(map(min, lo, w)), Vector(map(max, hi, w))
    elo, ehi = Vector((math.inf,) * 3), Vector((-math.inf,) * 3)
    for o in extra:
        for corner in o.bound_box:
            w = o.matrix_world @ Vector(corner)
            elo, ehi = Vector(map(min, elo, w)), Vector(map(max, ehi, w))
    ecentre = (elo + ehi) / 2
    scale = (max(hi - lo) * float(also_fit or 0.45)) / max(max(ehi - elo), 1e-6)
    target = Vector(((lo.x + hi.x) / 2 - (hi.x - lo.x) * 0.35, lo.y - (hi.y - lo.y) * 0.15 - (ehi.y - elo.y) * scale * 0.5, lo.z + (ehi.z - elo.z) * scale * 0.5))
    count = int(also_count or 1)
    width, half = hi.x - lo.x, (ehi.y - elo.y) * scale * 0.5
    groups = [extra] + [[e.copy() for e in extra] for _ in range(count - 1)]  # copies before any is moved
    placed = []
    for k in range(count):
        if count == 1:
            spot = target
        else:  # an arc of thrown ones: spread across the front, the middle ones nearer, each lying a little differently
            u = (k - (count - 1) / 2) / ((count - 1) / 2)
            spot = Vector(((lo.x + hi.x) / 2 + u * width * 0.95, lo.y - (hi.y - lo.y) * (0.12 + 0.2 * (1 - abs(u))) - half, lo.z + (ehi.z - elo.z) * scale * 0.5))
        for o in groups[k]:
            if k:
                scene.collection.objects.link(o)
            o.location = (o.location - ecentre) * scale + spot
            o.scale = o.scale * scale
            if k:
                o.rotation_euler[2] += k * 1.9
            placed.append(o)
    extra = placed
    meshes = meshes + extra
if a.foam and meshes:
    lo, hi = Vector((math.inf,) * 3), Vector((-math.inf,) * 3)
    for o in meshes:
        for corner in o.bound_box:
            w = o.matrix_world @ Vector(corner)
            lo, hi = Vector(map(min, lo, w)), Vector(map(max, hi, w))
    foam = solid("foam", (0.78, 0.93, 1.0), 0.25, 0.85)
    w, d, h = hi.x - lo.x, hi.y - lo.y, hi.z - lo.z
    cx = (lo.x + hi.x) / 2
    # (x, y, z as shares of the model's width, depth, height; radius as a share of its height): a heap on the ground right of the glue
    # grenade, and clumps flung up and out around the dispenser
    for x, y, z, r in ((0.62, -0.30, 0.00, 0.13), (0.80, -0.38, 0.00, 0.10), (0.70, -0.52, 0.00, 0.09), (0.55, -0.50, 0.00, 0.08), (0.78, -0.18, 0.00, 0.07),
                       (0.66, -0.36, 0.11, 0.08), (-0.75, 0.10, 0.70, 0.07), (0.78, 0.05, 0.55, 0.06), (-0.55, -0.15, 0.95, 0.05), (0.60, 0.00, 0.85, 0.05)):
        lump = part("sphere", (cx + x * w, lo.y + d * (0.5 + y), lo.z + h * z + r * h * 1.0), foam, r=r * h * 1.5)
        lump.scale = (1.0, 1.0, 0.8)
        meshes.append(lump)
if not meshes:
    raise SystemExit("No visible mesh in this file.")


def bounds(objects):
    lo = Vector((math.inf,) * 3)
    hi = Vector((-math.inf,) * 3)
    for o in objects:
        for corner in o.bound_box:
            w = o.matrix_world @ Vector(corner)
            lo = Vector(map(min, lo, w))
            hi = Vector(map(max, hi, w))
    return lo, hi


lo, hi = bounds(meshes)
size = hi - lo
span = max(size.x, size.y, size.z)
offsets = {
    "single": [],
    "scatter": [(-0.45, 0.3, 0.1), (0.5, 0.2, -0.1), (-0.2, -0.45, 0.05), (0.3, -0.4, -0.05)],
    "row": [(0.4, 0.2, 0), (0.8, 0.4, 0)],
    "line": [(0.7, 0, 0), (1.4, 0, 0), (-0.7, 0, 0), (-1.4, 0, 0)],
    "beam": [],
    "supply": [],
}[a.layout]
copies = []
for dx, dy, dz in offsets:
    for o in meshes:
        c = o.copy()
        scene.collection.objects.link(c)
        c.location = o.location + Vector((dx, dy, dz)) * span
        copies.append(c)
everything = meshes + copies
lo, hi = bounds(everything)
centre = (lo + hi) / 2

# Three-quarter view from the front right, slightly above (Blender: -Y is front).
view = Vector(tuple(float(v) for v in a.view.split(","))).normalized()
camera = bpy.data.objects.new("IconCamera", bpy.data.cameras.new("IconCamera"))
scene.collection.objects.link(camera)
camera.data.type = "ORTHO"
camera.location = centre + view * (span * 10 + 10)
camera.rotation_euler = (-view).to_track_quat("-Z", "Y").to_euler()
camera.data.clip_end = span * 40 + 40
scene.camera = camera
bpy.context.view_layer.update()
right = camera.matrix_world.to_3x3() @ Vector((1, 0, 0))
up = camera.matrix_world.to_3x3() @ Vector((0, 1, 0))
extent = 0.0
for o in everything:
    for corner in o.bound_box:
        d = o.matrix_world @ Vector(corner) - centre
        extent = max(extent, abs(d.dot(right)), abs(d.dot(up)))
camera.data.ortho_scale = extent * 2 * 1.12


def light(name, kind, energy, direction, colour=(1, 1, 1), location=None):
    data = bpy.data.lights.new(name, kind)
    data.energy = energy
    data.color = colour
    obj = bpy.data.objects.new(name, data)
    scene.collection.objects.link(obj)
    if location is not None:
        obj.location = location
    obj.rotation_euler = (-Vector(direction)).to_track_quat("-Z", "Y").to_euler()
    return obj


light("Key", "SUN", 4.0, (0.6, -0.8, 0.9))
light("Fill", "SUN", 1.2, (-0.9, -0.4, 0.3))
light("Rim", "SUN", 3.0, (-0.3, 0.9, 0.6))
if a.glow:
    colour = tuple(float(v) for v in a.glow.split(","))
    at = centre - Vector((0, 0, size.z * 0.4)) if a.glow_at == "below" else centre + view * span * 0.9
    point = light("Glow", "POINT", (60.0 if a.glow_at == "below" else 300.0) * span * span + 20, (0, 0, 1), colour, at)
    point.data.shadow_soft_size = span * 0.5

world = scene.world or bpy.data.worlds.new("World")
scene.world = world
world.use_nodes = True
background = next(n for n in world.node_tree.nodes if n.type == "BACKGROUND")
background.inputs["Color"].default_value = (0.05, 0.05, 0.06, 1)
background.inputs["Strength"].default_value = 0.6

for engine in ("BLENDER_EEVEE", "BLENDER_EEVEE_NEXT"):
    try:
        scene.render.engine = engine
        break
    except TypeError:
        continue
scene.render.film_transparent = True
if a.layout in ("beam", "supply"):
    scene.view_settings.view_transform = "Standard"  # keeps the beam's colour instead of washing it out
scene.view_settings.exposure = a.exposure
scene.render.resolution_x = scene.render.resolution_y = a.size
scene.render.resolution_percentage = 100
scene.render.image_settings.file_format = "PNG"
scene.render.image_settings.color_mode = "RGBA"
scene.render.filepath = a.out
bpy.ops.render.render(write_still=True)
if a.tint:
    import numpy
    tint = numpy.array([float(v) for v in a.tint.split(",")])
    image = bpy.data.images.load(a.out)
    image.colorspace_settings.name = "Non-Color"  # raw bytes in, raw bytes out
    pixels = numpy.array(image.pixels[:]).reshape(-1, 4)
    light = pixels[:, :3] @ numpy.array([0.2126, 0.7152, 0.0722])
    covered = pixels[:, 3] > 0.03
    top = max(float(numpy.percentile(light[covered], 98)), 1e-3) if covered.any() else 1.0
    shade = numpy.clip(light / top, 0.0, 1.0) ** 1.4
    pixels[:, :3] = tint * (0.12 + 0.88 * shade)[:, None]
    if a.mark:
        # A sign drawn into the picture, one step brighter than the tint with a dark rim, upper left where the shell leaves room:
        # crosshair (ring and cross), flame (a flame with a dark core) or lock (four corner brackets around a dot).
        n = a.size
        gy, gx = numpy.mgrid[0:n, 0:n].astype(float)
        cx, cy = n * 0.30, n * 0.70  # y counts up from the bottom here
        px, py = (gx - cx) * 256.0 / n, (gy - cy) * 256.0 / n

        def box(x0, x1, y0, y1):
            return numpy.maximum(numpy.maximum(x0 - px, px - x1), numpy.maximum(y0 - py, py - y1))

        def circle(x, y, r):
            return numpy.hypot(px - x, py - y) - r

        def flame(scale, y):
            """A flame, tip up: a triangle from (0, 54) joined with a circle at (0, -10) r 30, scaled and moved down to y."""
            sx, sy = px / scale, (py - y) / scale
            right = (52 * sx + 27 * (sy - 54)) / 58.6
            left = (-52 * sx + 27 * (sy - 54)) / 58.6
            tip = numpy.maximum(numpy.maximum(right, left), 2 - sy)
            return numpy.minimum(numpy.hypot(sx, sy + 10) - 30, tip) * scale
        if a.mark == "crosshair":
            ring = numpy.abs(numpy.hypot(px, py) - 34) - 3.5
            shape = numpy.minimum(numpy.minimum(ring, box(-50, 50, -3.5, 3.5)), box(-3.5, 3.5, -50, 50))
            core = None
        elif a.mark == "flame":
            shape = flame(1.0, 0)
            core = flame(0.42, -22)
        else:
            shape = circle(0, 0, 8)
            for sx in (-1, 1):
                for sy in (-1, 1):
                    shape = numpy.minimum(shape, box(min(sx * 46, sx * 20), max(sx * 46, sx * 20), min(sy * 46, sy * 46 - sy * 7), max(sy * 46, sy * 46 - sy * 7)))
                    shape = numpy.minimum(shape, box(min(sx * 46, sx * 46 - sx * 7), max(sx * 46, sx * 46 - sx * 7), min(sy * 46, sy * 20), max(sy * 46, sy * 20)))
            core = None
        bright = numpy.minimum(tint * 0.7 + 0.3, 1.0)
        dark = tint * 0.08
        rim = numpy.clip(1.0 - (shape - 4.0), 0.0, 1.0)  # about 1 px soft edge
        fill = numpy.clip(0.5 - shape, 0.0, 1.0)
        for coverage, colour in ((rim, dark), (fill, bright), (numpy.clip(0.5 - core, 0.0, 1.0) if core is not None else None, dark)):
            if coverage is None:
                continue
            c = coverage.reshape(-1)[:, None]
            pixels[:, :3] = pixels[:, :3] * (1 - c) + colour * c
            pixels[:, 3:4] = numpy.maximum(pixels[:, 3:4], c)
    image.pixels[:] = pixels.reshape(-1).tolist()
    image.filepath_raw = a.out
    image.file_format = "PNG"
    image.save()
print("ICON_RENDERED " + a.out)

