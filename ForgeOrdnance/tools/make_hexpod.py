"""Makes the one ForgeOrdnance drop pod (the hexagonal M02 "Hex Hellpod") from its Meshy source blend.

Run (Blender 5.x, background):
    blender -b <assets>/models/M02/M02_pod.blend --python Mods/ForgeOrdnance/tools/make_hexpod.py -- <out.blend>

Blender frame, metres, origin on the ground line (the drill is below it). The pod is a regular hexagon with its two corner
posts on +-X; each post has two door leaves, one per face, so there are four doors and two bays (+X and -X). The result:
  * the four Meshy door meshes (embedded in the hull, no hinge clearance) are replaced by clean hatch slabs: 4.8 cm thick
    before scaling, 6 mm chamfers, outer face carrying the pod's own texture (UVs transferred from the Meshy door), inner face
    steel, edges plain gunmetal; each door's origin is the outer corner of its hinge edge so the swing clears the frame;
  * clamps, light strip and the old anchors are dropped; two shelves (shelf_1 on +X, shelf_2 on -X) split each bay into a
    floor tier and an upper tier, hidden by the mod unless pickups are carried;
  * weapon_anchor_1/2 (+X/-X floor centre) and pickup_anchor_1..4 (floor tier +X, -X, upper tier +X, -X);
  * everything scaled uniformly by SCALE so a bay is just tall enough to stand a 1.13 m heavy weapon upright.
"""
import math
import sys

import bmesh
import bpy
from mathutils import Matrix, Vector
from mathutils.bvhtree import BVHTree
from mathutils.interpolate import poly_3d_calc

SCALE = 0.94
THICK = 0.048        # door slab thickness before scaling (4.5 cm after)
CHAMFER = 0.006
GAP = 0.0015         # reveal between a door and its frame / neighbour
SILL, HEAD = 0.449, 0.449 + 1.759
FLOOR = 0.833        # pedestal top, as the old anchors
BAY_HEIGHT = 1.23 / SCALE
TIER = 0.59 / SCALE
SHELF_T = 0.02 / SCALE
BAY_X0, BAY_X1, BAY_Y = 0.032, 0.38, 0.42   # usable bay of one side (before scaling), clear of the door slabs

OUT = sys.argv[sys.argv.index('--') + 1]
objs = bpy.data.objects
mat = lambda name: bpy.data.materials[name]


def door_frame(door):
    """Outward normal and tangent (toward the corner post on y = 0) of a door's face, from where its hinge sits."""
    sx = 1 if door.location.x > 0 else -1
    sy = 1 if door.location.y > 0 else -1
    n = Vector((0.866025 * sx, 0.5 * sy, 0))
    t = Vector((0.5 * sx, -0.866025 * sy, 0))
    return n, t


def skin_bvh(door, n, outer):
    """BVH of the Meshy door's outer (or inner) faces and the matching UV lookup."""
    bm = bmesh.new()
    bm.from_mesh(door.data)
    keep = [f for f in bm.faces if (f.material_index == (0 if outer else 1)) and (f.normal.dot(n) > .3 if outer else f.normal.dot(n) < -.3)]
    drop = [f for f in bm.faces if f not in set(keep)]
    bmesh.ops.delete(bm, geom=drop, context='FACES')
    bmesh.ops.triangulate(bm, faces=bm.faces[:])
    bm.transform(door.matrix_world)
    bm.faces.ensure_lookup_table()
    uv = bm.loops.layers.uv.active
    return bm, BVHTree.FromBMesh(bm), uv


def uv_at(bm, bvh, uv, point, n):
    """UV of the Meshy door's outermost surface straight behind this point (a ray along -n), else the nearest surface."""
    loc, _, index, _ = bvh.ray_cast(point + n * 0.3, -n)
    if loc is None:
        loc, _, index, _ = bvh.find_nearest(point)
    face = bm.faces[index]
    weights = poly_3d_calc([v.co for v in face.verts], loc)
    return sum((l[uv].uv * wt for l, wt in zip(face.loops, weights)), Vector((0, 0)))


def build_door(old, name):
    n, t = door_frame(old)
    origin = old.matrix_world.translation.copy()
    origin.z = SILL
    up = Vector((0, 0, 1))

    def end_t(p):   # where the y = 0 mitre plane cuts the slab at depth p (p < 0 is inward)
        return (-origin.y - p * n.y) / t.y - GAP

    def at(tt, p, z):
        return (t * tt + n * p + up * (z - SILL))

    bm = bmesh.new()
    t0 = GAP
    pts = {}
    for key, (tt, p, z) in {
        'ohb': (t0, 0, SILL + GAP), 'ofb': (end_t(0), 0, SILL + GAP), 'oft': (end_t(0), 0, HEAD - GAP), 'oht': (t0, 0, HEAD - GAP),
        'ihb': (t0, -THICK, SILL + GAP), 'ifb': (end_t(-THICK), -THICK, SILL + GAP), 'ift': (end_t(-THICK), -THICK, HEAD - GAP), 'iht': (t0, -THICK, HEAD - GAP),
    }.items():
        pts[key] = bm.verts.new(at(tt, p, z))
    V = lambda *k: [pts[x] for x in k]
    outer = bm.faces.new(V('ohb', 'ofb', 'oft', 'oht'))
    inner = bm.faces.new(V('ihb', 'iht', 'ift', 'ifb'))
    for ks in [('ohb', 'oht', 'iht', 'ihb'), ('ofb', 'ifb', 'ift', 'oft'), ('ohb', 'ihb', 'ifb', 'ofb'), ('oht', 'oft', 'ift', 'iht')]:
        bm.faces.new(V(*ks))
    bmesh.ops.recalc_face_normals(bm, faces=bm.faces[:])
    if outer.normal.dot(n) < 0:
        bmesh.ops.reverse_faces(bm, faces=bm.faces[:])
    edges = list({e for f in (outer, inner) for e in f.edges})
    res = bmesh.ops.bevel(bm, geom=edges, offset=CHAMFER, segments=1, affect='EDGES', profile=.5)
    bm.faces.ensure_lookup_table()
    outer = max((f for f in bm.faces if f.normal.dot(n) > .99), key=lambda f: f.calc_area())
    inner = max((f for f in bm.faces if f.normal.dot(n) < -.99), key=lambda f: f.calc_area())
    bmesh.ops.subdivide_edges(bm, edges=outer.edges[:], cuts=56, use_grid_fill=True)
    bm.faces.ensure_lookup_table()
    outer_faces = [f for f in bm.faces if f.normal.dot(n) > .99]
    inner_face = max((f for f in bm.faces if f.normal.dot(n) < -.99), key=lambda f: f.calc_area())
    uvl = bm.loops.layers.uv.new(old.data.uv_layers.active.name)
    sk_bm, sk_bvh, sk_uv = skin_bvh(old, n, True)
    for f in bm.faces:
        if f in set(outer_faces):
            f.material_index = 0
            for l in f.loops:
                l[uvl].uv = uv_at(sk_bm, sk_bvh, sk_uv, l.vert.co + origin, n)
        elif f == inner_face:
            f.material_index = 1
        else:
            f.material_index = 2
    sk_bm.free()
    for f in bm.faces:
        f.smooth = False
    mesh = bpy.data.meshes.new(name)
    bm.to_mesh(mesh)
    bm.free()
    for m in ('M02_Pod', 'Pod_InnerSteel', 'Pod_GunGrey'):
        mesh.materials.append(mat(m))
    ob = bpy.data.objects.new(name, mesh)
    ob.location = origin
    bpy.context.scene.collection.objects.link(ob)
    return ob


def clear_interior(old):
    """Pushes interior and cradle vertices that stand inside a door slab (ceiling plate and floor plate edges, rim caps)
    inward, past the slab."""
    n, t = door_frame(old)
    origin = old.matrix_world.translation.copy()
    for part in (objs['interior'], objs['cradle']):
        for v in part.data.vertices:
            w = part.matrix_world @ v.co - origin
            p, tt, z = w.dot(n), w.dot(t), w.z + origin.z
            if -THICK - 0.004 < p < 0.06 and -0.4 <= tt <= 0.7 and SILL <= z <= HEAD:
                v.co += n * (-THICK - 0.004 - p)


def box(name, x0, x1, y0, y1, z0, z1, material):
    bm = bmesh.new()
    bmesh.ops.create_cube(bm, size=1.0)
    bm.transform(Matrix.Translation((.5, .5, .5)))
    for v in bm.verts:
        v.co = Vector((x0 + v.co.x * (x1 - x0), y0 + v.co.y * (y1 - y0), z0 + v.co.z * (z1 - z0)))
    mesh = bpy.data.meshes.new(name)
    bm.to_mesh(mesh)
    bm.free()
    mesh.materials.append(mat(material))
    ob = bpy.data.objects.new(name, mesh)
    bpy.context.scene.collection.objects.link(ob)
    return ob


def empty(name, location):
    ob = bpy.data.objects.new(name, None)
    ob.location = location
    bpy.context.scene.collection.objects.link(ob)
    return ob


olds = {f'door_{i}': objs[f'door_{i}'] for i in range(1, 5)}
new = {name: build_door(old, name + '_slab') for name, old in olds.items()}
for old in olds.values():
    clear_interior(old)
for name in ('clamp_1', 'clamp_2', 'clamp_3', 'clamp_4', 'light_strip', 'weapon_anchor', *[f'payload_anchor_{i}' for i in range(1, 5)]):
    bpy.data.objects.remove(objs[name], do_unlink=True)
for name, old in olds.items():
    bpy.data.objects.remove(old, do_unlink=True)
    new[name].name = name
    new[name].data.name = name

cx = (BAY_X0 + BAY_X1) / 2
shelf_z = FLOOR + TIER
for i, s in enumerate((1, -1), start=1):
    x0, x1 = sorted((s * BAY_X0, s * BAY_X1))
    box(f'shelf_{i}', x0, x1, -BAY_Y, BAY_Y, shelf_z, shelf_z + SHELF_T, 'Pod_GunGrey')
    empty(f'weapon_anchor_{i}', (s * cx, 0, FLOOR))
    empty(f'pickup_anchor_{i}', (s * cx, 0, FLOOR))
    empty(f'pickup_anchor_{i + 2}', (s * cx, 0, shelf_z + SHELF_T))

for ob in bpy.data.objects:
    ob.location = ob.location * SCALE
    if ob.type == 'MESH':
        ob.data.transform(Matrix.Scale(SCALE, 4))
bpy.ops.wm.save_as_mainfile(filepath=OUT)
print('@@ saved', OUT, sorted(o.name for o in bpy.data.objects))
