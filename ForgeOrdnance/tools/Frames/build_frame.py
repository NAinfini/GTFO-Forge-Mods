"""Builds one exosuit from its baked cut file (Blender 5.2, background).

  blender --background --factory-startup --disable-autoexec --python-exit-code 1 <baked.blend> --python build_frame.py -- \
      --model M15 --out <dir> [--preview]

The input is the file the hinge add-on's bake.py writes from the owner's cut file (Tools/Blender/hinge_tool: the owner cuts the parts
and sets their hinges there): the mesh objects in the collection FIXED stay, those in every other collection open together as
one rigid part, and every part carries its move as fh_motion (bake.py's docstring), expanded by the add-on's motion.sequence.
Every object stands where it is on the shut suit (bake.py puts each part at its shut transform), so the shut shell is each
object's mesh at its world transform. A part's lining (faces with the add-on's lining material) is inner wall like the shell's own (interior.py dresses it all).
The scene's custom property "frame" holds the model, the joints and the source's triangle count.

Steps: join the groups into one skinned body at the shut pose (each part its own piece, weighted to its own bone), give the
shell real thickness closed on every edge (thin where a part has a lining, so the lining stays the inside one sees), find
every other face that looks into the suit (the cavity), check the shell is
closed and outward, every pilot slot (the path of each of the pilot's body parts straight out of the back) clear with every
part open, no part swept through the body or another part on its path, no second layer left under an opening part and no
small fitting left hovering (check_entry, written to <model>_entry.json), paint the light strips along the shell's structural
lines into the albedo, metallic and emission maps (glow.py), dress the inside (interior.py), give each part its own bone, key
the opening as the clip "open" (the parts' bones only), and write <model>_rigged.blend/.fbx, the four textures, the lining
textures and <model>_rig.json. Any failed check stops the build; with --preview the problems are only reported, so the owner can see the
cut."""
import argparse
import json
import math
import os
import shutil
import sys

import bmesh
import bpy
import numpy as np
from mathutils import Euler, Matrix, Vector
from mathutils.bvhtree import BVHTree

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
sys.path.insert(0, os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "..", "..", "..", "Tools", "Blender"))  # hinge_tool, the Blender add-on
import glow  # noqa: E402
import interior  # noqa: E402
import wear  # noqa: E402
from hinge_tool.model import lining_slots  # noqa: E402
from hinge_tool.motion import FPS, Path, screw_parts, sequence  # noqa: E402
# Final height (m), arm drop below horizontal (deg), skeleton family, texture layout.
MODELS = {
    "M15": {"name": "Infiltrator", "height": 2.25, "drop": 74, "rig": "halo5", "tex": "substance",
            "sets": {"Recruit Helmet": "Recruit_Helmet", "Infiltrator Body": "Infiltrator_Body",
                     "Male Techsuit Thighs": "Male_Techsuit_Thighs"}},
    "M16": {"name": "Wrath", "height": 2.45, "drop": 70, "rig": "halo5", "tex": "wrath"},
    "M17": {"name": "Helios", "height": 2.30, "drop": 74, "rig": "helios", "tex": "embedded", "tint": (0.66, 0.27, 0.19)},
    "M18": {"name": "Noble", "height": 2.20, "drop": 74, "rig": "halo5", "tex": "substance",
            "sets": {"Engineer Helmet": "Engineer_Helmet", "Noble Body": "Noble_Body",
                     "Male Techsuit Thighs": "Male_Techsuit_Thighs"}},
}
TEXTURES = ("albedo", "metallic", "normal", "emission")
# The cut file's collection of what stays (every other collection holding meshes is one opening part).
FIXED = "固定"
# Tiles in the reserved top strip of the atlas (u range, all at v 0.965..0.995): the dark cavity on the shell's inner wall
# and the clean trim on every cut edge.
TILE = {"cavity": (0.010, 0.080), "trim": (0.095, 0.125)}
TILE_V = (0.965, 0.995)
SHELL_T = 0.02         # shell thickness (m)
LINED_SHARE = 0.5      # an object with a lining: each of its two skins (the plate and its lining, 0.6 to 10 mm under it) is
LINED_MIN_T = 0.001    # thickened at most this share of the gap to the other, so neither shows through the other and the
                       # lining is the inside one sees; but at least LINED_MIN_T m (thinner slabs leave rims too small to close)
LINING_CLEAR = 0.004   # m: a lining stands at least this far under its plate (clear_lining), so the two slabs never cross
ISLAND = 0.003         # m²: a patch of inner faces smaller than this, mostly bordered by armour seen from outside, is armour
HEMISPHERE = 16        # rays per face for the cavity test (cavity_walls)
WELD = 0.00002         # m: points of a lining this close are one point (slivers the lining work leaves)
LINING_CRUMB = 1e-5    # m²: a loose bit of lining smaller than this is a sliver, not lining
TINY_AREA = 1e-6      # triangles smaller than this (m²) are collapsed; Unity's importer drops those under about 1e-7
SMALL = 0.1            # m: a separate piece smaller than this across is a fitting (check_entry: it must move with what it sits on)
TOUCH = 0.01           # m: a fitting this close to a surface sits on it


def arguments():
    argv = sys.argv[sys.argv.index("--") + 1:]
    p = argparse.ArgumentParser()
    p.add_argument("--model", required=True)
    p.add_argument("--out", required=True)
    p.add_argument("--preview", action="store_true", help="report failed checks instead of stopping")
    return p.parse_args(argv)


def log(*a):
    print("FRAME", *a, flush=True)


# ---------------------------------------------------------------------------------------------------------------- geometry
def pieces(faces):
    """Connected groups of faces (through shared edges)."""
    left = set(faces)
    out = []
    while left:
        seed = left.pop()
        stack, piece = [seed], [seed]
        while stack:
            f = stack.pop()
            for e in f.edges:
                for g in e.link_faces:
                    if g in left:
                        left.remove(g)
                        stack.append(g)
                        piece.append(g)
        out.append(piece)
    return out


def tile_point(name):
    u0, u1 = TILE[name]
    return ((u0 + u1) / 2, sum(TILE_V) / 2)


class Segment:
    """A body part's axis: point(s) along it, `w` toward the back, `u` toward its outer side."""

    def __init__(self, a, b, lateral):
        self.a = a.copy()
        self.b = b.copy()
        self.L = (b - a).length
        self.ax = (b - a) / self.L
        back = Vector((0, 1, 0))
        self.w = (back - back.dot(self.ax) * self.ax).normalized()
        u = lateral - lateral.dot(self.ax) * self.ax - lateral.dot(self.w) * self.w
        self.u = u.normalized()

    def point(self, s):
        return self.a + self.ax * s

    def local(self, p):
        d = p - self.a
        return d.dot(self.ax), d.dot(self.w), d.dot(self.u)


def dominant(f, deform, names):
    acc = {}
    for v in f.verts:
        for g, w in v[deform].items():
            acc[g] = acc.get(g, 0.0) + w
    if not acc:
        return None
    return names[max(acc, key=acc.get)]


# -------------------------------------------------------------------------------------------------------------- opening
LIMBS = ("upperarm", "forearm", "thigh", "shin")
# The pilot's body parts (half width, metres) and how far back their way in reaches: the opening must leave a slot this wide
# from each body part's axis straight out of the back (Slot).
PILOT = {"torso": 0.136, "upperarm": 0.036, "forearm": 0.032, "thigh": 0.052, "shin": 0.04}   # the game's pilot, slimmer than a reference body
SLOT_DEPTH = 1.0
SLOT_SHARE = 0.7   # of the cavity's width across a segment, the part a slot spans
SLOT_MIN = 0.015   # m: a slot narrower than this either side (a joint's narrowing) is no way in
STILL_M, STILL_DEG = 0.001, 0.1   # a part this close to its place (m, degrees) is there
LAYER_GAP = 0.015   # m: a fixed surface this close over or under an opening part, facing the same way, is a second layer of it
LAYER_NEAR, LAYER_SHARE = 0.025, 0.1   # a part lies under another when this share of it is this close to it, shut

class Slot:
    """The way one of the pilot's body parts goes in: from inside the segment straight out of the back. At each station
    along the axis the slot starts in the middle of the shut suit's cavity (front to back and across the segment) and spans
    SLOT_SHARE of the cavity's width there once the shell has its thickness (at most `half` either side): the opening must
    expose the inside, not narrow it (`fit`, on the unthickened shell). Checked as rays from there backwards (`rays`); a
    hit on the opened suit blocks it."""

    def __init__(self, name, seg, half, s0, s1, n_s, n_t):
        self.name, self.seg, self.half, self.n_t = name, seg, half, n_t
        self.s = [seg.L * (s0 + (s1 - s0) * k / (n_s - 1)) for k in range(n_s)]
        self.across = [(0.0, 0.0, half)] * n_s

    def fit(self, tree):
        """Centres and sizes each station on the cavity of `tree` (the shut suit): (depth, across, half width), or None
        where the cavity, less the shell's thickness (SHELL_T, given to it later, inward), is too small for a body part."""
        def reach(p, d, far):
            loc, nrm, idx, dist = tree.ray_cast(p, d, 0.5 if far is None else far)
            return dist if idx is not None else far

        self.across = []
        for s in self.s:
            p = self.seg.point(s)
            back, front = reach(p, self.seg.w, None), reach(p, -self.seg.w, None)
            if back is None or front is None:
                # The segment's axis runs outside the shell here (no wall both ways): no cavity to centre on.
                self.across.append(None)
                continue
            depth = (back - front) / 2
            p = p + self.seg.w * depth
            right, left = reach(p, self.seg.u, self.half / SLOT_SHARE + SHELL_T), reach(p, -self.seg.u, self.half / SLOT_SHARE + SHELL_T)
            centre, half = (right - left) / 2, min(self.half, SLOT_SHARE * ((right + left) / 2 - SHELL_T))
            if self.name != "torso" and abs(self.seg.u.x) > 0.3:
                # A limb's slot stays on its own side of the body's middle (where the legs meet, the cavity runs on
                # across the crotch to the other leg; the pilot's thigh does not).
                inner = -p.x / self.seg.u.x + 0.01
                if centre - half < inner:
                    outer = centre + half
                    centre, half = (inner + outer) / 2, (outer - inner) / 2
            ok = half >= SLOT_MIN and (back + front) / 2 - SHELL_T >= SLOT_MIN
            self.across.append((depth, centre, half) if ok else None)
        return self

    @property
    def count(self):
        return sum(1 for a in self.across if a) * self.n_t

    def rays(self, extra=0.0):
        """Rays from the slot's stations straight back, across its width; with `extra`, the slot grown by that much all
        round (across, and up and down the axis), a margin for what the build's later steps (triangulation, merging tiny
        triangles) move by."""
        for s, across in zip(self.s, self.across):
            if across is None:
                continue
            depth, centre, half = across
            for ds in ((0.0,) if not extra else (-extra, 0.0, extra)):
                for k in range(self.n_t):
                    t = centre + (half + extra) * (-1 + 2 * k / (self.n_t - 1))
                    yield self.seg.point(s + ds) + self.seg.w * depth + self.seg.u * t, self.seg.w


def pilot_slots(torso, segments):
    slots = [Slot("torso", torso, PILOT["torso"], 0.05, 0.95, 15, 7)]
    for side in ("L", "R"):
        for limb in LIMBS:
            # The shin's and forearm's slots stop short of the ankle and wrist, where the foot and hand (which never open)
            # begin.
            slots.append(Slot(limb + "." + side, segments[limb + "." + side], PILOT[limb], 0.12,
                              0.8 if limb in ("shin", "forearm") else 0.88, 7, 5))
    return slots


def slots_blocked(slots, tree, owner_of=None, hits=None):
    """Rays of `slots` that hit `tree`: (count, {slot: count}, {owner: count}); `hits` collects [slot, owner, ray origin,
    direction, distance] of the first 200."""
    total, by_slot, by_owner = 0, {}, {}
    for slot in slots:
        for o, d in slot.rays():
            loc, nrm, idx, dist = tree.ray_cast(o, d, SLOT_DEPTH)
            if idx is not None:
                total += 1
                by_slot[slot.name] = by_slot.get(slot.name, 0) + 1
                if owner_of is not None:
                    k = owner_of(idx)
                    by_owner[k] = by_owner.get(k, 0) + 1
                    if hits is not None and len(hits) < 200:
                        hits.append([slot.name, k, [round(c, 3) for c in o], [round(c, 3) for c in d], round(dist, 3)])
    return total, by_slot, by_owner


def rigid_fit(src, dst):
    """The rigid transform (a turn and a shift, no scale or mirror) that takes the points `src` closest onto `dst` (Kabsch),
    and the largest distance left between them."""
    a, b = np.asarray(src, dtype=float), np.asarray(dst, dtype=float)
    ca, cb = a.mean(0), b.mean(0)
    u, _, vt = np.linalg.svd((a - ca).T @ (b - cb))
    d = np.sign(np.linalg.det(vt.T @ u.T)) or 1.0
    r = vt.T @ np.diag([1.0, 1.0, d]) @ u.T
    shift = cb - r @ ca
    miss = float(np.linalg.norm(a @ r.T + shift - b, axis=1).max())
    m = Matrix.Identity(4)
    for i in range(3):
        for k in range(3):
            m[i][k] = r[i, k]
        m[i][3] = shift[i]
    return m, miss


def part_region(j, points):
    """Where a part sits shut (its points, shut): (region, side). The region is the body part it lies on, its bone's
    parent in the rig ("back" hangs on the chest): the one most of its points are nearest, of the torso and the limb segments,
    each measured from its surface (the axis less a typical radius), so a leaf of the back reaching down past the hip still
    hangs on the chest. Side (of its centre) is the suit's L (+X) or R, or C on the middle."""
    pts = np.asarray(points, dtype=float)
    c = Vector(pts.mean(0))
    segments = [("back", None, j["pelvis"] - Vector((0, 0, 0.2)), j["neck"], 0.15)]
    for s in ("L", "R"):
        for limb, end, r in (("upperarm", "forearm", 0.06), ("forearm", "hand", 0.05), ("thigh", "shin", 0.085),
                             ("shin", "foot", 0.06)):
            segments.append((limb, s, j[limb + "." + s], j[end + "." + s], r))
    def reach(v, g):
        # A limb claims only what lies along it, past its joint (not what sits over the joint, on the hip or the shoulder).
        if g[1] is not None and (v - g[2]).dot(g[3] - g[2]) <= 0:
            return float("inf")
        return segment_distance(v, g[2], g[3]) - g[4]

    votes = {}
    for p in pts[::max(1, len(pts) // 400)]:
        v = Vector(p)
        g = min(segments, key=lambda g: reach(v, g))
        votes[g[:2]] = votes.get(g[:2], 0) + 1
    region, side = max(votes, key=votes.get)
    if side is None:
        side = "L" if c.x > 0.03 else "R" if c.x < -0.03 else "C"
    return region, side


def off(d):
    """How far the rigid transform `d` is from rest: (metres its origin moved, degrees it turned)."""
    angle = d.to_quaternion().angle
    return d.translation.length, math.degrees(min(angle, 2 * math.pi - angle))


def posed_at(track, co, open_):
    """Where rest point `co` of a part is at `open_` (0..1) of the opening: its track's samples, linearly between them."""
    f = min(max(open_, 0.0), 1.0) * (len(track) - 1)
    k = min(int(f), len(track) - 2)
    return (track[k] @ co).lerp(track[k + 1] @ co, f - k)


def segment_distance(p, a, b):
    ab = b - a
    s = max(0.0, min(1.0, (p - a).dot(ab) / max(ab.length_squared, 1e-12)))
    return (a + ab * s - p).length


class Surface:
    """A BVH over faces (at the given positions) that also knows each face's outline, so a ray that only grazes a face's
    edge (a seam sliding along the cut it was made by) is told apart from one that goes through it."""

    def __init__(self, faces, where=lambda v: v.co):
        tmp = bmesh.new()
        vmap = {}
        self.outlines = []
        self.faces = []
        for f in faces:
            try:
                tmp.faces.new([vmap.setdefault(v, tmp.verts.new(where(v))) for v in f.verts])
                self.outlines.append([where(v).copy() for v in f.verts])
                self.faces.append(f)
            except ValueError:
                pass
        self.tree = BVHTree.FromBMesh(tmp)
        tmp.free()

    def through(self, origin, direction, length):
        """The first face the segment goes through (not just touching an edge within 3 mm), as (index, distance), or None."""
        start = origin
        left = length
        while left > 0:
            loc, nrm, idx, dist = self.tree.ray_cast(start, direction, left)
            if idx is None:
                return None
            outline = self.outlines[idx]
            edge = min(segment_distance(loc, a, b) for a, b in zip(outline, outline[1:] + outline[:1]))
            # Through it, not along it: a seam point sliding over the face it was cut from only grazes it.
            if edge > 0.003 and abs(direction.dot(nrm)) > 0.15:
                return idx, length - left + dist
            start = loc + direction * 1e-4
            left -= dist + 1e-4
        return None


def second_layers(bm, opening, source):
    """Fixed faces lying within LAYER_GAP straight over or under an opening face of the same source object (`source`, a face
    layer: the cut file's object it came from) and facing the same way (or exactly the other way: a doubled sheet): they
    would stay where the part was and show as a copy of it, so the owner must put them in the part too. A separate object
    lying on another (armor on the undersuit) is not a second layer of it. opening maps face -> part key. Returns the
    centres of the faces found."""
    surfaces = {}
    for f in opening:
        surfaces.setdefault(f[source], []).append(f)
    surfaces = {k: Surface(v) for k, v in surfaces.items()}
    found = []
    for f in bm.faces:
        surface = surfaces.get(f[source])
        if f in opening or surface is None:
            continue
        c = f.calc_center_median()
        loc, nrm, idx, dist = surface.tree.find_nearest(c, LAYER_GAP)
        if idx is None:
            continue
        g = surface.faces[idx]
        # Over or under it (or lying right on it, a doubled sheet), not beside it: the nearest point lies straight along
        # the part's normal, inside the part's face rather than on its edge (a neighbouring face of the same sheet meets
        # the part at its edge, beside it).
        off = c - loc
        along = off.dot(g.normal)
        outline = [v.co for v in g.verts]
        edge = min(segment_distance(loc, a, b) for a, b in zip(outline, outline[1:] + outline[:1]))
        if abs(f.normal.dot(g.normal)) > 0.85 and (off - along * g.normal).length < 0.003 and edge > 0.001:
            found.append(c)
    return found

def check_entry(bm, deform, ids, plates, slots, out_path, layers, fittings, preview):
    """With every part fully open, every pilot slot is clear: rays from each body part's axis straight out of the back meet
    nothing within SLOT_DEPTH (the same rays meet the shut suit, which the report also counts). Over the
    whole opening (every sample of the parts' tracks), no shell point of a part may cross the fixed shell or another part's shell.
    `layers` (second layers left under a part) and `fittings` (small pieces left sitting on another part) must be empty.
    Writes the result to `out_path` for the preview and the offline checks; a failed check stops the build unless `preview`."""
    by_gid = {ids[p["node"]]: i for i, p in enumerate(plates)}
    owner = {}
    for v in bm.verts:
        gid = next((g for g, w in v[deform].items() if w >= 0.5 and g in by_gid), None)
        if gid is not None:
            owner[v] = by_gid[gid]

    def posed(v, open_):
        i = owner.get(v)
        if i is None:
            return v.co
        return posed_at(plates[i]["track"], v.co, open_)

    names = {i: n for n, i in ids.items()}
    shut = Surface(bm.faces)
    shut_blocked, shut_by_slot, _ = slots_blocked(slots, shut.tree)
    opened_surface = Surface(bm.faces, lambda v: posed(v, 1.0))

    def owner_of(idx):
        f = opened_surface.faces[idx]
        return dominant(f, deform, names) or "?"

    hits = []
    blocked, by_slot, by_owner = slots_blocked(slots, opened_surface.tree, owner_of, hits)
    rays = sum(s.count for s in slots)
    # The swing: each part's moving shell points, step by step, against the fixed shell and the other parts at that step.
    shell = list(bm.faces)
    faces_of = [[] for _ in plates]
    for f in shell:
        i = owner.get(f.verts[0])
        if i is not None and all(owner.get(v) == i for v in f.verts):
            faces_of[i].append(f)
    fixed_surface = Surface([f for f in shell if all(v not in owner for v in f.verts)])
    steps = len(plates[0]["track"]) - 1 if plates else 1
    samples = []
    for i, p in enumerate(plates):
        pts = list({v for f in faces_of[i] for v in f.verts})
        samples.append(pts[::max(1, len(pts) // 300)])
    # Pairs that can meet: their swept points' boxes (over the whole swing) overlap.
    swept = []
    for i, s in enumerate(samples):
        cos = [posed(v, k / steps) for v in s for k in range(0, steps + 1, max(1, steps // 6))]
        swept.append((Vector((min(c.x for c in cos), min(c.y for c in cos), min(c.z for c in cos))) - Vector((0.05,) * 3),
                      Vector((max(c.x for c in cos), max(c.y for c in cos), max(c.z for c in cos))) + Vector((0.05,) * 3))
                     if cos else None)
    pairs = [(i, j) for i in range(len(plates)) for j in range(len(plates)) if i != j and swept[i] and swept[j]
             and all(swept[i][0][a] <= swept[j][1][a] and swept[j][0][a] <= swept[i][1][a] for a in range(3))]
    into_body = {}
    into_part = {}
    # Clearance (reported, not enforced): each part that starts moving later (an inner layer) against each part that started
    # earlier (the outer layer over it), the smallest distance between them over the later part's swing.
    def moved(a, b):
        return any(abs(a[r][c] - b[r][c]) > 1e-5 for r in range(3) for c in range(4))

    start = [next((k for k, m in enumerate(p["track"]) if moved(m, p["track"][0])), len(p["track"])) for p in plates]
    # Layers: a later part with at least LAYER_SHARE of its points within LAYER_NEAR of an earlier part, shut (not two
    # neighbours that only meet at an edge).
    rest_surfaces = {}
    layered = set()
    for i, j in pairs:
        if not start[j] < start[i]:
            continue
        if j not in rest_surfaces:
            rest_surfaces[j] = Surface(faces_of[j])
        near = sum(1 for v in samples[i] if rest_surfaces[j].tree.find_nearest(v.co)[3] < LAYER_NEAR)
        if samples[i] and near >= LAYER_SHARE * len(samples[i]):
            layered.add((i, j))
    gaps = {}
    for k in range(1, steps + 1):
        a, b = (k - 1) / steps, k / steps
        part_surfaces = {j: Surface(faces_of[j], lambda v, b=b: posed(v, b)) for j in {j for _, j in pairs}}
        for i, s in enumerate(samples):
            for v in s:
                p0, p1 = posed(v, a), posed(v, b)
                d = p1 - p0
                length = d.length
                if length < 1e-6:
                    continue
                d /= length
                hit = fixed_surface.through(p0, d, length)
                if hit is not None and not (k == 1 and hit[1] < 0.004):
                    into_body[plates[i]["node"]] = into_body.get(plates[i]["node"], 0) + 1
                for i2, j in pairs:
                    if i2 != i:
                        continue
                    hit = part_surfaces[j].through(p0, d, length)
                    if hit is not None and not (k == 1 and hit[1] < 0.004):
                        key = plates[i]["node"] + " > " + plates[j]["node"]
                        into_part[key] = into_part.get(key, 0) + 1
                    if (i, j) in layered and start[i] <= k:
                        key = plates[i]["node"] + " | " + plates[j]["node"]
                        gap = part_surfaces[j].tree.find_nearest(p1)[3]
                        if gap is not None and gap < gaps.get(key, 1e9):
                            gaps[key] = gap
    result = {"slots": {s.name: {"halfWidth": [round(min(a[2] for a in s.across if a), 3), round(max(a[2] for a in s.across if a), 3)],
                                 "stations": [len([a for a in s.across if a]), len(s.across)],
                                 "rays": s.count, "blockedShut": shut_by_slot.get(s.name, 0),
                                 "blocked": by_slot.get(s.name, 0)} for s in slots},
              "rays": rays, "slotRays": [[round(c, 5) for c in (*o, *d)] for s in slots for o, d in s.rays()], "raysBlocked": blocked, "raysBlockedShut": shut_blocked, "blockedBy": by_owner, "hits": hits,
              "parts": len(plates), "motion": "rigid paths from the cut file", "selection": "owner's cut file",
              "secondLayersLeft": len(layers), "secondLayersAt": [[round(c, 3) for c in p] for p in layers[:50]],
              "hovering": fittings, "sweptIntoBody": into_body, "sweptIntoParts": into_part,
              "layerGaps": {k: round(v, 4) for k, v in sorted(gaps.items(), key=lambda kv: kv[1])}}
    with open(out_path, "w", encoding="utf-8") as fh:
        json.dump(result, fh, indent=1)
    log("entry check: %d of %d pilot-slot rays blocked open %s (%d shut); swing crosses the body %s, other parts %s; %d second-layer "
        "faces left, %d fittings hovering" % (blocked, rays, by_owner, shut_blocked, into_body, into_part, len(layers), len(fittings)))
    problems = []
    # The way in, the swing and the clearance between layers are reported only: the opening is judged by eye (a part may graze
    # another where no one sees it), and the slots are a reference body, not the game's pilot.
    if layers:
        problems.append("%d fixed faces lie as a second layer under an opening part, e.g. at %s" % (
            len(layers), ", ".join("(%.2f %.2f %.2f)" % tuple(p) for p in layers[:3])))
    for fit in fittings:
        problems.append("a fitting at (%.2f %.2f %.2f) is in %s but sits only on %s" % (*fit["at"], fit["in"], ", ".join(fit["sitsOn"])))
    for p in problems:
        log("PROBLEM", p)
    if problems and not preview:
        raise SystemExit("The opening is not clean: " + "; ".join(problems))


def hovering(bm, opening):
    """Small separate pieces (under SMALL across: a bolt, a vent, a cut-off scrap) that sit only on another group's surface
    (within TOUCH): they would hover where that group was, or fly off with a part they do not sit on. A link bar (face layer
    "link") sits on its piece by design and is not one. opening maps face -> group name (absent: fixed). Returns [{at, in,
    sitsOn}]."""
    every = Surface(bm.faces)
    link = bm.faces.layers.int["link"]
    found = []
    for piece in pieces(bm.faces):
        if piece[0][link]:
            continue
        pts = [v.co for f in piece for v in f.verts]
        if max(max(p[i] for p in pts) - min(p[i] for p in pts) for i in range(3)) >= SMALL:
            continue
        own = set(piece)
        mine = opening.get(piece[0], FIXED)
        touched = set()
        for p in pts[::max(1, len(pts) // 60)]:
            for loc, nrm, idx, dist in every.tree.find_nearest_range(p, TOUCH):
                g = every.faces[idx]
                if g not in own:
                    touched.add(opening.get(g, FIXED))
        if touched and mine not in touched:
            centre = sum(pts, Vector()) / len(pts)
            found.append({"at": [round(c, 3) for c in centre], "in": mine, "sitsOn": sorted(touched)})
    return found

# ---------------------------------------------------------------------------------------------------------------- rig
def build_armature(joints, plates):
    data = bpy.data.armatures.new("rig")
    rig = bpy.data.objects.new("rig", data)
    bpy.context.scene.collection.objects.link(rig)
    bpy.context.view_layer.objects.active = rig
    bpy.ops.object.mode_set(mode="EDIT")
    eb = data.edit_bones

    def bone(n, head, tail, parent=None, connect=False):
        b = eb.new(n)
        b.head, b.tail = head, tail
        if parent:
            b.parent = eb[parent]
            b.use_connect = connect
        return b

    j = joints
    bone("root", Vector((0, 0, 0)), Vector((0, -0.3, 0)))
    bone("pelvis", j["pelvis"], j["spine"], "root")
    bone("spine", j["spine"], j["chest"], "pelvis", True)
    bone("chest", j["chest"], j["neck"], "spine", True)
    bone("head", j["neck"], j["headtip"], "chest", True)
    for s in ("L", "R"):
        bone("thigh." + s, j["thigh." + s], j["shin." + s], "pelvis")
        bone("shin." + s, j["shin." + s], j["foot." + s], "thigh." + s, True)
        bone("foot." + s, j["foot." + s], j["toe." + s], "shin." + s, True)
        bone("upperarm." + s, j["upperarm." + s], j["forearm." + s], "chest")
        bone("forearm." + s, j["forearm." + s], j["hand." + s], "upperarm." + s, True)
        bone("hand." + s, j["hand." + s], j["handtip." + s], "forearm." + s, True)
    for pl in plates:
        b = eb.new(pl["node"])
        b.head = pl["centre"]
        b.tail = pl["centre"] + Vector((0, 0, 0.12))
        b.parent = eb[pl["parent"]]
    bpy.ops.object.mode_set(mode="OBJECT")
    return rig


def plate_action(rig, plates, name, key):
    """The clip `name` ("open" from each part's "track", "close" from its "close"): every part's bone along it, one key per
    sample (frames 1..n). A track is the part's rigid move in the suit's space; its bone, at rest under a parent at rest,
    takes that move as B^-1 @ D @ B (B its rest matrix)."""
    action = bpy.data.actions.new(name)
    action.use_fake_user = True
    rig.animation_data.action = action
    for p in plates:
        pb = rig.pose.bones[p["node"]]
        pb.rotation_mode = "XYZ"
        rest = rig.data.bones[p["node"]].matrix_local
        last = Euler((0, 0, 0), "XYZ")
        for k, d in enumerate(p[key]):
            basis = rest.inverted() @ d @ rest
            pb.location = basis.translation
            last = basis.to_euler("XYZ", last)
            pb.rotation_euler = last
            pb.keyframe_insert("location", frame=1 + k)
            pb.keyframe_insert("rotation_euler", frame=1 + k)
    return action


def thickness_modifier(obj, group):
    """The shell's thickness, SHELL_T inward of every open sheet (times the weight of vertex group `group`) with rims along its
    edges (solidify)."""
    mod = obj.modifiers.new("thickness", "SOLIDIFY")
    mod.solidify_mode = "NON_MANIFOLD"
    # Fixed thickness: the constraint mode pushes sharp corners out into long spikes.
    mod.nonmanifold_thickness_mode = "FIXED"
    mod.nonmanifold_boundary_mode = "NONE"
    mod.thickness = SHELL_T
    mod.offset = -1.0
    mod.use_rim = True
    mod.vertex_group = group
    mod.thickness_vertex_group = 0.0
    return mod


def lined_thickness(bm):
    """{vertex: thickness (m)} of the sheets of every object with a lining (face attributes "lined", "lining", "source"): a
    point of its plate grows toward its lining, a point of its lining toward its plate, each at most LINED_SHARE of the gap
    between them there (at least LINED_MIN_T, at most SHELL_T), so the two slabs meet inside and neither shows through the
    other."""
    lined, lining, source = (bm.faces.layers.int[n] for n in ("lined", "lining", "source"))
    by_source = {}
    for f in bm.faces:
        if f[lined]:
            by_source.setdefault(f[source], ([], []))[1 if f[lining] else 0].append(f)
    out = {}
    for own, inside in by_source.values():
        if not own or not inside:
            continue
        for faces, other in ((own, Surface(inside)), (inside, Surface(own))):
            for v in {v for f in faces for v in f.verts}:
                gap = other.tree.find_nearest(v.co)[3]
                t = SHELL_T if gap is None else max(LINED_MIN_T, min(SHELL_T, LINED_SHARE * gap))
                out[v] = min(out.get(v, SHELL_T), t)
    return out


def solidify(body):
    """Gives every open sheet of the shell real thickness (SHELL_T inward; an object with a lining, which faces into the suit,
    lined_thickness: its plate and its lining each a thin slab growing toward the other), closed along every cut and outer
    edge; the rim faces take the trim tile (cavity_walls() then finds every wall that faces the inside). Pieces that are
    already closed solids keep their shape (turned outward if the source had them inside out). Faces end in material 0 the armour
    (the atlas) or 1 the lining's (face attribute "lining", its own side of its slab), which interior.dress takes over."""
    me = body.data
    for p in me.polygons:
        p.material_index = 0
    me.materials.clear()
    me.materials.append(bpy.data.materials.new("body"))
    group = body.vertex_groups.new(name="thickness")
    bm = bmesh.new()
    bm.from_mesh(me)
    deform = bm.verts.layers.deform.verify()
    thin = lined_thickness(bm)
    for v in bm.verts:
        v[deform][group.index] = thin.get(v, SHELL_T) / SHELL_T
    closed, turned = [], 0
    for piece in pieces(bm.faces):
        if all(len(e.link_faces) == 2 for f in piece for e in f.edges):
            vol = sum(f.verts[0].co.dot(f.verts[i].co.cross(f.verts[i + 1].co)) / 6.0
                      for f in piece for i in range(1, len(f.verts) - 1))
            if vol < 0:
                bmesh.ops.reverse_faces(bm, faces=piece)
                turned += 1
            closed.extend(piece)
    bm.to_mesh(me)
    solid = body.copy()
    solid.data = me.copy()
    bpy.context.scene.collection.objects.link(solid)
    bm.faces.index_update()
    own = set(f.index for f in closed)
    keep = bm.copy()
    bmesh.ops.delete(bm, geom=closed, context="FACES")
    bm.to_mesh(me)
    bm.free()
    bmesh.ops.delete(keep, geom=[f for f in keep.faces if f.index not in own], context="FACES")
    keep.to_mesh(solid.data)
    keep.free()
    log("shell: %d closed solid faces kept (%d turned outward), %d sheet faces to thicken (%d vertices of lined objects, %.1f..%.1f mm)" % (
        len(solid.data.polygons), turned, len(me.polygons), len(thin), 1000 * min(thin.values(), default=0), 1000 * max(thin.values(), default=0)))
    for name in ("inner", "rim"):
        me.materials.append(bpy.data.materials.new(name))
    mod = thickness_modifier(body, group.name)
    mod.material_offset = 1
    mod.material_offset_rim = 2
    with bpy.context.temp_override(object=body, active_object=body):
        bpy.ops.object.modifier_apply(modifier=mod.name)
    uvl = me.uv_layers["UVMap"].data
    trim = tile_point("trim")
    cavity = tile_point("cavity")
    counts = {0: 0, 1: 0, 2: 0}
    lining = me.attributes["lining"].data
    final = []
    for p in me.polygons:
        counts[p.material_index] = counts.get(p.material_index, 0) + 1
        if p.material_index == 2:
            for i in p.loop_indices:
                uvl[i].uv = trim
        elif p.material_index == 1 and lining[p.index].value:
            # The lining's back, toward the plate, is the shell's dark inside (its UVs are the lining's grid, not the atlas).
            for i in p.loop_indices:
                uvl[i].uv = cavity
        # The lining's own side (the outer copy of its sheet); its back, toward the plate, and its rims are the shell's.
        final.append(1 if p.material_index == 0 and lining[p.index].value else 0)
    while len(me.materials) > 1:
        me.materials.pop()
    lining_materials = [bpy.data.materials.new("lining"), bpy.data.materials.new("lining-metal"), bpy.data.materials.new("lining-rubber")]
    for m in lining_materials:
        me.materials.append(m)
    me.polygons.foreach_set("material_index", final)
    if len(solid.data.polygons):
        for m in lining_materials:
            solid.data.materials.append(m)
        solid_lining = solid.data.attributes["lining"].data
        for p in solid.data.polygons:
            p.material_index = 1 if solid_lining[p.index].value else 0
        with bpy.context.temp_override(active_object=body, selected_editable_objects=[body, solid], object=body):
            bpy.ops.object.join()
    else:
        bpy.data.objects.remove(solid, do_unlink=True)
    body.vertex_groups.remove(body.vertex_groups["thickness"])
    lining_faces = sum(1 for p in me.polygons if p.material_index)
    # A sheet the source had facing inward comes out as an inside-out slab (its inner wall outside): turn it round, so the
    # outside faces out.
    bm = bmesh.new()
    bm.from_mesh(me)
    inverted = 0
    for piece in pieces(bm.faces):
        vol = sum(f.verts[0].co.dot(f.verts[i].co.cross(f.verts[i + 1].co)) / 6.0
                  for f in piece for i in range(1, len(f.verts) - 1))
        if vol < 0:
            bmesh.ops.reverse_faces(bm, faces=piece)
            inverted += 1
    bm.to_mesh(me)
    bm.free()
    log("shell: %d inside-out slabs turned round" % inverted)
    log("shell thickness %.3f m: outer %d, inner %d, rim %d faces; lining %d faces" % (
        SHELL_T, counts[0], counts[1], counts[2], lining_faces))


def cavity_walls(bm):
    """Every face that looks into the suit's inside takes the cavity tile (interior.dress turns it into the wall), so an opened part shows a clean hollow
    whose edges are the shell's own rims, never armor texture or the source's undersuit seen from within. A face looks
    inside when, in the shut suit, nothing outside can see it: of HEMISPHERE rays spread over the side it faces, at most
    one gets out. A groove or the gap between the legs sees out; the inside of the shell does not. Judged per face, so a
    source sheet whose winding changes part way is handled too. A face one can see from outside stays armour (interior.exposed_faces:
    a sliver in a crease fails the rays above from its middle, yet shows), and so does a small patch of inner faces lying
    among such faces (ISLAND). Returns how many faces it took."""
    uv = bm.loops.layers.uv["UVMap"]
    link = bm.faces.layers.int["link"]
    cavity = tile_point("cavity")
    tree = BVHTree.FromBMesh(bm)
    # Directions over a hemisphere about +Z (a golden-angle spiral, from the pole down to 80 degrees off it).
    dirs = []
    for k in range(HEMISPHERE):
        z = 1 - (k + 0.5) / HEMISPHERE * (1 - math.cos(math.radians(80)))
        r = math.sqrt(max(0.0, 1 - z * z))
        a = k * 2.399963
        dirs.append(Vector((r * math.cos(a), r * math.sin(a), z)))
    dark = set()
    bm.faces.ensure_lookup_table()
    seen_outside = interior.exposed_faces(bm)

    def in_atlas(f):
        return all(0.0 <= c <= 1.0 for l in f.loops for c in l[uv].uv)

    for f in bm.faces:
        n = f.normal
        if n.length < 0.5 or f[link] or f.material_index != 0 or (f.index in seen_outside and in_atlas(f)):
            continue     # a link keeps its piece's colour, a lining is wall already
        t = n.orthogonal().normalized()
        b = n.cross(t)
        c = f.calc_center_median() + n * 0.001
        out = 0
        for d in dirs:
            if tree.ray_cast(c, t * d.x + b * d.y + n * d.z, 3.0)[2] is None:
                out += 1
                if out > 1:
                    break
        if out <= 1:
            dark.add(f)
    kept = 0
    for piece in pieces(dark):
        own = set(piece)
        border = outside = 0.0
        for f in piece:
            for e in f.edges:
                for g in e.link_faces:
                    if g not in own:
                        border += e.calc_length()
                        if g.index in seen_outside:
                            outside += e.calc_length()
        if sum(f.calc_area() for f in piece) < ISLAND and outside >= 0.6 * border and all(in_atlas(f) for f in piece):
            dark -= own
            kept += len(piece)
    for f in dark:
        for l in f.loops:
            l[uv].uv = cavity
    log("seen from outside: %d faces stay armour, %d more in small patches among them" % (len(seen_outside), kept))
    return len(dark)


def add_visor_pane(bm, deform, head, model):
    """Closes a helmet's open visor hole with a pane of glass (glow.VISOR_PANES: its outline and light): a closed slab 2 mm thick on the head's bone, on a
    curved surface fitted to the helmet's own front around the hole, 3 mm behind it, mapped flat on the atlas's glass tile."""
    spec = glow.VISOR_PANES.get(model)
    if not spec:
        return
    outline = spec["outline"]
    tree = BVHTree.FromBMesh(bm)
    cx, cz = sum(p[0] for p in outline) / len(outline), sum(p[1] for p in outline) / len(outline)
    samples = []
    for k, (x0, z0) in enumerate(outline):
        x1, z1 = outline[(k + 1) % len(outline)]
        for t in np.linspace(0.0, 1.0, 8, endpoint=False):
            x, z = x0 + (x1 - x0) * t, z0 + (z1 - z0) * t
            x, z = cx + (x - cx) * 1.05, cz + (z - cz) * 1.05
            hit = tree.ray_cast(Vector((x, -1.0, z)), Vector((0, 1, 0)), 0.9)
            if hit[0] is not None:
                samples.append((x, z, hit[0].y))
    a = np.asarray(samples, dtype=float)
    A = np.stack([np.ones(len(a)), a[:, 0] ** 2, a[:, 1] - cz, (a[:, 1] - cz) ** 2], axis=1)
    coef, *_ = np.linalg.lstsq(A, a[:, 2], rcond=None)

    def surface(x, z, back):
        return Vector((x, float(coef @ np.array([1.0, x * x, z - cz, (z - cz) ** 2])) + 0.003 + back, z))

    uv = bm.loops.layers.uv["UVMap"]
    u0, u1, v0, v1 = glow.PANE_TILE
    xs, zs = [p[0] for p in outline], [p[1] for p in outline]
    wide, low, high = max(abs(x) for x in xs), min(zs), max(zs)

    def uv_of(p):
        return (u0 + (u1 - u0) * (0.5 + p.x / (2 * wide)), v0 + (v1 - v0) * (p.z - low) / (high - low))

    rings = 6
    grid = []    # rings from the middle out; ring 0 one point
    for back in (0.0, 0.002):
        layer = [[surface(cx, cz, back)]]
        for r in range(1, rings + 1):
            ring = []
            for k, (x0, z0) in enumerate(outline):
                x1, z1 = outline[(k + 1) % len(outline)]
                for t in np.linspace(0.0, 1.0, 4, endpoint=False):
                    px, pz = x0 + (x1 - x0) * t, z0 + (z1 - z0) * t
                    ring.append(surface(cx + (px - cx) * r / rings, cz + (pz - cz) * r / rings, back))
            layer.append(ring)
        grid.append([[bm.verts.new(p) for p in ring] for ring in layer])
    for layer in grid:
        for ring in layer:
            for v in ring:
                v[deform][head] = 1.0

    def face(vs):
        f = bm.faces.new(vs)
        f.smooth = True
        f.material_index = 0
        for l in f.loops:
            l[uv].uv = uv_of(l.vert.co)

    for side, layer in enumerate(grid):
        turn = side == 1
        for r in range(rings):
            inner, outer = layer[r], layer[r + 1]
            n = len(outer)
            for k in range(n):
                if r == 0:
                    q = [inner[0], outer[(k + 1) % n], outer[k]]
                else:
                    q = [inner[k], inner[(k + 1) % n], outer[(k + 1) % n], outer[k]]
                face(q[::-1] if turn else q)
    front, rear = grid[0][-1], grid[1][-1]
    for k in range(len(front)):
        face([front[k], front[(k + 1) % len(front)], rear[(k + 1) % len(front)], rear[k]])
    bm.normal_update()
    # Outward: the front must face the viewer (-Y).
    if grid[0][1][0].link_faces and grid[0][1][0].link_faces[0].normal.y > 0:
        all_faces = list({f for layer in grid for ring in layer for v in ring for f in v.link_faces})
        bmesh.ops.reverse_faces(bm, faces=all_faces)
    bmesh.ops.triangulate(bm, faces=list({f for layer in grid for ring in layer for v in ring for f in v.link_faces if len(f.verts) > 3}))
    log("visor pane: %d samples of the helmet's front, fit %.1f mm" % (len(a), 1000 * float(np.abs(A @ coef - a[:, 2]).mean())))


def check_closed(bm):
    """Every edge of the shell has exactly two faces running opposite ways (closed, consistently oriented), and every
    closed piece encloses positive volume (outward normals)."""
    shell = list(bm.faces)
    own = set(shell)
    open_edges = flipped = 0
    for e in {e for f in shell for e in f.edges}:
        loops = [l for l in e.link_loops if l.face in own]
        if len(loops) != 2:
            open_edges += 1
            if open_edges <= 5:
                log("open edge: %d shell faces, %d faces, at (%.3f %.3f %.3f), length %.2e" % (
                    len(loops), len(e.link_faces), *((e.verts[0].co + e.verts[1].co) / 2), e.calc_length()))
        elif loops[0].vert == loops[1].vert:
            flipped += 1
    inward = 0
    for piece in pieces(shell):
        vol = 0.0
        for f in piece:
            vs = [v.co for v in f.verts]
            for i in range(1, len(vs) - 1):
                vol += vs[0].dot(vs[i].cross(vs[i + 1])) / 6.0
        if vol <= 0:
            inward += 1
            c = sum((f.calc_center_median() for f in piece), Vector()) / len(piece)
            log("inward piece: %d faces, volume %.2e, centre (%.2f %.2f %.2f), area %.4f" % (
                len(piece), vol, *c, sum(f.calc_area() for f in piece)))
    tiny = sum(1 for f in bm.faces if f.calc_area() < TINY_AREA)
    log("closed check: %d shell faces, %d open edges, %d reversed edges, %d inward pieces, %d tiny triangles" % (
        len(shell), open_edges, flipped, inward, tiny))
    if open_edges or flipped or inward or tiny:
        raise SystemExit("Shell is not closed and outward: %d open edges, %d reversed edges, %d inward pieces, %d tiny triangles"
                         % (open_edges, flipped, inward, tiny))


# ---------------------------------------------------------------------------------------------------------------- main
def main():
    args = arguments()
    try:
        body, plates, joints, tris0, opening = read_cut(args.model)
        shape(args, body, plates, joints, tris0, opening)
    except SystemExit as stop:
        # One line the preview command shows the owner as it is.
        if isinstance(stop.code, str):
            log("STOP", stop.code)
        raise


def baked_tracks(parts, names):
    """Each part's move from the fh_motion bake.py gave it, expanded by the add-on's motion.sequence (the one implementation of
    the stroke): {object: {"track", "close", "events", "link"}} and the open clip's sample count. Every object of a group
    carries the same move."""
    specs = {}
    for o, index in parts:
        spec = o.get("fh_motion")
        if not spec:
            raise SystemExit("「%s」没有开合插件烘焙的运动（fh_motion）：构建只读 hinge_tool/bake.py 写出的文件" % o.name)
        if index in specs and specs[index] != spec:
            raise SystemExit("「%s」组里的物体带了不同的运动：同一组的物体要一起移动" % names[index])
        specs[index] = spec
    order = sorted(specs)
    index_of = {names[i]: k for k, i in enumerate(order)}
    plan = []
    for i in order:
        m = json.loads(specs[i])
        if "link" in m:
            if m["driver"] not in index_of:
                raise SystemExit("连杆「%s」带它的部件「%s」不在文件里" % (names[i], m["driver"]))
            plan.append({"link": m["link"], "driver": index_of[m["driver"]]})
        else:
            parent = m.get("parent")
            if parent and parent not in index_of:
                raise SystemExit("「%s」挂在「%s」上，但文件里没有这个部件" % (names[i], parent))
            plan.append({"path": Path(m["path"]), "frames": tuple(m["frames"]), "parent": index_of.get(parent) if parent else None})
    opened, closed, events = sequence(plan)
    by_index = {i: k for k, i in enumerate(order)}
    moved = {}
    for o, index in parts:
        k = by_index[index]
        moved[o] = {"track": opened[k], "close": closed[k], "events": events[k], "link": "link" in plan[k]}
    return moved, len(opened[0])


def orient_lining(me, slots):
    """Turns each loose sheet of a mesh's lining (faces in material `slots`, sharing no edge with the plate's own faces) so it
    faces away from the plate, into the suit, like a lining joined to its plate by a rim does: the thickening then grows each
    sheet toward the other and the lining's face is the inside one sees. Returns how many sheets were turned."""
    bm = bmesh.new()
    bm.from_mesh(me)
    bm.normal_update()
    lining = {f for f in bm.faces if f.material_index in slots}
    own = [f for f in bm.faces if f.material_index not in slots]
    turned = 0
    if lining and own:
        plate = Surface(own)
        left = set(lining)
        while left:
            seed = left.pop()
            stack, sheet, joined = [seed], [seed], False
            while stack:
                f = stack.pop()
                for e in f.edges:
                    for g in e.link_faces:
                        if g not in lining:
                            joined = True
                        elif g in left:
                            left.remove(g)
                            stack.append(g)
                            sheet.append(g)
            if joined:
                continue
            facing = 0.0
            for f in sheet:
                loc, nrm, idx, dist = plate.tree.find_nearest(f.calc_center_median())
                if idx is not None:
                    facing += f.normal.dot(plate.faces[idx].normal) * f.calc_area()
            if facing > 0:
                bmesh.ops.reverse_faces(bm, faces=sheet)
                turned += 1
    bm.to_mesh(me)
    bm.free()
    return turned


def clear_lining(me, slots):
    """Moves every point of a lining closer than LINING_CLEAR to its object's plate further into the suit (along the point's
    normal: orient_lining turned the lining to face in), so its slab can never cross the plate's and show on the outside. Points shared
    with the plate stay. Returns how many moved."""
    bm = bmesh.new()
    bm.from_mesh(me)
    bm.normal_update()
    own = [f for f in bm.faces if f.material_index not in slots]
    moved = 0
    if own:
        plate = Surface(own)
        for v in bm.verts:
            if not v.link_faces or any(f.material_index not in slots for f in v.link_faces):
                continue
            gap = plate.tree.find_nearest(v.co)[3]
            if gap is not None and gap < LINING_CLEAR:
                v.co += v.normal * (LINING_CLEAR - gap)
                moved += 1
    bm.to_mesh(me)
    bm.free()
    return moved


def tidy_lining(me, slots):
    """Makes a lined object's mesh something the thin slabs of its plate and lining can close (the add-on's lining work
    leaves slivers far below anything visible): points of its lining closer than WELD become one, loose bits of lining
    smaller than LINING_CRUMB go, and an edge shared by more than two faces (sheets meeting in a fan, as where a lining's
    rim folds onto its plate) is split so each sheet keeps its own border. Returns (points merged, crumbs removed, edges
    split)."""
    bm = bmesh.new()
    bm.from_mesh(me)
    before = len(bm.verts)
    bmesh.ops.remove_doubles(bm, verts=list({v for f in bm.faces if f.material_index in slots for v in f.verts}), dist=WELD)
    welded = before - len(bm.verts)
    crumbs = [piece for piece in pieces(bm.faces)
              if all(f.material_index in slots for f in piece) and sum(f.calc_area() for f in piece) < LINING_CRUMB]
    if crumbs:
        bmesh.ops.delete(bm, geom=[f for piece in crumbs for f in piece], context="FACES")
    fans = [e for e in bm.edges if len(e.link_faces) > 2]
    if fans:
        bmesh.ops.split_edges(bm, edges=fans)
    bm.to_mesh(me)
    bm.free()
    return welded, len(crumbs), len(fans)


def read_cut(model):
    """The baked cut file to one body mesh at the shut pose and each part's track: its rigid move from shut, sampled over the
    opening. Face attributes: "group" (0 fixed, else the part's place in name order), "source" (the object it came from),
    "link" (a link bar the add-on made: not armour, it keeps its colour inside and may sit on its piece), "lining" (a face of a
    lining) and "lined" (a face of an object with a lining). Every mesh object must lie in FIXED or one part's collection, and
    every part carry the move bake.py gave it."""
    scene = bpy.context.scene
    meta = json.loads(scene.get("frame", "{}"))
    if meta.get("model") != model:
        raise SystemExit("The open file is not %s's cut file (scene property 'frame' says %r)" % (model, meta.get("model")))
    if bpy.data.collections.get(FIXED) is None:
        raise SystemExit("The cut file has no collection %s" % FIXED)
    # Every other collection holding meshes is one part (bake.py names each after its first piece), in name order.
    names = [FIXED] + sorted(c.name for c in bpy.data.collections if c.name != FIXED and any(o.type == "MESH" for o in c.objects))
    meshes = []
    for index, name in enumerate(names):
        meshes += [(o, index) for o in bpy.data.collections[name].objects if o.type == "MESH" and len(o.data.polygons)]
    seen = set()
    for o, index in meshes:
        if o in seen:
            raise SystemExit("「%s」同时在几个组里：一个物体只能放进一个组" % o.name)
        seen.add(o)
    stray = [o.name for o in bpy.data.objects if o.type == "MESH" and len(o.data.polygons) and o not in seen]
    if stray:
        raise SystemExit("这些物体不在任何组里：%s（放进「%s」或某个部件的组）" % ("、".join(stray), FIXED))
    if not meshes:
        raise SystemExit("切分文件的「%s」和部件组里都没有外壳" % FIXED)
    parts = [(o, index) for o, index in meshes if index]
    moved, samples = baked_tracks(parts, names)
    # Every object onto the suit's space at its shut place (where bake.py stands it), then one body.
    scene.frame_set(1)
    turned = welded = crumbs = split = cleared = 0
    for k, (o, index) in enumerate(meshes):
        m = o.matrix_world.copy()
        if o.data.users > 1:
            o.data = o.data.copy()
        me = o.data
        o.animation_data_clear()
        o.parent = None
        o.matrix_world = Matrix.Identity(4)
        me.transform(m)
        if m.determinant() < 0:
            me.flip_normals()
        slots = lining_slots(o)
        if any(p.material_index in slots for p in me.polygons):
            w, c, s = tidy_lining(me, slots)
            welded += w
            crumbs += c
            split += s
        lining = [int(p.material_index in slots) for p in me.polygons]
        if any(lining):
            turned += orient_lining(me, slots)
            cleared += clear_lining(me, slots)
        for name, values in (("group", [index] * len(lining)), ("source", [k] * len(lining)),
                             ("link", [int(o in moved and moved[o]["link"])] * len(lining)), ("lining", lining),
                             ("lined", [int(any(lining))] * len(lining))):
            attr = me.attributes.get(name) or me.attributes.new(name, "INT", "FACE")
            attr.data.foreach_set("value", values)
        me.update()
    # Each part's body region and side, from where it sits shut: the bone it hangs on.
    joints = {k: Vector(v) for k, v in meta["joints"].items()}

    def points(o):
        co = np.empty(len(o.data.vertices) * 3)
        o.data.vertices.foreach_get("co", co)
        return co.reshape(-1, 3)

    where = {index: part_region(joints, np.concatenate([points(o) for o, i in parts if i == index]))
             for index in {i for _, i in parts}}
    plates = []
    counts = {}
    for index, name in enumerate(names):
        objs = [o for o, i in parts if i == index]
        if not objs:
            continue
        region, side = where[index]
        counts[region, side] = counts.get((region, side), 0) + 1
        # The owner's ruling on its hinge in the add-on ("body" allowed): it may end nearer the body than it starts.
        into_body = any("body" in json.loads(o["fh_motion"]).get("allow", []) for o in objs)
        plates.append({"group": index, "name": name, "node": "plate.%s.%s.%d" % (region, side, counts[region, side]),
                       "region": region, "side": side, "parent": "chest" if region == "back" else region + "." + side,
                       "intoBody": into_body, **moved[objs[0]]})
    for o in list(bpy.data.objects):
        if o not in seen:
            bpy.data.objects.remove(o, do_unlink=True)
    objs = [o for o, _ in meshes]
    body = objs[0]
    with bpy.context.temp_override(active_object=body, object=body, selected_editable_objects=objs):
        bpy.ops.object.join()
    body.name = body.data.name = "body"
    for coll in list(body.users_collection):
        coll.objects.unlink(body)
    scene.collection.objects.link(body)
    for coll in list(bpy.data.collections):
        bpy.data.collections.remove(coll)
    opening = {"samples": samples}
    log("cut file: %d mesh objects, %d samples, %d loose lining sheets turned into the suit, %d lining points moved clear of their plate, lined objects: %d points closer than %.2f mm "
        "merged, %d lining slivers removed, %d edges with more than two faces split; %d parts: %s" % (
            len(objs), samples, turned, cleared, welded, 1000 * WELD, crumbs, split, len(plates), ", ".join(p["name"] for p in plates)))
    return body, plates, joints, meta["tris0"], opening


def save_png(path, px, colour, alpha):
    h, w = px.shape[:2]
    img = bpy.data.images.new(os.path.basename(path), w, h, alpha=alpha)
    img.colorspace_settings.name = "sRGB" if colour else "Non-Color"
    img.pixels.foreach_set(np.ascontiguousarray(px, dtype=np.float32).reshape(-1))
    img.filepath_raw = path
    img.file_format = "PNG"
    img.save()
    bpy.data.images.remove(img)


def shape(args, body, plates, joints, tris0, opening_info):
    """The joined cut to the rigged suit: parts, thickness, checks, rig, clips and export."""
    cfg = MODELS[args.model]
    H = cfg["height"]
    os.makedirs(args.out, exist_ok=True)
    # The textures make_cut_file.py baked, where the file's armour material reads them (beside the owner's cut file); the light
    # strips are painted from these into the copies (glow.paint).
    sources = {}
    for kind in TEXTURES:
        name = "%s_%s.png" % (args.model, kind)
        img = next((i for i in bpy.data.images if os.path.basename(bpy.path.abspath(i.filepath)) == name), None)
        if img is None or not os.path.isfile(bpy.path.abspath(img.filepath)):
            raise SystemExit("The cut file's armour texture %s is missing" % name)
        source = os.path.abspath(bpy.path.abspath(img.filepath))
        sources[kind] = source
        if source !=os.path.abspath(os.path.join(args.out, name)):
            shutil.copyfile(source, os.path.join(args.out, name))
        img.filepath = os.path.join(args.out, name)
    interior.write_textures(args.model, args.out, save_png)
    me = body.data
    j = joints
    bm0 = bmesh.new()
    bm0.from_mesh(me)
    bvh = BVHTree.FromBMesh(bm0)

    def mid_y(z):
        f = bvh.ray_cast(Vector((0, -3, z)), Vector((0, 1, 0)), 6)
        b = bvh.ray_cast(Vector((0, 3, z)), Vector((0, -1, 0)), 6)
        return (f[0].y + b[0].y) / 2 if f[0] and b[0] else 0.0

    z0 = min(j["thigh.L"].z, j["thigh.R"].z) - 0.05 * H
    z1 = j["neck"].z - 0.025 * H
    torso = Segment(Vector((0, mid_y(j["pelvis"].z), z0)), Vector((0, mid_y(j["chest"].z), z1)), Vector((1, 0, 0)))
    bm0.free()
    ids = {g.name: g.index for g in body.vertex_groups}
    bm = bmesh.new()
    bm.from_mesh(me)
    deform = bm.verts.layers.deform.verify()
    names = {v: k for k, v in ids.items()}
    segments = {}
    for side, sign in (("L", 1.0), ("R", -1.0)):
        lat = Vector((sign, 0, 0))
        for bone, end in (("upperarm", "forearm"), ("forearm", "hand"), ("thigh", "shin"), ("shin", "foot")):
            segments[bone + "." + side] = Segment(j[bone + "." + side], j[end + "." + side], lat)
    slots = pilot_slots(torso, segments)
    shut = Surface(bm.faces)
    for slot in slots:
        slot.fit(shut.tree)

    # The owner's groups: each part its own piece (sharing no edge or corner with anything else), weighted to its own bone.
    group = bm.faces.layers.int["group"]
    by_index = {p["group"]: p for p in plates}
    opening = {f: by_index[f[group]]["name"] for f in bm.faces if f[group] in by_index}
    source = bm.faces.layers.int["source"]
    layers = second_layers(bm, opening, source)
    fittings = hovering(bm, opening)
    for p in plates:
        faces = [f for f in bm.faces if f[group] == p["group"]]
        pts = [v.co.copy() for f in faces for v in f.verts]
        p["faces"] = len(faces)
        p["size"] = [max(q[i] for q in pts) - min(q[i] for q in pts) for i in range(3)]
        p["centre"] = sum(pts, Vector()) / max(1, len(pts))
        ret = bmesh.ops.split(bm, geom=faces, use_only_faces=False)
        faces = [g for g in ret["geom"] if isinstance(g, bmesh.types.BMFace)]
        if p["node"] not in ids:
            ids[p["node"]] = body.vertex_groups.new(name=p["node"]).index
            names[ids[p["node"]]] = p["node"]
        gid = ids[p["node"]]
        for v in {v for f in faces for v in f.verts}:
            for k in list(v[deform].keys()):
                del v[deform][k]
            v[deform][gid] = 1.0
        axis, centre, angle, along = screw_parts(p["track"][-1])
        p["screw"] = {"axis": [round(c, 4) for c in axis], "through": [round(c, 4) for c in centre],
                      "degrees": round(math.degrees(angle), 1), "slide": round(along, 4)}
        log("part %s (%s): %d faces, size %.2fx%.2fx%.2f, open: turned %.0f deg about (%.2f %.2f %.2f) through (%.2f %.2f %.2f), "
            "slid %.3f m" % (p["node"], p["name"], p["faces"], *p["size"], math.degrees(angle), *axis, *centre, along))
    bm.faces.layers.int.remove(group)
    bm.to_mesh(me)
    bm.free()
    if not plates:
        log("PROBLEM nothing opens: every group but %s is empty" % FIXED)
        if not args.preview:
            raise SystemExit("Nothing opens: the cut file's parts are all empty.")

    solidify(body)

    bm = bmesh.new()
    bm.from_mesh(me)
    deform = bm.verts.layers.deform.verify()
    log("faces looking into the suit (dark cavity): %d" % cavity_walls(bm))
    for name in ("link", "lining", "lined", "source"):
        bm.faces.layers.int.remove(bm.faces.layers.int[name])

    # Ship triangles, not polygons: the FBX importer triangulates large or concave polygons on its own and can drop them, which
    # opens the shell. The checks below then see exactly the triangles the bundle gets. Quads are split one by one along their
    # shorter diagonal, or the other one when that pair of corners is already joined (where a lining's rim folds round its
    # plate, two quads can share a diagonal; splitting both along it would put four faces on one edge).
    def joined(u, v):
        return any(e.other_vert(u) is v for e in u.link_edges)

    for f in [f for f in bm.faces if len(f.verts) == 4]:
        a, b, c, d = f.verts
        pairs = sorted(((a, c), (b, d)), key=lambda q: (q[0].co - q[1].co).length)
        pair = next((q for q in pairs if not joined(*q)), None)
        if pair is not None:
            bmesh.utils.face_split(f, *pair)
    bmesh.ops.triangulate(bm, faces=[f for f in bm.faces if len(f.verts) > 3], quad_method="BEAUTY", ngon_method="BEAUTY")
    # Unity drops triangles under about 1e-7 m² on import, which would open the shell there: collapse each tiny triangle's
    # shortest safe edge instead (the neighbours close over it). An edge is safe when its two ends share exactly the two
    # opposite corners (the link condition), so the collapse can't pinch a thin slab into a non-manifold seam.
    def safe(e):
        a, b = e.verts
        na = {x for x in (l.other_vert(a) for l in a.link_edges)}
        nb = {x for x in (l.other_vert(b) for l in b.link_edges)}
        return len(e.link_faces) == 2 and len(na & nb) == 2

    for _ in range(20):
        used, edges = set(), []
        for f in bm.faces:
            if f.calc_area() < TINY_AREA:
                for e in sorted(f.edges, key=lambda e: e.calc_length()):
                    near = {v for x in e.verts for ff in x.link_faces for v in ff.verts}
                    if safe(e) and not (used & near):
                        used |= near
                        edges.append(e)
                        break
        if not edges:
            break
        bmesh.ops.collapse(bm, edges=edges, uvs=True)
    # A tiny triangle no collapse can take is a needle (its corners nearly in a line, as on a thin slab's rim): turn its
    # longest edge within the quad it makes with its neighbour, kept when the smaller of the two triangles grows; a few
    # rounds walk a needle along a rim until it is gone.
    uv_layer = bm.loops.layers.uv["UVMap"]
    cavity_uv = tile_point("cavity")
    for _ in range(4):
        for f in [f for f in bm.faces if f.calc_area() < TINY_AREA]:
            if not f.is_valid:
                continue
            e = max(f.edges, key=lambda e: e.calc_length())
            if len(e.link_faces) != 2:
                continue
            was = min(x.calc_area() for x in e.link_faces)
            turned_edge = bmesh.utils.edge_rotate(e, True)
            if turned_edge is None:
                continue
            if min(x.calc_area() for x in turned_edge.link_faces) <= was:
                bmesh.utils.edge_rotate(turned_edge, False)
                continue
            # Turned between the armour and a lining, an armour triangle can take a corner of the lining's grid UVs: it lies
            # at the lining's edge, so it takes the shell's dark inside.
            for x in turned_edge.link_faces:
                if x.material_index == 0 and any(not (0.0 <= c <= 1.0) for l in x.loops for c in l[uv_layer].uv):
                    for l in x.loops:
                        l[uv_layer].uv = cavity_uv
        tidy_faces = [f for f in bm.faces if len(f.verts) > 3]
        if tidy_faces:
            bmesh.ops.triangulate(bm, faces=tidy_faces)
    # A needle nothing above could take: its far corner moves off its longest edge, out of the neighbour's plane, by the little it takes.
    for _ in range(3):
        for f in [f for f in bm.faces if f.calc_area() < TINY_AREA]:
            e = max(f.edges, key=lambda e: e.calc_length())
            apex = next(v for v in f.verts if v not in e.verts)
            other = next((g for g in e.link_faces if g is not f), None)
            if other is not None and e.calc_length() > 1e-6:
                apex.co += other.normal * (3.0 * TINY_AREA / e.calc_length())
    # A paper-thin closed piece (a doubled decal in the source) has almost no volume, and a collapse can tip its sign:
    # turn such a piece round as the thickening step does.
    for piece in pieces(bm.faces):
        vol = sum(f.verts[0].co.dot(f.verts[1].co.cross(f.verts[2].co)) / 6.0 for f in piece)
        if vol < 0:
            bmesh.ops.reverse_faces(bm, faces=piece)
            log("tiny-triangle collapse: turned a %d-face piece round (volume %.1e)" % (len(piece), vol))
    bmesh.ops.delete(bm, geom=[v for v in bm.verts if not v.link_faces], context="VERTS")
    add_visor_pane(bm, deform, ids["head"], args.model)
    check_closed(bm)
    check_entry(bm, deform, ids, plates, slots, os.path.join(args.out, args.model + "_entry.json"), layers, fittings, args.preview)
    bm.normal_update()
    glow.paint(bm, {i: n for n, i in ids.items()}, joints, sources,
               {k: os.path.join(args.out, "%s_%s.png" % (args.model, k)) for k in TEXTURES}, log, args.model)
    interior.dress(bm, deform, joints, torso.a, torso.b, [ids[p["node"]] for p in plates], args.model, log)
    check_closed(bm)
    bm.to_mesh(me)
    bm.free()
    me.update()
    # Wear and rust on the finished shell's armour maps.
    wear.paint(body, args.model, args.out, log)
    tris = sum(len(p.vertices) - 2 for p in me.polygons)
    lining_tris = [sum(len(p.vertices) - 2 for p in me.polygons if p.material_index == m) for m in (1, 2, 3)]
    if not all(lining_tris):
        raise SystemExit("The inside has no fabric, metal or rubber faces (triangles %s)" % lining_tris)
    uvs = me.uv_layers["UVMap"].data
    outside = [p.index for p in me.polygons if p.material_index == 0
               and any(not (0.0 <= c <= 1.0) for i in p.loop_indices for c in uvs[i].uv)]
    if outside:
        raise SystemExit("%d armour faces have UVs outside the atlas (the bundle build refuses them), e.g. at (%.3f %.3f %.3f)" % (
            len(outside), *me.polygons[outside[0]].center))
    log("triangles", tris, "lining fabric/metal/rubber", lining_tris)
    # Second UV set for the bundle's closed-shell check: (vertex index, 0). The bundle drops it after the check.
    idl = me.uv_layers.new(name="ids")
    for p in me.polygons:
        for i in p.loop_indices:
            idl.data[i].uv = (float(me.loops[i].vertex_index), 0.0)
    me.uv_layers.active = me.uv_layers["UVMap"]
    me.uv_layers["UVMap"].active_render = True

    rig = build_armature(joints, plates)
    body.parent = rig
    mod = body.modifiers.new("rig", "ARMATURE")
    mod.object = rig
    unw = sum(1 for v in me.vertices if not any(g.weight > 1e-4 for g in v.groups))
    if unw:
        raise SystemExit("%d vertices unweighted" % unw)
    bpy.context.scene.render.fps = FPS
    rig.animation_data_create()
    actions = {}
    if plates:
        actions["open"] = plate_action(rig, plates, "open", "track")
        actions["close"] = plate_action(rig, plates, "close", "close")
    rig.animation_data.action = None
    # The FBX carries every action in the file (one take each, every bone baked over it): only the suit's own clips stay,
    # never the actions the cut file brought along (the add-on's previews ran to thousands).
    kept = set(actions.values())
    stale = [a for a in bpy.data.actions if a not in kept]
    for a in stale:
        bpy.data.actions.remove(a)
    if stale:
        log("dropped %d actions the cut file brought along; the FBX takes are %s" % (len(stale), ", ".join(sorted(actions))))
    for name, action in actions.items():
        track = rig.animation_data.nla_tracks.new()
        track.name = name
        strip = track.strips.new(name, int(action.frame_range[0]), action)
        strip.name = name
        track.mute = True
    base = os.path.join(args.out, args.model + "_rigged")
    bpy.ops.wm.save_as_mainfile(filepath=base + ".blend", copy=True)
    bpy.ops.object.select_all(action="SELECT")
    bpy.ops.export_scene.fbx(filepath=base + ".fbx", use_selection=False, object_types={"ARMATURE", "MESH"},
                             apply_unit_scale=True, apply_scale_options="FBX_SCALE_UNITS", bake_space_transform=True,
                             axis_forward="-Z", axis_up="Y", add_leaf_bones=False, bake_anim=True,
                             bake_anim_use_all_actions=True, bake_anim_use_nla_strips=False, bake_anim_force_startend_keying=True,
                             bake_anim_simplify_factor=0.0, embed_textures=False, path_mode="STRIP")
    record = {"model": args.model, "name": cfg["name"], "height": H, "fps": FPS, "sourceTriangles": tris0, "triangles": tris,
              "shellThickness": SHELL_T, "liningTriangles": lining_tris,
              "clips": {"open": [1, opening_info["samples"]],
                        "close": [1, len(plates[0]["close"]) if plates else 1]},
              "opening": opening_info,
              "joints": {k: [round(c, 4) for c in v] for k, v in joints.items()},
              "torso": {"a": [round(c, 4) for c in torso.a], "b": [round(c, 4) for c in torso.b]},
              "plates": [{"node": p["node"], "name": p["name"], "parent": p["parent"], "open": p["screw"], "intoBody": p["intoBody"],
                          "events": {k: round(v, 3) for k, v in p["events"].items()},
                          "faces": p["faces"], "size": [round(c, 3) for c in p["size"]],
                          "centre": [round(c, 3) for c in p["centre"]]} for p in plates]}
    with open(os.path.join(args.out, args.model + "_rig.json"), "w", encoding="utf-8") as f:
        json.dump(record, f, indent=1)
    log("DONE", args.model, "triangles", tris, "plates", len(plates))


if __name__ == "__main__":
    main()
