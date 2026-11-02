"""The ways a piece opens (presets): each makes the piece's path (motion.py segments) from its shape, and for the parallel-link
swing and the scissor the links that carry it. One preset may run after another (`then`). Distances in metres, angles in
degrees, directions a word or a world (x, y, z):
  up, down, back (+Y, the suit's back), front, left (+X, the suit's left), right, out (the piece's outward normal), in,
  limb / limb- (along the body part it sits on, toward its far end / its joint), hinge / hinge- (along its hinge line),
  neck (toward the suit's middle and up), outside (away from the suit's middle).
A direction word slides along the piece's surface (the word's direction less the normal; api.move records the rail it got
as "rail"), except out and in; a vector (x, y, z) is followed exactly.

  hinge(deg)                        turn on its hinge line (the cut's default)
  slide(direction, distance, go, depth)   telescoping: go="over" lifts `depth` out and passes OVER the plate ahead (on
                                    top), go="under" sinks `depth` in and passes UNDER it (hidden), "flat" stays on the
                                    surface (the older under=1 / -1 / 0 mean the same)
  offset(direction, distance, lift)  lift (or press, lift < 0) off the surface first, then slide
  stepped(direction, distance, rise, steps)    a staircase: slide, rise, slide, ...
  plug(push, edge, deg | direction, distance)  push straight out, then turn about an edge (or slide)
  twist(deg, size=15)                turn about the limb's axis to unlatch, wait, then `then`; without `deg` (signed) it is
                                    `size` degrees the way `then` opens the piece outward
  lift_turn(deg, lift=0.02)          lift straight out from the limb's axis, then turn `deg` (signed) round the limb's axis
  flare(lift | deg)                  lift a little and drop back (a ready move before the rest), then `then`
  zlink(direction, deg, length)      swing out on two parallel links, staying level (a Z or folding path)
  scissor(direction, distance)       straight out on two crossed-link scissors
  scissor_door(deg, pivot)           turn in its own plane about an axis through the piece's normal at `pivot` (an edge word)
  edge(edge, deg)                    turn about one of its own edges (the side `edge` points to)
  turn(axis, deg, pivot)             turn `deg` (signed) about any axis (a word or vector) through pivot: hinge, centre, an
                                    edge word (e.g. limb: its far end) or a world point
  zlink and scissor set their ends on what stays down onto it (the gap left is the link spec's seatMm)
  nest(distance, toward, depth)      slide along the limb into the next section (a telescoping sleeve)
Groups (api.move_group): louver, petal, iris, fold, spine."""
import math

import numpy as np
from mathutils import Matrix, Vector

from . import body, geom, model, motion

EDGE_BAND = 0.012     # m: points this close to the extreme along a side are its edge
LINK_W, LINK_T = 0.012, 0.007   # m: a link bar's width (along its pins) and thickness
LINK_INSET = 0.012    # m: links sit this far under the piece's surface
LINK_MARGIN = 0.02    # m: and this far in from its leading and trailing edges
LINK_SPREAD = 0.3     # the two links (or scissors) of a piece stand this share of its width (10th-90th percentile, so a stray
                      # spur does not count) either side of its middle
SCISSOR_DROP = 0.015  # m: a scissor's body ends sit this far under its piece ends, shut (deeper to reach what stays)
SCISSOR_MIN_DROP = 0.005
ANCHOR_REACH = 0.03   # m: a link's fixed end looks this far into (or out of) the suit for what stays to sit on


class Shape:
    """A piece as it stands (its own surface's points and area-weighted normals, world: model.own, no lining), moved by `m`
    from shut."""

    def __init__(self, hinge, m=None):
        m = m or Matrix.Identity(4)
        pts, nrm = [], Vector()
        for o in model.pieces_of(hinge):
            b = m @ model.base(o)
            keep, polys = model.own(o)
            pts += [b @ o.data.vertices[i].co for i in keep]
            n3 = b.to_3x3()
            for p in polys:
                nrm += (n3 @ p.normal) * p.area
        self.hinge, self.m = hinge, m
        self.pts = pts
        self.np = np.array([tuple(p) for p in pts])
        self.centre = Vector(self.np.mean(axis=0))
        self.n = nrm.normalized() if nrm.length > 1e-9 else Vector((0, 1, 0))
        p, a = model.line(hinge)
        self.line = (m @ p, (m.to_3x3() @ a).normalized())
        main, rim = model.main_island(hinge)
        # the main body's outline (all of it for a closed, thickened plate), moved with the piece: where its edges are (a
        # bulge, a rivet or a trim island on it is not an edge)
        rim = rim if len(rim) else main
        self.rim = rim @ np.array(m.to_3x3()).T + np.array(tuple(m.translation)) if len(rim) else self.np
        j = body.joints(__import__("bpy").context.scene)
        self.limb = None
        if j is not None:
            reg, side, _ = model.body_part(hinge, j)
            h, t = body.seat(j, reg, side if side in ("L", "R") else "L")
            self.limb = (Vector(h), (Vector(t) - Vector(h)).normalized())
            # Outward is away from the body part's axis, whichever way the source's faces are wound.
            r = self.centre - self.limb[0]
            r -= self.limb[1] * r.dot(self.limb[1])
            if r.length > 1e-6 and self.n.dot(r) < 0:
                self.n = -self.n

    def moved(self, segments):
        return Shape(self.hinge, motion.Path(segments).end() @ self.m) if segments else self

    def direction(self, d):
        if not isinstance(d, str):
            return Vector(d).normalized()
        x = 1 if self.centre.x >= 0 else -1
        words = {"up": (0, 0, 1), "down": (0, 0, -1), "back": (0, 1, 0), "front": (0, -1, 0), "left": (1, 0, 0),
                 "right": (-1, 0, 0), "outside": (x, 0, 0), "neck": (-x, 0, 1)}
        if d in words:
            return Vector(words[d]).normalized()
        if d in ("out", "in"):
            return self.n * (1 if d == "out" else -1)
        if d in ("limb", "limb-"):
            if self.limb is None:
                raise ValueError("这个模型没有关节，不能用 limb")
            return self.limb[1] * (1 if d == "limb" else -1)
        if d in ("hinge", "hinge-"):
            return self.line[1] * (1 if d == "hinge" else -1)
        raise ValueError("不认识的方向：%s" % d)

    def along(self, d):
        """The direction `d` along the surface (less the normal), unless it is out or in, or given as a vector (x, y, z):
        a vector is taken as it is, so a slide goes exactly that way (projecting it can turn it inward on a slanted plate)."""
        v = self.direction(d)
        if not isinstance(d, str) or d in ("out", "in"):
            return v
        t = v - self.n * v.dot(self.n)
        if t.length < 1e-6:
            raise ValueError("方向 %s 和板面垂直，不能沿面滑" % (d,))
        return t.normalized()

    def extent(self, v):
        s = self.np @ np.array(tuple(v))
        return float(s.max() - s.min())

    def edge(self, side):
        """(point, unit axis) of the piece's edge on `side`: the line through the points of its main body's outline (rim)
        farthest that way, so a bulge standing out of the plate is never taken for its edge."""
        d = self.along(side) if side not in ("out", "in") else self.direction(side)
        s = self.rim @ np.array(tuple(d))
        band = self.rim[s >= s.max() - EDGE_BAND]
        if len(band) < 2:
            band = self.rim[np.argsort(-s)[:8]]
        c, a = geom.axis_of([Vector(p) for p in band])
        return c, a

    def lifts(self, point, axis):
        """The sign of a turn about (point, axis) that lifts the piece's far side off its surface (along its normal)."""
        r = self.centre - point
        r -= axis * r.dot(axis)
        return 1 if axis.cross(r).dot(self.n) >= 0 else -1

    def under(self, q, inset=LINK_INSET):
        """The point on the piece nearest `q`, pushed `inset` under its surface."""
        k = int(np.argmin(((self.np - np.array(tuple(q))) ** 2).sum(axis=1)))
        return Vector(self.np[k]) - self.n * inset


def _scale(segments, a, b):
    path = motion.Path(segments)
    out = []
    for s in path.segments:
        s = dict(s)
        s["at"] = [a + (b - a) * s["at"][0], a + (b - a) * s["at"][1]]
        out.append(s)
    return out


def _total(segments):
    return sum(motion.reach(s) for s in segments)


# ---------------------------------------------------------------------------------------------------------------- presets
def p_hinge(shape, deg=None):
    return None, []


def p_slide(shape, direction, distance=None, under=0, depth=0.025, go=None):
    """Slide `distance` along `direction`. How it clears the plate ahead: go="over" (or under=1) first lifts `depth` out off
    the surface and passes over it, on top; go="under" (or under=-1) first sinks `depth` in and passes beneath it, hidden;
    go="flat" (under=0) slides on the surface. `go` wins over `under`."""
    if go is not None:
        if go not in GO:
            raise ValueError("go 是 over（从上面滑过）、under（从下面藏进去）或 flat（贴面）")
        under = GO[go]
    t = shape.along(direction)
    dist = distance if distance is not None else 0.7 * shape.extent(t)
    if not under:
        return [motion.slide(t * dist)], []
    return [motion.slide(shape.n * depth * (1 if under > 0 else -1), at=(0.0, 0.35)), motion.slide(t * dist, at=(0.1, 1.0))], []


GO = {"over": 1, "flat": 0, "under": -1}


def p_offset(shape, direction, distance=None, lift=0.025):
    t = shape.along(direction)
    dist = distance if distance is not None else 0.7 * shape.extent(t)
    return [motion.slide(shape.n * lift), motion.slide(t * dist)], []


def p_stepped(shape, direction, distance=None, rise=0.015, steps=3):
    t = shape.along(direction)
    dist = distance if distance is not None else 0.7 * shape.extent(t)
    segs = []
    for k in range(steps):
        segs.append(motion.slide(t * dist / steps))
        if k < steps - 1:
            segs.append(motion.slide(shape.n * rise))
    return segs, []


def p_edge(shape, edge, deg=60.0):
    point, axis = shape.edge(edge)
    return [motion.turn(point, axis, shape.lifts(point, axis) * abs(deg))], []


def p_plug(shape, push=0.03, push_dir="out", edge=None, deg=50.0, direction=None, distance=None):
    out = [motion.slide(shape.direction(push_dir) * push)]
    after = shape.moved(out)
    if edge is not None:
        rest, _ = p_edge(after, edge, deg)
    elif direction is not None:
        rest, _ = p_slide(after, direction, distance)
    else:
        return out, []
    return _then(out, rest), []


def _twist_sign(shape, then):
    """+1 or -1: the way round the limb's axis the move that follows turns the piece (`then` = (kind, {settings}), or its hinge
    turn without one), judged by its first turn - the outward way, so the unlatch and the opening go the same way round. A move
    with no turn in it (a slide) gives the hinge's own sense."""
    axis = shape.limb[1]
    rest = PRESETS[then[0]](shape, **then[1])[0] if then else None
    p, a = shape.line
    turns = [motion.turn(p, a, shape.hinge["fh_angle"])] if rest is None else [s for s in rest if motion.kind(s) == "turn"]
    if not turns:
        turns = [motion.turn(p, a, shape.hinge["fh_angle"])]
    s = turns[0]
    return (1 if s["turn"] >= 0 else -1) * (1 if Vector(s["axis"]).dot(axis) >= 0 else -1)


def p_twist(shape, deg=None, size=15.0, then=None):
    """Turn about the limb's axis by `deg` (signed). Without `deg` it is `size` degrees the way the move that follows opens
    the piece outward (`then`; a hinge's own turn when there is none)."""
    if shape.limb is None:
        raise ValueError("这个模型没有关节，不能绕肢体轴转")
    if deg is None:
        deg = size * _twist_sign(shape, then)
    return [motion.turn(shape.limb[0], shape.limb[1], deg)], []


def p_lift_turn(shape, deg, lift=0.02):
    """Lift `lift` straight out from the limb's axis (along the line from the axis through the piece's middle), then turn
    `deg` (signed, right-handed about the limb's axis toward its far end) about the limb's axis, round the limb."""
    if shape.limb is None:
        raise ValueError("这个模型没有关节，不能绕肢体轴转")
    p, a = shape.limb
    r = shape.centre - p
    r -= a * r.dot(a)
    if r.length < 1e-6:
        raise ValueError("件的中心在肢体轴上，分不出径向")
    out = [motion.slide(r.normalized() * lift)]
    return _then(out, [motion.turn(p, a, deg)]), []


def p_flare(shape, lift=0.0015, deg=None):
    if deg:
        p, a = shape.line
        return [motion.turn(p, a, shape.lifts(p, a) * abs(deg), back=True)], []
    return [motion.slide(shape.n * lift, back=True)], []


def p_nest(shape, distance=None, toward="limb", depth=0.025):
    return p_slide(shape, toward, distance, under=-1, depth=depth)


def p_turn(shape, axis, deg, pivot="hinge"):
    """Turn `deg` (signed, right-handed about `axis`) about the line along `axis` (a direction word or vector) through
    `pivot`: "hinge" (a point of its hinge line), "centre", an edge word (the middle of the piece's edge that way, e.g.
    "limb" for its far end along the limb), or a world point (x, y, z)."""
    a = shape.direction(axis)
    if not isinstance(pivot, str):
        p = Vector(pivot)
    elif pivot == "hinge":
        p = shape.line[0]
    elif pivot == "centre":
        p = shape.centre
    else:
        p, _ = shape.edge(pivot)
    return [motion.turn(p, a, deg)], []


def p_scissor_door(shape, deg=45.0, pivot="hinge"):
    if pivot == "hinge":
        p = shape.line[0]
    else:
        p, _ = shape.edge(pivot)
    return [motion.turn(p, shape.n, deg)], []


def _stays():
    """BVH of what stays (every mesh that is not a piece or a link), as it stands shut."""
    import bpy
    from mathutils.bvhtree import BVHTree
    verts, polys = [], []
    for o in bpy.context.scene.objects:
        if o.type != "MESH" or o.get("fh_hinge") or o.get("fh_link") or not o.data.polygons:
            continue
        off = len(verts)
        verts += [tuple(o.matrix_world @ v.co) for v in o.data.vertices]
        polys += [tuple(off + i for i in p.vertices) for p in o.data.polygons]
    return BVHTree.FromPolygons(verts, polys) if polys else None


def _seat(tree, ends, down):
    """How far to move the fixed ends of a piece's links along `down` (a unit vector into the suit) so they sit on what
    stays (`tree`, _stays()). Each end's own shift is how far along `down` it meets the shell (negative: back up, for an end
    already inside it; when `down` misses it, as far along `down` as the nearest point of the shell lies); they all move by
    one amount (the links stay parallel): of no shift, those shifts and their mean, the one that leaves the farthest end
    nearest the shell. ANCHOR_REACH is kept short so an end is not sunk deep into the suit, where its link would run
    through the body. 0 when no end finds the shell within ANCHOR_REACH (the check then reports the links' fixed ends in the air)."""
    if tree is None:
        return 0.0
    shift = []
    for p in ends:
        hit = tree.ray_cast(p, down, ANCHOR_REACH)[0]
        back = tree.ray_cast(p, -down, ANCHOR_REACH)[0]
        if hit is not None and (back is None or (hit - p).length <= (back - p).length):
            shift.append((hit - p).dot(down))
        elif back is not None:
            shift.append(-(back - p).length)
        else:
            q = tree.find_nearest(p, ANCHOR_REACH)[0]
            if q is not None:
                shift.append((q - p).dot(down))
    if not shift:
        return 0.0
    return min([0.0] + shift + [sum(shift) / len(shift)],
               key=lambda s: max(tree.find_nearest(p + down * s)[3] for p in ends))


def _gap_mm(tree, p):
    """How far a link's fixed end is from what stays (mm), None without anything that stays."""
    return None if tree is None else round(tree.find_nearest(p)[3] * 1000, 1)


def p_zlink(shape, direction, deg=135.0, length=None):
    """Two parallel links under the piece: each pinned on what stays ahead of the piece (the way it goes) and on the piece
    behind; the piece swings out on them, level, and comes down over its neighbour that way. The fixed ends are moved
    together straight into the suit (along the piece's inward normal) onto what stays (_seat), so neither hangs in the air;
    the links stay parallel, each spec keeps the gap its end is left from the shell ("seatMm")."""
    d = shape.along(direction)
    t = -d
    axis = t.cross(shape.n).normalized()
    ext = shape.extent(d)
    length = length if length is not None else 0.45 * ext
    s = shape.np @ np.array(tuple(d))
    w = shape.np @ np.array(tuple(axis))
    lo, hi = np.percentile(w, (10, 90))
    centre_w = (lo + hi) / 2
    half = LINK_SPREAD * (hi - lo)
    radius = t * length
    bs = []
    for side in (-1, 1):
        q = shape.centre + d * (s.min() + LINK_MARGIN - shape.centre.dot(d)) + axis * (centre_w + side * half - shape.centre.dot(axis))
        b = shape.under(q)
        b -= shape.n * max(0.0, max((p - shape.under(p, 0.0)).dot(shape.n) + LINK_INSET for p in (b, b - radius)))
        bs.append(b)
    tree = _stays()
    radius = radius + shape.n * _seat(tree, [b - radius for b in bs], -shape.n)   # both ends alike: the links stay parallel
    links = [{"a": list(b - radius), "b": list(b), "axis": list(axis), "seatMm": _gap_mm(tree, b - radius)} for b in bs]
    return [motion.arc(axis, radius, deg)], links


def p_scissor(shape, direction="out", distance=0.06, along=None):
    """Two crossed-link scissors under the piece, across its long side: each bar pinned at one end and sliding at the other,
    one bar's slide on the piece, the other's on what stays; the piece runs straight out on them. Each scissor's ends on what
    stays go down (a deeper drop) until they sit on it; the gap each is left is in its spec ("seatMm")."""
    e = shape.direction(direction)
    if along is None:
        cov = np.cov((shape.np - shape.np.mean(axis=0)).T)
        vals, vecs = np.linalg.eigh(cov)
        t = Vector(vecs[:, -1])
    else:
        t = shape.direction(along)
    t = (t - e * t.dot(e)).normalized()
    k = t.cross(e).normalized()
    ext = shape.extent(t)

    def spread(drop):
        """(bar length, the span of its ends along t) for a scissor whose ends on what stays sit `drop` under the piece's."""
        length = max(0.6 * ext, (distance + drop) / 0.85)
        return length, math.sqrt(max(length ** 2 - drop ** 2, 1e-6))
    span = spread(SCISSOR_DROP)[1]
    w = shape.np @ np.array(tuple(k))
    lo, hi = np.percentile(w, (10, 90))
    centre_k = (lo + hi) / 2
    half = LINK_SPREAD * (hi - lo)
    tree = _stays()
    links = []
    for side in (-1, 1):
        # Centred on the piece along t, LINK_INSET under it there, then sunk further wherever the curved piece falls away
        # so that no bar end shows through it.
        c = shape.centre + k * (centre_k + side * half - shape.centre.dot(k))
        q = shape.under(c, 0.0)
        c += shape.n * ((q - c).dot(shape.n) - LINK_INSET)
        ends = [c + t * s * span / 2 + k * b * LINK_W * 0.55 - e * drop for s in (-1, 1) for b in (-1, 1)
                for drop in (0.0, SCISSOR_DROP)]
        c -= shape.n * max(0.0, max((p - shape.under(p, 0.0)).dot(shape.n) + LINK_INSET for p in ends))
        # each end on what stays goes down onto it (its own deeper drop: the two bars are separate links), so the scissor
        # stands on the shell, not in the air; the span is set by the deeper of the two
        drops = [SCISSOR_DROP, SCISSOR_DROP]
        for _ in range(3):
            span = spread(max(drops))[1]
            drops = [max(SCISSOR_MIN_DROP, d + _seat(tree, [c + t * s * span / 2 - e * d], -e))
                     for s, d in zip((-1, 1), drops)]
        span = spread(max(drops))[1]
        for bar in (-1, 1):
            off = k * bar * LINK_W * 0.55
            top0, top1 = c - t * span / 2 + off, c + t * span / 2 + off
            low0, low1 = top0 - e * drops[0], top1 - e * drops[1]
            if bar < 0:   # pinned on what stays, sliding on the piece
                links.append({"a": list(low0), "b": list(top1), "axis": list(k), "bRail": list(t),
                              "seatMm": _gap_mm(tree, low0)})
            else:         # pinned on the piece, sliding on what stays
                links.append({"a": list(low1), "b": list(top0), "axis": list(k), "aRail": list(t),
                              "seatMm": _gap_mm(tree, low1)})
        span = spread(SCISSOR_DROP)[1]
    return [motion.slide(e * distance)], links


PRESETS = {"hinge": p_hinge, "slide": p_slide, "offset": p_offset, "stepped": p_stepped, "edge": p_edge, "plug": p_plug,
           "twist": p_twist, "flare": p_flare, "nest": p_nest, "scissor_door": p_scissor_door, "zlink": p_zlink,
           "scissor": p_scissor, "turn": p_turn, "lift_turn": p_lift_turn}
LABEL = {"hinge": "铰链", "slide": "伸缩滑板", "offset": "错层滑移", "stepped": "阶梯滑移", "edge": "绕边翻", "plug": "塞拉门",
         "twist": "旋转解锁", "flare": "鳞片预启", "nest": "套筒收缩", "scissor_door": "剪刀门", "zlink": "Z 形连杆",
         "scissor": "剪式伸缩", "turn": "绕轴转", "lift_turn": "外抬绕肢转", "path": "自定路径"}
# Presets that wait between their first part and `then` (an unlatch before the move).
PAUSE = {"twist": 0.12, "flare": 0.05}


def _then(first, rest, pause=0.0):
    """`first` then `rest`, sharing the time by how far each carries the piece, with a pause between."""
    a, b = _total(first), _total(rest)
    split = max(0.15, min(0.6, a / max(a + b, 1e-9)))
    return _scale(first, 0.0, split) + _scale(rest, split + pause, 1.0)


def build(hinge, kind, then=None, **kw):
    """(segments or None for the hinge, link specs) of preset `kind` on the piece, then `then` = (kind, {settings}); those
    settings may hold a `then` of their own (a chain of presets, each after the one before)."""
    return _build(Shape(hinge), hinge, kind, then, kw)


def _build(shape, hinge, kind, then, kw):
    if kind not in PRESETS:
        raise ValueError("没有这种开法：%s（%s）" % (kind, "、".join(PRESETS)))
    if kind == "twist":
        kw = dict(kw, then=then)     # an unset deg takes the way the move after it opens
    segs, links = PRESETS[kind](shape, **kw)
    if then:
        if segs is None:
            p, a = shape.line
            segs = [motion.turn(p, a, hinge["fh_angle"])]
        k2, kw2 = then[0], dict(then[1])
        rest, more = _build(shape.moved(segs), hinge, k2, kw2.pop("then", None), kw2)
        if rest is None:
            q, a = shape.moved(segs).line
            rest = [motion.turn(q, a, hinge["fh_angle"])]
        segs = _then(segs, rest, PAUSE.get(kind, 0.0))
        links += more
    return segs, links


# ------------------------------------------------------------------------------------------------------------------ links
def make_link(hinge, spec, k):
    """A link bar (a closed box from end a to end b, LINK_W along its pins, LINK_T thick) as a part of its own: coloured
    like the nearest spot of the piece it carries (its UVs all on that spot)."""
    import bpy
    a, b, n = Vector(spec["a"]), Vector(spec["b"]), Vector(spec["axis"]).normalized()
    u = (b - a).normalized()
    k_ = (n - u * n.dot(u)).normalized()
    w = u.cross(k_)
    ends = (a - u * LINK_T / 2, b + u * LINK_T / 2)
    vs = [e + k_ * sx * LINK_W / 2 + w * sy * LINK_T / 2 for e in ends for sx, sy in ((-1, -1), (1, -1), (1, 1), (-1, 1))]
    faces = [(0, 3, 2, 1), (4, 5, 6, 7), (0, 1, 5, 4), (1, 2, 6, 5), (2, 3, 7, 6), (3, 0, 4, 7)]
    name = "%s-连杆%d" % (hinge.name.replace("铰链-", ""), k + 1)
    me = bpy.data.meshes.new(name)
    me.from_pydata([tuple(v) for v in vs], [], faces)
    me.update()
    # outward: the first face's normal must point away from the box centre
    centre = sum(vs, Vector()) / 8
    if me.polygons[0].normal.dot(me.polygons[0].center - centre) < 0:
        for p in me.polygons:
            p.flip()
    ps = model.pieces_of(hinge)
    mid = (a + b) / 2
    best = None
    for o in ps:
        ok, loc, nrm, face = o.closest_point_on_mesh(o.matrix_world.inverted() @ mid)
        if ok and o.data.polygons[face].material_index in model.lining_slots(o):
            ok = False                  # coloured like the plate, not its lining
        if ok:
            d = (o.matrix_world @ loc - mid).length
            if best is None or d < best[0]:
                best = (d, o, face)
    uv = (0.5, 0.5)
    if best is not None:
        o, face = best[1], best[2]
        if o.data.uv_layers:
            lay = o.data.uv_layers.get("UVMap") or o.data.uv_layers[0]
            uv = tuple(lay.data[o.data.polygons[face].loop_indices[0]].uv)
            mi = o.data.polygons[face].material_index
            if o.data.materials:
                me.materials.append(o.data.materials[mi])
    lay = me.uv_layers.new(name="UVMap")
    for d in lay.data:
        d.uv = uv
    obj = bpy.data.objects.new(name, me)
    for c in (ps[0].users_collection if ps else [bpy.context.scene.collection]):
        c.objects.link(obj)
    obj["fh_link"] = hinge.name
    obj["fh_link_spec"] = __import__("json").dumps(spec)
    model.remember_base(obj)
    return obj


def clear_links(hinge):
    import bpy
    for o in model.links_of(hinge):
        me = o.data
        bpy.data.objects.remove(o, do_unlink=True)
        if me.users == 0:
            bpy.data.meshes.remove(me)


def path_points(hinge, n=24):
    """The piece's centre along its path (world), for drawing."""
    c = Shape(hinge).centre
    return [model.turn(hinge, k / n) @ c for k in range(n + 1)]
