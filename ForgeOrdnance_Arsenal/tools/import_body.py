"""Turns a downloaded glTF gun (Sketchfab, CC BY) into a heavy weapon master .blend: one mesh object named --name (identity
transform, front -Y, up +Z, the grip at the origin, the bore on x = 0) plus the empties `grip`, `muzzle` and the hand sockets, and one more
mesh object per --split (a magazine). fit_sight.py then fits the reflex sight.
The download is untrusted data: it is only imported as glTF, and the source file is never written.

blender -b --factory-startup --python import_body.py -- --glb <file.glb> --name M08 --length 1.04 [--turn-z 0] [--grip x,y,z]
    [--drop name,name] [--remove x0,x1,y0,y1,z0,z1 ...] [--split Name=x0,x1,y0,y1,z0,z1[,px,py,pz] ...]
    [--socket Name=x,y,z ...] [--max-tris 19000] [--ao 0.85] [--texture 2048] [--list-parts] --save <new.blend>

- --drop removes the glTF objects whose own or parent name contains one of the words (a separate scope and its mounts);
- the model turns --turn-z degrees about Z (front to -Y), takes --length along Y, and its bounding box centre goes to the origin;
  --grip (in that centred frame) then becomes the origin;
- --remove deletes the loose parts lying wholly inside a box of the final frame (a scope or handle modelled into the single mesh);
- --split moves the loose parts wholly inside a box into their own mesh object `Name` (a magazine the reload animation moves). Its origin
  is (px, py, pz), by default the box centre; it shares the gun's materials;
- --socket adds an empty `Name` at x,y,z of the final frame: `RightHand` (the centre of the pistol grip) and `LeftHand` (the centre of the
  fore grip or handguard) are the palm points the game's hands are put on;
- the bore is put on x = 0 after the removals (a part taken off one side does not move the gun sideways);
- above --max-tris the mesh is welded and reduced with Blender's quadric (collapse) decimation, which keeps UVs, and its corner
  normals are copied from the nearest face of the full mesh; at or below it the imported mesh and its normals are kept unchanged.
  The budget is shared between the gun and its split objects by triangle count;
- each material keeps its base colour, metallic-roughness (glTF: G roughness, B metallic) and normal images, at most --texture
  pixels a side. The game's gear shader has no occlusion input, so the occlusion (R of the glTF ORM image) is multiplied into the
  base colour in linear space, weighted by --ao.
- `muzzle` is put at the front-most part of the bore: the centre of the vertices within 1 cm of the front.

The arguments used for the M08-M10 masters are in the header of fit_sight.py.
"""
import argparse, math, sys
import bpy, bmesh
import numpy as np
from mathutils import Vector, Matrix

argv = sys.argv[sys.argv.index("--") + 1:]
p = argparse.ArgumentParser()
p.add_argument("--glb", required=True)
p.add_argument("--name", required=True)
p.add_argument("--length", type=float, required=True)
p.add_argument("--turn-z", type=float, default=0.0)
p.add_argument("--grip", default="0,0,0")
p.add_argument("--drop", default="")
p.add_argument("--remove", action="append", default=[])
p.add_argument("--split", action="append", default=[])
p.add_argument("--socket", action="append", default=[])
p.add_argument("--max-tris", type=int, default=19000)
p.add_argument("--ao", type=float, default=0.85)
p.add_argument("--texture", type=int, default=2048)
p.add_argument("--list-parts", action="store_true")
p.add_argument("--save", required=True)
a = p.parse_args(argv)

bpy.ops.wm.read_factory_settings(use_empty=True)
bpy.ops.import_scene.gltf(filepath=a.glb)
scene = bpy.context.scene
drop = [w for w in a.drop.split(",") if w]

def dropped(o):
    names = [o.name] + ([o.parent.name] if o.parent else [])
    return any(w in n for w in drop for n in names)

# 1. One mesh in world space, minus the dropped parts.
dg = bpy.context.evaluated_depsgraph_get()
meshes = [o for o in scene.objects if o.type == "MESH"]
gone = [o.parent.name if o.parent else o.name for o in meshes if dropped(o)]
print("BODY dropped", len(gone), "objects:", ", ".join(sorted(gone)))
keep = [o for o in meshes if not dropped(o)]
materials = []
for o in keep:
    for s in o.material_slots:
        if s.material is not None and s.material not in materials: materials.append(s.material)
bm = bmesh.new()
for o in keep:
    me = o.evaluated_get(dg).to_mesh().copy()
    me.transform(o.matrix_world)
    remap = [materials.index(s.material) for s in o.material_slots]
    for poly in me.polygons: poly.material_index = remap[poly.material_index] if remap else 0
    if me.has_custom_normals:
        # bmesh drops custom normals: keep them as a corner attribute and restore below.
        me.attributes.new("body_normal", "FLOAT_VECTOR", "CORNER").data.foreach_set("vector", np.array(
            [c.vector for c in me.corner_normals], dtype=np.float32).ravel())
    bm.from_mesh(me); bpy.data.meshes.remove(me)
body_me = bpy.data.meshes.new(a.name)
bm.to_mesh(body_me); bm.free()
for poly in body_me.polygons: poly.use_smooth = True
for o in list(scene.objects): bpy.data.objects.remove(o)
for m in materials: body_me.materials.append(m)

# 2. Turn, scale and centre; then the grip to the origin.
v = body_me.vertices
body_me.transform(Matrix.Rotation(math.radians(a.turn_z), 4, "Z"))
co = np.array([x.co for x in v])
mn, mx = co.min(0), co.max(0)
s = a.length / (mx[1] - mn[1])
grip = Vector([float(c) for c in a.grip.split(",")])
body_me.transform(Matrix.Translation(-grip) @ Matrix.Scale(s, 4) @ Matrix.Translation(-Vector((mn + mx) / 2)))
normals = body_me.attributes.get("body_normal")
if normals is not None:
    # Rotation and uniform scale keep normal directions up to the turn.
    n = np.empty(len(body_me.loops) * 3, dtype=np.float32); normals.data.foreach_get("vector", n)
    r = np.array(Matrix.Rotation(math.radians(a.turn_z), 3, "Z"))
    n = (n.reshape(-1, 3) @ r.T).astype(np.float32); normals.data.foreach_set("vector", n.ravel())
co = np.array([x.co for x in v]); mn, mx = co.min(0), co.max(0)
print("BODY size %.4f x %.4f x %.4f m, scale %.6f, bounds %s .. %s" % (*(mx - mn), s, np.round(mn, 4), np.round(mx, 4)))

def loose_parts(b):
    b.verts.ensure_lookup_table(); b.verts.index_update(); seen, parts = set(), []
    for v0 in b.verts:
        if v0.index in seen: continue
        group, stack = [], [v0]; seen.add(v0.index)
        while stack:
            c = stack.pop(); group.append(c)
            for e in c.link_edges:
                o = e.other_vert(c)
                if o.index not in seen: seen.add(o.index); stack.append(o)
        parts.append(group)
    return parts

# 3. Remove boxes.
def boxed(spec):
    name, _, rest = spec.partition("=")
    return name, [float(c) for c in rest.split(",")]

bm = bmesh.new(); bm.from_mesh(body_me)
doomed = []
parts = loose_parts(bm)
for box in a.remove:
    x0, x1, y0, y1, z0, z1 = [float(c) for c in box.split(",")]
    for g in parts:
        if all(x0 <= q.co.x <= x1 and y0 <= q.co.y <= y1 and z0 <= q.co.z <= z1 for q in g): doomed.extend(g)
before = len(bm.faces)
bmesh.ops.delete(bm, geom=list(set(doomed)), context="VERTS")
print("BODY removed", before - len(bm.faces), "faces in", len(a.remove), "boxes")
bm.normal_update()
if a.list_parts:
    for g in sorted(loose_parts(bm), key=lambda g: -max(q.co.z for q in g))[:60]:
        c = np.array([q.co for q in g]); lo, hi = c.min(0), c.max(0)
        print("PART verts=%5d x %.4f..%.4f y %.4f..%.4f z %.4f..%.4f" % (len(g), lo[0], hi[0], lo[1], hi[1], lo[2], hi[2]))

# 3b. Split objects: the loose parts wholly inside a box leave the gun mesh for a mesh of their own.
split_meshes = []
for spec in a.split:
    name, vals = boxed(spec)
    x0, x1, y0, y1, z0, z1 = vals[:6]
    pivot = Vector(vals[6:9]) if len(vals) >= 9 else Vector(((x0 + x1) / 2, (y0 + y1) / 2, (z0 + z1) / 2))
    inside = set()
    for g in loose_parts(bm):
        if all(x0 <= q.co.x <= x1 and y0 <= q.co.y <= y1 and z0 <= q.co.z <= z1 for q in g): inside.update(q.index for q in g)
    if not inside: raise SystemExit("--split %s: no loose part lies wholly inside the box." % name)
    sub = bm.copy(); sub.verts.ensure_lookup_table()
    bmesh.ops.delete(sub, geom=[q for q in sub.verts if q.index not in inside], context="VERTS")
    bmesh.ops.delete(bm, geom=[q for q in bm.verts if q.index in inside], context="VERTS")
    bm.verts.ensure_lookup_table(); bm.verts.index_update()
    sub_me = bpy.data.meshes.new(name)
    bmesh.ops.translate(sub, verts=sub.verts, vec=-pivot)
    sub.to_mesh(sub_me); sub.free()
    for poly in sub_me.polygons: poly.use_smooth = True
    for m in materials: sub_me.materials.append(m)
    split_meshes.append((name, sub_me, pivot))
    print("BODY split %s: %d verts, pivot %s" % (name, len(sub_me.vertices), tuple(round(c, 4) for c in pivot)))
bm.to_mesh(body_me); bm.free()

# 4. Reduce.
obj = bpy.data.objects.new(a.name, body_me); scene.collection.objects.link(obj)
objects = [obj]
for name, sub_me, pivot in split_meshes:
    o = bpy.data.objects.new(name, sub_me); o.location = pivot; scene.collection.objects.link(o); objects.append(o)
def tri_count(me):
    me.calc_loop_triangles(); return len(me.loop_triangles)
total = sum(tri_count(o.data) for o in objects)
def restore_normals(me):
    n = np.empty(len(me.loops) * 3, dtype=np.float32); me.attributes["body_normal"].data.foreach_get("vector", n)
    me.attributes.remove(me.attributes["body_normal"])
    me.normals_split_custom_set(n.reshape(-1, 3).tolist())

def reduce(o, budget):
    me = o.data
    tris = tri_count(me)
    if tris <= budget:
        if "body_normal" in me.attributes: restore_normals(me)
        return tris
    # The full mesh with its own normals is the reference the reduced mesh copies its corner normals from.
    ref = bpy.data.objects.new(o.name + "_ref", me.copy()); scene.collection.objects.link(ref)
    if "body_normal" in ref.data.attributes: restore_normals(ref.data)
    if "body_normal" in me.attributes: me.attributes.remove(me.attributes["body_normal"])
    b = bmesh.new(); b.from_mesh(me)
    bmesh.ops.remove_doubles(b, verts=b.verts, dist=1e-5)
    b.to_mesh(me); b.free()
    welded = tri_count(me)
    mod = o.modifiers.new("decimate", "DECIMATE"); mod.decimate_type = "COLLAPSE"
    mod.ratio = budget / welded; mod.use_collapse_triangulate = True
    bpy.context.view_layer.objects.active = o; o.select_set(True)
    bpy.ops.object.modifier_apply(modifier=mod.name)
    # Welding and collapsing bend the shading; the corner normals come back from the nearest face of the full mesh, so the
    # imported normals (and the normal map baked against them) keep the original look.
    tr = o.modifiers.new("normals", "DATA_TRANSFER"); tr.object = ref
    tr.use_loop_data = True; tr.data_types_loops = {"CUSTOM_NORMAL"}; tr.loop_mapping = "POLYINTERP_NEAREST"
    bpy.ops.object.modifier_apply(modifier=tr.name)
    bpy.data.objects.remove(ref)
    print("BODY %s decimated %d -> %d triangles (welded %d), normals taken from the full mesh" % (o.name, tris, tri_count(me), welded))
    return tri_count(me)

if total > a.max_tris:
    # A split part keeps at least 1500 triangles: below that the nearest-face normals contradict the thin plates' winding.
    for o in objects: reduce(o, max(1500 if o is not obj else 200, int(a.max_tris * tri_count(o.data) / total)))
else:
    for o in objects: reduce(o, a.max_tris)
    print("BODY kept %d triangles and the imported normals" % total)

# 5. Materials: base colour with occlusion, ORM and normal images, packed into the .blend.
def feeding(node, socket_name):
    def up(sock, seen):
        for l in sock.links:
            nd = l.from_node
            if nd in seen: continue
            seen.add(nd)
            if nd.type == "TEX_IMAGE" and nd.image is not None: return nd
            for i in nd.inputs:
                f = up(i, seen)
                if f is not None: return f
        return None
    return up(node.inputs[socket_name], set())

def to_lin(c): return np.where(c <= 0.04045, c / 12.92, ((c + 0.055) / 1.055) ** 2.4)
def to_srgb(c): return np.where(c <= 0.0031308, c * 12.92, 1.055 * np.power(np.maximum(c, 0), 1 / 2.4) - 0.055)

def read(img):
    w, h = img.size
    d = np.empty(w * h * 4, dtype=np.float32); img.pixels.foreach_get(d)
    return d.reshape(h, w, 4)

def store(name, data, colour):
    h, w = data.shape[:2]
    img = bpy.data.images.new(name, w, h, alpha=True)
    img.colorspace_settings.name = "sRGB" if colour else "Non-Color"
    img.pixels.foreach_set(np.ascontiguousarray(data, dtype=np.float32).ravel())
    if max(w, h) > a.texture:
        k = a.texture / max(w, h); img.scale(max(1, int(w * k)), max(1, int(h * k)))
    img.file_format = "PNG"; img.pack()
    return img

for i, m in enumerate(materials):
    nt = m.node_tree
    bsdf = next(n for n in nt.nodes if n.type == "BSDF_PRINCIPLED")
    base_n, orm_n, nrm_n = feeding(bsdf, "Base Color"), feeding(bsdf, "Metallic") or feeding(bsdf, "Roughness"), feeding(bsdf, "Normal")
    tag = "%s_%s" % (a.name, "body" if i == 0 else m.name.lower())
    base = read(base_n.image)
    orm = read(orm_n.image) if orm_n is not None else None
    if orm is not None:
        if orm.shape[:2] != base.shape[:2]:
            t = store("tmp", orm, False); t.scale(base.shape[1], base.shape[0]); orm = read(t); bpy.data.images.remove(t)
        ao = 1.0 - a.ao * (1.0 - orm[..., 0])
        # Byte images give sRGB-encoded values; occlusion darkens linear light.
        base[..., :3] = to_srgb(to_lin(base[..., :3]) * ao[..., None])
    images = {"base": store(tag + "_basecolor", base, True)}
    if orm is not None: images["orm"] = store(tag + "_orm", read(orm_n.image), False)
    if nrm_n is not None: images["normal"] = store(tag + "_normal", read(nrm_n.image), False)
    # A clean node tree the stage exporter reads: base -> Base Color, ORM -> Separate (G roughness, B metallic), normal map.
    nt.nodes.clear()
    out = nt.nodes.new("ShaderNodeOutputMaterial"); b = nt.nodes.new("ShaderNodeBsdfPrincipled")
    nt.links.new(b.outputs[0], out.inputs["Surface"])
    t = nt.nodes.new("ShaderNodeTexImage"); t.image = images["base"]; nt.links.new(t.outputs["Color"], b.inputs["Base Color"])
    if "orm" in images:
        t = nt.nodes.new("ShaderNodeTexImage"); t.image = images["orm"]
        sep = nt.nodes.new("ShaderNodeSeparateColor"); nt.links.new(t.outputs["Color"], sep.inputs["Color"])
        nt.links.new(sep.outputs["Green"], b.inputs["Roughness"]); nt.links.new(sep.outputs["Blue"], b.inputs["Metallic"])
    if "normal" in images:
        t = nt.nodes.new("ShaderNodeTexImage"); t.image = images["normal"]
        nm = nt.nodes.new("ShaderNodeNormalMap"); nt.links.new(t.outputs["Color"], nm.inputs["Color"]); nt.links.new(nm.outputs["Normal"], b.inputs["Normal"])
    m.name = tag
    print("BODY material %s: %s" % (tag, ", ".join("%s %dx%d" % (k, *im.size) for k, im in images.items())))
for im in list(bpy.data.images):
    if im.users == 0: bpy.data.images.remove(im)

# 6. Empties. The bore goes to x = 0 first: the muzzle is the centre of the vertices within 1 cm of the front.
def front_centre(me):
    c = np.array([x.co for x in me.vertices])
    f = c[c[:, 1] < c[:, 1].min() + 0.01]
    return Vector(((f[:, 0].min() + f[:, 0].max()) / 2, c[:, 1].min(), (f[:, 2].min() + f[:, 2].max()) / 2))
shift_x = front_centre(body_me).x
if abs(shift_x) > 1e-5:
    for o in objects:
        if o is obj: o.data.transform(Matrix.Translation((-shift_x, 0, 0)))
        else: o.location.x -= shift_x
    print("BODY bore moved %.4f m onto x = 0" % -shift_x)
muzzle = front_centre(body_me)
sockets = [("grip", Vector()), ("muzzle", muzzle)]
for spec in a.socket:
    name, vals = boxed(spec)
    sockets.append((name, Vector(vals[:3])))
for name, loc in sockets:
    e = bpy.data.objects.new(name, None); e.empty_display_size = 0.03; e.location = loc; scene.collection.objects.link(e)
co = np.array([x.co for x in body_me.vertices])
print("BODY %s %d triangles, bounds %s .. %s, muzzle %s" % (a.name, sum(tri_count(o.data) for o in objects), np.round(co.min(0), 4), np.round(co.max(0), 4),
      tuple(round(c, 4) for c in muzzle)))
bpy.ops.wm.save_as_mainfile(filepath=a.save, copy=False)
print("BODY saved", a.save)
