"""Fits an open reflex sight onto a gun master .blend (front -Y, up +Z, grip at origin) and saves a NEW .blend; the source is never
written. The sight is "Red Dot Sight" by Jean PFP (CC0, https://opengameart.org/content/red-dot-sight), kept locally in
Source/mods/ordnance-assets/downloads/red-dot-sight/red_dot.blend; it is only appended as data, no script in it runs.

blender -b --factory-startup --disable-autoexec <gun.blend> --python fit_sight.py -- --gun <mesh object> --sight <red_dot.blend>
    --mount=x,y [--length 0.10] --save <new.blend>

- appends the sight (modifiers applied) and cuts away its two lowest blocks (the base plate and the lever block under the housing, z below
  the housing's underside), so the housing itself is what stands on the gun;
- scales it to --length along Y, window frame to the front (-Y), base centre at --mount (x, y); the base sits straight on the highest
  part of the gun under its footprint (the rail), with no riser;
- fails when any gun part shows through the window from 0.3 m behind it (the game's aim camera), printing the import_body.py --remove
  boxes that take the part (a front sight post) off the gun; the sight is never lifted on a riser;
- adds the emissive red dot `reticle` at the window centre and the empty `SightLook` there (identity rotation).

Masters of 2026-10-28 (c): bodies from the Sketchfab downloads in Source/mods/ordnance-assets/downloads/<weapon>/ made by
import_body.py, then this script (the previous masters are kept in models/<M>/replaced-1028c/<M>.blend). The hand sockets are the palm
points of the pistol grip (`RightHand`) and the fore grip or handguard (`LeftHand`); the split `Magazine` is the part the reload moves:
  M08  import_body.py --glb heavy-mg/m249_machine_gun_game-ready_download.glb --name M08 --length 1.04 --grip 0,0.19,0
         --remove=-0.03,0.03,-0.145,0.0775,0.1776,0.27 --remove=-0.0195,0.0195,0.0355,0.0813,0.2055,0.26
         --remove=-0.0245,-0.0199,-0.1085,0.0022,0.1685,0.1836 --remove=0.0199,0.0245,-0.1085,0.0022,0.1685,0.1836
         --remove=-0.0245,0.0245,-0.1085,-0.1083,0.1685,0.1914 --remove=-0.0245,0.0245,0.0020,0.0022,0.1685,0.1914
         --remove=0.015,0.05,-0.365,-0.2365,0.185,0.26 --remove=0.026,0.039,-0.365,-0.284,0.1465,0.26
         --split=Magazine=-0.045,0.045,-0.205,-0.11,-0.075,0.04 --socket=RightHand=0,0.02,-0.035 --socket=LeftHand=0,-0.32,0.035
       fit_sight.py --gun M08 --mount=0,-0.053
  M09  import_body.py --glb anti-materiel-rifle/barrett_m82_bakedtextured.glb --name M09 --length 1.126 --turn-z -90
         --grip 0,0.225,0 --drop telescope,knob,_len_,optic_mount
         (the Magazine box, hand sockets: see the pipeline record in VALIDATION.md)
       fit_sight.py --gun M09 --mount=0,-0.12
  M10  import_body.py --glb grenade-launcher/rg-6_grenade_launcher.glb --name M10 --length 0.778 --grip 0,0.133,0
         --remove=-0.03,0.03,-0.463,-0.378,0.03,0.2 --remove=-0.03,0.03,-0.523,-0.49,0.045,0.14
         --socket=RightHand=0,0.02,-0.03 --socket=LeftHand=0,-0.43,-0.075
       fit_sight.py --gun M10 --mount=0,-0.07
"""
import argparse, math, sys
import bpy, bmesh
from mathutils import Vector, Matrix
from mathutils.bvhtree import BVHTree

argv = sys.argv[sys.argv.index("--") + 1:]
p = argparse.ArgumentParser()
p.add_argument("--gun", required=True)
p.add_argument("--sight", required=True)
p.add_argument("--mount", required=True)
p.add_argument("--length", type=float, default=0.10)
p.add_argument("--save", required=True)
a = p.parse_args(argv)
mount_xy = [float(v) for v in a.mount.split(",")]
assert len(mount_xy) == 2, "--mount takes x,y"
scene = bpy.context.scene
gun = bpy.data.objects[a.gun]
assert gun.matrix_world == Matrix.Identity(4), "gun object must have an identity transform"

# 1. Append the sight and bake it into one mesh.
with bpy.data.libraries.load(a.sight, link=False) as (src, dst):
    dst.objects = [n for n in src.objects]
parts = [o for o in dst.objects if o is not None and o.type == "MESH"]
for o in parts: scene.collection.objects.link(o)
dg = bpy.context.evaluated_depsgraph_get()
bm = bmesh.new()
for o in parts:
    ev = o.evaluated_get(dg); me = ev.to_mesh()
    tmp = me.copy(); tmp.transform(o.matrix_world); bm.from_mesh(tmp); bpy.data.meshes.remove(tmp)
    ev.to_mesh_clear()
for o in parts: bpy.data.objects.remove(o)
bmesh.ops.remove_doubles(bm, verts=bm.verts, dist=1e-5)
# The sight's own base plate and lever block lie below its housing (z < 0 in the download's frame); the housing starts at z = 0.
BASE_CUT = 0.0
cut = bmesh.ops.bisect_plane(bm, geom=list(bm.verts) + list(bm.edges) + list(bm.faces), plane_co=Vector((0, 0, BASE_CUT)),
                             plane_no=Vector((0, 0, 1)), clear_inner=True, dist=1e-5)
rim = [e for e in bm.edges if e.is_boundary]
if rim: bmesh.ops.holes_fill(bm, edges=rim, sides=64)
bmesh.ops.recalc_face_normals(bm, faces=bm.faces)
pts = [v.co.copy() for v in bm.verts]
assert all(q.z >= BASE_CUT - 1e-4 for q in pts), "the sight base was not cut away"
mn = Vector((min(q.x for q in pts), min(q.y for q in pts), min(q.z for q in pts)))
mx = Vector((max(q.x for q in pts), max(q.y for q in pts), max(q.z for q in pts)))
# The frame is the tall part; turn it to the front (-Y).
top = [q for q in pts if q.z > mn.z + 0.8 * (mx.z - mn.z)]
frame_y = sum(q.y for q in top) / len(top)
flip = frame_y > (mn.y + mx.y) / 2
s = a.length / (mx.y - mn.y)
cx, cy = (mn.x + mx.x) / 2, (mn.y + mx.y) / 2
for v in bm.verts:
    q = v.co - Vector((cx, cy, mn.z))
    if flip: q = Vector((-q.x, -q.y, q.z))  # a 180 degree turn about Z, not a mirror
    v.co = q * s
bm.normal_update()
sight_me = bpy.data.meshes.new("reflex_sight"); bm.to_mesh(sight_me); bm.free()
# Sight-local frame: base centre at the origin, length along Y, window toward -Y.
sp = [v.co for v in sight_me.vertices]
smn = Vector((min(q.x for q in sp), min(q.y for q in sp), min(q.z for q in sp)))
smx = Vector((max(q.x for q in sp), max(q.y for q in sp), max(q.z for q in sp)))
tall = [q for q in sp if q.z > smn.z + 0.8 * (smx.z - smn.z)]
fy0, fy1 = min(q.y for q in tall), max(q.y for q in tall)
fy = (fy0 + fy1) / 2
tree = BVHTree.FromPolygons([v.co.copy() for v in sight_me.vertices], [tuple(f.vertices) for f in sight_me.polygons])
# The window: the longest open stretch along Z at x=0 through the frame's depth, below the frame top.
steps, open_z = 400, []
for i in range(steps):
    z = smn.z + (smx.z - smn.z) * (i + 0.5) / steps
    origin = Vector((0.0, fy1 + 0.002, z))
    hit = tree.ray_cast(origin, Vector((0, -1, 0)), (fy1 - fy0) + 0.004)
    open_z.append(hit[0] is None)
runs, start = [], None
for i, o in enumerate(open_z + [False]):
    if o and start is None: start = i
    if not o and start is not None: runs.append((start, i - 1)); start = None
runs = [r for r in runs if r[1] < steps - 1]  # open above the frame top is not a window
assert runs, "no window found in the sight frame"
r0, r1 = max(runs, key=lambda r: r[1] - r[0])
z_lo = smn.z + (smx.z - smn.z) * r0 / steps
z_hi = smn.z + (smx.z - smn.z) * (r1 + 1) / steps
win = Vector((0.0, fy, (z_lo + z_hi) / 2))
half_h = (z_hi - z_lo) / 2
# Window half width at the window centre height.
xs = []
for i in range(200):
    x = (smx.x) * (i + 0.5) / 200
    hit = tree.ray_cast(Vector((x, fy1 + 0.002, win.z)), Vector((0, -1, 0)), (fy1 - fy0) + 0.004)
    xs.append(hit[0] is None)
half_w = smx.x * (xs.index(False) / 200 if False in xs else 1.0)
print("FIT sight %.4f x %.4f x %.4f m, window centre z=%.4f half %.4f x %.4f, frame y %.4f..%.4f flip=%s" % (
    smx.x - smn.x, smx.y - smn.y, smx.z - smn.z, win.z, half_w, half_h, fy0, fy1, flip))

# 2. The base stands on the highest part of the gun under its footprint.
gun_tree = BVHTree.FromObject(gun, bpy.context.evaluated_depsgraph_get())
rail = None
for i in range(9):
    for j in range(15):
        x = mount_xy[0] + smn.x + (smx.x - smn.x) * (i + 0.5) / 9
        y = mount_xy[1] + smn.y + (smx.y - smn.y) * (j + 0.5) / 15
        hit = gun_tree.ray_cast(Vector((x, y, 1.0)), Vector((0, 0, -1)))
        if hit[0] is not None and (rail is None or hit[0].z > rail): rail = hit[0].z
assert rail is not None, "no gun part under the sight footprint at --mount"
mount = Vector((mount_xy[0], mount_xy[1], rail))
print("FIT rail top z=%.4f under the footprint, the sight base sits on it" % rail)

# 3. Nothing of the gun inside the window seen from 0.3 m behind SightLook. A part that shows (a front sight post) stops the script with
# the --remove boxes that take it off.
look = mount + win
eye = look + Vector((0, 0.3, 0))
a_w = math.atan2(half_w, eye.y - look.y)
# (the frame's lower border hides the last 2 mm of the window)
a_lo = math.atan2(-half_h + 0.002, eye.y - look.y)
gm = bmesh.new(); gm.from_mesh(gun.data); gm.verts.ensure_lookup_table(); gm.verts.index_update()
shows = {v.index for v in gm.verts if eye.y - v.co.y >= 0.02
         and math.atan2(abs(v.co.x - look.x), eye.y - v.co.y) <= a_w + math.radians(1.5)
         and math.atan2(v.co.z - look.z, eye.y - v.co.y) > a_lo}
seen, doomed = set(), []
for v0 in gm.verts:
    if v0.index in seen: continue
    group, stack = [], [v0]; seen.add(v0.index)
    while stack:
        c = stack.pop(); group.append(c)
        for e in c.link_edges:
            o = e.other_vert(c)
            if o.index not in seen: seen.add(o.index); stack.append(o)
    if not any(c.index in shows for c in group): continue
    lo = Vector((min(c.co.x for c in group), min(c.co.y for c in group), min(c.co.z for c in group)))
    hi = Vector((max(c.co.x for c in group), max(c.co.y for c in group), max(c.co.z for c in group)))
    doomed.append("--remove=%.4f,%.4f,%.4f,%.4f,%.4f,%.4f" % (lo.x - 0.001, hi.x + 0.001, lo.y - 0.001, hi.y + 0.001, lo.z - 0.001, hi.z + 0.001))
gm.free()
assert not doomed, "gun parts show through the window; take them off with import_body.py: " + " ".join(doomed)

def flat_material(name, colour, metallic, roughness, emission=None):
    m = bpy.data.materials.new(name); m.use_nodes = True
    b = next(n for n in m.node_tree.nodes if n.type == "BSDF_PRINCIPLED")
    b.inputs["Base Color"].default_value = colour
    b.inputs["Metallic"].default_value = metallic
    b.inputs["Roughness"].default_value = roughness
    if emission:
        b.inputs["Emission Color"].default_value = emission[0]
        b.inputs["Emission Strength"].default_value = emission[1]
    return m
body = flat_material(a.gun + "_sight", (0.035, 0.036, 0.04, 1), 0.7, 0.42)
dot_mat = flat_material(a.gun + "_reticle", (1.0, 0.05, 0.03, 1), 0.0, 0.5, ((1.0, 0.04, 0.02, 1), 8.0))

sight = bpy.data.objects.new("sight", sight_me)
sight.location = mount
sight_me.materials.append(body)
for poly in sight_me.polygons: poly.use_smooth = False
scene.collection.objects.link(sight)

# Red dot: a 1.4 mm (radius) disc at the window centre facing the eye (+Y), slightly behind the frame's front.
bm = bmesh.new()
bmesh.ops.create_circle(bm, cap_ends=True, radius=0.0014, segments=16)
bmesh.ops.rotate(bm, verts=bm.verts, cent=Vector(), matrix=Matrix.Rotation(math.radians(-90), 3, "X"))
for f in bm.faces:
    f.normal_update()
    if f.normal.y < 0: f.normal_flip()
dot_me = bpy.data.meshes.new("reticle"); bm.to_mesh(dot_me); bm.free()
dot_me.materials.append(dot_mat)
dot = bpy.data.objects.new("reticle", dot_me)
dot.location = look
scene.collection.objects.link(dot)

e = bpy.data.objects.new("SightLook", None)
e.empty_display_size = 0.01
e.location = look
scene.collection.objects.link(e)
print("FIT SightLook", tuple(round(c, 4) for c in look))
bpy.ops.wm.save_as_mainfile(filepath=a.save, copy=False)
print("FIT saved", a.save)
