"""Stages one part shown with a suit (props.json: a rocket launcher, a rocket) for FramesBundleBuild.cs.

blender --background --factory-startup --python build_prop.py -- <props.json> <prefab> <source folder> <stage folder>

Reads the part's finished low-poly GLB (its mesh and its baked base colour, normal, metallic and roughness textures), joins its
meshes, turns it by "Turn" (Blender XYZ degrees) so its front is Blender -Y and its top +Z, scales it uniformly (never mirrored)
so it is "Length" metres from back to front, puts its origin at the bounding box's centre ("Origin": "centre") or the centre of
its underside ("bottom"), and adds an empty per "Sockets" entry (Unity space of the prefab: x right, y up, z forward, metres).
Writes <stage>/model.fbx (the build's export convention: Blender -Y is Unity +Z), albedo.png, normal.png (OpenGL, as baked),
metallic.png (R metal, G and A smoothness, as the suits') at "Texture" pixels, and prop.json (prefab, texture size, sockets,
triangles, size)."""
import bpy, bmesh, json, math, os, sys
import numpy as np
from mathutils import Euler, Matrix, Vector

args = sys.argv[sys.argv.index("--") + 1:]
config_path, prefab, source, stage = args
with open(config_path, encoding="utf-8") as f:
    config = json.load(f)
prop = next((p for p in config["Props"] if p["Prefab"] == prefab), None)
if prop is None:
    raise SystemExit("props.json has no part " + prefab)
glb = os.path.join(source, prop["File"])
if not os.path.isfile(glb):
    raise SystemExit("Missing the low-poly model of " + prefab + ": " + glb)
size = int(prop["Texture"])
os.makedirs(stage, exist_ok=True)

bpy.ops.wm.read_factory_settings(use_empty=True)
bpy.ops.import_scene.gltf(filepath=glb)
meshes = [o for o in bpy.context.scene.objects if o.type == "MESH"]
if not meshes:
    raise SystemExit(glb + " has no mesh")


def find_bsdf(objects):
    for o in objects:
        for slot in o.material_slots:
            m = slot.material
            if m and m.use_nodes:
                for n in m.node_tree.nodes:
                    if n.type == "BSDF_PRINCIPLED":
                        return n
    return None


bsdf = find_bsdf(meshes)
if bsdf is None:
    raise SystemExit(glb + " has no Principled BSDF material")


def pixels(image):
    w, h = image.size
    if w == 0 or h == 0:
        raise SystemExit("Texture " + image.name + " has no pixels")
    copy = image.copy()
    copy.scale(size, size)
    px = np.array(copy.pixels[:], dtype=np.float32).reshape(size, size, 4)
    bpy.data.images.remove(copy)
    return px


def channel_of(socket):
    """(pixels HxWx4 or None, channel index or None for colour, constant) for an input socket."""
    if not socket.is_linked:
        v = socket.default_value
        return None, None, v
    link = socket.links[0]
    node = link.from_node
    if node.type == "NORMAL_MAP":
        return channel_of(node.inputs["Color"])
    if node.type == "TEX_IMAGE":
        return pixels(node.image), None, None
    if node.type in ("SEPARATE_COLOR", "SEPRGB", "SEPARATE_RGB"):
        name = link.from_socket.name
        index = {"Red": 0, "R": 0, "Green": 1, "G": 1, "Blue": 2, "B": 2}[name]
        px, _, const = channel_of(node.inputs[0])
        return px, index, const
    raise SystemExit("Unsupported node " + node.type + " feeding " + socket.name)


def as_map(socket, fallback):
    px, index, const = channel_of(socket)
    if px is None:
        value = const if const is not None else fallback
        if hasattr(value, "__len__"):
            return np.broadcast_to(np.array(value[:4] if len(value) >= 4 else list(value) + [1.0], dtype=np.float32), (size, size, 4)).copy()
        return np.full((size, size), float(value), dtype=np.float32)
    return px if index is None else px[:, :, index]


albedo = as_map(bsdf.inputs["Base Color"], (1, 1, 1, 1))
metal = as_map(bsdf.inputs["Metallic"], 0.0)
rough = as_map(bsdf.inputs["Roughness"], 0.5)
normal_px = None
if bsdf.inputs["Normal"].is_linked:
    normal_px, _, _ = channel_of(bsdf.inputs["Normal"])
if metal.ndim == 3:
    metal = metal[:, :, 0]
if rough.ndim == 3:
    rough = rough[:, :, 0]
smooth = 1.0 - rough


def save(name, px, colour):
    img = bpy.data.images.new(prefab + "-" + name, size, size, alpha=True)
    img.colorspace_settings.name = "sRGB" if colour else "Non-Color"
    img.pixels.foreach_set(np.ascontiguousarray(px, dtype=np.float32).ravel())
    img.filepath_raw = os.path.join(stage, name + ".png")
    img.file_format = "PNG"
    img.save()


albedo_rgba = albedo.copy()
albedo_rgba[:, :, 3] = 1.0
save("albedo", albedo_rgba, True)
save("metallic", np.stack([metal, smooth, np.zeros_like(smooth), smooth], axis=-1), False)
if normal_px is None:
    normal_px = np.zeros((size, size, 4), dtype=np.float32)
    normal_px[:, :, :] = (0.5, 0.5, 1.0, 1.0)
normal_px = normal_px.copy()
normal_px[:, :, 3] = 1.0
save("normal", normal_px, False)

# One mesh, turned, scaled and placed.
bpy.ops.object.select_all(action="DESELECT")
for o in meshes:
    o.select_set(True)
bpy.context.view_layer.objects.active = meshes[0]
if len(meshes) > 1:
    bpy.ops.object.join()
obj = bpy.context.view_layer.objects.active
for o in list(bpy.context.scene.objects):
    if o != obj:
        bpy.data.objects.remove(o, do_unlink=True)
obj.parent = None
bpy.ops.object.transform_apply(location=True, rotation=True, scale=True)
turn = Euler([math.radians(a) for a in prop["Turn"]], "XYZ").to_matrix().to_4x4()
obj.data.transform(turn)
co = np.array([v.co[:] for v in obj.data.vertices])
lo, hi = co.min(axis=0), co.max(axis=0)
length = hi[1] - lo[1]
scale = float(prop["Length"]) / length
centre = (lo + hi) / 2
origin = Vector((centre[0], centre[1], lo[2] if prop["Origin"] == "bottom" else centre[2]))
obj.data.transform(Matrix.Scale(scale, 4) @ Matrix.Translation(-origin))
obj.data.update()
co = np.array([v.co[:] for v in obj.data.vertices])
lo, hi = co.min(axis=0), co.max(axis=0)
obj.name = prefab
obj.data.materials.clear()
mesh = obj.data
triangles = sum(len(p.vertices) - 2 for p in mesh.polygons)

sockets = {}
for name, at in prop.get("Sockets", {}).items():
    # Unity (x, y, z) is Blender (-x, -z, y) under the export convention.
    empty = bpy.data.objects.new(name, None)
    empty.location = (-at[0], -at[2], at[1])
    bpy.context.scene.collection.objects.link(empty)
    sockets[name] = at

bpy.ops.object.select_all(action="SELECT")
bpy.ops.export_scene.fbx(filepath=os.path.join(stage, "model.fbx"), use_selection=True, object_types={"MESH", "EMPTY"},
                         apply_unit_scale=True, apply_scale_options="FBX_SCALE_UNITS", bake_space_transform=True,
                         axis_forward="-Z", axis_up="Y", add_leaf_bones=False, bake_anim=False, mesh_smooth_type="FACE",
                         embed_textures=False, path_mode="STRIP")
# Unity space size: x across, y up, z back to front.
dims = hi - lo
record = {"prefab": prefab, "texture": size, "sockets": list(sockets), "triangles": triangles,
          "size": [float(dims[0]), float(dims[2]), float(dims[1])], "scale": scale}
with open(os.path.join(stage, "prop.json"), "w", encoding="utf-8") as f:
    json.dump(record, f, indent=1)
print("PROP %s %d triangles, %.3f x %.3f x %.3f m (across, up, long), scale %.4f, sockets %s"
      % (prefab, triangles, dims[0], dims[2], dims[1], scale, ", ".join(sockets) or "none"))
