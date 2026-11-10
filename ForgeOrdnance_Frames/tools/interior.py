"""The inside of an exosuit (build_frame.py calls dress on the finished shell): what a pilot sees when the suit stands open.

Every face that nothing outside can see in the shut suit (exposed_faces, rays from the 26 directions; the shell's inner wall, every part's
lining, found by geometry in build_frame.cavity_walls, never by a part's name) becomes a ribbed rubber mat, mapped round the body part it lies on. On that wall stands real geometry: quilted
pads, one closed solid each, laid out by body region (chest and back, waist, pelvis, arms, thighs, shins) and fitted by rays from
the body part's axis to the wall of each part separately, so a pad belongs to exactly one part and opens with it; bare metal frames
at the region borders and a spine beam down the sternum; light bars on the spine beam, at the waist and down each limb, mapped to the
armour's lit tile so the cockpit glows in the suit's own colour; corrugated conduits with clips, webbing straps with buckles, small warning
plates. The cut edges near an opening become rubber gaskets. All suits share one set of tiling textures (fabric normal and smoothness, metal,
rubber); only the fabric's colour map is the suit's own (SUITS). Each piece is closed and outward like the rest of the shell.

Materials: 1 fabric (an atlas of pad faces, FABRIC_* below), 2 metal, 3 rubber."""
import math
import os

import bmesh
import numpy as np
from mathutils import Vector
from mathutils.bvhtree import BVHTree
from mathutils.kdtree import KDTree

FABRIC, METAL, RUBBER = 1, 2, 3
TILE = 0.16            # m: one tile of the metal and rubber textures
CAVITY_U, TRIM_U, TILE_V = (0.010, 0.080), (0.095, 0.125), (0.965, 0.995)   # build_frame.TILE, the atlas strip's dark and trim tiles
LIT_U = (0.140, 0.170) # the atlas strip's lit tile (glow.paint lights it): light bars in the armour's material, so they take the suit's colour and state
LIGHT_BAR = 0.006      # m: a light bar's width
FABRIC_SIZE, TILE_SIZE = 1024, 512
GRID = 4               # a pad's quads per side
MIN_PAD = 0.03         # m: a pad is split in two until it fits its wall, down to this size
AXIS_SQUARE = 0.72     # how square to the rays from its body part's axis a plate's wall must lie to be furnished from that axis
FIT = 0.05             # m: largest distance of a pad's wall from a smooth patch
STEEP = 1.3            # largest slope of a pad's wall to the rays that find it
SINK = 0.002           # m: how far a piece's foot goes into the wall (less than build_frame.LINING_CLEAR: it never reaches the plate's outside)
RMAX = 0.62            # m: the farthest wall a body part's axis looks for
TAUT_GAP = 7            # stations: the widest gap in the wall a run along it spans
TINY_AREA = 1e-6       # m²: as build_frame.TINY_AREA, what the importer would drop
GASKET = 0.015         # m: an armour rim this close to an opening part is a gasket

# The fabric atlas: four rows of four 256 px cells; (column, row) of a pad face. Row 3 and 2 are the two pad colours, row 1 the small plates,
# row 0 the webbing, which wraps across the whole texture's width.
BOX, DIAMOND, VCHANNEL, HCHANNEL = 0, 1, 2, 3
HAZARD, STENCIL, TAB, PATCH = (0, 1), (1, 1), (2, 1), (3, 1)
TONE_A, TONE_B = 3, 2
# Per suit: fabric colours (sRGB), accent plate colour, then the layout. pad: width, height, thickness of a chest pad; frame: width and height
# of the metal frames; rows as (start, end, row height, pad width, variant, tone, thickness) along the torso from the hips to the neck.
SUITS = {
    # Sirius: charcoal over dark steel blue, diamond quilted chest, lean frames.
    "M15": dict(a=(0.235, 0.250, 0.285), b=(0.150, 0.190, 0.265), web=(0.120, 0.130, 0.150), accent=(0.86, 0.60, 0.10), frame=(0.026, 0.008),
                torso=[(0.02, 0.21, 0.120, 0.125, BOX, TONE_B, 0.020), (0.25, 0.47, 0.072, 0.190, HCHANNEL, TONE_A, 0.018), (0.51, 0.92, 0.140, 0.115, DIAMOND, TONE_A, 0.028)],
                limb=(0.125, 0.075, VCHANNEL, TONE_A, 0.016), conduits=2),
    # Taishan: dark olive, big thick blocks, heavy frames.
    "M16": dict(a=(0.225, 0.240, 0.165), b=(0.160, 0.175, 0.125), web=(0.140, 0.140, 0.105), accent=(0.84, 0.66, 0.08), frame=(0.040, 0.012),
                torso=[(0.02, 0.21, 0.150, 0.170, BOX, TONE_B, 0.030), (0.25, 0.47, 0.095, 0.220, HCHANNEL, TONE_B, 0.026), (0.51, 0.92, 0.175, 0.150, BOX, TONE_A, 0.038)],
                limb=(0.125, 0.078, HCHANNEL, TONE_A, 0.020), conduits=3),
    # Thunderbird: graphite with deep oxblood panels, channel stitched.
    "M17": dict(a=(0.200, 0.200, 0.215), b=(0.330, 0.125, 0.110), web=(0.115, 0.115, 0.120), accent=(0.80, 0.30, 0.07), frame=(0.030, 0.009),
                torso=[(0.02, 0.21, 0.120, 0.125, BOX, TONE_A, 0.020), (0.25, 0.47, 0.075, 0.190, HCHANNEL, TONE_B, 0.020), (0.51, 0.92, 0.150, 0.110, VCHANNEL, TONE_A, 0.028)],
                limb=(0.130, 0.080, VCHANNEL, TONE_B, 0.016), conduits=2),
    # Nightowl: dark coyote, slim small pads and thin rails.
    "M18": dict(a=(0.255, 0.225, 0.170), b=(0.180, 0.165, 0.130), web=(0.165, 0.145, 0.100), accent=(0.70, 0.74, 0.12), frame=(0.020, 0.007),
                torso=[(0.02, 0.21, 0.095, 0.095, DIAMOND, TONE_B, 0.016), (0.25, 0.47, 0.058, 0.150, HCHANNEL, TONE_A, 0.014), (0.51, 0.92, 0.110, 0.090, DIAMOND, TONE_A, 0.022)],
                limb=(0.100, 0.062, DIAMOND, TONE_A, 0.013), conduits=1),
}


def exposed_faces(bm, step=0.006, share=0.2):
    """Indices of the faces one can see from outside the shut suit: parallel rays on a grid from the 26 directions of a cube's corners, edges and
    faces, first hit only. A face counts when, from one direction, at least `share` of its area is seen (the walls seen in part
    through a gap between plates do not)."""
    tree = BVHTree.FromBMesh(bm)
    bm.faces.ensure_lookup_table()
    cos = np.array([v.co for v in bm.verts])
    mid = Vector(((cos.min(0) + cos.max(0)) / 2).tolist())
    radius = float(np.linalg.norm(cos.max(0) - cos.min(0))) / 2 + 0.05
    n = int(radius / step)
    seen = set()
    for x in (-1, 0, 1):
        for y in (-1, 0, 1):
            for z in (-1, 0, 1):
                if not (x or y or z):
                    continue
                d = Vector((x, y, z)).normalized()
                u = d.orthogonal().normalized()
                w = d.cross(u)
                hits = {}
                start = mid - d * radius * 1.1
                for i in range(-n, n + 1):
                    row = start + u * (i * step)
                    for j in range(-n, n + 1):
                        idx = tree.ray_cast(row + w * (j * step), d, radius * 2.2)[2]
                        if idx is not None:
                            hits[idx] = hits.get(idx, 0) + 1
                for idx, count in hits.items():
                    if idx in seen:
                        continue
                    f = bm.faces[idx]
                    if count * step * step / max(abs(f.normal.dot(d)), 0.2) >= share * max(f.calc_area(), 1e-9):
                        seen.add(idx)
    return seen


def tile_uv(f, uv, u_range):
    """Whether every corner of face `f` lies in the atlas tile u_range."""
    return all(u_range[0] - 1e-4 <= l[uv].uv.x <= u_range[1] + 1e-4 and TILE_V[0] - 1e-4 <= l[uv].uv.y <= TILE_V[1] + 1e-4 for l in f.loops)


class Axis:
    """A body part's axis: rays leave it square to it, `w` toward the back, `u` toward its outer side; angle 0 looks at the front."""

    def __init__(self, name, a, b, lateral, radius):
        self.name, self.a, self.radius = name, a.copy(), radius
        self.L = (b - a).length
        self.ax = (b - a) / self.L
        back = Vector((0, 1, 0))
        self.w = (back - back.dot(self.ax) * self.ax).normalized()
        self.u = (lateral - lateral.dot(self.ax) * self.ax - lateral.dot(self.w) * self.w).normalized()

    def point(self, s):
        return self.a + self.ax * (s * self.L)

    def ray(self, angle):
        return self.u * math.sin(angle) - self.w * math.cos(angle)

    def reach(self, p):
        """Distance of `p` from this body part's surface."""
        t = max(0.0, min(1.0, (p - self.a).dot(self.ax) / self.L))
        return (self.a + self.ax * (t * self.L) - p).length - self.radius


class Owner:
    """One moving part or the fixed shell, as a tree over its own faces, knowing which of them are wall."""

    def __init__(self, gid, faces, walls):
        self.gid = gid
        index, verts, polys = {}, [], []
        self.faces = []
        for f in faces:
            polys.append([index.setdefault(v, len(index)) for v in f.verts])
            self.faces.append(f)
        verts = [None] * len(index)
        for v, i in index.items():
            verts[i] = v.co.copy()
        self.tree = BVHTree.FromPolygons(verts, polys)
        self.walls = walls

    def hit(self, origin, direction):
        """(distance, face) of the first wall this part shows a ray from inside, or None."""
        start, gone = origin, 0.0
        for _ in range(4):
            loc, nrm, idx, dist = self.tree.ray_cast(start, direction, RMAX - gone)
            if idx is None:
                return None
            f = self.faces[idx]
            if nrm.dot(direction) < 0:
                return (gone + dist, f) if f in self.walls else None
            start = loc + direction * 1e-4
            gone += dist + 1e-4
        return None


class Kit:
    """Adds closed pieces to the shell mesh."""

    def __init__(self, bm, uv, deform, owners, plate_gids):
        self.bm, self.uv, self.deform, self.owners, self.plate_gids = bm, uv, deform, owners, plate_gids
        self.counts = {}
        self.points = []

    def weigh(self, v, owner, face):
        d = v[self.deform]
        if owner.gid is not None:
            d[owner.gid] = 1.0
            return
        near = min(face.verts, key=lambda q: (q.co - v.co).length_squared)
        for g, w in near[self.deform].items():
            d[g] = w

    def vert(self, co, owner, face):
        v = self.bm.verts.new(co)
        self.points.append(v)
        self.weigh(v, owner, face)
        return v

    def face(self, vs, uvs, material, smooth, kind):
        f = self.bm.faces.new(vs)
        f.material_index = material
        f.smooth = smooth
        for l, c in zip(f.loops, uvs):
            l[self.uv].uv = c
        self.counts[kind] = self.counts.get(kind, 0) + 1
        return f


def fit_patch(r):
    """r (rows, columns of distances) with one soft pass, and its largest distance from the flat patch through its corners."""
    a = np.asarray(r, dtype=float)
    s = a.copy()
    s[1:-1, 1:-1] = (a[1:-1, 1:-1] * 4 + a[:-2, 1:-1] + a[2:, 1:-1] + a[1:-1, :-2] + a[1:-1, 2:]) / 8
    n, m = a.shape
    y, x = np.mgrid[0:n, 0:m]
    A = np.stack([x.ravel(), y.ravel(), x.ravel() * y.ravel(), np.ones(n * m)], axis=1)
    coef, *_ = np.linalg.lstsq(A, s.ravel(), rcond=None)
    return s, float(np.abs(A @ coef - s.ravel()).max())


class AxisFrame:
    """Lays pieces round a body part: rays from its axis. Arc length a round it at a station s (0..1 along it), along the first wall any part
    shows there, so a pad keeps its width on any section; a is signed, 0 the front, + toward the part's outer side."""
    STEPS = 90

    def __init__(self, axis, axes, owners):
        self.axis, self.axes, self.owners, self.tables = axis, axes, owners, {}
        self.L = axis.L
        self.ax = axis.ax

    def table(self, s):
        key = round(s, 3)
        t = self.tables.get(key)
        if t is not None:
            return t
        o = self.axis.point(s)
        angles = np.linspace(0, math.pi, self.STEPS)
        tab = []
        for sign in (1.0, -1.0):
            arc, last, out = 0.0, None, [0.0]
            for k, a in enumerate(angles):
                d = self.axis.ray(sign * a)
                best = None
                for ow in self.owners:
                    h = ow.hit(o, d)
                    if h is not None and (best is None or h[0] < best):
                        best = h[0]
                if best is None:
                    best = last if last is not None else 0.2
                if k:
                    prev = last if last is not None else best
                    arc += math.hypot(best - prev, (best + prev) / 2 * (angles[1] - angles[0]))
                    out.append(arc)
                last = best
            tab.append(np.array(out))
        self.tables[key] = (angles, tab[0], tab[1])
        return self.tables[key]

    def extent(self, s):
        _, plus, minus = self.table(s)
        return plus[-1], minus[-1]

    def sample(self, owner, a, s):
        """(ray origin, ray direction, distance to the owner's wall, face), or None."""
        if not 0.0 <= s <= 1.0:
            return None
        angles, plus, minus = self.table(s)
        side = plus if a >= 0 else minus
        if abs(a) > side[-1]:
            return None
        o = self.axis.point(s)
        d = self.axis.ray(math.copysign(float(np.interp(abs(a), side, angles)), a))
        h = owner.hit(o, d)
        return None if h is None else (o, d, h[0], h[1])

    def accept(self, p):
        return min(self.axes, key=lambda x: x.reach(p)) is self.axis


class PlateFrame:
    """Lays pieces on one wide plate: rays square to its best flat plane; a across it from its middle, s (0..1) along the body part it lies on."""

    def __init__(self, owner, faces, along):
        pts = np.array([f.calc_center_median() for f in faces], dtype=float)
        weights = np.array([f.calc_area() for f in faces], dtype=float)
        centre = (pts * weights[:, None]).sum(0) / weights.sum()
        q = pts - centre
        _, vectors = np.linalg.eigh((q * weights[:, None]).T @ q)
        n = Vector(vectors[:, 0])
        if sum((f.normal * f.calc_area() for f in faces), Vector()).dot(n) < 0:
            n = -n
        self.ax = (along - along.dot(n) * n).normalized()
        self.across = n.cross(self.ax)
        self.n = n
        co = np.array([v.co for f in faces for v in f.verts], dtype=float) - centre
        aa, ss, hh = co @ np.array(self.across), co @ np.array(self.ax), co @ np.array(n)
        self.width = float(aa.max() - aa.min())
        self.L = float(ss.max() - ss.min())
        self.centre = Vector(centre) + self.across * float((aa.max() + aa.min()) / 2) + self.ax * float(ss.min())
        self.lift = float(hh.max()) + 0.02

    def extent(self, s):
        return self.width / 2, self.width / 2

    def sample(self, owner, a, s):
        if not 0.0 <= s <= 1.0 or abs(a) > self.width / 2:
            return None
        o = self.centre + self.across * a + self.ax * (s * self.L) + self.n * self.lift
        h = owner.hit(o, -self.n)
        return None if h is None else (o, -self.n, h[0], h[1])

    def accept(self, p):
        return True


def add_pad(kit, frame, owner, a0, a1, s0, s1, cell, thick, depth=0):
    """A pad over a0..a1 and station s0..s1 on `owner`'s wall (cell: atlas column and row of its face), split when the wall is not one flat
    patch there. Returns how many pads stood."""
    L = frame.L
    w, h = a1 - a0, (s1 - s0) * L

    def split():
        if max(w, h) < 2 * MIN_PAD or depth >= 3:
            return 0
        gap = 0.004
        if w >= h:
            m = (a0 + a1) / 2
            return (add_pad(kit, frame, owner, a0, m - gap / 2, s0, s1, cell, thick * 0.9, depth + 1)
                    + add_pad(kit, frame, owner, m + gap / 2, a1, s0, s1, cell, thick * 0.9, depth + 1))
        m = (s0 + s1) / 2
        g = gap / 2 / L
        return (add_pad(kit, frame, owner, a0, a1, s0, m - g, cell, thick * 0.9, depth + 1)
                + add_pad(kit, frame, owner, a0, a1, m + g, s1, cell, thick * 0.9, depth + 1))

    # Nothing of this owner anywhere over the cell: nothing to split for.
    probes = [(a0 + w * x, s0 + (s1 - s0) * y) for x, y in ((.5, .5), (.1, .1), (.9, .1), (.1, .9), (.9, .9))]
    if not any(frame.sample(owner, *p) for p in probes):
        return 0
    n = GRID
    grid = [[None] * (n + 1) for _ in range(n + 1)]
    for j in range(n + 1):
        for i in range(n + 1):
            q = frame.sample(owner, a0 + w * i / n, s0 + (s1 - s0) * j / n)
            if q is None:
                return split()
            grid[j][i] = q
    # A pad follows a curved wall; it stops at a step in it.
    r, off = fit_patch([[g[2] for g in row] for row in grid])
    jump = max(float(np.abs(np.diff(r, axis=0)).max()) / (h / n), float(np.abs(np.diff(r, axis=1)).max()) / (w / n))
    if off > FIT or jump > STEEP:
        return split()
    centre = grid[n // 2][n // 2]
    if not frame.accept(centre[0] + centre[1] * centre[2]):
        return 0
    pts = [[grid[j][i][0] + grid[j][i][1] * r[j, i] for i in range(n + 1)] for j in range(n + 1)]
    thick = min(thick, 0.22 * min(w, h))
    # The rounded corners: the corner points pull in along the diagonal.
    def rounded(i, j):
        p = pts[j][i]
        if i in (0, n) and j in (0, n):
            return p.lerp(pts[n // 2][n // 2], 0.07)
        return p
    # The inward normal at each point, from the grid.
    du = pts[n // 2][n] - pts[n // 2][0]
    dv = pts[n][n // 2] - pts[0][n // 2]
    flat = du.cross(dv).normalized()
    if flat.dot(centre[1]) > 0:
        flat = -flat
    flip = du.cross(dv).dot(flat) < 0
    cu, cv = cell
    col = 0.25 * (cu + 0.02), 0.25 * (cv + 0.02)
    span = 0.25 * 0.96
    verts = {}
    for j in range(n + 1):
        for i in range(n + 1):
            x, y = 2 * i / n - 1, 2 * j / n - 1
            dome = (max(0.0, 1 - abs(x) ** 4) * max(0.0, 1 - abs(y) ** 4)) ** 0.5
            tan_u = pts[j][min(i + 1, n)] - pts[j][max(i - 1, 0)]
            tan_v = pts[min(j + 1, n)][i] - pts[max(j - 1, 0)][i]
            nrm = tan_u.cross(tan_v)
            nrm = (nrm if nrm.dot(flat) > 0 else -nrm).normalized().lerp(flat, 0.5).normalized()
            verts[i, j] = kit.vert(rounded(i, j) + nrm * (thick * dome - SINK), owner, grid[j][i][3])
    faces = []
    for j in range(n):
        for i in range(n):
            q = [(i, j), (i + 1, j), (i + 1, j + 1), (i, j + 1)]
            if flip:
                q.reverse()
            faces.append(kit.face([verts[c] for c in q], [(col[0] + span * c[0] / n, col[1] + span * c[1] / n) for c in q], FABRIC, True, "pad"))
    # The foot, hidden in the wall: a fan round its middle.
    edge = [(i, 0) for i in range(n)] + [(n, j) for j in range(n)] + [(i, n) for i in range(n, 0, -1)] + [(0, j) for j in range(n, 0, -1)]
    if not flip:
        edge.reverse()
    hub = kit.vert(centre[0] + centre[1] * (r[n // 2, n // 2] + SINK), owner, centre[3])
    middle = col[0] + span / 2, col[1] + span / 2
    for k, c in enumerate(edge):
        c1 = edge[(k + 1) % len(edge)]
        foot = kit.face([verts[c], verts[c1], hub], [(col[0] + span * c[0] / n, col[1] + span * c[1] / n), (col[0] + span * c1[0] / n, col[1] + span * c1[1] / n), middle], FABRIC, False, "foot")
        for e in foot.edges:
            e.smooth = False
    return 1


def pad_rows(kit, frame, owners, s0, s1, row_h, pad_w, cell, thick, start=0.0, gap=0.005):
    """Rows of pads over stations s0..s1 on every owner, mirrored about the middle."""
    stood = 0
    L = frame.L
    rows = max(1, int(round((s1 - s0) * L / row_h)))
    step = (s1 - s0) / rows
    for k in range(rows):
        a, b = s0 + k * step + gap / 2 / L, s0 + (k + 1) * step - gap / 2 / L
        plus, minus = frame.extent((a + b) / 2)
        for sign, top in ((1.0, plus), (-1.0, minus)):
            x = start
            while x < top:
                c0, c1 = x + gap / 2, x + pad_w - gap / 2
                lo, hi = (c0, c1) if sign > 0 else (-c1, -c0)
                for o in owners:
                    stood += add_pad(kit, frame, o, lo, hi, a, b, cell, thick)
                x += pad_w
    return stood


def smooth_line(values, passes=2):
    a = np.array(values, dtype=float)
    for _ in range(passes):
        if len(a) > 2:
            a[1:-1] = (a[:-2] + 2 * a[1:-1] + a[2:]) / 4
    return a


def beam(kit, stations, owner, width, height, lift, material, uv_of, kind):
    """A closed box along stations [(wall point, ray direction into the wall, width direction, face)]: `height` over the wall, `lift` under its foot.
    uv_of(side, along, across) -> uv; side 0 top, 1 and 3 flanks, 2 foot."""
    ring = []
    length = 0.0
    lengths = []
    for k, (p, d, wd, f) in enumerate(stations):
        if k:
            length += (p - stations[k - 1][0]).length
        lengths.append(length)
        n = -d
        a = p + n * (lift - SINK)
        b = p + n * (lift + height)
        h = wd * (width / 2)
        ring.append([kit.vert(a - h, owner, f), kit.vert(a + h, owner, f), kit.vert(b + h, owner, f), kit.vert(b - h, owner, f)])
    # Outward: judge one side by the direction it must face.
    d0 = -stations[0][1]
    probe = (ring[1][3].co - ring[0][3].co).cross(ring[0][2].co - ring[0][3].co)
    flip = probe.dot(d0) < 0
    for k in range(len(ring) - 1):
        for side in range(4):
            q = [ring[k][side], ring[k][(side + 1) % 4], ring[k + 1][(side + 1) % 4], ring[k + 1][side]]
            uvs = [uv_of(side, lengths[k], 0), uv_of(side, lengths[k], 1), uv_of(side, lengths[k + 1], 1), uv_of(side, lengths[k + 1], 0)]
            if flip:
                q.reverse()
                uvs.reverse()
            kit.face(q, uvs, material, side == 2, kind)
    for rk, rev in ((ring[0], not flip), (ring[-1], flip)):
        q = list(rk)
        if rev:
            q.reverse()
        kit.face(q, [uv_of(1, c * width, x) for c, x in ((0, 0), (0, 1), (1, 1), (1, 0))], material, False, kind)


def metal_uv(width):
    """The metal tile: the top across the tile's middle, with its line of rivets; the flanks and foot on its plain edge."""
    def uv(side, along, across):
        if side == 2:
            return along / TILE, 0.25 + 0.5 * across
        return along / TILE, (0.02 + 0.1 * across) if side in (1, 3) else 0.05 + 0.1 * across
    return uv


def webbing_uv():
    def uv(side, along, across):
        return along / 0.16, 0.25 * (0.08 + 0.84 * across)
    return uv


def rect_uv(cell):
    def uv(side, along, across):
        return 0.25 * (cell[0] + 0.05 + 0.9 * min(1.0, along)), 0.25 * (cell[1] + 0.05 + 0.9 * across)
    return uv


def runs(samples, jump):
    """Splits stations (None where the wall is not this owner's) into runs without a gap or a step."""
    out, cur = [], []
    for q in samples:
        if q is None or (cur and abs(q[2] - cur[-1][2]) > jump):
            if len(cur) > 1:
                out.append(cur)
            cur = []
        if q is not None:
            cur.append(q)
    if len(cur) > 1:
        out.append(cur)
    return out


def along_ring(kit, frame, owners, s, width, height, lift, material, uv_of, kind, min_len=0.07, side_limit=None):
    """A frame across the wall at station s on every owner."""
    for owner in owners:
        for sign, top in zip((1.0, -1.0), frame.extent(s)):
            step = 0.014
            if side_limit is not None:
                top = min(top, side_limit)
            line = []
            x = 0.0 if sign > 0 else step
            while x < top:
                q = frame.sample(owner, sign * x, s)
                if q is not None and not frame.accept(q[0] + q[1] * q[2]):
                    q = None
                if q is not None and any(frame.sample(owner, sign * x, s + e * width / 2 / frame.L) is None for e in (-1.0, 1.0)):
                    q = None
                line.append(q)
                x += step
            for run in runs(line, 0.02):
                if (len(run) - 1) * step < min_len:
                    continue
                r = smooth_line([q[2] for q in run])
                st = [(q[0] + q[1] * rr, q[1], frame.ax, q[3]) for q, rr in zip(run, r)]
                beam(kit, st, owner, width, height, lift, material, uv_of, kind)


def lit_uv(side, along, across):
    return (sum(LIT_U) / 2, sum(TILE_V) / 2)


def light_bar_axis(kit, frame, owner, a, s0, s1, lift):
    """A thin light along the body part at arc a, standing `lift` off the wall, on the armour's material (lit tile)."""
    along_axis(kit, frame, [owner], a, s0, s1, LIGHT_BAR, 0.004, lift, 0, lit_uv, "light", min_len=0.1)


def line_along(frame, owner, a, s0, s1, ds):
    line = []
    s = s0
    while s <= s1:
        q = frame.sample(owner, a, s)
        if q is not None and not frame.accept(q[0] + q[1] * q[2]):
            q = None
        line.append(q)
        s += ds
    return line


def taut(line):
    """A tube, strap or beam running along a wall spans its dents: the stations from the first wall to the last with small gaps filled, at distances on
    one low curve through the wall (the farther outliers left out). Returns the stations, or [] when too little wall is there."""
    known = [k for k, q in enumerate(line) if q is not None]
    if len(known) < 4:
        return []
    filled = []
    for k in range(known[0], known[-1] + 1):
        q = line[k]
        if q is None:
            p0 = max(i for i in known if i < k)
            p1 = min(i for i in known if i > k)
            if p1 - p0 > TAUT_GAP:
                return []
            t = (k - p0) / (p1 - p0)
            a, b = line[p0], line[p1]
            q = (a[0].lerp(b[0], t), a[1].lerp(b[1], t).normalized(), a[2] + (b[2] - a[2]) * t, a[3])
        filled.append(q)
    x = np.arange(len(filled), dtype=float)
    r = np.array([q[2] for q in filled], dtype=float)
    keep = np.ones(len(r), dtype=bool)
    for _ in range(3):
        fit = np.polyval(np.polyfit(x[keep], r[keep], min(3, int(keep.sum()) - 1)), x)
        keep = np.abs(r - fit) < 0.02
        if keep.sum() < 4:
            return []
    return [(q[0], q[1], float(rr), q[3]) for q, rr in zip(filled, fit)]


def along_axis(kit, frame, owners, a, s0, s1, width, height, lift, material, uv_of, kind, min_len=0.06):
    """A beam along the body part at signed arc a, on every owner."""
    for owner in owners:
        run = taut(line_along(frame, owner, a, s0, s1, 0.02 / frame.L))
        if (len(run) - 1) * 0.02 < min_len:
            continue
        st = [(q[0] + q[1] * q[2], q[1], frame.ax.cross(q[1]).normalized(), q[3]) for q in run]
        beam(kit, st, owner, width, height, lift, material, uv_of, kind)


def conduit(kit, frame, owner, a, s0, s1, radius, lift, sides=6):
    """A corrugated tube along the body part at arc a; only where the wall is smooth."""
    run = taut(line_along(frame, owner, a, s0, s1, 0.03 / frame.L))
    if (len(run) - 1) * 0.03 < 0.1:
        return
    rings = []
    length = 0.0
    prev = None
    for q in run:
        c = q[0] + q[1] * (q[2] - lift - radius)
        if prev is not None:
            length += (c - prev).length
        prev = c
        side = frame.ax.cross(q[1]).normalized()
        up = -q[1]
        rings.append(([kit.vert(c + side * (radius * math.cos(2 * math.pi * k / sides)) + up * (radius * math.sin(2 * math.pi * k / sides)), owner, q[3]) for k in range(sides)], length))
    d = rings[1][0][0].co - rings[0][0][0].co
    c0 = rings[0][0][0].co - rings[0][0][sides // 2].co
    c1 = rings[0][0][1].co - rings[0][0][0].co
    flip = c0.dot(c1.cross(d)) < 0
    for k in range(len(rings) - 1):
        for m in range(sides):
            q = [rings[k][0][m], rings[k][0][(m + 1) % sides], rings[k + 1][0][(m + 1) % sides], rings[k + 1][0][m]]
            u0, u1 = m / sides * 0.5, (m + 1) / sides * 0.5
            uvs = [(u0, rings[k][1] / TILE), (u1, rings[k][1] / TILE), (u1, rings[k + 1][1] / TILE), (u0, rings[k + 1][1] / TILE)]
            if flip:
                q.reverse()
                uvs.reverse()
            kit.face(q, uvs, RUBBER, True, "conduit")
    for ring, rev in ((rings[0][0], not flip), (rings[-1][0], flip)):
        q = list(ring)
        if rev:
            q.reverse()
        kit.face(q, [(0.0, 0.0)] * sides, RUBBER, False, "conduit")


def plate(kit, frame, owner, a, s, width, height, lift, cell, material=FABRIC):
    """A small rectangular plate on the wall: width across, height along."""
    q0 = frame.sample(owner, a - width / 2, s)
    q1 = frame.sample(owner, a + width / 2, s)
    if q0 is None or q1 is None or abs(q0[2] - q1[2]) > 0.03:
        return False
    length = (q0[0] + q0[1] * q0[2] - q1[0] - q1[1] * q1[2]).length
    st = [(q[0] + q[1] * q[2], q[1], frame.ax, q[3]) for q in (q0, q1)]
    base = rect_uv(cell)

    def uv(side, along, across):
        return base(side, along / max(length, 1e-4), across)

    beam(kit, st, owner, height, 0.004, lift, material, uv, "plate")
    return True


def furnish_torso(kit, frame, owners, cfg, fixed):
    fw, fh = cfg["frame"]
    steel = metal_uv(fw)
    # Down the middle of the front stand the spine beam and the conduits beside it; the pads begin beyond them.
    conduit_room = fw * 0.65 + 0.008 + 0.021 * cfg["conduits"]
    stood = 0
    for i, (s0, s1, rh, pw, var, tone, t) in enumerate(cfg["torso"]):
        stood += pad_rows(kit, frame, owners, s0, s1, rh, pw, (var, tone), t, start=conduit_room if i else fw * 0.6)
    # The frames at the borders of the regions and the spine beam.
    edges = [cfg["torso"][0][1] + 0.02, cfg["torso"][1][1] + 0.02, cfg["torso"][2][1] + 0.015]
    for s in edges:
        along_ring(kit, frame, owners, s, fw, fh, 0.0, METAL, steel, "frame")
    along_axis(kit, frame, [fixed], 0.0, 0.04, 0.95, fw * 1.1, fh * 1.3, 0.0, METAL, steel, "spine")
    # Conduits beside the beam; webbing over the chest and the waist; small warning plates.
    for k in range(cfg["conduits"]):
        off = fw * 0.65 + 0.012 + k * 0.021
        for sign in (1.0, -1.0):
            conduit(kit, frame, fixed, sign * off, 0.25, 0.92, 0.0085, 0.0)
    web = webbing_uv()
    for sign in (1.0, -1.0):
        along_axis(kit, frame, [fixed], sign * (conduit_room + 0.09), 0.50, 0.93, 0.036, 0.004, cfg["torso"][2][6] + 0.002, FABRIC, web, "strap")
    along_ring(kit, frame, [fixed], (cfg["torso"][1][0] + cfg["torso"][1][1]) / 2, 0.042, 0.004, cfg["torso"][1][6] + 0.002, FABRIC, web, "strap", side_limit=0.42)
    for sign in (1.0, -1.0):
        plate(kit, frame, fixed, sign * (conduit_room + 0.05), edges[1], 0.07, fw * 0.8, fh, HAZARD)
        plate(kit, frame, fixed, sign * (conduit_room + 0.05), edges[0], 0.05, fw * 0.8, fh, PATCH)
    plate(kit, frame, fixed, 0.0, 0.88, 0.05, 0.036, fh * 1.3, STENCIL)
    # The cockpit's lights: two rails along the spine beam and a line round the waist frame, in the suit's colour.
    for sign in (1.0, -1.0):
        light_bar_axis(kit, frame, fixed, sign * fw * 0.35, 0.06, 0.93, fh * 1.3)
    along_ring(kit, frame, [fixed], edges[1], LIGHT_BAR, 0.003, fh, 0, lit_uv, "light", side_limit=0.3)
    return stood


def furnish_limb(kit, frame, owners, cfg, scale):
    fw, fh = cfg["frame"]
    h, w, var, tone, t = cfg["limb"]
    stood = pad_rows(kit, frame, owners, 0.08, 0.5, h * scale, w * scale, (var, tone), t * scale)
    stood += pad_rows(kit, frame, owners, 0.54, 0.92, h * scale, w * scale, (var, tone), t * scale)
    steel = metal_uv(fw)
    along_ring(kit, frame, owners, 0.52, fw * 0.75, fh * 0.8, 0.0, METAL, steel, "frame", min_len=0.05)
    along_ring(kit, frame, owners, 0.06, fw * 0.6, fh * 0.7, 0.0, METAL, steel, "frame", min_len=0.05)
    along_ring(kit, frame, owners, 0.94, fw * 0.6, fh * 0.7, 0.0, METAL, steel, "frame", min_len=0.05)
    light_bar_axis(kit, frame, owners[0], 0.0, 0.08, 0.92, 0.002)
    return stood


def furnish_plate(kit, frame, owner, cfg, chest):
    """A wide plate: rows of pads over it and one rail across its middle."""
    if frame.width < 0.08 or frame.L < 0.08:
        return 0
    fw, fh = cfg["frame"]
    if chest:
        _, _, rh, pw, var, tone, t = cfg["torso"][2]
    else:
        rh, pw, var, tone, t = cfg["limb"]
    gap = fw * 0.9 if frame.L > 0.2 else 0.0
    m = 0.5
    g = gap / 2 / frame.L
    stood = pad_rows(kit, frame, [owner], 0.03, m - g, rh, pw, (var, tone), t) + pad_rows(kit, frame, [owner], m + g, 0.97, rh, pw, (var, tone), t)
    if gap:
        along_ring(kit, frame, [owner], m, fw * 0.8, fh * 0.8, 0.0, METAL, metal_uv(fw), "frame", min_len=0.05)
    return stood


def mat_u(u):
    """The plain half of the rubber tile (u 0.5..1) tiles in v only; across, the wall folds to and fro inside it."""
    return 0.5 + 0.5 * abs((u * 2 % 2) - 1)


def soften(walls):
    """The wall is a soft skin, not the low-poly facets of its mesh: smooth shaded, keeping creases and its border with anything else."""
    for f in walls:
        f.smooth = True
        for e in f.edges:
            faces = e.link_faces
            e.smooth = len(faces) == 2 and all(g in walls for g in faces) and faces[0].normal.dot(faces[1].normal) > 0.55


def wall_uvs(bm, uv, walls, axes):
    """The rubber mat on every wall face: mapped round the nearest body part (its ribs run round it), or flat on a face square to it."""
    for f in walls:
        c = f.calc_center_median()
        ax = min(axes, key=lambda x: x.reach(c))
        n = f.normal
        f.material_index = RUBBER
        if abs(n.dot(ax.ax)) > 0.75:
            k = max(range(3), key=lambda i: abs(n[i]))
            a, b = [i for i in range(3) if i != k]
            for l in f.loops:
                l[uv].uv = (mat_u(l.vert.co[a] / TILE), l.vert.co[b] / TILE)
            continue
        first = None
        for l in f.loops:
            d = l.vert.co - ax.a
            th = math.atan2(d.dot(ax.u), -d.dot(ax.w))
            if first is None:
                first = th
            while th - first > math.pi:
                th -= 2 * math.pi
            while first - th > math.pi:
                th += 2 * math.pi
            l[uv].uv = (mat_u(th * max(ax.radius, 0.05) / TILE), d.dot(ax.ax) / TILE)


def seeing_walls(bm, uv, axes, walls):
    """Adds to `walls` every armour face that sees a body axis, in the shut suit, from where that axis is inside the shell: nothing on the
    outside can. (The escape test of build_frame.cavity_walls misses inner faces beside a seam.) Returns how many."""
    tree = BVHTree.FromBMesh(bm)
    dirs = [Vector(d) for d in ((1, 0, 0), (-1, 0, 0), (0, 1, 0), (0, -1, 0), (0.7, 0.7, 0), (-0.7, 0.7, 0), (0.7, -0.7, 0), (-0.7, -0.7, 0))]

    def inside(p):
        for d in dirs:
            hit = tree.ray_cast(p, d, 1.5)
            if hit[2] is None or hit[1].dot(d) > 0:
                return False
        return True

    stations = [(ax, [p for p in (ax.point(k / 12) for k in range(13)) if inside(p)]) for ax in axes]
    found = 0
    for f in bm.faces:
        if f in walls or f.material_index != 0 or tile_uv(f, uv, TRIM_U):
            continue
        c = f.calc_center_median()
        n = f.normal
        for ax, pts in sorted(stations, key=lambda q: q[0].reach(c))[:2]:
            if not pts:
                continue
            t = min(pts, key=lambda p: (p - c).length_squared)
            d = t - c
            length = d.length
            if length < 1e-3 or d.dot(n) <= 0.15 * length:
                continue
            d /= length
            if tree.ray_cast(c + n * 0.001, d, length)[2] is None:
                walls.add(f)
                found += 1
                break
    return found


def gaskets(bm, uv, rims, plates_faces):
    """Rims of opening parts, and rims of the shell beside one, become ribbed rubber; each mapped along its longest edge."""
    verts = list({v for f in plates_faces for v in f.verts})
    if not verts:
        return 0
    kd = KDTree(len(verts))
    for i, v in enumerate(verts):
        kd.insert(v.co, i)
    kd.balance()
    plate_set = set(plates_faces)
    done = 0
    for f in rims:
        if f not in plate_set and kd.find(f.calc_center_median())[2] > GASKET:
            continue
        e = max(f.edges, key=lambda x: x.calc_length())
        a = (e.verts[1].co - e.verts[0].co).normalized()
        o = e.verts[0].co
        b = f.normal.cross(a)
        f.material_index = RUBBER
        for l in f.loops:
            d = l.vert.co - o
            l[uv].uv = (0.25 + 0.2 * max(-1.0, min(1.0, d.dot(b) / TILE)), d.dot(a) / TILE)
        done += 1
    return done


def prune(bm, points):
    """A piece the wall's shape folded (a tiny triangle, or turned inside out) goes: the shell stays closed and outward. Returns how many went."""
    left = set(points)
    dropped = 0
    while left:
        stack = [left.pop()]
        piece = set(stack)
        while stack:
            for e in stack.pop().link_edges:
                w = e.verts[0] if e.verts[1] in piece else e.verts[1]
                if w not in piece:
                    piece.add(w)
                    left.discard(w)
                    stack.append(w)
        faces = {f for v in piece for f in v.link_faces}
        volume = 0.0
        tiny = False
        for f in faces:
            a, b, c = (v.co for v in f.verts)
            volume += a.dot(b.cross(c)) / 6.0
            tiny = tiny or f.calc_area() < TINY_AREA
        if tiny or volume <= 0.0:
            bmesh.ops.delete(bm, geom=list(piece), context="VERTS")
            dropped += 1
    return dropped


def dress(bm, deform, joints, torso_a, torso_b, plate_gids, model, log):
    """Dresses the shell in `bm` (triangles, material 1..3 lining, UVMap): see the module's docstring. plate_gids: the vertex
    groups of the opening parts."""
    cfg = SUITS[model]
    uv = bm.loops.layers.uv["UVMap"]
    bm.normal_update()
    bm.faces.ensure_lookup_table()
    walls, rims, by_owner = set(), [], {}
    plate_gids = set(plate_gids)
    for f in bm.faces:
        if f.material_index in (FABRIC, METAL, RUBBER) or (f.material_index == 0 and tile_uv(f, uv, CAVITY_U)):
            walls.add(f)
        elif f.material_index == 0 and tile_uv(f, uv, TRIM_U):
            rims.append(f)
        gid = next((g for g, w in f.verts[0][deform].items() if w >= 0.5 and g in plate_gids), None)
        by_owner.setdefault(gid, []).append(f)
    # The body parts.
    j = {k: Vector(v) for k, v in joints.items()}
    torso = Axis("torso", Vector(torso_a), Vector(torso_b), Vector((1, 0, 0)), 0.15)
    axes = [torso]
    for side, sign in (("L", 1.0), ("R", -1.0)):
        lat = Vector((sign, 0, 0))
        for limb, end, r in (("upperarm", "forearm", 0.06), ("forearm", "hand", 0.05), ("thigh", "shin", 0.085), ("shin", "foot", 0.06)):
            axes.append(Axis(limb + "." + side, j[limb + "." + side], j[end + "." + side], lat, r))
    seen = seeing_walls(bm, uv, axes, walls)
    wall_uvs(bm, uv, walls, axes)
    soften(walls)
    owners = {g: Owner(g, fs, walls) for g, fs in by_owner.items()}
    fixed = owners[None]
    all_owners = list(owners.values())
    plate_faces = [f for g, fs in by_owner.items() if g is not None for f in fs]
    gasket_count = gaskets(bm, uv, rims, plate_faces)
    kit = Kit(bm, uv, deform, all_owners, plate_gids)
    # A plate wrapped round its body part is furnished from the part's axis; a wide plate that lies aslant to the rays from it, from its own plane.
    around, wide = {ax: [fixed] for ax in axes}, []
    for gid, owner in owners.items():
        if gid is None:
            continue
        faces = [f for f in by_owner[gid] if f in walls]
        if not faces:
            continue
        faces = faces[::max(1, len(faces) // 300)]
        centre = sum((f.calc_center_median() for f in faces), Vector()) / len(faces)
        ax = min(axes, key=lambda x: x.reach(centre))
        total = square = 0.0
        for f in faces:
            c = f.calc_center_median()
            t = (c - ax.a).dot(ax.ax)
            radial = (c - (ax.a + ax.ax * t))
            if radial.length > 1e-4:
                area = f.calc_area()
                total += area
                square += area * abs(f.normal.dot(radial.normalized()))
        if total and square / total >= AXIS_SQUARE:
            around[ax].append(owner)
        elif total:
            wide.append((owner, faces, ax))
    stood = {}
    for ax in axes:
        frame = AxisFrame(ax, axes, all_owners)
        if ax is torso:
            stood[ax.name] = furnish_torso(kit, frame, around[ax], cfg, fixed)
        else:
            stood[ax.name] = furnish_limb(kit, frame, around[ax], cfg, 1.0 if ax.name[:5] in ("thigh", "shin.") else 0.8)
    stood["wide"] = sum(furnish_plate(kit, PlateFrame(owner, faces, ax.ax), owner, cfg, ax is torso) for owner, faces, ax in wide)
    log("interior: %d wide plates of %d" % (len(wide), len(owners) - 1))
    bmesh.ops.triangulate(bm, faces=[f for f in bm.faces if len(f.verts) > 3], quad_method="BEAUTY", ngon_method="BEAUTY")
    kit.counts["dropped"] = prune(bm, kit.points)
    log("interior: %d wall faces (%d of them by seeing a body axis), %d gaskets, pads %s, added %s" % (len(walls), seen, gasket_count, stood, kit.counts))


# ---------------------------------------------------------------------------------------------------------------- the textures
def _noise(n, cells, rng):
    g = rng.random((cells, cells), dtype=np.float32)
    pos = np.arange(n, dtype=np.float32) * (cells / n)
    i = pos.astype(np.int64)
    f = pos - i
    f = f * f * (3 - 2 * f)
    rows = g[:, i] * (1 - f) + g[:, (i + 1) % cells] * f
    return rows[i] * (1 - f)[:, None] + rows[(i + 1) % cells] * f[:, None]


def _normal(height, strength):
    gx = (np.roll(height, -1, axis=1) - np.roll(height, 1, axis=1)) * 0.5 * strength
    gy = (np.roll(height, -1, axis=0) - np.roll(height, 1, axis=0)) * 0.5 * strength
    n = np.stack([-gx, -gy, np.ones_like(gx)], axis=-1)
    n /= np.linalg.norm(n, axis=-1, keepdims=True)
    return n * 0.5 + 0.5


def _pad_cell(n, variant, rng):
    """One pad face (n x n): (height 0..1, shade), its stitching near the rim and its own quilting."""
    y, x = np.mgrid[0:n, 0:n].astype(np.float32) / (n - 1)
    edge = np.minimum(np.minimum(x, 1 - x), np.minimum(y, 1 - y))
    # The seam round the rim: a groove with dashed thread beside it.
    groove = np.exp(-((edge - 0.055) / 0.013) ** 2)
    along = np.where(np.minimum(x, 1 - x) < np.minimum(y, 1 - y), y, x)
    thread = np.exp(-((edge - 0.095) / 0.007) ** 2) * (np.sin(along * math.pi * 44) > -0.2)
    quilt = np.zeros_like(x)
    puff = np.ones_like(x)
    if variant == DIAMOND:
        a, b = ((x + y) * 2.5) % 1.0, ((x - y) * 2.5) % 1.0
        d = np.minimum(np.minimum(a, 1 - a), np.minimum(b, 1 - b))
        quilt = np.exp(-(d / 0.06) ** 2)
        puff = np.sin(np.pi * a) ** 0.5 * np.sin(np.pi * b) ** 0.5
    elif variant == VCHANNEL:
        a = (x * 4) % 1.0
        quilt = np.exp(-(np.minimum(a, 1 - a) / 0.06) ** 2)
        puff = np.sin(np.pi * a) ** 0.5
    elif variant == HCHANNEL:
        a = (y * 3) % 1.0
        quilt = np.exp(-(np.minimum(a, 1 - a) / 0.06) ** 2)
        puff = np.sin(np.pi * a) ** 0.5
    else:
        # Box: one bar tack across the middle of a plain pad.
        quilt = np.exp(-((np.abs(x - 0.5) + np.abs(y - 0.5) - 0.05).clip(0) / 0.03) ** 2) * 0.5
        puff = 1.0 - quilt * 0.5
    inner = np.clip(edge / 0.055, 0, 1)
    height = (0.55 * puff + 0.45) * inner - 0.6 * groove - 0.5 * quilt + 0.12 * thread
    shade = 0.74 + 0.30 * puff * inner - 0.36 * groove - 0.16 * quilt + 0.30 * thread
    shade *= np.clip(0.55 + edge / 0.07, 0.55, 1.0)
    return height, shade


def fabric_common():
    """The atlas shared by every suit: (height map, shade map, material masks)."""
    n = FABRIC_SIZE
    cell = n // 4
    rng = np.random.default_rng(1723)
    height = np.zeros((n, n), np.float32)
    shade = np.ones((n, n), np.float32)
    tone = np.zeros((n, n), np.int32)      # 0 A, 1 B, 2 webbing, 3 accent, 4 dark patch, 5 stencil
    smooth = np.full((n, n), 0.16, np.float32)
    for row, t in ((TONE_A, 0), (TONE_B, 1)):
        for col in range(4):
            h, s = _pad_cell(cell, col, rng)
            y0, x0 = row * cell, col * cell
            height[y0:y0 + cell, x0:x0 + cell] = h
            shade[y0:y0 + cell, x0:x0 + cell] = s
            tone[y0:y0 + cell, x0:x0 + cell] = t
    y, x = np.mgrid[0:cell, 0:cell].astype(np.float32) / (cell - 1)
    # Row 1: the hazard plate (chevrons), the stencil plate (bars), the plain tab, the dark hook-and-loop patch.
    stripes = ((x * 3 + y * 1.5) % 1.0) < 0.5
    border = np.minimum(np.minimum(x, 1 - x), np.minimum(y, 1 - y))
    tone[cell:2 * cell, 0:cell] = np.where(stripes & (border > 0.05), 3, 4)
    shade[cell:2 * cell, 0:cell] = 0.95
    bars = (((y * 5) % 1.0) < 0.45) & (x > 0.12) & (x < 0.88 - 0.35 * ((np.floor(y * 5) * 7) % 3 == 0)) & (y > 0.12) & (y < 0.88)
    tone[cell:2 * cell, cell:2 * cell] = np.where(bars, 5, 4)
    shade[cell:2 * cell, cell:2 * cell] = 1.0
    tone[cell:2 * cell, 2 * cell:3 * cell] = 3
    shade[cell:2 * cell, 2 * cell:3 * cell] = 0.9
    tone[cell:2 * cell, 3 * cell:4 * cell] = 4
    hook = 0.5 + 0.5 * np.sin(x * math.pi * 60) * np.sin(y * math.pi * 60)
    height[cell:2 * cell, 3 * cell:4 * cell] = 0.3 * hook
    shade[cell:2 * cell, 3 * cell:4 * cell] = 0.7 + 0.5 * hook
    smooth[cell:2 * cell, 0:3 * cell] = 0.42
    # Row 0: the webbing, wrapping across: a herringbone weave between two selvedges.
    yy, xx = np.mgrid[0:cell, 0:n].astype(np.float32)
    herring = np.abs(((yy / cell) * 10) % 2 - 1)
    twill = np.sin((xx / n * 64 + herring * 2.5) * 2 * math.pi)
    selv = np.minimum(yy, cell - 1 - yy) / cell
    side = np.exp(-((selv - 0.08) / 0.015) ** 2)
    height[0:cell, :] = 0.4 * (twill * 0.5 + 0.5) - 0.4 * side
    shade[0:cell, :] = 0.8 + 0.28 * (twill * 0.5 + 0.5) - 0.3 * side
    tone[0:cell, :] = 2
    # A plain weave over all the cloth, in the pads' 256 px per cell.
    threads = 200
    fx, fy = (np.arange(n, dtype=np.float32)[None, :] / n * threads) % 1.0, (np.arange(n, dtype=np.float32)[:, None] / n * threads) % 1.0
    over = ((np.floor(np.arange(n)[None, :] / n * threads) + np.floor(np.arange(n)[:, None] / n * threads)) % 2) == 0
    weave = np.where(over, np.sin(np.pi * fx) ** 0.7, np.sin(np.pi * fy) ** 0.7).astype(np.float32)
    grain = 0.6 * _noise(n, 64, rng) + 0.4 * _noise(n, 256, rng)
    cloth = np.isin(tone, (0, 1))
    fine = 0.10 * weave * cloth + 0.05 * grain
    height = height + fine * 0.5
    shade = shade * (1.0 + (0.26 * (weave - 0.6)) * cloth + 0.12 * (grain - 0.5))
    smooth = smooth + 0.08 * (grain - 0.5)
    return height.astype(np.float32), np.clip(shade, 0, 1.6).astype(np.float32), tone, np.clip(smooth, 0, 1).astype(np.float32)


def write_textures(model, out, save_png):
    """<model>_lining_albedo.png, the suit's own, and the shared lining_normal, lining_metallic and lining_metal_*, lining_rubber_* maps."""
    height, shade, tone, smooth = fabric_common()
    cfg = SUITS[model]
    # sRGB colours, so a dark tone keeps its hue: shade multiplies in that space.
    table = np.array([cfg["a"], cfg["b"], cfg["web"], cfg["accent"], (0.035, 0.035, 0.037), (0.62, 0.62, 0.58)], np.float32)
    albedo = np.clip(table[tone] * shade[..., None], 0.0, 1.0)
    one = np.ones(albedo.shape[:2] + (1,), np.float32)
    save_png(os.path.join(out, "%s_lining_albedo.png" % model), np.concatenate([albedo, one], axis=-1), True, False)
    zero = np.zeros_like(smooth)
    save_png(os.path.join(out, "lining_normal.png"), np.concatenate([_normal(height, 16.0), one], axis=-1), False, False)
    save_png(os.path.join(out, "lining_metallic.png"), np.stack([zero, smooth, zero, smooth], axis=-1), False, True)

    n = TILE_SIZE
    one = np.ones((n, n, 1), np.float32)
    rng = np.random.default_rng(4242)
    y, x = np.mgrid[0:n, 0:n].astype(np.float32) / n
    # Metal: brushed along the tile, a few scuffs; a line of rivets every quarter tile down the middle of a frame's top.
    brushed = np.mean([np.roll(_noise(n, 256, rng), s, axis=1) for s in (0, 7, 15, 31, 47, 63)], axis=0)
    brushed = (brushed - brushed.mean()) / (brushed.std() + 1e-6)
    blot = _noise(n, 12, rng)
    dx = (x * 4 % 1.0) - 0.5
    dy = (y - 0.5) * 4
    d = np.sqrt(dx * dx + dy * dy)
    rivet = np.clip(1 - d / 0.13, 0, 1)
    collar = np.exp(-((d - 0.15) / 0.03) ** 2)
    chamfer = np.exp(-((np.minimum(np.abs(y - 0.25), np.abs(y - 0.75))) / 0.012) ** 2)
    h = 0.03 * brushed + 1.5 * np.sqrt(rivet) - 0.3 * collar - 0.6 * chamfer
    v = 0.30 + 0.025 * brushed + 0.05 * (blot - 0.5) + 0.03 * (rivet > 0) - 0.08 * collar - 0.05 * chamfer
    albedo = np.clip(np.stack([v * 0.98, v, v * 1.03], axis=-1), 0.0, 1.0)
    sm = np.clip(0.38 + 0.05 * brushed + 0.16 * (blot - 0.5), 0.05, 0.9)
    metal = np.full_like(v, 0.7)
    for kind, px, colour, alpha in (("albedo", np.concatenate([albedo, one], axis=-1), True, False),
                                    ("normal", np.concatenate([_normal(h, 6.0), one], axis=-1), False, False),
                                    ("metallic", np.stack([metal, sm, np.zeros_like(sm), sm], axis=-1), False, True)):
        save_png(os.path.join(out, "lining_metal_%s.png" % kind), px, colour, alpha)
    # Rubber, left half: 32 soft ribs a tile, across v (hoses, gaskets). Right half: a plain mat with a fine moulded grid (the wall), a little darker.
    ribbed = x < 0.5
    rib = np.sin(np.pi * ((y * 32) % 1.0)) ** 1.5
    grain = 0.6 * _noise(n, 128, rng) + 0.4 * _noise(n, 32, rng)
    dots = np.sin(np.pi * ((y * 48) % 1.0)) * np.sin(np.pi * ((x * 48) % 1.0))
    # The mat is sewn in panels, 8 cm square (the fold of mat_u keeps them whole), so no wall reads as flat.
    seam_x = np.minimum(np.abs(x - 0.5), np.abs(x - 1.0)) * TILE
    seam_y = np.minimum(np.minimum(y, np.abs(y - 0.5)), np.abs(y - 1.0)) * TILE
    seam = np.minimum(seam_x, seam_y)
    groove = np.exp(-(seam / 0.0035) ** 2)
    stitch = np.exp(-((seam - 0.007) / 0.0012) ** 2) * (np.sin(np.where(seam_x < seam_y, y, x) * math.pi * 40) > 0)
    h = np.where(ribbed, rib + 0.12 * grain, 0.06 * dots + 0.08 * grain - 2.2 * groove + 0.5 * stitch)
    v = np.where(ribbed, 0.13 + 0.06 * rib, 0.135 + 0.010 * dots - 0.06 * groove + 0.06 * stitch) + 0.02 * (grain - 0.5)
    albedo = np.clip(np.stack([v, v, v * 1.04], axis=-1), 0.0, 1.0)
    sm = np.clip(np.where(ribbed, 0.22 + 0.2 * rib, 0.14 + 0.03 * dots) + 0.05 * (grain - 0.5), 0, 1)
    for kind, px, colour, alpha in (("albedo", np.concatenate([albedo, one], axis=-1), True, False),
                                    ("normal", np.concatenate([_normal(h, 3.0), one], axis=-1), False, False),
                                    ("metallic", np.stack([np.zeros_like(sm), sm, np.zeros_like(sm), sm], axis=-1), False, True)):
        save_png(os.path.join(out, "lining_rubber_%s.png" % kind), px, colour, alpha)
