"""The exosuits' lights (build_frame.py calls paint on the finished shell, before the rig): light strips along the shell's
structural lines and a glowing visor, painted into the textures the bundle ships (no geometry, no second material, no light).
emission.png holds the strips at STRIP_LEVEL and the visor up to 1 (the head is the brightest light), in grey: the game tints the
whole map per suit and per state through the gear shader's _EmissiveColor (Core FrameGlow). Under every strip albedo.png and
metallic.png get a dark glossy insert, so a wrecked suit's strips read as dark glass. The source model's own lamps are kept only
on the head; elsewhere they would be stray dots.

The lines are few, long and placed on purpose, each a rail grown from the shell's structural lines:
  - a structural line is a chain of the outer armour's crease edges (two armour faces bent CREASE to CREASE_MAX degrees), smooth
    (no turn sharper than TURN), at least SHORTEST m long;
  - every slot (SLOTS: a body part, a side, front or back) takes the chain that best fits it (on a limb: the most length along
    the limb's axis; on the trunk: the longest) and grows it into a rail: from either end, the next chain that carries on in the
    same direction within REACH_ON m (across a plate's seam, onto the next limb segment) is joined, and the gap between them is
    painted on the two pieces' own surfaces up to their edges, so the line runs on across the seam;
  - a limb rail on one segment only takes the chain on the limb's other segment at the same place around the limb (pair), so it`r`n    runs from shoulder to wrist or hip to ankle;`r`n  - a rail shorter than RAIL_SHORTEST m is dropped, and so is any chain mostly within SPACING of a rail already taken.
Left and right, front and back are chosen each on their own (nothing is mirrored); every slot exists on both sides, so the two
sides carry about the same light. A strip is painted only on the pieces of its own rail (a part and what it sits on never share
an edge), so it opens with its part and never stays behind. Faces mapped to the atlas's reserved strip (the cavity and the cut
trim) are never painted.

The visor, the suit's brightest light and its focus: where the source head has lamps of its own (a lit visor, eyes) they glow, up to
LAMP_LEVEL; else the largest dark patch of the helmet's face at eye height (the glass) glows at VISOR_LEVEL with a full eye band
through its middle; else a slit is cut across the helmet's face at eye height at full brightness."""
import math
import os

import bpy
import numpy as np
from mathutils import Vector
from mathutils.bvhtree import BVHTree
from mathutils.kdtree import KDTree

CREASE = 25.0          # deg: an edge bent at least this much is a crease
CREASE_MAX = 120.0     # deg: sharper than this is a corner of a thin slab, not a panel line
TURN = 42.0            # deg: a chain does not turn sharper than this at a joint
SHORTEST = 0.15        # m: the shortest chain worth a light
REACH_ON = 0.10        # m: a rail carries on to a chain whose end is this close to its own end
CARRY_ANGLE = 35.0     # deg: and whose direction (and the step to it) stays this close to the rail's
RAIL_SHORTEST = 0.30   # m: a limb rail shorter than this is no line (trunk rails: SHORTEST)
PAIR_SHORTEST = 0.12   # m: the least length along the limb of the chain a limb rail takes on the limb's other segment
PAIR_ANGLE = 40.0      # deg: and how far around the limb from the rail it may sit
SPACING = 0.03        # m: a chain mostly this close to a rail taken is the same line
CORE = 0.0045          # m: half the strip's lit width
HALO = 0.0075          # m: the soft edge beyond the core (falls to nothing)
HALO_LEVEL = 0.5       # the soft edge's brightness next to the core, of the core's
STRIP_LEVEL = 0.8      # the strips in the emission map (Core FrameGlow.StripLevel); the visor reaches 1
VISOR_LEVEL = 0.55     # dark visor glass, its eye band 1
LAMP_LEVEL = 0.9      # a head's own lamps at their brightest (a large lit visor at 1 blooms to white)
VISOR_LAMPS = 300      # texels: a head with this much light of its own (a lit visor, eyes) glows there
EYE = 0.55             # eye height between the neck and the top of the head
GLASS_DARK = 0.10      # albedo luminance: visor glass is at most this dark (or the darkest quarter of the face, if darker)
GLASS_AREA = 0.0015    # m²: the least glass that is a visor
SLIT_HALF = 0.008      # m: half the height of the eye slit on a helmet without glass
SLIT_WIDE = 0.065      # m: half its width
INSERT = 0.006         # m: half the width of the dark glass insert under the strip
STEP = 0.001           # m: sample spacing along a line
TILE_V = 0.965         # the atlas's reserved strip starts here (build_frame.py TILE_V)
LIMBS = {"arm": ("upperarm", "forearm"), "leg": ("thigh", "shin")}
AXES = {"upperarm": ("upperarm", "forearm"), "forearm": ("forearm", "hand"), "thigh": ("thigh", "shin"), "shin": ("shin", "foot")}
# Slots: (group, facing, rails). Every slot is filled on each side on its own.
SLOTS = [("chest", "F", 2), ("back", "B", 2), ("pelvis", "F", 1), ("pelvis", "B", 1),
         ("arm", "F", 1), ("arm", "B", 1), ("leg", "F", 1), ("leg", "B", 2)]


def read(path):
    """A PNG as (rows from the bottom, columns, RGBA) floats, as stored (no colour management)."""
    img = bpy.data.images.load(path, check_existing=False)
    img.colorspace_settings.name = "Non-Color"
    w, h = img.size
    px = np.empty(w * h * 4, np.float32)
    img.pixels.foreach_get(px)
    bpy.data.images.remove(img)
    return px.reshape(h, w, 4)


def write(path, pixels):
    h, w = pixels.shape[:2]
    img = bpy.data.images.new("glow-out", w, h, alpha=True)
    img.colorspace_settings.name = "Non-Color"
    img.pixels.foreach_set(np.clip(pixels, 0.0, 1.0).astype(np.float32).ravel())
    img.filepath_raw = path
    img.file_format = "PNG"
    img.save()
    bpy.data.images.remove(img)


def part_of(bone):
    """plate.forearm.L.2 -> forearm; plate.back.R.1 -> back; chest -> chest."""
    parts = bone.split(".")
    if parts[0] == "plate":
        parts = parts[1:]
    return parts[0]


def piece_ids(bm):
    ids = {}
    n = 0
    for f in bm.faces:
        if f in ids:
            continue
        ids[f] = n
        stack = [f]
        while stack:
            g = stack.pop()
            for e in g.edges:
                for h in e.link_faces:
                    if h not in ids:
                        ids[h] = n
                        stack.append(h)
        n += 1
    return ids


def tree_of(points):
    t = KDTree(max(1, len(points)))
    for i, p in enumerate(points):
        t.insert(p, i)
    t.balance()
    return t


def samples(a, b):
    n = max(1, int((b - a).length / STEP))
    return [a.lerp(b, i / n) for i in range(n + 1)]


def paint(bm, groups, joints, sources, textures, log):
    """bm: the shut, triangulated shell (material 0 the armour, UV layer UVMap, deform weights); groups: vertex group index ->
    bone name; joints: joint name -> Vector (the rig's); sources and textures: {kind: png path} of the albedo, metallic and
    emission maps, read from sources and written to textures (never the same file: painting twice would paint over the strips).
    Returns the rails taken (for previews)."""
    for kind in ("albedo", "metallic", "emission"):
        if os.path.abspath(sources[kind]) == os.path.abspath(textures[kind]):
            raise SystemExit("light strips: %s would be painted in place (%s)" % (kind, textures[kind]))
    uv = bm.loops.layers.uv["UVMap"]
    deform = bm.verts.layers.deform.verify()
    bm.faces.ensure_lookup_table()
    emission = read(sources["emission"])
    albedo = read(sources["albedo"])
    metal = read(sources["metallic"])
    H, W = emission.shape[:2]

    def lit(f):
        return f.material_index == 0 and all(l[uv].uv.y < TILE_V for l in f.loops)

    def bone_of(f):
        w = {}
        for v in f.verts:
            for k, x in v[deform].items():
                w[k] = w.get(k, 0.0) + x
        return groups[max(w, key=w.get)] if w else "pelvis"

    piece = piece_ids(bm)
    lit_faces = [f for f in bm.faces if lit(f)]
    bone = {f: bone_of(f) for f in lit_faces}

    # ---- structural lines
    crease = []
    for e in bm.edges:
        if len(e.link_faces) != 2:
            continue
        a, b = e.link_faces
        if a not in bone or b not in bone or piece[a] != piece[b]:
            continue
        if CREASE <= math.degrees(a.normal.angle(b.normal, 0.0)) <= CREASE_MAX:
            crease.append(e)
    on = set(crease)
    used = set()

    def extend(chain, v, e):
        while True:
            d0 = (v.co - e.other_vert(v).co).normalized()
            best, turn = None, TURN
            for g in v.link_edges:
                if g is e or g not in on or g in used:
                    continue
                t = math.degrees(d0.angle((g.other_vert(v).co - v.co).normalized(), math.pi))
                if t < turn:
                    best, turn = g, t
            if best is None:
                return
            used.add(best)
            chain.append(best)
            e, v = best, best.other_vert(v)

    chains = []
    for e in sorted(crease, key=lambda e: -e.calc_length()):
        if e in used:
            continue
        used.add(e)
        fwd, back = [], []
        extend(fwd, e.verts[1], e)
        extend(back, e.verts[0], e)
        edges = back[::-1] + [e] + fwd
        # The chain's points in order.
        if len(edges) == 1:
            pts = [e.verts[0].co.copy(), e.verts[1].co.copy()]
        else:
            first = edges[0]
            shared = set(first.verts) & set(edges[1].verts)
            v = first.other_vert(next(iter(shared))) if shared else first.verts[0]
            pts = [v.co.copy()]
            for x in edges:
                v = x.other_vert(v)
                pts.append(v.co.copy())
        length = sum((pts[i + 1] - pts[i]).length for i in range(len(pts) - 1))
        if length < SHORTEST:
            continue
        faces = [f for x in edges for f in x.link_faces]
        f = faces[len(faces) // 2]
        b = bone[f]
        part = part_of(b)
        if part in ("hand", "foot", "head"):
            continue
        normal = sum((g.normal for g in faces), Vector()).normalized()
        mid = sum(pts, Vector()) / len(pts)
        named = b.split(".")[2] if b.startswith("plate.") else (b.split(".")[1] if "." in b else "")
        side = named if named in ("L", "R") else ("L" if mid.x > 0 else "R")
        facing = "F" if normal.y < 0 else "B"
        group = next((g for g, segs in LIMBS.items() if part in segs), None)
        if group is None:
            if part == "back":
                group, facing = "back", "B"
            elif part == "pelvis":
                group = "pelvis"
            else:
                group = "chest" if facing == "F" else "back"
        chains.append({"pts": pts, "length": length, "part": part, "group": group, "side": side, "facing": facing,
                       "piece": piece[f], "mid": mid, "taken": False})

    def axis(part, side):
        a, b = AXES[part]
        return (joints[b + "." + side] - joints[a + "." + side]).normalized()

    def fit(c):
        """How well a chain fills its slot: length along the limb on a limb, its length on the trunk."""
        if c["part"] in AXES:
            ax = axis(c["part"], c["side"])
            return sum(abs((c["pts"][i + 1] - c["pts"][i]).dot(ax)) for i in range(len(c["pts"]) - 1))
        return c["length"]

    rails = []
    taken_pts = []

    cache = {"n": -1, "tree": None}

    def near_taken(c):
        if not taken_pts:
            return False
        if cache["n"] != len(taken_pts):
            cache["n"], cache["tree"] = len(taken_pts), tree_of(taken_pts)
        t = cache["tree"]
        probe = c["pts"][:: max(1, len(c["pts"]) // 12)]
        return sum(1 for p in probe if t.find(p)[2] < SPACING) > 0.4 * len(probe)

    def grow(rail, pool):
        """Joins the chains that carry the rail on at either end."""
        while True:
            best = None
            for end in (0, 1):
                tip = rail["pts"][-1] if end else rail["pts"][0]
                inward = rail["pts"][-2] if end else rail["pts"][1]
                k = len(rail["pts"]) - 1 if end else 0
                # The rail's direction at its end, over its last few centimetres.
                j = k
                while 0 < j < len(rail["pts"]) - 1 and (rail["pts"][j] - tip).length < 0.04:
                    j += -1 if end else 1
                d = (tip - rail["pts"][j]).normalized() if (tip - rail["pts"][j]).length > 1e-6 else (tip - inward).normalized()
                for c in pool:
                    if c["taken"]:
                        continue
                    for flip in (False, True):
                        pts = c["pts"][::-1] if flip else c["pts"]
                        gap = pts[0] - tip
                        if gap.length > REACH_ON:
                            continue
                        step = (pts[min(len(pts) - 1, 8)] - pts[0]).normalized()
                        if math.degrees(d.angle(step, math.pi)) > CARRY_ANGLE:
                            continue
                        if gap.length > 0.01 and math.degrees(d.angle(gap.normalized(), math.pi)) > CARRY_ANGLE + 10:
                            continue
                        if best is None or gap.length < best[0]:
                            best = (gap.length, end, c, pts)
            if best is None:
                return
            _, end, c, pts = best
            c["taken"] = True
            rail["chains"].append(c)
            if end:
                rail["bridges"].append((rail["pts"][-1], pts[0], rail["tail"], c["piece"]))
                rail["pts"] = rail["pts"] + pts
                rail["tail"] = c["piece"]
            else:
                rev = pts[::-1]
                rail["bridges"].append((rev[-1], rail["pts"][0], c["piece"], rail["head"]))
                rail["pts"] = rev + rail["pts"]
                rail["head"] = c["piece"]

    def around(c):
        """Where a limb chain sits around its segment: the unit direction from the segment's axis to the chain's middle."""
        a, _ = AXES[c["part"]]
        ax = axis(c["part"], c["side"])
        r = sum(c["pts"], Vector()) / len(c["pts"]) - joints[a + "." + c["side"]]
        r -= ax * r.dot(ax)
        return r.normalized() if r.length > 1e-6 else ax

    def pair(rail, pool):
        """A limb rail on one segment takes the chain on the limb's other segment that sits at the same place around the limb
        (within PAIR_ANGLE), so the line runs on past the joint; a gap of up to REACH_ON between them is painted across."""
        seed = rail["chains"][0]
        other = next(s for s in LIMBS[seed["group"]] if s != seed["part"])
        where = around(seed)
        best = None
        for d in pool:
            if d["part"] != other or fit(d) < PAIR_SHORTEST:
                continue
            turn = math.degrees(where.angle(around(d), math.pi))
            if turn > PAIR_ANGLE:
                continue
            score = fit(d) - 0.004 * turn
            if best is None or score > best[0]:
                best = (score, d)
        if best is None:
            return
        d = best[1]
        d["taken"] = True
        rail["chains"].append(d)
        ends = [(a, b) for a in (rail["pts"][0], rail["pts"][-1]) for b in (d["pts"][0], d["pts"][-1])]
        a, b = min(ends, key=lambda e: (e[0] - e[1]).length)
        if (a - b).length <= REACH_ON:
            rail["bridges"].append((a, b, seed["piece"], d["piece"]))
        rail["pts"] = rail["pts"] + list(d["pts"])

    report = []
    for group, facing, count in SLOTS:
        for side in ("L", "R"):
            pool = [c for c in chains if c["group"] == group and c["side"] == side and c["facing"] == facing]
            if not pool and (group, facing) == ("pelvis", "B"):
                # A pelvis with no crease of its own at the back: its line is the lowest of the back's, below the middle of the spine.
                waist = joints["spine"].z + 0.5 * (joints["chest"].z - joints["spine"].z)
                pool = [c for c in chains if c["group"] == "back" and c["side"] == side and c["mid"].z < waist]
            got = 0
            for c in sorted(pool, key=lambda c: -fit(c)):
                if got >= count:
                    break
                if c["taken"] or near_taken(c):
                    continue
                c["taken"] = True
                rail = {"pts": list(c["pts"]), "chains": [c], "bridges": [], "length": c["length"], "slot": (group, facing, side),
                        "head": c["piece"], "tail": c["piece"]}
                grow(rail, [x for x in chains if x["side"] == side and x["group"] == c["group"] and not near_taken(x)])
                if group in LIMBS and len({x["part"] for x in rail["chains"]}) == 1:
                    pair(rail, [x for x in pool if not x["taken"] and not near_taken(x)])
                total = sum(x["length"] for x in rail["chains"])
                if group in LIMBS and total < RAIL_SHORTEST:
                    for x in rail["chains"][1:]:
                        x["taken"] = False
                    continue
                rail["length"] = total
                rails.append(rail)
                taken_pts.extend(rail["pts"])
                got += 1
            report.append("%s%s.%s %d" % (group, facing, side, got))
    log("light strips: %d crease edges, %d chains of %.2f m or more, %d rails, %.1f m of light (%s)" % (
        len(crease), len(chains), SHORTEST, len(rails), sum(r["length"] for r in rails), ", ".join(report)))
    log("light strips by side: L %.2f m, R %.2f m; front %.2f m, back %.2f m" % tuple(
        sum(r["length"] for r in rails if test(r)) for test in (lambda r: r["slot"][2] == "L", lambda r: r["slot"][2] == "R",
                                                                 lambda r: r["slot"][1] == "F", lambda r: r["slot"][1] == "B")))

    # ---- paint
    strength = np.zeros((H, W), np.float32)
    insert = np.zeros((H, W), bool)
    by_piece = {}
    for f in lit_faces:
        by_piece.setdefault(piece[f], []).append(f)
    vert_tree = {}

    def faces_near(points, pieces):
        out = set()
        for pc in pieces:
            if pc not in vert_tree:
                vs = list({v for f in by_piece.get(pc, []) for v in f.verts})
                vert_tree[pc] = (tree_of([v.co for v in vs]), vs)
            t, vs = vert_tree[pc]
            for p in points[::4]:
                for _, i, _ in t.find_range(p, CORE + HALO + 0.03):
                    for f in vs[i].link_faces:
                        if f in bone and piece[f] == pc:
                            out.add(f)
        return out

    def raster(f):
        """The texels inside face f: (xs, ys, world points)."""
        P = np.array([list(v.co) for v in f.verts], np.float64)
        T = np.array([[l[uv].uv.x * W, l[uv].uv.y * H] for l in f.loops], np.float64)
        x0, y0 = np.floor(T.min(axis=0)).astype(int)
        x1, y1 = np.ceil(T.max(axis=0)).astype(int)
        x0, y0, x1, y1 = max(x0, 0), max(y0, 0), min(x1, W - 1), min(y1, H - 1)
        if x1 < x0 or y1 < y0:
            return None
        gx, gy = np.meshgrid(np.arange(x0, x1 + 1) + 0.5, np.arange(y0, y1 + 1) + 0.5)
        (ax, ay), (bx, by), (cx, cy) = T
        den = (by - cy) * (ax - cx) + (cx - bx) * (ay - cy)
        if abs(den) < 1e-12:
            return None
        l1 = ((by - cy) * (gx - cx) + (cx - bx) * (gy - cy)) / den
        l2 = ((cy - ay) * (gx - cx) + (ax - cx) * (gy - cy)) / den
        l3 = 1.0 - l1 - l2
        # Half a texel of slack, so a strip has no gaps along the triangles' shared edges.
        slack = 0.5 / max(1.0, min(np.linalg.norm(T[0] - T[1]), np.linalg.norm(T[1] - T[2]), np.linalg.norm(T[2] - T[0])))
        inside = (l1 >= -slack) & (l2 >= -slack) & (l3 >= -slack)
        if not inside.any():
            return None
        ys, xs = np.nonzero(inside)
        world = np.stack([l1[inside], l2[inside], l3[inside]], axis=1) @ P
        return xs + x0, ys + y0, world

    reach = CORE + HALO
    for rail in rails:
        lines = []
        for c in rail["chains"]:
            pts = []
            for i in range(len(c["pts"]) - 1):
                pts += samples(c["pts"][i], c["pts"][i + 1])
            lines.append((pts, {c["piece"]}))
        for a, b, pa, pb in rail["bridges"]:
            # The step across a seam, laid on the two pieces' own surfaces.
            faces = by_piece.get(pa, []) + (by_piece.get(pb, []) if pb != pa else [])
            surf = BVHTree.FromPolygons([v.co for f in faces for v in f.verts], [[3 * i, 3 * i + 1, 3 * i + 2] for i in range(len(faces))])
            pts = []
            for p in samples(a, b):
                hit = surf.find_nearest(p)
                if hit[0] is not None:
                    pts.append(hit[0])
            lines.append((pts, {pa, pb}))
        for pts, pieces in lines:
            if not pts:
                continue
            tree = tree_of(pts)
            for f in faces_near(pts, pieces):
                r = raster(f)
                if r is None:
                    continue
                xs, ys, world = r
                for k in range(len(xs)):
                    d = tree.find(Vector(world[k]))[2]
                    if d >= reach:
                        continue
                    s = 1.0 if d <= CORE else HALO_LEVEL * (1.0 - (d - CORE) / HALO) ** 1.5
                    if s > strength[ys[k], xs[k]]:
                        strength[ys[k], xs[k]] = s
                    if d <= INSERT:
                        insert[ys[k], xs[k]] = True

    # ---- the visor: the head's own lamps where the source has a lit visor or eyes; else its dark glass at eye height; else a
    # slit across the helmet's face.
    head = [f for f in lit_faces if part_of(bone[f]) == "head"]
    lamps = emission[:, :, :3].max(axis=2)
    keep = np.zeros((H, W), bool)
    visor = np.zeros((H, W), np.float32)
    lum = albedo[:, :, :3] @ np.array([0.2126, 0.7152, 0.0722], np.float32)
    rows = []
    for f in head:
        r = raster(f)
        if r is None:
            continue
        keep[r[1], r[0]] = True
        rows.append((f,) + r)
    eye = joints["neck"].z + EYE * (joints["headtip"].z - joints["neck"].z)
    own = (lamps > 0.02) & keep
    how = ""
    if own.sum() >= VISOR_LAMPS:
        top = float(lamps[own].max())
        visor[own] = np.clip(lamps[own] / top, 0.0, 1.0) * LAMP_LEVEL
        how = "the head's own lamps (%d texels)" % int(own.sum())
    else:
        front = [x for x in rows if x[0].normal.y < -0.35 and abs(x[3][:, 2].mean() - eye) < 0.09 and abs(x[3][:, 0].mean()) < 0.12]
        dark = {}
        if front:
            levels = np.array([lum[x[2], x[1]].mean() for x in front])
            cut = min(GLASS_DARK, float(np.percentile(levels, 25)))
            dark = {x[0]: x for x, v in zip(front, levels) if v <= cut}
        best, seen = [], set()
        for f in dark:
            if f in seen:
                continue
            comp, stack = [], [f]
            seen.add(f)
            while stack:
                h = stack.pop()
                comp.append(h)
                for e in h.edges:
                    for n in e.link_faces:
                        if n in dark and n not in seen:
                            seen.add(n)
                            stack.append(n)
            if sum(x.calc_area() for x in comp) > sum(x.calc_area() for x in best):
                best = comp
        area = sum(x.calc_area() for x in best)
        if area >= GLASS_AREA:
            patch = [dark[f] for f in best]
            zs = np.concatenate([x[3][:, 2] for x in patch])
            mid, half = (float(zs.min()) + float(zs.max())) / 2, max(0.004, (float(zs.max()) - float(zs.min())) * 0.2)
            for f, xs, ys, world in patch:
                band = np.clip(1.0 - (np.abs(world[:, 2] - mid) - half) / (half * 0.8), 0.0, 1.0)
                visor[ys, xs] = np.maximum(visor[ys, xs], VISOR_LEVEL + (1.0 - VISOR_LEVEL) * band)
            how = "dark glass, %d faces, %.0f cm²" % (len(patch), area * 1e4)
        else:
            # A slit: the front of the helmet at eye height, SLIT_HALF high, SLIT_WIDE across, its ends fading.
            n = 0
            for f, xs, ys, world in rows:
                if f.normal.y > -0.3:
                    continue
                dz = np.abs(world[:, 2] - eye)
                dx = np.abs(world[:, 0])
                level = np.clip(1.0 - (dz - SLIT_HALF) / 0.004, 0.0, 1.0) * np.clip((SLIT_WIDE - dx) / 0.015, 0.0, 1.0)
                if level.max() <= 0:
                    continue
                n += 1
                visor[ys, xs] = np.maximum(visor[ys, xs], level)
            how = "a slit at eye height across %d faces (no glass found)" % n
    log("visor: %s; %d texels lit" % (how, int((visor > 0).sum())))
    light = visor.copy()
    light = np.maximum(light, strength * STRIP_LEVEL)
    emission[:, :, 0] = emission[:, :, 1] = emission[:, :, 2] = light
    emission[:, :, 3] = 1.0
    albedo[insert, :3] = albedo[insert, :3] * 0.15 + 0.02
    metal[insert, 0] = 0.0
    metal[insert, 1] = 0.85
    metal[insert, 3] = 0.85
    for kind, pixels in (("emission", emission), ("albedo", albedo), ("metallic", metal)):
        write(textures[kind], pixels)
    log("lights painted: %d strip texels, %d under glass, %d visor texels; %d of %d source lamp texels kept (head only)" % (
        int((strength > 0).sum()), int(insert.sum()), int((visor > 0).sum()), int(((lamps > 0.02) & keep).sum()), int((lamps > 0.02).sum())))
    return rails
