"""Inside Blender: assembles the baked nodes of one model (tripo_masters.py) into its master .blend: one atlas, one material, the nodes at their places
and in the hierarchy the game moves them by, oriented the way the code expects. blender -b --factory-startup --python tripo_assemble.py -- <assemble.json>"""
import json
import math
import os
import sys

import bpy
import numpy as np
from mathutils import Matrix, Vector

cfg = json.load(open(sys.argv[sys.argv.index("--") + 1], encoding="utf-8"))
work, out, prefab = cfg["work"], cfg["out"], cfg["prefab"]
os.makedirs(out, exist_ok=True)
info = json.load(open(os.path.join(work, "split", "nodes.json"), encoding="utf-8"))
size = cfg["atlas"]
for leftover in list(bpy.data.objects):  # the startup scene's cube, camera and light
    bpy.data.objects.remove(leftover)


def read(path):
    if not os.path.exists(path):
        return None
    image = bpy.data.images.load(path)
    w, h = image.size
    data = np.empty(w * h * 4, dtype=np.float32)
    image.pixels.foreach_get(data)
    bpy.data.images.remove(image)
    return data.reshape(h, w, 4)


def save(name, array, colour):
    image = bpy.data.images.new(prefab + "_" + name, size, size, alpha=True)
    image.colorspace_settings.name = "sRGB" if colour else "Non-Color"
    image.pixels.foreach_set(np.ascontiguousarray(array, dtype=np.float32).ravel())
    image.filepath_raw = os.path.join(out, prefab + "_" + name + ".png")
    image.file_format = "PNG"
    image.save()
    return image


def place(tiles):
    """Square tiles, largest first, each in the smallest free rectangle that holds it; returns (x, y) in pixels per node."""
    free = [(0, 0, size, size)]
    at = {}
    for name in sorted(tiles, key=lambda n: (-tiles[n], n)):
        side = tiles[name]
        free.sort(key=lambda f: f[2] * f[3])
        slot = next((f for f in free if min(f[2], f[3]) >= side), None)
        if slot is None:
            raise SystemExit("The tiles of " + prefab + " do not fit the atlas.")
        free.remove(slot)
        x, y, w, h = slot
        free += [(x + side, y, w - side, side), (x, y + side, w, h - side)]
        at[name] = (x, y)
    return at

def bare_metal(colour):
    """A source with no material maps (a Tripo colour texture): worn, bright, unsaturated texels are bare metal, the rest matte paint."""
    rgb = colour[..., :3]
    top, low = rgb.max(-1), rgb.min(-1)
    sat = (top - low) / np.maximum(top, 1e-4)
    return np.clip((top - 0.25) / 0.25, 0, 1) * (1 - np.clip((sat - 0.15) / 0.25, 0, 1))


AO_WEIGHT = 0.6
at = place(cfg["tiles"])
base = np.zeros((size, size, 4), dtype=np.float32)
base[..., 3] = 1
normal = np.zeros((size, size, 4), dtype=np.float32)
normal[...] = (0.5, 0.5, 1, 1)
orm = np.zeros((size, size, 4), dtype=np.float32)
orm[...] = (1, 0.6, 0, 1)
objects = {}
for name in cfg["nodes"]:
    tex = os.path.join(work, name, "textures", name + "_")
    x, y = at[name]
    side = cfg["tiles"][name]
    colour = read(tex + "basecolor.png")
    if colour is None:
        raise SystemExit(name + ": no colour was baked.")
    ao = read(tex + "ao.png")
    if ao is not None:
        colour[..., :3] *= ((1 - AO_WEIGHT * (1 - ao[..., :1])) ** (1 / 2.2))
    base[y:y + side, x:x + side] = colour
    nm = read(tex + "normal_opengl.png")
    if nm is not None:
        # An inside-out patch of the source bakes normals pointing into the surface, which render black: turn them back.
        inward = nm[..., 2] < 0.5
        nm[inward, :3] = 1 - nm[inward, :3]
        print("INWARD", name, round(float(inward.mean()), 4))
        normal[y:y + side, x:x + side] = nm
    rough, metal = read(tex + "roughness.png"), read(tex + "metallic.png")
    report = json.load(open(os.path.join(work, name, "report.json"), encoding="utf-8"))
    plain = rough is None or metal is None or ("roughness" in report.get("maps_skipped", {}))
    tile = orm[y:y + side, x:x + side]
    if plain or (np.ptp(rough[..., 0]) < 0.02 and np.ptp(metal[..., 0]) < 0.02):
        worn = bare_metal(colour)
        tile[..., 1] = 0.62 - 0.22 * worn
        tile[..., 2] = 0.3 + 0.6 * worn
    else:
        tile[..., 1] = rough[..., 0]
        tile[..., 2] = metal[..., 0]
    bpy.ops.import_scene.gltf(filepath=os.path.join(work, name, name + ".glb"))
    meshes = [o for o in bpy.context.selected_objects if o.type == "MESH"]
    if len(meshes) != 1:
        raise SystemExit(name + ": expected one mesh in the baked file.")
    obj = meshes[0]
    for other in bpy.context.selected_objects:
        if other is not obj:
            bpy.data.objects.remove(other)
    obj.parent = None
    obj.matrix_world = Matrix.Identity(4)
    obj.name = name
    obj.data.name = prefab + "_" + name
    obj.data.materials.clear()
    uv = obj.data.uv_layers.active.data
    coords = np.empty(len(uv) * 2, dtype=np.float32)
    uv.foreach_get("uv", coords)
    coords = coords.reshape(-1, 2) * (side / size) + np.array([x, y], dtype=np.float32) / size
    uv.foreach_set("uv", coords.ravel())
    objects[name] = obj

material = bpy.data.materials.new(prefab)
material.use_nodes = True
tree = material.node_tree
bsdf = next(n for n in tree.nodes if n.type == "BSDF_PRINCIPLED")
node = tree.nodes.new("ShaderNodeTexImage")
node.image = save("basecolor", base, True)
tree.links.new(node.outputs["Color"], bsdf.inputs["Base Color"])
node = tree.nodes.new("ShaderNodeTexImage")
node.image = save("orm", orm, False)
split = tree.nodes.new("ShaderNodeSeparateColor")
tree.links.new(node.outputs["Color"], split.inputs[0])
tree.links.new(split.outputs[1], bsdf.inputs["Roughness"])
tree.links.new(split.outputs[2], bsdf.inputs["Metallic"])
node = tree.nodes.new("ShaderNodeTexImage")
node.image = save("normal", normal, False)
nm = tree.nodes.new("ShaderNodeNormalMap")
tree.links.new(node.outputs["Color"], nm.inputs["Color"])
tree.links.new(nm.outputs["Normal"], bsdf.inputs["Normal"])
for obj in objects.values():
    obj.data.materials.append(material)

# The whole model's placement.
X = Matrix.Identity(4)
if cfg.get("turn"):
    X = Matrix.Rotation(math.pi, 4, "Z")
if cfg.get("nose"):
    X = Matrix.Rotation(math.radians(90), 4, "X")
if cfg.get("lift"):
    X = Matrix.Translation((0, 0, cfg["lift"])) @ X


def bounds(names):
    lo, hi = Vector((1e9,) * 3), Vector((-1e9,) * 3)
    for n in names:
        world = Matrix(info[n]["world"])
        for v in objects[n].data.vertices:
            p = world @ v.co
            lo = Vector(map(min, lo, p))
            hi = Vector(map(max, hi, p))
    return lo, hi


lo, hi = bounds(cfg["nodes"])
if cfg.get("invert"):  # a dropped bomb stands nose down on the ground
    X = Matrix.Rotation(math.pi, 4, "X") @ X
if cfg.get("stand"):
    X = Matrix.Translation((0, 0, hi.z if cfg.get("invert") else -lo.z)) @ X
if cfg.get("fit"):
    s = cfg["fit"] / (hi.z - lo.z)
    X = Matrix.Scale(s, 4) @ Matrix.Translation(-(lo + hi) / 2)
weapon = cfg.get("weapon")
if weapon:
    X = Matrix(((-1, 0, 0, 0), (0, 0, -1, 0), (0, -1, 0, 0), (0, 0, 0, 1)))


def world_of(name):
    return X @ Matrix(info[name]["world"])


def place_node(name, obj):
    parent = cfg.get("parents", {}).get(name, info[name]["parent"])
    keep = parent is not None and parent in info and not cfg.get("flat") and (info[parent]["mesh"] or parent in cfg.get("parents", {}).values())
    obj.rotation_mode = "QUATERNION"
    if keep:
        obj.parent = bpy.data.objects[parent]
        obj.matrix_parent_inverse = Matrix.Identity(4)
        obj.matrix_basis = world_of(parent).inverted() @ world_of(name)
    else:
        obj.matrix_basis = world_of(name)


for name, entry in info.items():
    if not entry["mesh"] and any(p["parent"] == name for p in info.values()) and name not in cfg.get("parents", {}).values() and entry["parent"] is None:
        continue  # the source's root empty
    if not entry["mesh"] and name not in objects:
        empty = bpy.data.objects.new(name, None)
        bpy.context.scene.collection.objects.link(empty)
        objects[name] = empty
for name, obj in objects.items():
    place_node(name, obj)

if weapon:
    # The master's grip point is the origin: everything moves so that it is, by an offset on every top-level node.
    grip = Vector(weapon["grip"])
    for obj in objects.values():
        obj.location = obj.location - grip if obj.parent is None else obj.location
    bpy.context.view_layer.update()
    for obj in objects.values():  # fit_sight.py takes a gun with an identity transform
        if obj.type == "MESH":
            obj.data.transform(obj.matrix_world)
            obj.matrix_basis = Matrix.Identity(4)
    for name, p in (("RightHand", (0, 0, 0)), ("LeftHand", weapon["left"]), ("grip", (0, 0, 0))):
        e = bpy.data.objects.new(name, None)
        bpy.context.scene.collection.objects.link(e)
        e.location = p
    bpy.context.view_layer.update()
    front = min((obj.matrix_world @ v.co for obj in objects.values() if obj.type == "MESH" for v in obj.data.vertices), key=lambda p: p.y)
    e = bpy.data.objects.new("muzzle", None)
    bpy.context.scene.collection.objects.link(e)
    e.location = (0, front.y, weapon.get("bore", 0.0))

meshes = [obj for obj in objects.values() if obj.type == "MESH"]
if len(meshes) == 1:
    meshes[0].name = prefab

if cfg.get("well"):
    # The floor has no hole: a dark disc just above it, under the rim, is what the opened base shows.
    radius, height = cfg["well"]
    mesh = bpy.data.meshes.new(prefab + "_well")
    count = 48
    verts = [(0, 0, height)] + [(radius * math.cos(2 * math.pi * i / count), radius * math.sin(2 * math.pi * i / count), height) for i in range(count)]
    faces = [(0, 1 + i, 1 + (i + 1) % count) for i in range(count)]
    mesh.from_pydata(verts, [], faces)
    mesh.update()
    dark = bpy.data.materials.new("well")
    dark.use_nodes = True
    b = next(n for n in dark.node_tree.nodes if n.type == "BSDF_PRINCIPLED")
    b.inputs["Base Color"].default_value = (0.006, 0.006, 0.007, 1)
    b.inputs["Roughness"].default_value = 0.9
    b.inputs["Metallic"].default_value = 0.0
    mesh.materials.append(dark)
    obj = bpy.data.objects.new("well", mesh)
    bpy.context.scene.collection.objects.link(obj)

for o in list(bpy.data.objects):
    if o.name not in bpy.context.scene.objects:
        bpy.data.objects.remove(o)
bpy.ops.wm.save_as_mainfile(filepath=os.path.join(out, prefab + ".blend"))
print("ORDNANCE_MASTER", prefab, sum(len(o.data.polygons) for o in bpy.context.scene.objects if o.type == "MESH"))
