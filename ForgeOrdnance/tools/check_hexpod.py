"""Checks the one drop pod blend (make_hexpod.py output): required parts, door clearance over the whole swing, door
thickness and edge material.

    blender -b <M02_hex.blend> --python Mods/ForgeOrdnance/tools/check_hexpod.py

Prints "CHECK ok" or each failure as "CHECK fail: ...", exit code 1 on failure.
"""
import math
import sys

import bmesh
import bpy
from mathutils import Matrix
from mathutils.bvhtree import BVHTree

OPEN = 85.0
SIGNS = {'door_1': -1, 'door_2': 1, 'door_3': -1, 'door_4': 1}   # Blender degrees; Unity angles are the negatives
failures = []
objs = bpy.data.objects

required = ['body', 'interior', 'cradle', 'shelf_1', 'shelf_2', 'weapon_anchor_1', 'weapon_anchor_2'] \
    + [f'door_{i}' for i in range(1, 5)] + [f'pickup_anchor_{i}' for i in range(1, 5)]
for name in required:
    if name not in objs:
        failures.append('missing ' + name)
for name in objs.keys():
    if name.startswith(('clamp_', 'light_strip', 'payload_anchor', 'weapon_anchor') ) and name not in required:
        failures.append('obsolete part ' + name)


def tree(ob, matrix):
    bm = bmesh.new()
    bm.from_mesh(ob.data)
    bm.transform(matrix)
    result = BVHTree.FromBMesh(bm)
    bm.free()
    return result


if not failures:
    fixed = {n: tree(objs[n], objs[n].matrix_world) for n in ('body', 'interior', 'cradle', 'shelf_1', 'shelf_2')}
    for name, sign in SIGNS.items():
        door = objs[name]
        pivot = door.matrix_world.translation
        for step in range(0, int(OPEN) + 1, 3):
            matrix = Matrix.Translation(pivot) @ Matrix.Rotation(math.radians(sign * step), 4, 'Z')
            moving = tree(door, matrix)
            for other, bvh in fixed.items():
                hits = len(moving.overlap(bvh))
                if hits:
                    failures.append(f'{name} at {step} deg intersects {other} ({hits} triangle pairs)')
                    break
    for a, b in (('door_1', 'door_2'), ('door_3', 'door_4')):
        for step in range(0, int(OPEN) + 1, 3):
            pa, pb = objs[a].matrix_world.translation, objs[b].matrix_world.translation
            ta = tree(objs[a], Matrix.Translation(pa) @ Matrix.Rotation(math.radians(SIGNS[a] * step), 4, 'Z'))
            tb = tree(objs[b], Matrix.Translation(pb) @ Matrix.Rotation(math.radians(SIGNS[b] * step), 4, 'Z'))
            if ta.overlap(tb):
                failures.append(f'{a} and {b} intersect at {step} deg')
    for name in SIGNS:
        door = objs[name]
        pivot = door.matrix_world.translation
        # thickness: outer-to-inner distance along the face normal
        mesh = door.data
        sx, sy = (1 if pivot.x > 0 else -1), (1 if pivot.y > 0 else -1)
        normal = (0.866025 * sx, 0.5 * sy, 0.0)
        proj = [(v.co.x * normal[0] + v.co.y * normal[1]) for v in mesh.vertices]
        thickness = max(proj) - min(proj)
        if not 0.03 <= thickness <= 0.06:
            failures.append(f'{name} thickness {thickness:.3f} m outside 3-6 cm')
        slots = [m.name for m in mesh.materials]
        side = [p for p in mesh.polygons if abs(p.normal.x * normal[0] + p.normal.y * normal[1]) < .9 and abs(p.normal.z) < .9]
        side_materials = {slots[p.material_index] for p in side}
        if side_materials != {'Pod_GunGrey'}:
            failures.append(f'{name} edge faces use {sorted(side_materials)}, expected only Pod_GunGrey')
    if objs['weapon_anchor_1'].location.x <= 0 or objs['weapon_anchor_2'].location.x >= 0:
        failures.append('weapon anchors are not on +X and -X')
    ceiling = max(v.co.z for v in objs['interior'].data.vertices)
    bay = ceiling - objs['weapon_anchor_1'].location.z
    if bay < 1.20:
        failures.append(f'weapon bay {bay:.3f} m cannot stand a 1.13 m weapon')

for failure in failures:
    print('CHECK fail:', failure)
if not failures:
    print('CHECK ok')
sys.exit(1 if failures else 0)
