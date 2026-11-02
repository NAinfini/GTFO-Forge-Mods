"""Mesh work for the click tool (bmesh, in the object's own space): the plate under the cursor, the line to cut or hinge on
(a natural seam, a straight run of edges, or an annotation stroke snapped onto the edges), the piece between the lines, and
splitting it off. Nothing here knows about exosuits."""
import heapq
import math

import bmesh
import numpy as np
from mathutils import Vector
from mathutils.bvhtree import BVHTree
from mathutils.kdtree import KDTree

SHARP_DEG = 30.0      # an edge bending this much is a natural line (a panel's edge)
NEAR_SEAM = 0.025     # m: a natural line this close to the cursor is offered first
NEAR_STROKE = 0.03    # m: an annotation stroke this close to the cursor is offered first
TURN_DOT = 0.5        # a traced line continues only on edges within 60 degrees of its direction
MAX_STEPS = 4000


def kd(points):
    t = KDTree(max(1, len(points)))
    for i, p in enumerate(points):
        t.insert(p, i)
    t.balance()
    return t


class Plate:
    """One connected piece of a mesh object (the faces linked to the clicked one), with its natural lines and lookups."""

    def __init__(self, obj, face_index):
        self.obj = obj
        self.bm = bmesh.new()
        self.bm.from_mesh(obj.data)
        self.bm.faces.ensure_lookup_table()
        self.bm.edges.ensure_lookup_table()
        self.bm.verts.ensure_lookup_table()
        self.bm.normal_update()
        seed = self.bm.faces[face_index]
        self.faces = linked({seed})
        self.island = self.faces          # all faces linked to the clicked one (self.faces may be narrowed)
        self.natural = natural_edges(self.bm, {e for f in self.faces for e in f.edges})
        self.index()

    def index(self):
        """The lookups of the plate's faces (call again after narrowing self.faces)."""
        self.edges = {e for f in self.faces for e in f.edges}
        self.verts = {v for f in self.faces for v in f.verts}
        self.border = {e for e in self.edges if sum(1 for g in e.link_faces if g in self.faces) < 2}
        self.border_verts = {v for e in self.border for v in e.verts}
        self.natural &= self.edges
        self.edge_list = list(self.edges)
        self.edge_kd = kd([(e.verts[0].co + e.verts[1].co) / 2 for e in self.edge_list])
        self.vert_list = list(self.verts)
        self.vert_kd = kd([v.co for v in self.vert_list])
        self.area = sum(f.calc_area() for f in self.faces)
        index = {v: i for i, v in enumerate(self.vert_list)}
        self.bvh = BVHTree.FromPolygons([v.co for v in self.vert_list], [[index[v] for v in f.verts] for f in self.faces])

    def on(self, p, tol):
        return self.bvh.find_nearest(p, tol)[0] is not None

    def free(self):
        self.bm.free()

    def nearest_edge(self, p):
        co, i, d = self.edge_kd.find(p)
        return self.edge_list[i], d

    def nearest_vert(self, p):
        co, i, d = self.vert_kd.find(p)
        return self.vert_list[i], d


def linked(seed):
    out, stack = set(seed), list(seed)
    while stack:
        for e in stack.pop().edges:
            for g in e.link_faces:
                if g not in out:
                    out.add(g)
                    stack.append(g)
    return out


def natural_edges(bm, edges):
    """Edges where the model itself has a line: a sharp bend, a texture (UV island) or material change, or a marked seam or
    sharp edge."""
    uv = bm.loops.layers.uv.active
    out = set()
    for e in edges:
        if len(e.link_faces) != 2:
            continue
        f, g = e.link_faces
        if e.seam or not e.smooth or f.material_index != g.material_index:
            out.add(e)
            continue
        if math.degrees(f.normal.angle(g.normal, 0)) >= SHARP_DEG:
            out.add(e)
            continue
        if uv is not None:
            for v in e.verts:
                a = next((l[uv].uv for l in v.link_loops if l.face is f), None)
                b = next((l[uv].uv for l in v.link_loops if l.face is g), None)
                if a is not None and b is not None and (a - b).length > 1e-4:
                    out.add(e)
                    break
    return out


def other(e, v):
    return e.verts[1] if e.verts[0] is v else e.verts[0]


def walk(plate, v, d, seen, only):
    """From vertex v heading along d: the straightest next edges (only edges in `only` when given) until the plate's border
    (unless following it), a sharp turn or a closed loop. Returns the edges."""
    out = []
    for _ in range(MAX_STEPS):
        if v in plate.border_verts and out and only is not plate.border:
            break
        best, score = None, TURN_DOT
        for e in v.link_edges:
            if e not in plate.edges or e in seen:
                continue
            w = (other(e, v).co - v.co)
            if w.length < 1e-9:
                continue
            if only is not None and e not in only:
                continue
            s = w.normalized().dot(d)
            if s > score:
                best, score = e, s
        if best is None:
            break
        seen.add(best)
        out.append(best)
        nv = other(best, v)
        d = (d + (nv.co - v.co).normalized()).normalized()
        v = nv
    return out


def trace(plate, e, only=None):
    """A line through edge e both ways (an ordered list of edges), on edges in `only` when given."""
    a, b = e.verts
    d = (b.co - a.co).normalized()
    seen = {e}
    fwd = walk(plate, b, d, seen, only)
    back = walk(plate, a, -d, seen, only)
    return list(reversed(back)) + [e] + fwd


def straightness(edges):
    vs = chain_points(edges)
    if len(vs) < 2:
        return 0.0
    length = sum(e.calc_length() for e in edges)
    return (vs[-1] - vs[0]).length / max(length, 1e-9)


def chain_verts(edges):
    """The vertices along a chain of edges, in order."""
    if not edges:
        return []
    if len(edges) == 1:
        return list(edges[0].verts)
    a, b = edges[0].verts
    v = a if b in edges[1].verts else b
    out = [v]
    for e in edges:
        v = other(e, v)
        out.append(v)
    return out


def chain_points(edges):
    """The vertex positions along a chain of edges, in order."""
    return [v.co.copy() for v in chain_verts(edges)]


def candidates(plate, p, strokes):
    """Lines offered at point p (object space), best first, as (kind, edges) with kind stroke / seam / edge / run: an annotation stroke near p snapped onto the edges, the natural
    line nearest p, the plate's own edge near p, then the straight runs of edges through the vertex nearest p."""
    out = []
    for pts in strokes:
        if min((q - p).length for q in pts) <= NEAR_STROKE:
            line = snap_stroke(plate, pts)
            if line:
                out.append(("stroke", line))
    for kind, edges in (("seam", plate.natural), ("edge", plate.border)):
        near = [x for x in edges if ((x.verts[0].co + x.verts[1].co) / 2 - p).length <= NEAR_SEAM]
        if near:
            out.append((kind, trace(plate, min(near, key=lambda x: ((x.verts[0].co + x.verts[1].co) / 2 - p).length), edges)))
    v, _ = plate.nearest_vert(p)
    runs = [trace(plate, x) for x in v.link_edges if x in plate.edges]
    runs.sort(key=lambda r: -(straightness(r) * sum(x.calc_length() for x in r)))
    out += [("run", r) for r in runs]
    unique, keys = [], set()
    for kind, r in out:
        k = frozenset(r)
        if r and k not in keys:
            keys.add(k)
            unique.append((kind, r))
    return unique


def shortest(plate, start, goals, cost, avoid=()):
    """The cheapest run of the plate's edges from vertex `start` to any of `goals` (cost(edge) per edge, edges in `avoid`
    never used): an ordered list of edges, [] when start is a goal, None when none is reachable."""
    if start in goals:
        return []
    dist, prev, heap, done = {start: 0.0}, {}, [(0.0, id(start), start)], set()
    end = None
    while heap:
        c, _, v = heapq.heappop(heap)
        if v in done:
            continue
        done.add(v)
        if v in goals:
            end = v
            break
        for e in v.link_edges:
            if e not in plate.edges or e in avoid:
                continue
            w = other(e, v)
            nc = c + cost(e)
            if nc < dist.get(w, float("inf")):
                dist[w], prev[w] = nc, (v, e)
                heapq.heappush(heap, (nc, id(w), w))
    if end is None:
        return None
    out, v = [], end
    while v is not start:
        v, e = prev[v]
        out.append(e)
    return out[::-1]


STROKE_ON = 0.01      # m: stroke points this close to the plate are on it
STROKE_STEP = 0.015   # m: the stroke is followed through its points this far apart
STROKE_TO_EDGE = 0.04 # m: a stroke ending this close to the plate's edge is carried on to it


def snap_stroke(plate, pts):
    """A drawn stroke as a run of the plate's edges through its points (edges off the stroke cost much more, natural ones
    less), carried on to the plate's edge where it stops just short of it."""
    near = [q for q in pts if plate.on(q, STROKE_ON)]
    if len(near) < 2:
        return []
    skd = kd(near)

    def cost(e):
        off = skd.find((e.verts[0].co + e.verts[1].co) / 2)[2]
        return e.calc_length() * (1 + (off / 0.004) ** 2) * (0.7 if e in plate.natural else 1.0)

    keys = [plate.nearest_vert(near[0])[0]]
    for q in near[1:]:
        if (q - keys[-1].co).length >= STROKE_STEP:
            keys.append(plate.nearest_vert(q)[0])
    last = plate.nearest_vert(near[-1])[0]
    if last is not keys[-1]:
        keys.append(last)
    if len(keys) < 2:
        return []
    seq = [keys[0]]                 # the vertices along the stroke, loops cut out
    for b in keys[1:]:
        for e in shortest(plate, seq[-1], {b}, cost) or ():
            w = other(e, seq[-1])
            seq = seq[:seq.index(w) + 1] if w in seq else seq + [w]
    line = [next(e for e in a.link_edges if other(e, a) is b) for a, b in zip(seq, seq[1:])]
    if not line:
        return []
    used = set(line)

    def to_edge(v):
        if v in plate.border_verts:
            return []
        run = shortest(plate, v, plate.border_verts, lambda e: e.calc_length(), used)
        return run if run is not None and sum(e.calc_length() for e in run) <= STROKE_TO_EDGE else []

    head = to_edge(seq[0])
    tail = to_edge(seq[-1])
    return [e for e in reversed(head)] + line + tail

SIDE_STEP = 0.003     # m: a line is sampled this finely for the side test
BETWEEN_MARGIN = 0.02 # m: a piece between two lines reaches this much farther than they are apart (the plate's thickness)


def side_field(plate, line, toward):
    """Which side of a line each face of the plate lies on: {face: (signed distance, distance to the line)}, the side
    positive toward point `toward`. The side is taken across the surface (the line's direction crossed with the surface
    normal under it), so a thick plate's inner skin and edges go with the outer skin above them, and past its ends the line
    carries on straight."""
    vs = chain_verts(line)
    samples = []
    for a, b in zip(vs, vs[1:]):
        t = b.co - a.co
        if t.length < 1e-9:
            continue
        perp = t.normalized().cross((a.normal + b.normal).normalized()).normalized()
        k = max(1, int(t.length / SIDE_STEP))
        samples += [(a.co + t * (s / k), perp) for s in range(k + 1)]
    if not samples:
        return {}
    tree = kd([p for p, _ in samples])

    def side(c):
        _, i, d = tree.find(c)
        p, perp = samples[i]
        return (c - p).dot(perp), d

    flip = -1.0 if side(toward)[0] < 0 else 1.0
    out = {}
    for f in plate.faces:
        s, d = side(f.calc_center_median())
        out[f] = (flip * s, d)
    return out


def grow(seeds, allowed):
    """The faces of `allowed` connected to the seeds (through shared edges)."""
    comp = {f for f in seeds if f in allowed}
    stack = list(comp)
    while stack:
        for e in stack.pop().edges:
            for g in e.link_faces:
                if g in allowed and g not in comp:
                    comp.add(g)
                    stack.append(g)
    return comp


def between(plate, cut, hinge):
    """The faces between the cut line and the hinge line: on the hinge's side of the cut and the cut's side of the hinge, no
    farther from either line than the lines are apart (plus BETWEEN_MARGIN), connected to the faces along the cut. None when
    that is nothing or the whole plate."""
    pc, ph = chain_points(cut), chain_points(hinge)
    hk, ck = kd(ph), kd(pc)
    # Each side is the one facing the other line where they are closest to each other's middle.
    sc = side_field(plate, cut, hk.find(pc[len(pc) // 2])[0])
    sh = side_field(plate, hinge, ck.find(ph[len(ph) // 2])[0])
    reach = max(hk.find(p)[2] for p in pc) + BETWEEN_MARGIN
    inside = {f for f in plate.faces if f in sc and f in sh and sc[f][0] > 0 and sh[f][0] > 0
              and sc[f][1] <= reach and sh[f][1] <= reach}
    faces = grow({g for e in cut for g in e.link_faces}, inside)
    return faces if faces and len(faces) < len(plate.faces) else None


def area(faces):
    return sum(f.calc_area() for f in faces)

def axis_of(points):
    """(centre, unit direction) of a line of points (its principal direction)."""
    a = np.array([tuple(p) for p in points])
    c = a.mean(axis=0)
    d = np.linalg.svd(a - c)[2][0]
    return Vector(c), Vector(d).normalized()


def lift(centres, normals, areas, centre, axis, sign):
    """How the far edge of the piece (its faces farthest from the hinge) starts to move when it turns `sign` about the hinge:
    the area-weighted mean of its unit velocity along each face's normal (1: straight off the surface, -1: into it; thin rim
    faces hardly count)."""
    pts, nrm, w = np.asarray(centres, float), np.asarray(normals, float), np.asarray(areas, float)
    c, a = np.asarray(centre, float), np.asarray(axis, float)
    r = pts - c
    r -= np.outer(r @ a, a)
    dist = np.linalg.norm(r, axis=1)
    far = dist >= np.quantile(dist, 0.8)
    v = np.cross(a * sign, r[far])
    v /= np.maximum(np.linalg.norm(v, axis=1), 1e-12)[:, None]
    n = nrm[far] / np.maximum(np.linalg.norm(nrm[far], axis=1), 1e-12)[:, None]
    return float(((v * n).sum(axis=1) * w[far]).sum() / max(w[far].sum(), 1e-12))


def split_off(obj, indices, name):
    """The faces of obj's mesh with these indices as a new object beside it (same materials, attributes, place); obj keeps
    the rest."""
    import bpy
    idx = set(indices)
    src = bmesh.new()
    src.from_mesh(obj.data)
    piece = src.copy()
    piece.faces.ensure_lookup_table()
    bmesh.ops.delete(piece, geom=[f for f in piece.faces if f.index not in idx], context="FACES")
    me = bpy.data.meshes.new(name)
    piece.to_mesh(me)
    piece.free()
    src.faces.ensure_lookup_table()
    bmesh.ops.delete(src, geom=[f for f in src.faces if f.index in idx], context="FACES")
    src.to_mesh(obj.data)
    src.free()
    obj.data.update()
    for m in obj.data.materials:
        me.materials.append(m)
    new = bpy.data.objects.new(name, me)
    new.matrix_world = obj.matrix_world.copy()
    for vg in obj.vertex_groups:
        new.vertex_groups.new(name=vg.name)
    for c in obj.users_collection:
        c.objects.link(new)
    return new


def merge_back(piece, into):
    """Join `piece` back into `into` (welding the seam it was cut along) and remove it."""
    import bpy
    bm = bmesh.new()
    bm.from_mesh(into.data)
    other_bm = bmesh.new()
    other_bm.from_mesh(piece.data)
    other_bm.transform(into.matrix_world.inverted() @ piece.matrix_world)
    tmp = bpy.data.meshes.new("_fh_tmp")
    other_bm.to_mesh(tmp)
    other_bm.free()
    bm.from_mesh(tmp)
    bpy.data.meshes.remove(tmp)
    bmesh.ops.remove_doubles(bm, verts=[v for v in bm.verts if v.is_boundary], dist=1e-5)
    bm.to_mesh(into.data)
    bm.free()
    into.data.update()
    me = piece.data
    bpy.data.objects.remove(piece, do_unlink=True)
    if me.users == 0:
        bpy.data.meshes.remove(me)
