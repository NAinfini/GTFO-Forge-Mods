"""Mesh readers and the third-person level of detail for the energy guns (runs inside Blender, used by export_parts_blender.py).

Each body part keeps its full mesh for the first-person view and gets one reduced mesh for every other view. The reduced
mesh uses the part's own textures, so every corner takes its UV from the surface of the full mesh above or below it inside
the same UV island: the texture and the normal map land where they did, and no texel is stretched across a seam.
"""
import math

import bpy
import numpy as np
from mathutils import Vector
from mathutils.bvhtree import BVHTree
from mathutils.interpolate import poly_3d_calc

PLANAR_ANGLE = math.radians(1.5)
SAME_UV = 1e-6
LIFT = 0.008                                   # metres: a corner looks for the surface this far above and below itself
SMOOTH_ANGLE = math.radians(35)


def corners(obj, deps):
    """triangulated evaluated mesh as per-corner world-space (positions, normals, uvs), Blender frame"""
    return read(obj, deps)[:3]


def read(obj, deps):
    """corners() plus the UV island id of every triangle"""
    ev = obj.evaluated_get(deps)
    me = ev.to_mesh()
    try:
        me.calc_loop_triangles()
        nt, nl, nv = len(me.loop_triangles), len(me.loops), len(me.vertices)
        if nt == 0 or me.uv_layers.active is None:
            raise RuntimeError(f"{obj.name}: needs triangles and a UV map")
        loops = np.empty(nt * 3, np.int32)
        me.loop_triangles.foreach_get("loops", loops)
        co = np.empty(nv * 3, np.float32)
        me.vertices.foreach_get("co", co)
        vi = np.empty(nl, np.int32)
        me.loops.foreach_get("vertex_index", vi)
        nrm = np.empty(nl * 3, np.float32)
        me.corner_normals.foreach_get("vector", nrm)
        uv = np.empty(nl * 2, np.float32)
        me.attributes[me.uv_layers.active.name].data.foreach_get("vector", uv)
        islands = uv_islands(me, uv.reshape(-1, 2), loops.reshape(-1, 3))
    finally:
        ev.to_mesh_clear()
    m = np.array(obj.matrix_world, np.float64)
    pos = co.reshape(-1, 3).astype(np.float64)[vi[loops]] @ m[:3, :3].T + m[:3, 3]
    normal = nrm.reshape(-1, 3).astype(np.float64)[loops] @ np.linalg.inv(m[:3, :3])
    return pos, normal / np.linalg.norm(normal, axis=1, keepdims=True), uv.reshape(-1, 2).astype(np.float64)[loops], islands


def uv_islands(me, uv, tri_loops):
    """island id per loop triangle: faces joined across every edge whose two sides agree about the UVs"""
    nl, npoly = len(me.loops), len(me.polygons)
    start = np.empty(npoly, np.int32)
    me.polygons.foreach_get("loop_start", start)
    total = np.empty(npoly, np.int32)
    me.polygons.foreach_get("loop_total", total)
    edge = np.empty(nl, np.int32)
    me.loops.foreach_get("edge_index", edge)
    poly = np.repeat(np.arange(npoly, dtype=np.int32), total)
    nxt = start[poly] + (np.arange(nl, dtype=np.int32) - start[poly] + 1) % total[poly]
    order = np.argsort(edge, kind="stable")
    parent = np.arange(npoly)

    def find(a):
        while parent[a] != a:
            parent[a] = parent[parent[a]]
            a = parent[a]
        return a

    i = 0
    while i < nl - 1:
        a, b = order[i], order[i + 1]
        if edge[a] == edge[b] and (i + 2 >= nl or edge[order[i + 2]] != edge[a]):
            if np.abs(uv[a] - uv[nxt[b]]).max() < SAME_UV and np.abs(uv[nxt[a]] - uv[b]).max() < SAME_UV:
                parent[find(poly[a])] = find(poly[b])
            i += 2
        else:
            i += 1
    root = np.array([find(p) for p in range(npoly)])
    return root[poly[tri_loops[:, 0]]]


def bake(obj, name, modifier=None):
    """a standalone object holding obj's evaluated mesh (after `modifier(obj)` is added) at obj's world transform"""
    if modifier:
        modifier(obj)
    bpy.context.view_layer.update()
    mesh = bpy.data.meshes.new_from_object(obj.evaluated_get(bpy.context.evaluated_depsgraph_get()))
    copy = bpy.data.objects.new(name, mesh)
    bpy.context.scene.collection.objects.link(copy)
    copy.matrix_world = obj.matrix_world
    return copy


def discard(*objs):
    for o in objs:
        mesh = o.data
        bpy.data.objects.remove(o)
        if mesh.users == 0:
            bpy.data.meshes.remove(mesh)


def collapsed(obj, target):
    """a new object with about `target` triangles: coplanar faces dissolved, the rest collapsed by quadric error"""
    def planar(o):
        m = o.modifiers.new("planar", "DECIMATE")
        m.decimate_type = "DISSOLVE"
        m.angle_limit = PLANAR_ANGLE
        m.delimit = {"UV", "MATERIAL", "SHARP"}

    flat = bake(obj, obj.name + ".flat", planar)
    obj.modifiers.clear()
    flat.data.calc_loop_triangles()
    left = len(flat.data.loop_triangles)
    if left <= target:
        return flat

    def collapse(o):
        m = o.modifiers.new("collapse", "DECIMATE")
        m.decimate_type = "COLLAPSE"
        m.ratio = max(0.001, target / left)
        m.use_collapse_triangulate = True

    low = bake(flat, obj.name + ".low", collapse)
    discard(flat)
    return low


def low_corners(obj, deps, target):
    """-> (positions, normals, uvs) per corner like corners(), for a mesh of about `target` triangles; the normals
    are smoothed from the new faces, the UVs read from the full mesh where each corner lands"""
    full_pos, _, full_uv, full_pos_islands = read(obj, deps)
    copy = bake(obj, obj.name + ".copy")
    low = collapsed(copy, target)
    pos = corners(low, bpy.context.evaluated_depsgraph_get())[0].reshape(-1, 3, 3)
    discard(low, copy)
    pos = pos[np.linalg.norm(np.cross(pos[:, 1] - pos[:, 0], pos[:, 2] - pos[:, 0]), axis=1) > 1e-12]
    tris = full_pos.reshape(-1, 3, 3)
    uvs = full_uv.reshape(-1, 3, 2)
    whole = BVHTree.FromPolygons([Vector(v) for v in tris.reshape(-1, 3)], [(3 * i, 3 * i + 1, 3 * i + 2) for i in range(len(tris))])
    trees = {}
    for island in np.unique(full_pos_islands):
        ids = np.flatnonzero(full_pos_islands == island)
        trees[island] = (ids, BVHTree.FromPolygons([Vector(v) for v in tris[ids].reshape(-1, 3)], [(3 * i, 3 * i + 1, 3 * i + 2) for i in range(len(ids))]))
    face = np.cross(pos[:, 1] - pos[:, 0], pos[:, 2] - pos[:, 0])
    face /= np.maximum(np.linalg.norm(face, axis=1, keepdims=True), 1e-12)
    out_uv = []
    for t, n in zip(pos, face):
        n = Vector(n)
        centre = Vector(t.mean(0))
        hit = whole.ray_cast(centre + n * LIFT, -n, 3 * LIFT)[2]
        if hit is None:
            hit = whole.ray_cast(centre - n * LIFT, n, 3 * LIFT)[2]
        if hit is None:
            hit = whole.find_nearest(centre)[2]
        ids, tree = trees[full_pos_islands[hit]]
        for corner in t:
            c = Vector(corner)
            point, _, index, _ = tree.ray_cast(c + n * LIFT, -n, 3 * LIFT)
            if index is None:
                point, _, index, _ = tree.ray_cast(c - n * LIFT, n, 3 * LIFT)
            if index is None:
                point, _, index, _ = tree.find_nearest(c)
            tri = ids[index]
            out_uv.append(np.array(poly_3d_calc([Vector(v) for v in tris[tri]], point)) @ uvs[tri])
    return pos.reshape(-1, 3), smooth_normals(pos), np.array(out_uv)


def smooth_normals(pos):
    """per-corner normals of a triangle soup: area-weighted over the faces at the same point that bend less than SMOOTH_ANGLE"""
    face = np.cross(pos[:, 1] - pos[:, 0], pos[:, 2] - pos[:, 0])
    area = np.linalg.norm(face, axis=1)
    face /= np.maximum(area, 1e-12)[:, None]
    flat = pos.reshape(-1, 3)
    _, point = np.unique(np.rint(flat * 1e5).astype(np.int64), axis=0, return_inverse=True)
    order = np.argsort(point.reshape(-1), kind="stable")
    bounds = np.flatnonzero(np.diff(point.reshape(-1)[order])) + 1
    out = np.empty_like(flat)
    for group in np.split(order, bounds):
        f = group // 3
        n = face[f]
        out[group] = ((n @ n.T > math.cos(SMOOTH_ANGLE)) * area[f]) @ n + 1e-9 * n
    return out / np.linalg.norm(out, axis=1, keepdims=True)

