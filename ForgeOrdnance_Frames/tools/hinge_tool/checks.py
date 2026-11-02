"""Checks against the moves the owner set (they never change anything). Per hinge (its pieces move as one, with its links):

  attach  (a piece turning on its hinge) every frame of the open and close sequence, the piece's surface next to its hinge
          axis (within NEAR_LINE_M, all along it) stays within ATTACH_MM more of what it hangs on than shut: what stays, or
          for a piece hanging on another (a fold's leaf) that piece as it moves; under two such points: the hinge is not on
          the piece.
  links   (a piece carried by links) every frame, each link's ends stay on the piece and on what stays (within LINK_MM).
          A piece on any other path (slides, plugs, twists) rides on rails the model does not have: attach is not checked.
  anchor  (a piece carried by links) each link's end on what stays is within ANCHOR_MM of it, shut (pinned on something).
  lift    (a piece turning on its hinge) over its first LIFT_DEG the far edge (the fifth of the piece farthest from the
          hinge) moves off the piece: the mean of each far point's unit displacement along the piece's outward direction
          (away from its limb's axis, or its mean normal on the back) is over LIFT. Judged on the piece as a whole, so a
          shell wrapped round a limb counts as lifting when it swings out.
  start   (every piece, whatever its move) over the first START_SHARE of its path, no more than START_PCT of its points go
          into a part that stays they were not already against shut (within START_TOUCH_M of it, or under it): a slide
          that starts into the shell, a plug pushed inward. Shallow; through measures depth.
  body    (suits with joints) at no frame does any point of the piece or its links sink deeper than BODY_MM into the body
          than shut. The body is a capsule round each body part's axis, as wide as the suit's innermost layer (the 10th
          percentile distance of the suit's points nearest it), and only inside the shut suit: a point that the shut shell
          hides from the axis (the ray from the axis to it crosses the shell) is outside the body, so a piece swinging out
          past the elbow or knee over the next part's shell is not counted.
  cover   (suits with joints) fully open, the share of the way in (where the inner-layer pieces sat shut, seen from behind)
          the piece and its links cover, counting only what stands behind the inside of the way in (within COVER_BEHIND_M of
          the inner layer's deepest surface there, or farther back): a piece in front of the body hides nothing of it from
          behind. A piece still in its own shut place blocks the way in (it is a door not opened), so an inner piece opened
          a little covers much of its own place; over COVER_PCT is listed.
  arc     (suits with joints) the arc round a limb the opened pieces of one layer cover together; over ARC_DEG[layer] is
          listed for every piece of that layer on that limb. The area share of each piece's source object is reported.
  axis    (suits with joints) a limb piece's hinge axis more than AXIS_DEG off the limb's own axis (hinges run along the limb);
          shoulder caps (api.edit part="shoulder"), underarm pieces (part="underarm"), the back and pieces on other paths
          skip axis and arc.
  float   (every piece) the piece stays held by its hinge or rail all along its path (held()): every hinge line it turns
          on comes within FLOAT_MM of what holds it (what stays, or the piece it hangs on, and the pieces it rests on,
          api.rest_on), whatever the opening; every slide along it starts within FLOAT_MM of them and leaves at least
          RAIL_MM of the piece over where it started (floatWhere names the segment, railOverlapMm the overlap). A path with
          neither (a link swing, a screw): at ¼, ½ and fully along it the nearest the piece and its links come to them,
          posed there, measured to the other's surface both ways round (floatAt).
  clash   (every piece, with its links) over the frames the pair moves in (every CLASH_EVERY-th from the first of the two
          starting, and the end: never moved by another piece's timing), no pair of pieces goes through each other more than
          CLASH_MM deeper than it does shut. The depth (_crossing) is how far one pokes through the other at a crossing: the
          shallower side's farthest point from the other's surface, over points that lie straight over its sheet, so big
          triangles and a plate standing behind its tip do not inflate it; the pair, the frame and the point are listed.
  through (every piece, with its links) over every frame it moves in (with the pieces it hangs on, and its settling, and
          between frames: through()), it goes no more than THROUGH_MM into what stays (every part in no piece or link: the
          shell, parts marked still, a body mesh) than it does shut: through one part's surface (less its crossing with
          that same part shut), or under the shell toward the body (each point less its own depth shut, held over two half
          frames running; inside and outside judged
          against the whole shut suit, pieces included), or its lining standing past a part on the way in from its plate; the worst
          fails it. Each way is listed on its own (throughCross..., throughUnder..., throughLining...: depth, frame, part,
          point), throughMm is the worst. Unlike start it covers the whole path, unlike body it is the real surfaces, not
          the capsules.
Every check reads a piece by its own surface (model.own: a lining's faces and the points only it has left out), so a lining
of any thickness neither moves the points sampled nor fills the arc's bins; the lining is measured on its own (through,
"lining").
  dir     (suits with joints) the piece opens the way its body part wants (direction(): shoulders up and away from the middle,
          the back's top row up or back and not down, the back off the body, limbs away from the limb's axis); a score under
          DIR_MIN is turned the wrong way (api.flip_dir turns a hinge the other way).
  jump    (every piece) no frame of the open and close track moves it over JUMP_MM and JUMP_RATIO times the frames either
          side (a move squeezed into one frame reads as a teleport).
Every result is per hinge; the owner allows an item (stored on the hinge, fh_allow) or fixes it. The summary also gives,
for suits with joints, each arm and leg segment's arc straight behind it left clear when fully open (back_clear; a figure,
not a check)."""
import hashlib
import json
import math

import bpy
import numpy as np
from mathutils import Matrix
from mathutils.bvhtree import BVHTree
from mathutils.kdtree import KDTree

from . import body, model, motion

ATTACH_MM, NEAR_LINE_M = 5.0, 0.008
LINK_MM, ANCHOR_MM = 1.0, 5.0
LIFT, LIFT_DEG = 0.2, 5.0
START_SHARE, START_PCT, START_SEAM_M, START_TOUCH_M = 0.15, 2.0, 0.004, 0.005
BODY_MM = 5.0
COVER_PCT, COVER_BEHIND_M = 1.0, 0.01
ARC_DEG = {"outer": 160.0, "inner": 120.0}
AXIS_DEG = 5.0
FLOAT_MM, FLOAT_AT, FLOAT_SAMPLES, FAR, RAIL_MM = 10.0, (0.25, 0.5, 1.0), 3000, 1e9, 10.0
CLASH_MM, CLASH_EVERY = 5.0, 2
THROUGH_MM, INSIDE_REACH_M, INSIDE_SAMPLES, THROUGH_SUB, THROUGH_PEAKS = 5.0, 0.06, 600, 4, 3
# clash depth (_pokes): at each crossing, over the points within CLASH_REACH, triangles sampled every CLASH_STEP (at most
# CLASH_SUB to a side); a point counts when it lies over the other's sheet (the gap runs within acos CLASH_OVER of the
# sheet's normal, and the nearest face there faces within acos CLASH_SHEET of it), each limit a short ramp
POKE_RUN = 64
CLASH_REACH, CLASH_STEP, CLASH_SUB, CLASH_OVER, CLASH_SHEET = 0.04, 0.004, 8, 0.9, 0.7
RES = 0.004
BIN_DEG, CLEAR_REACH_M = 2, 0.25
DIR_MIN, UNDERARM_EARLY, UNDERARM_OFF_BACK_DEG, UNDERARM_ROUND_DEG = 0.0, 0.25, 60.0, 15.0
JUMP_MM, JUMP_RATIO = 20.0, 2.5
NAMES = {"attach": "铰链脱开", "links": "连杆脱开", "lift": "起开方向", "start": "起步撞入", "body": "穿入身体",
         "cover": "挡入口", "arc": "覆盖超限", "axis": "铰链偏离肢体轴", "float": "悬空", "clash": "件间互撞",
         "dir": "开向反了", "jump": "单帧跳变", "anchor": "连杆固定端悬空", "through": "穿入固定壳"}


def _round(x, n=1):
    return None if x is None else round(x, n)


def mat(m):
    return np.array(m)


def apply(m, pts):
    return pts @ m[:3, :3].T + m[:3, 3]


def mesh_arrays(objs, transform=None):
    """(points, triangles, faces (centres, normals, areas), area) of objects' own surfaces (model.own: no lining) at their
    shut place (or `transform` @ it)."""
    pts, faces, tris, area = [], [], [], 0.0
    for o in objs:
        m = model.base(o)
        if transform is not None:
            m = transform @ m
        me = o.data
        off = sum(len(p) for p in pts)
        keep, polys = model.own(o)
        at = {v: k for k, v in enumerate(keep)}
        pts.append(np.array([tuple(m @ me.vertices[i].co) for i in keep]).reshape(-1, 3))
        n3 = m.to_3x3()
        for poly in polys:
            faces.append((tuple(m @ poly.center), tuple(n3 @ poly.normal), poly.area))
            vs = [at[v] for v in poly.vertices]
            tris += [(off + vs[0], off + vs[k], off + vs[k + 1]) for k in range(1, len(vs) - 1)]
            area += poly.area
    return (np.concatenate(pts) if pts else np.zeros((0, 3))), tris, [list(x) for x in zip(*faces)], area


def collect(scene):
    """The scene's hinges, pieces and links as arrays and their tracks: [{hinge, pieces, pts, faces, tris, area, open, close,
    links: [(obj, pts, tris, open, close, spec)], f0, f1, layer, line, source}], the fixed points and triangles, the
    joints (or None) and the name of the object each fixed triangle is from."""
    hs, ls, parts = model.parts(scene)
    opened, closed, _ = motion.sequence(parts) if parts else ([], [], [])
    groups = []
    for i, h in enumerate(hs):
        ps = model.pieces_of(h, scene)
        pts, tris, faces, area = mesh_arrays(ps)
        f0, f1 = model.frames(h)
        lpts = [np.array([tuple(model.base(o) @ o.data.vertices[v].co) for v in model.lining(o)]).reshape(-1, 3)
                for o in ps]                 # the lining's inner points, shut (model.lining)
        groups.append({"hinge": h, "pieces": ps, "pts": pts, "faces": faces, "tris": tris, "area": area, "open": opened[i],
                       "close": closed[i], "links": [], "f0": f0, "f1": f1, "layer": h.get("fh_layer", "outer"),
                       "line": model.line(h), "source": ps[0].get("fh_source", ""), "index": i,
                       "owner": np.repeat(np.arange(len(ps)), [len(model.own(o)[0]) for o in ps]),
                       "lining": np.concatenate(lpts)})
    for k, o in enumerate(ls):
        g = next(g for g in groups if g["hinge"].name == o["fh_link"])
        pts, tris, _, _ = mesh_arrays([o])
        g["links"].append((o, pts, tris, opened[len(hs) + k], closed[len(hs) + k], model.link_spec(o)))
    moving = {o for g in groups for o in g["pieces"]} | set(ls)
    fixed_objs = [o for o in scene.objects if o.type == "MESH" and o not in moving and o.data.vertices]
    fixed = [np.array([tuple(o.matrix_world @ v.co) for v in o.data.vertices]).reshape(-1, 3) for o in fixed_objs]
    ftris, fowner, off = [], [], 0
    for o, p in zip(fixed_objs, fixed):
        for poly in o.data.polygons:
            vs = list(poly.vertices)
            ftris += [(off + vs[0], off + vs[k], off + vs[k + 1]) for k in range(1, len(vs) - 1)]
            fowner += [o.name] * (len(vs) - 2)
        off += len(p)
    fixed = np.concatenate(fixed) if fixed else np.zeros((0, 3))
    return groups, fixed, ftris, body.joints(scene), fowner


def kd(points):
    t = KDTree(max(1, len(points)))
    for i, p in enumerate(points):
        t.insert(p, i)
    t.balance()
    return t


def hinge_points(g, p0, a, n=24):
    """Points on the piece's surface next to its hinge axis (within NEAR_LINE_M), sampled all along the piece's reach."""
    tree = BVHTree.FromPolygons([tuple(p) for p in g["pts"]], g["tris"])
    s = (g["pts"] - p0) @ a
    out = []
    for t in np.linspace(s.min(), s.max(), n):
        q, _, _, d = tree.find_nearest(tuple(p0 + a * t), NEAR_LINE_M)
        if q is not None:
            out.append(tuple(q))
    return np.array(out).reshape(-1, 3)


def outward(g, j):
    """The piece's outward direction: away from its limb's axis through its centre, or its mean normal (the back, no joints)."""
    c = g["pts"].mean(axis=0)
    if j is not None:
        reg, side, _ = model.body_part(g["hinge"], j)
        if reg != "back" and g["hinge"].get("fh_part") != "shoulder":
            h, t = body.seat(j, reg, side if side in ("L", "R") else "L")
            ax = (t - h) / np.linalg.norm(t - h)
            r = c - h
            r -= ax * (r @ ax)
            if np.linalg.norm(r) > 1e-6:
                return r / np.linalg.norm(r)
    centres, normals, areas = (np.asarray(x, float) for x in g["faces"])
    n = (normals * areas[:, None]).sum(axis=0)
    return n / max(np.linalg.norm(n), 1e-12)


def lift(g, j, p0, a, sign):
    """The far edge's start off the piece (see the module docstring), for a turn `sign` about (p0, a)."""
    pts = g["pts"]
    r = pts - p0
    r -= np.outer(r @ a, a)
    dist = np.linalg.norm(r, axis=1)
    far = pts[dist >= np.quantile(dist, 0.8)]
    m = mat(motion.hinge_turn(tuple(p0), tuple(a), sign * LIFT_DEG))
    d = apply(m, far) - far
    d /= np.maximum(np.linalg.norm(d, axis=1), 1e-12)[:, None]
    return float((d @ outward(g, j)).mean())


def jumps(g):
    """(mm, frame) of the worst single-frame jump of a piece over its open and close track: a frame in which its farthest-moving
    point moves over JUMP_MM and over JUMP_RATIO times as far as in the frames either side (a move squeezed into a frame reads
    as the piece teleporting); None when there is none."""
    pts = g["pts"][::max(1, len(g["pts"]) // 300)]
    track = g["open"] + g["close"]
    pos = [apply(mat(m), pts) for m in track]
    step = [float(np.linalg.norm(b - a, axis=1).max()) * 1000 for a, b in zip(pos, pos[1:])]
    worst = None
    for k, s in enumerate(step):
        side = max(step[k - 1] if k else 0.0, step[k + 1] if k + 1 < len(step) else 0.0)
        if s > JUMP_MM and s > JUMP_RATIO * side and (worst is None or s > worst[0]):
            worst = (round(s, 1), k + 2)
    return worst


def direction(g, j):
    """(score, the way wanted in words, the way it goes) of a piece's opening against the way its body part wants (the
    owner's rules), from where its points go between shut and fully open:
      declared (api.edit expect=, fh_dir_want): the way the owner set for this one piece: the move's share along it
      underarm (fh_part "underarm", api.edit part=): down first, then round the outside of the torso to the side or the
               front, turning with the ribcage as it goes (turning is fine; staying outside the shell is the through
               check's): the least of the down share of its first UNDERARM_EARLY of the way, and how far past
               UNDERARM_OFF_BACK_DEG its end stands from straight behind about the torso's axis (over the rest of the way
               to straight in front), and how far past UNDERARM_ROUND_DEG it has gone round from where it sat (a piece
               only dropping straight down from the side does not count as going round)
      shoulder (a cap, fh_part "shoulder", or a piece over the shoulders off the middle): up and away from the suit's middle;
               the score is the least of the move's share along (up + away from the middle) and along up
      back, top row or over the shoulders: off the body or up, and not down: the least of half the off-body share plus half
               the up share, and the up share
      back: off the body: how much farther its points stand from the torso's axis, as a share of how far they move
      limb: the same, from the limb's axis (a half that swings round its hinge keeps a positive score; one that turns into
            the limb goes negative).
    1 is straight the wanted way, below 0 the other way (DIR_MIN fails it)."""
    h = g["hinge"]
    pts = g["pts"][::max(1, len(g["pts"]) // 400)]
    moved = apply(mat(model.turn(h, 1.0)), pts)
    step = np.linalg.norm(moved - pts, axis=1).mean()
    if step < 1e-5:
        return None
    c = pts.mean(axis=0)
    d = (moved - pts).mean(axis=0)
    d = d / max(np.linalg.norm(d), 1e-12)
    up = np.array([0.0, 0.0, 1.0])
    outside = np.array([np.sign(c[0]) if abs(c[0]) > 0.03 else 0.0, 0.0, 0.0])
    reg, side, over = model.body_part(h, j)
    hh, tt = body.seat(j, reg, side if side in ("L", "R") else "L")

    def off_axis():
        """How much farther from the body part's axis the points end, as a share of how far they move."""
        return float((body.from_axis(hh, tt, moved)[0] - body.from_axis(hh, tt, pts)[0]).mean() / step)
    want = model.expected_dir(h)
    if want is not None:
        score, text = float(d @ np.array(want[1])), "朝%s（单件声明）" % want[0]
    elif h.get("fh_part") == "underarm":
        early = (apply(mat(model.turn(h, UNDERARM_EARLY)), pts) - pts).mean(axis=0)
        down = float(-early[2] / max(np.linalg.norm(early), 1e-12))
        th, tt_ = body.seat(j, "back", "C")
        a0, a1 = (float(x) for x in body.angles(th, tt_, np.array([pts.mean(axis=0), moved.mean(axis=0)])))
        off0, off_back = min(a0, 360.0 - a0), min(a1, 360.0 - a1)
        score = min(down, (off_back - UNDERARM_OFF_BACK_DEG) / (180.0 - UNDERARM_OFF_BACK_DEG),
                    (off_back - off0 - UNDERARM_ROUND_DEG) / UNDERARM_ROUND_DEG)
        text = "先向下，再绕躯干外侧到侧面或前方（终点离正后方 %.0f°，要 %.0f° 以上；绕过 %.0f°，要 %.0f° 以上）" % (
            off_back, UNDERARM_OFF_BACK_DEG, off_back - off0, UNDERARM_ROUND_DEG)
    elif h.get("fh_part") == "shoulder" or (reg == "back" and over and abs(c[0]) > 0.12):
        want = (up + outside) / np.linalg.norm(up + outside)
        score, text = min(float(d @ want), float(d @ up)), "向上、远离中线"
    elif reg == "back" and (over or h.get("fh_row") == "top"):
        score, text = min(0.5 * off_axis() + 0.5 * float(d @ up), float(d @ up)), "向上或向后、不向下"
    elif reg == "back":
        score, text = off_axis(), "离开身体向后、向外"
    else:
        score, text = off_axis(), "离开肢体轴向外"
    return round(score, 2), text, [round(float(x), 2) for x in d]


def raster(points, tris, box, depth=None):
    """The triangles seen from behind (along Y) as a pixel mask over `box`. depth="near" or "far" also returns, per pixel,
    the largest or smallest Y (the surface nearest the viewer behind, or the deepest one); -inf / inf where empty."""
    lo_x, hi_z, W, H = box
    im = np.zeros((H, W), bool)
    ys = None if depth is None else np.full((H, W), -np.inf if depth == "near" else np.inf)
    px = (points[:, 0] - lo_x) / RES
    pz = (hi_z - points[:, 2]) / RES
    for a, b, c in tris:
        xs, zs = px[[a, b, c]], pz[[a, b, c]]
        x0, x1 = int(max(0, xs.min())), int(min(W - 1, xs.max())) + 1
        z0, z1 = int(max(0, zs.min())), int(min(H - 1, zs.max())) + 1
        if x1 <= x0 or z1 <= z0:
            continue
        gx, gz = np.meshgrid(np.arange(x0, x1) + 0.5, np.arange(z0, z1) + 0.5)
        d = (zs[1] - zs[2]) * (xs[0] - xs[2]) + (xs[2] - xs[1]) * (zs[0] - zs[2])
        if abs(d) < 1e-12:
            continue
        l1 = ((zs[1] - zs[2]) * (gx - xs[2]) + (xs[2] - xs[1]) * (gz - zs[2])) / d
        l2 = ((zs[2] - zs[0]) * (gx - xs[2]) + (xs[0] - xs[2]) * (gz - zs[2])) / d
        inside = (l1 >= 0) & (l2 >= 0) & (l1 + l2 <= 1)
        im[z0:z1, x0:x1] |= inside
        if ys is not None:
            y = l1 * points[a, 1] + l2 * points[b, 1] + (1 - l1 - l2) * points[c, 1]
            win = ys[z0:z1, x0:x1]
            pick = np.maximum if depth == "near" else np.minimum
            win[inside] = pick(win[inside], y[inside])
    return im if ys is None else (im, ys)


def _surface(pts, tris):
    return BVHTree.FromPolygons([tuple(p) for p in pts], tris)


def _nearest(pts, tree, limit=FAR):
    """The shortest distance from the points (sampled) to the surface `tree` when under `limit`, else `limit`."""
    best = limit
    for p in pts[::max(1, len(pts) // FLOAT_SAMPLES)]:
        hit = tree.find_nearest(tuple(p), best)
        if hit[0] is not None:
            best = min(best, float(hit[3]))
    return best


def _posed(g, m):
    """A group's points, triangles and surface with its links, moved by `m` from shut."""
    pts, tris = [apply(mat(m), g["pts"])], list(g["tris"])
    for _, lp, lt, _, _, spec in g["links"]:
        off = sum(len(x) for x in pts)
        pts.append(apply(mat(motion.link_pose(spec, m)), lp))
        tris += [(a + off, b + off, c + off) for a, b, c in lt]
    pts = np.concatenate(pts)
    return pts, _surface(pts, tris)


def held(g, by_name, fixed_tree):
    """How a piece is held all along its path: (the worst gap in metres between a hinge line or rail start and what holds
    it, which part of the path that is (words), the least overlap in metres of a slide with its rail, or None without a
    slide). What holds it: what stays, the pieces it hangs on (shut, as its path is written) and the pieces it rests on
    (rest_on, fully open). Each turn: the gap from its hinge line, over the piece's reach along it, to the nearest of them
    (it does not change as the piece opens); a hinge after a slide or push rides out on it, so its line is measured with
    those slides taken back. Each slide along the piece (not a push out or in off its face): the gap from the piece where
    the slide starts to them, and how much of the piece's length along the slide is still over where it started when the
    slide ends (its rail: a slide longer than the piece runs off it). None when the path has neither (links: see
    anchor)."""
    h = g["hinge"]
    chain = [by_name[p.name] for p in model.chain(h)[1:] if p.name in by_name]
    rests = [by_name[n] for n in model.rests_on(h) if n in by_name]
    targets = [_posed(p, Matrix.Identity(4))[1] for p in chain] + ([fixed_tree] if fixed_tree is not None else [])
    targets += [_posed(p, model.turn(p["hinge"], 1.0))[1] for p in rests]
    if not targets:
        return None
    centres, normals, areas = (np.asarray(x, float) for x in g["faces"])
    n0 = (normals * areas[:, None]).sum(axis=0)
    n0 /= max(np.linalg.norm(n0), 1e-12)

    def gap(pts):
        return min(min(float(t.find_nearest(tuple(p))[3]) for t in targets) for p in pts)
    pose, mount = Matrix.Identity(4), Matrix.Identity(4)
    worst, where, overlap = None, None, None
    for k, s in enumerate(sorted(model.path(h).segments, key=lambda s: s["at"][0])):
        if s.get("back"):                        # out and back: it ends where it started
            continue
        kind, d = motion.kind(s), None
        pts = apply(mat(pose), g["pts"][::max(1, len(g["pts"]) // 300)])
        if kind == "turn":
            p0, a = np.array(s["point"]), np.array(s["axis"]) / np.linalg.norm(s["axis"])
            t = (pts - p0) @ a
            line = [p0 + a * x for x in np.linspace(t.min(), t.max(), 24)]
            d = gap(apply(mat(mount.inverted()), np.array(line)))
            what = "第 %d 段转动的铰链线" % (k + 1)
        elif kind == "slide":
            v = np.array(s["slide"])
            length = float(np.linalg.norm(v))
            u = v / max(length, 1e-12)
            if length > 1e-6 and abs(float(u @ (mat(pose)[:3, :3] @ n0))) <= 0.7:   # along the piece, not off its face
                d = gap(pts)
                what = "第 %d 段滑移的起点" % (k + 1)
                span = float(np.ptp(pts @ u))
                overlap = span - length if overlap is None else min(overlap, span - length)
            mount = motion.seg_pose(s, 1.0) @ mount  # the rail or push carries the hinges after it out with the piece
        if d is not None and (worst is None or d > worst):
            worst, where = d, what
        pose = motion.seg_pose(s, 1.0) @ pose
    if worst is None and overlap is None:
        return None
    return worst, where, overlap


def floating(g, by_name, fixed, fixed_tree):
    """(the worst gap in metres, the path fraction it is at) of a piece with its links to what stays and the pieces it hangs on
    at FLOAT_AT; (None, None) when there is nothing to measure against. A gap is the nearest the two surfaces come, found from
    the points of each to the surface of the other (a plate crossing a surface between its points still touches it)."""
    h = g["hinge"]
    parents = [by_name[p.name] for p in model.chain(h)[1:] if p.name in by_name]
    parents += [by_name[n] for n in model.rests_on(h) if n in by_name and by_name[n] not in parents]
    worst = (None, None)
    for t in FLOAT_AT:
        pts, mover = _posed(g, model.turn(h, t))
        targets = [(fixed, fixed_tree)] if fixed_tree is not None else []
        targets += [_posed(p, model.turn(p["hinge"], t)) for p in parents]
        gap = FAR
        for tpts, tree in targets:
            d = _nearest(pts, tree)
            lo, hi = pts.min(axis=0) - d, pts.max(axis=0) + d
            near = tpts[np.all((tpts >= lo) & (tpts <= hi), axis=1)]
            gap = min(gap, _nearest(near, mover, d))
        if gap < FAR and (worst[0] is None or gap > worst[0]):
            worst = (gap, t)
    return worst


def _frame_mesh(g, k, s=0.0):
    """(points, triangles as an array, surface, point tree) of a group with its links at frame k of the opening, or `s` of
    the way on to frame k + 1."""
    def at(track):
        return mat(track[k]) if not s else mat(Matrix(track[k]).lerp(Matrix(track[k + 1]), s))
    pts, tris = [apply(at(g["open"]), g["pts"])], list(g["tris"])
    for _, lp, lt, lo, _, _ in g["links"]:
        off = sum(len(x) for x in pts)
        pts.append(apply(at(lo), lp))
        tris += [(a + off, b + off, c + off) for a, b, c in lt]
    pts = np.concatenate(pts)
    return pts, np.array(tris, int).reshape(-1, 3), _surface(pts, tris), kd([tuple(p) for p in pts])


def _samples(pts, tris):
    """Points spread over triangles (rows of `tris`, an index array), no farther than CLASH_STEP apart, so a big triangle is
    measured over its face and not only at its corners."""
    tris = np.asarray(tris, int).reshape(-1, 3)
    if not len(tris):
        return np.zeros((0, 3))
    a, b, c = pts[tris[:, 0]], pts[tris[:, 1]], pts[tris[:, 2]]
    edge = np.maximum(np.maximum(np.sqrt(((b - a) ** 2).sum(axis=1)), np.sqrt(((c - b) ** 2).sum(axis=1))),
                      np.sqrt(((a - c) ** 2).sum(axis=1)))
    ns = np.minimum(CLASH_SUB, np.maximum(1, np.ceil(edge / CLASH_STEP))).astype(int)
    blocks = [None] * len(tris)
    for n in np.unique(ns):
        ij = np.array([(i / n, j / n) for i in range(n + 1) for j in range(n + 1 - i)])
        rows = np.nonzero(ns == n)[0]
        ab, ac = (b - a)[rows], (c - a)[rows]
        got = a[rows][:, None, :] + ab[:, None, :] * ij[None, :, 0:1] + ac[:, None, :] * ij[None, :, 1:2]
        for r, block in zip(rows, got):
            blocks[r] = block
    return np.concatenate(blocks)


def _ramp(v, lo, width):
    """0 at or below lo, 1 from lo + width, straight between: a limit that a point crosses gradually, not at a step."""
    return np.clip((v - lo) / width, 0.0, 1.0)


def _pokes(x, y, centres, xs, ys):
    """How far surface x goes through surface y at each crossing (both from _frame_mesh; centres (P, 3) the crossings, one
    per pair of crossing triangles, xs / ys those triangles of each). At each crossing the points of x near it (its corners
    and points over its crossing triangles) that lie straight over y's sheet there (the crossing triangle of y's normal;
    the gap to y's nearest point runs along it and that face is part of the sheet, not its rim) are split by the side of
    the sheet they are on, and the poke is the shallower side's farthest gap to y's surface. Every limit is a ramp
    (_ramp) and nothing is grouped on a grid, so the depth moves smoothly with the surfaces. ((P,) depths in metres, (P, 3)
    the poking points)."""
    xp, xt, _, xkd = x
    yp, yt, ytree, _ = y
    tri = yp[yt[ys]]
    n = np.cross(tri[:, 1] - tri[:, 0], tri[:, 2] - tri[:, 0])
    ln = np.linalg.norm(n, axis=1)
    ok = ln > 1e-12
    n = n / np.maximum(ln, 1e-12)[:, None]
    seen = set()
    for c in np.unique(np.round(centres / (CLASH_REACH / 2)), axis=0) * (CLASH_REACH / 2):
        seen.update(i for _, i, _ in xkd.find_range(tuple(c), CLASH_REACH * 1.5))
    pts = np.concatenate([xp[sorted(seen)].reshape(-1, 3), _samples(xp, xt[np.unique(xs)])])
    depth, where = np.zeros(len(centres)), np.zeros((len(centres), 3))
    if not len(pts):
        return depth, where
    # crossings in runs of POKE_RUN that lie close together, each run looked at only with the points in reach of its box
    # (a point out of reach of every crossing in a run weighs nothing there): the same figures, far fewer distances
    order = np.lexsort(np.floor(centres / CLASH_REACH).T[::-1])
    runs = [order[k:k + POKE_RUN] for k in range(0, len(order), POKE_RUN)]

    def in_box(p, run):
        lo, hi = centres[run].min(axis=0) - CLASH_REACH, centres[run].max(axis=0) + CLASH_REACH
        return np.nonzero(np.all((p >= lo) & (p <= hi), axis=1))[0]
    near = np.zeros(len(pts), bool)
    for run in runs:
        rows = in_box(pts, run)
        if len(rows):
            d = pts[rows][:, None, :] - centres[run][None]
            near[rows] |= (np.sqrt((d * d).sum(axis=2)) < CLASH_REACH).any(axis=1)
    pts = pts[near]
    _, first = np.unique(pts, axis=0, return_index=True)
    pts = pts[np.sort(first)]          # the same point twice (a corner of several triangles) is looked up once
    far = (0.0,) * 7                   # nothing in reach: a gap of 0, dropped below
    hits = [(*h[0], *h[1], h[3]) if h[0] is not None else far
            for h in map(ytree.find_nearest, pts.tolist(), [CLASH_REACH * 2] * len(pts))]
    hits = np.array(hits, float).reshape(-1, 7)
    loc, nrm, gap = hits[:, 0:3], hits[:, 3:6], hits[:, 6]
    use = gap > 1e-5
    pts, loc, nrm, gap = pts[use], loc[use], nrm[use], gap[use]
    if not len(pts):
        return depth, where
    for run in runs:
        rows = in_box(pts, run)
        if not len(rows):
            continue
        p, c, nn = pts[rows], centres[run], n[run]
        d = p[:, None, :] - c[None]
        reach = _ramp(CLASH_REACH - np.sqrt((d * d).sum(axis=2)), 0.0, CLASH_REACH / 4)
        along = (p - loc[rows]) @ nn.T
        w = reach * _ramp(np.abs(nrm[rows] @ nn.T), CLASH_SHEET, 0.1) * _ramp(np.abs(along) / gap[rows][:, None], CLASH_OVER, 0.05)
        val = gap[rows][:, None] * w
        hi = np.where(along > 0, val, 0.0)
        lo = np.where(along < 0, val, 0.0)
        h, l = hi.max(axis=0), lo.max(axis=0)
        pick = np.where(h <= l, hi.argmax(axis=0), lo.argmax(axis=0))
        depth[run] = np.where(ok[run], np.minimum(h, l), 0.0)
        where[run] = p[pick]
    return depth, where


def _crossing(a, b):
    """How far two surfaces (from _frame_mesh) go through each other: at each crossing (a pair of crossing triangles) the
    shallower of a through b and b through a (_pokes), so a plate's tip pushed 3 mm through another counts 3 mm however big
    its triangles are or however far the rest of it stands behind, and a seam the two only share counts 0. Returns (the
    deepest crossing in metres, where: the poking point)."""
    pairs = a[2].overlap(b[2])
    if not pairs:
        return 0.0, None
    ia, ib = (np.array(v, int) for v in zip(*pairs))
    centres = (a[0][a[1][ia]].mean(axis=1) + b[0][b[1][ib]].mean(axis=1)) / 2
    d1, w1 = _pokes(a, b, centres, ia, ib)
    d2, w2 = _pokes(b, a, centres, ib, ia)
    d = np.minimum(d1, d2)
    k = int(np.argmax(d))
    if d[k] <= 0:
        return 0.0, None
    w = w1[k] if d1[k] <= d2[k] else w2[k]
    return float(d[k]), tuple(round(float(v), 4) for v in w)


def _window(groups, i):
    """(first, last) frame index piece i moves in, with the pieces it hangs on, and its settling."""
    n = len(groups[0]["open"])
    settle = int(math.ceil(motion.SETTLE_S * motion.FPS)) + 1
    spans = [model.frames(h) for h in model.chain(groups[i]["hinge"])]
    return max(0, min(s[0] for s in spans) - 1), min(n - 1, max(s[1] for s in spans) + settle)


def _under(pts, caps, tree, suit):
    """How far each point lies under what stays, inside the suit: a point with nothing of the shut suit (`suit`: what
    stays and every piece and link in its shut place, so a piece's own layer counts as shell though it opens later)
    between it and the axis of the body part it is nearest (seen from the body it is not outside the suit) and a surface
    of what stays (`tree`) out from it (away from that axis) within INSIDE_REACH_M that is over the same body part: the
    distance to that surface; 0 elsewhere (outside the suit, nothing over it, or only another part's shell, as an arm's
    over the side of the torso)."""
    _, which = caps.depth(pts)
    v = pts - caps.foot(pts, which)
    r = np.linalg.norm(v, axis=1)
    u = v / np.maximum(r, 1e-9)[:, None]
    out = np.zeros(len(pts))
    hits = {}
    for k, (p, d) in enumerate(zip(pts, u)):
        loc = tree.ray_cast(tuple(p + d * 1e-4), tuple(d), INSIDE_REACH_M)[0]
        if loc is None or (r[k] > 1e-4 and suit.ray_cast(tuple(p - d * 1e-4), tuple(-d), float(r[k]))[0] is not None):
            continue
        hits[k] = np.array(loc)
    if hits:
        ks = np.array(list(hits))
        locs = np.array([hits[k] for k in ks])
        _, part = caps.depth(locs)
        same = part == which[ks]           # the shell over it is its own body part's (not an arm over the side of the torso)
        out[ks[same]] = np.linalg.norm(locs[same] - pts[ks[same]], axis=1)
    return out


def _still_parts(fixed, ftris, fowner):
    """{object name: (points, triangles, surface, point tree)} of each part that stays, on its own."""
    by = {}
    for t, name in zip(ftris, fowner):
        by.setdefault(name, []).append(t)
    out = {}
    for name, tris in by.items():
        tris = np.array(tris, int)
        used, inv = np.unique(tris, return_inverse=True)
        pts, tris = fixed[used], inv.reshape(-1, 3)
        out[name] = (pts, tris, _surface(pts, tris.tolist()), kd([tuple(p) for p in pts]))
    return out


def start_hits(sample, moved, tree, fowner, parts):
    """(how many points of `sample` go into a part that stays on the straight way to `moved`, the soonest such:
    (distance, point, direction, where, part name) or None). Only a part the point was not against shut counts: one it
    lies within START_TOUCH_M of, or whose surface it meets from behind (it lies under that part shut), is passed through,
    as through's baselines measure a piece against its own shut state; START_SEAM_M is skipped at the start of each way."""
    trees, hits, first = {}, 0, None
    for p, q in zip(sample, moved):
        d = q - p
        length = float(np.linalg.norm(d))
        if length < 1e-6:
            continue
        u = d / length
        at = START_SEAM_M
        while at < length:
            loc, normal, idx, dist = tree.ray_cast(tuple(p + u * at), tuple(u), length - at)
            if loc is None:
                break
            name = fowner[idx]
            if name not in trees:
                pts, tris = parts[name][0], parts[name][1]
                trees[name] = BVHTree.FromPolygons([tuple(x) for x in pts], tris.tolist())
            if float(np.dot(normal, u)) < 0 and trees[name].find_nearest(tuple(p))[3] > START_TOUCH_M:
                hits += 1
                if first is None or at + dist < first[0]:
                    first = (at + dist, p, u, np.array(loc), name)
                break
            at += dist + 1e-4      # against it shut: go on past it
    return hits, first


def through(groups, i, parts, tree, caps=None, suit=None):
    """{how: (depth in metres, frame, where, the part of what stays or None)} of the deepest piece i goes into what stays
    more than it does shut, each way on its own, over the frames it moves in (with the pieces it hangs on, and its
    settling); a way it never does is left out. The ways:
      how="cross": its surface going through the surface of one part that stays (`parts`, _still_parts), measured as clash
                   measures a pair (_crossing), less its crossing with that same part shut (a part it crosses shut never
                   hides a crossing with another); every frame, and THROUGH_SUB steps between frames round its
                   THROUGH_PEAKS deepest (by the crossing as it is, not less shut), so a short sharp poke while the piece moves fast is not missed;
      how="under": a point of the piece gone under what stays (`tree`), toward the body (_under, the shut suit `suit`
                   deciding inside and outside), less the same point's own depth shut and no more than it has moved: a
                   piece whose path takes it inside the shell or behind a plate shows here even where its surface never
                   crosses one. Every half frame; a point counts as deep as it stays over two half frames running, so a
                   point whose inside test flips for an instant at a seam or a hole does not count. Needs the joints
                   (caps, suit).
      how="lining": its lining (model.lining: the points only the lining has) standing past a part that
                   stays: each lining point's way in from the plate point it stands under (the plate's nearest, shut)
                   meets what stays (`tree`), and the depth is how much of that way lies past it, less the same point's
                   depth shut; frames and steps between them as "cross". The lining is never measured as a surface on
                   its own: a sheet a few mm under a plate that already lies against a part shut read that crossing
                   anywhere from 1 to 9 mm between two poses less than a millimetre apart.
    The piece's surface for "cross" and "under" is its own (model.own), so a lining of any thickness reads as none does.
    There is no coarse way: a search (run(coarse=True)) reads through exactly as the full check, so a candidate it passes
    passes the full check on through (a coarser frame step read up to 4 mm lower: a short poke between frames, a point
    held under the shell for one frame, and passed candidates the full check then failed)."""
    a, b = _window(groups, i)
    g = groups[i]
    boxes = {n: (p[0].min(axis=0), p[0].max(axis=0)) for n, p in parts.items()}
    worst = {}
    steps = list(range(max(a, 1), b + 1))

    def cross(m):
        mlo, mhi = m[0].min(axis=0), m[0].max(axis=0)
        return {name: _crossing(m, part) for name, part in parts.items()
                if not (np.any(mlo > boxes[name][1]) or np.any(mhi < boxes[name][0]))}
    shut = cross(_frame_mesh(g, 0))

    def worse_cross(k, s, frame):
        top = 0.0
        for name, (d, w) in cross(_frame_mesh(g, k, s)).items():
            top = max(top, d)
            d -= shut.get(name, (0.0, None))[0]
            if d > 0 and ("cross" not in worst or d > worst["cross"][0]):
                worst["cross"] = (d, frame, w, name)
        return top
    lp = g["lining"]
    if len(lp) and tree is not None:
        # each lining point with the plate's point it stands under (the nearest of the plate's own, shut)
        base = g["pts"][[i for _, i, _ in map(kd([tuple(p) for p in g["pts"]]).find, lp.tolist())]]

        def segs(m):
            """How far each lining point stands past a part that stays, on the way in from its plate point (0 where the
            way meets nothing), and where it meets one."""
            P, Q = apply(m, base), apply(m, lp)
            out, at = np.zeros(len(lp)), [None] * len(lp)
            for n, (p, q) in enumerate(zip(P, Q)):
                d = q - p
                ln = float(np.linalg.norm(d))
                if ln < 1e-6:
                    continue
                loc, _, _, dist = tree.ray_cast(tuple(p + d / ln * 1e-4), tuple(d / ln), ln - 1e-4)
                if loc is not None:
                    out[n], at[n] = ln - 1e-4 - dist, loc
            return out, at
        lin0 = segs(mat(g["open"][0]))[0]

    def worse_lining(k, s, frame):
        m = mat(g["open"][k]) if not s else mat(Matrix(g["open"][k]).lerp(Matrix(g["open"][k + 1]), s))
        d, at = segs(m)
        more = d - lin0
        n = int(np.argmax(more))
        if more[n] > 0 and ("lining" not in worst or more[n] > worst["lining"][0]):
            loc = tuple(at[n])
            name = min(parts, key=lambda x: parts[x][2].find_nearest(loc)[3] or 1.0) if parts else None
            worst["lining"] = (float(more[n]), frame, tuple(round(float(c), 4) for c in loc), name)
        return float(d.max()) if len(d) else 0.0
    ways = [("cross", worse_cross)] + ([("lining", worse_lining)] if len(lp) and tree is not None else [])
    for how, worse in ways:
        each = {k: worse(k, 0.0, k + 1) for k in steps}
        # between frames round the THROUGH_PEAKS deepest frames that stand above both neighbours, picked by the depth as it
        # is (not less its depth shut): a piece against a part shut that reads no deeper on the whole frames can still go
        # far deeper between them while it moves fast
        peaks = [k for k, d in each.items() if d > 0 and d >= each.get(k - 1, 0.0) and d >= each.get(k + 1, 0.0)]
        for k in sorted(peaks, key=lambda k: -each[k])[:THROUGH_PEAKS]:
            for s in [x / THROUGH_SUB for x in range(1, THROUGH_SUB)]:
                for k0 in (k - 1, k):
                    if 0 <= k0 < len(g["open"]) - 1:
                        worse(k0, s, k0 + 1 + (1 if s >= 0.5 else 0))
    if caps is not None:
        own = g["pts"][::max(1, len(g["pts"]) // INSIDE_SAMPLES)]
        d0 = _under(own, caps, tree, suit)
        times = [(k, s) for k in steps for s in (0.0, 0.5) if not s or k + 1 < len(g["open"])]
        prev = None
        for k, s in times:
            m = mat(g["open"][k]) if not s else mat(Matrix(g["open"][k]).lerp(Matrix(g["open"][k + 1]), s))
            pts = apply(m, own)
            more = np.minimum(_under(pts, caps, tree, suit) - d0, np.linalg.norm(pts - own, axis=1))
            if prev is not None:
                held_ = np.minimum(more, prev)
                n = int(np.argmax(held_))
                if held_[n] > 0 and ("under" not in worst or held_[n] > worst["under"][0]):
                    worst["under"] = (float(held_[n]), k + 1, tuple(round(float(c), 4) for c in pts[n]), None)
            prev = more
    return worst

def clashes(groups, only=None, every=CLASH_EVERY):
    """{(i, k): (depth in metres, frame, where)} for the pairs of groups i < k that cross each other deeper than they do shut,
    worst over every `every`-th frame of the opening (and its last), checked only for pairs with a group index in `only`."""
    def when(i, m):
        """The frames a pair is looked at: from the first of the two starting to the last settling, every `every`-th
        counted from that start, and its end: its own times only, so another piece's timing never moves the samples."""
        (a0, b0), (a1, b1) = _window(groups, i), _window(groups, m)
        a, b = min(a0, a1), max(b0, b1)
        return set(range(a, b + 1, every)) | {b}
    pair_frames = {}
    for i in range(len(groups)):
        for m in range(i + 1, len(groups)):
            if only is None or i in only or m in only:
                pair_frames[(i, m)] = when(i, m) | {0}
    frames = sorted(set().union(*pair_frames.values())) if pair_frames else []
    base, worst = {}, {}
    for k in frames:
        meshes = {}

        def mesh(i):
            if i not in meshes:
                meshes[i] = _frame_mesh(groups[i], k)
            return meshes[i]
        boxes = {}

        def box(i):
            if i not in boxes:
                pts = apply(mat(groups[i]["open"][k]), groups[i]["pts"])
                boxes[i] = (pts.min(axis=0), pts.max(axis=0))
            return boxes[i]
        for i in range(len(groups)):
            for m in range(i + 1, len(groups)):
                if k not in pair_frames.get((i, m), ()):
                    continue
                (l1, h1), (l2, h2) = box(i), box(m)
                if np.any(l1 > h2) or np.any(l2 > h1):
                    continue
                depth, where = _crossing(mesh(i), mesh(m))
                if k == 0:
                    base[(i, m)] = depth
                if depth - base.get((i, m), 0.0) > worst.get((i, m), (0.0, 0))[0]:
                    worst[(i, m)] = (depth - base.get((i, m), 0.0), k + 1, where)
    return worst


def back_clear(groups, j, at):
    """{"upperarm.L": degrees, ...}: fully open (frame `at` of the opening), for each arm and leg segment the arc round its
    axis, centred straight back, that no opened piece or link stands in (points along the segment, within CLEAR_REACH_M of
    its axis), in BIN_DEG steps; 360 when nothing is round it, 0 when something stands straight behind. A figure to read,
    not a check."""
    pts = []
    for g in groups:
        pts.append(apply(mat(g["open"][at]), g["pts"]))
        pts += [apply(mat(lo[at]), lp) for _, lp, _, lo, _, _ in g["links"]]
    pts = np.concatenate(pts) if pts else np.zeros((0, 3))
    out = {}
    n = int(360 / BIN_DEG)
    for side in ("L", "R"):
        for seg in ("upperarm", "forearm", "thigh", "shin"):
            hh, tt = body.seat(j, seg, side)
            d = tt - hh
            s = ((pts - hh) @ d) / (d @ d)
            dist, _ = body.from_axis(hh, tt, pts)
            near = pts[(s >= 0) & (s <= 1) & (dist <= CLEAR_REACH_M)]
            used = set((np.floor(body.angles(hh, tt, near) / BIN_DEG).astype(int) % n).tolist())
            if not used:
                out["%s.%s" % (seg, side)] = 360
                continue
            free = 0
            for step in (1, -1):                 # out from straight back (the bin either side of 0°) each way
                k = 0 if step == 1 else n - 1
                while k % n not in used and free < n:
                    free += 1
                    k += step
            out["%s.%s" % (seg, side)] = int(min(free, n) * BIN_DEG)
    return out


def region(h, j):
    """(body region, side) a piece sits on: a limb segment, "back", or its own part for a shoulder cap (hinged on the neck
    side about Y) or an underarm piece (not along the arm)."""
    reg, side, _ = model.body_part(h, j)
    if h.get("fh_part") in ("shoulder", "underarm"):
        reg = h["fh_part"]
    return reg, side


_SETUP = {}


def _setup(groups, fixed, ftris, j, fowner):
    """(fixed point tree, fixed surface, _still_parts, body capsules or None, the shut suit's surface or None): built from
    what stays and every piece and link shut, and kept for the next run while those are the same (a search changes only
    how a piece moves, so these are built once, not every try)."""
    h = hashlib.sha1(fixed.tobytes())
    h.update(np.asarray(ftris, np.int64).tobytes())
    h.update("\n".join(fowner).encode())
    for k in sorted(j or {}):
        h.update(k.encode() + np.asarray(j[k], float).tobytes())
    for g in groups:
        h.update(g["pts"].tobytes())
        h.update(np.asarray(g["tris"], np.int64).tobytes())
        for _, lp, lt, _, _, _ in g["links"]:
            h.update(lp.tobytes())
            h.update(np.asarray(lt, np.int64).tobytes())
    key = h.hexdigest()
    if _SETUP.get("key") == key:
        return _SETUP["value"]
    fixed_kd = kd([tuple(p) for p in fixed])
    fixed_bvh = BVHTree.FromPolygons([tuple(p) for p in fixed], ftris) if len(ftris) else None
    parts = _still_parts(fixed, ftris, fowner) if len(ftris) else {}
    caps = shell = None
    if j is not None:
        suit = np.concatenate([fixed] + [g["pts"] for g in groups])
        caps = body.Capsules(j, suit[::max(1, len(suit) // 20000)])
        # The shut suit as one surface: a point beyond it, seen from the body's axis, is outside the body.
        verts, tris = [fixed], list(ftris)
        off = len(fixed)
        for g in groups:
            for pts, tr in [(g["pts"], g["tris"])] + [(lp, lt) for _, lp, lt, _, _, _ in g["links"]]:
                verts.append(pts)
                tris += [(a + off, b + off, c + off) for a, b, c in tr]
                off += len(pts)
        shell = BVHTree.FromPolygons([tuple(p) for p in np.concatenate(verts)], tris)
    _SETUP.clear()
    _SETUP.update(key=key, value=(fixed_kd, fixed_bvh, parts, caps, shell))
    return _SETUP["value"]


def _way(groups, fixed):
    """(raster box, the way in, its floor): the inner layer shut, seen from behind. It depends only on the shut suit, so it
    is kept with _setup's value (call _setup first) for the next run."""
    key = tuple(g["layer"] for g in groups)
    if _SETUP.get("wayKey") == key:
        return _SETUP["way"]
    allp = np.concatenate([fixed] + [g["pts"] for g in groups])
    lo_x, hi_x = allp[:, 0].min() - 0.05, allp[:, 0].max() + 0.05
    lo_z, hi_z = allp[:, 2].min() - 0.05, allp[:, 2].max() + 0.05
    box = (lo_x, hi_z, int((hi_x - lo_x) / RES), int((hi_z - lo_z) / RES))
    way = np.zeros((box[3], box[2]), bool)
    floor = np.full(way.shape, np.inf)       # the inside of the way in: the deepest Y of the inner layer, shut
    for g in groups:
        if g["layer"] == "inner":
            m, ys = raster(g["pts"], g["tris"], box, depth="far")
            way |= m
            floor = np.minimum(floor, ys)
    _SETUP.update(wayKey=key, way=(box, way, floor))
    return _SETUP["way"]


def run(scene, only=None, coarse=False):
    """All checks; returns {hinge name: row} with "failed" (ids not allowed) and "allowed" lists, and a summary. `only` (a set
    of hinge names) returns just those pieces' rows, each with every field the full run gives it. `coarse` (a quick look
    for a search, never what is delivered): every row marked "coarse", body every CLASH_EVERY-th frame, clashes every
    second of the clash frames, and nothing measured on the suit as a whole (area, cover, arc, the way in, back clear);
    through as the full run reads it (through()). Every frame it looks at is one the full run looks at too, so body and
    clash never read higher than there; `only` makes the suit-wide figures fill only those pieces' rows (no summary
    figures)."""
    groups, fixed, ftris, j, fowner = collect(scene)
    if not groups:
        return {}, {"pieces": 0}
    by_name = {g["hinge"].name: g for g in groups}
    fixed_kd, fixed_bvh, parts, caps, shell = _setup(groups, fixed, ftris, j, fowner)

    def sunk(pts, d0):
        """How much deeper than shut (d0) each point lies in the body, 0 where the shut suit's shell hides it from the body's
        axis, and in which body part."""
        d, which = caps.depth(pts)
        more = np.clip(d - d0, 0, None)
        feet = caps.foot(pts, which)
        for i in np.nonzero(more > 0.001)[0]:
            v = pts[i] - feet[i]
            length = float(np.linalg.norm(v))
            if length > 0.003 and shell.ray_cast(tuple(feet[i]), tuple(v / length), length - 0.002)[0] is not None:
                more[i] = 0.0
        return more, which
    open_frames = len(groups[0]["open"])
    rows = {}
    for g in groups:
        h = g["hinge"]
        if only is not None and h.name not in only:
            continue
        track = g["open"] + g["close"]
        segs = model.path(h).segments
        hinged = len(segs) == 1 and motion.kind(segs[0]) == "turn" and (model.turns_on_hinge(h) or h.get("fh_parent"))  # its hinge, or a fold's
        p0, a = (np.array(segs[0]["point"]), np.array(segs[0]["axis"])) if hinged else (None, None)
        kind = model.move_of(h).get("kind", "hinge")
        row = {"name": h.name, "pieces": [o.name for o in g["pieces"]], "angle": _round(model.angle_of(h)), "move": kind,
               "frames": [g["f0"], g["f1"]], "failed": []}
        par = by_name.get(h.get("fh_parent", ""))
        if hinged:
            # attach: the piece's surface points nearest the hinge axis, all along it, stay with what they hang on
            on = hinge_points(g, p0, a)
            if len(on) < 2:
                row["attachMm"] = None
                row["failed"].append("attach")
            else:
                worst = 0.0
                if par is None:
                    shut = np.array([fixed_kd.find(tuple(q))[2] for q in on]) if len(fixed) else np.zeros(len(on))
                    for m in track[::2]:
                        q = apply(mat(m), on)
                        gap = np.array([fixed_kd.find(tuple(x))[2] for x in q]) if len(fixed) else np.zeros(len(on))
                        worst = max(worst, float((gap - shut).max()))
                else:
                    ptrack = par["open"] + par["close"]
                    shut = None
                    for k in range(0, len(track), 2):
                        t = kd([tuple(x) for x in apply(mat(ptrack[k]), par["pts"])])
                        gap = np.array([t.find(tuple(x))[2] for x in apply(mat(track[k]), on)])
                        shut = gap if shut is None else shut
                        worst = max(worst, float((gap - shut).max()))
                row["attachMm"] = round(worst * 1000, 1)
                if worst * 1000 > ATTACH_MM:
                    row["failed"].append("attach")
            row["lift"] = round(lift(g, j, p0, a, 1 if segs[0]["turn"] >= 0 else -1), 2)
            if row["lift"] <= LIFT:
                row["failed"].append("lift")
        else:
            row["attachMm"] = None
            if g["links"]:
                worst = 0.0
                for o, lp, _, lo, lc, spec in g["links"]:
                    a0, b0 = np.array(spec["a"]), np.array(spec["b"])
                    for m, lm in zip(track, lo + lc):
                        pm, lmm = mat(m), mat(lm)
                        on_piece = apply(lmm, b0[None])[0] - apply(pm, b0[None])[0]
                        on_body = apply(lmm, a0[None])[0] - a0
                        if spec.get("bRail"):     # that end slides along the piece: only off its rail counts
                            r = pm[:3, :3] @ np.array(spec["bRail"])
                            on_piece -= r * (on_piece @ r)
                        if spec.get("aRail"):
                            r = np.array(spec["aRail"])
                            on_body -= r * (on_body @ r)
                        axis = np.array(spec["axis"])
                        on_body -= axis * (on_body @ axis)     # pins slide along their axis with the piece
                        worst = max(worst, float(np.linalg.norm(on_piece)), float(np.linalg.norm(on_body)))
                row["linksMm"] = round(worst * 1000, 2)
                if worst * 1000 > LINK_MM:
                    row["failed"].append("links")
                # anchor: each link's end on what stays sits on it (the link is pinned on something)
                if fixed_bvh is not None:
                    row["anchorMm"] = round(max(fixed_bvh.find_nearest(tuple(spec["a"]))[3]
                                                for _, _, _, _, _, spec in g["links"]) * 1000, 1)
                    if row["anchorMm"] > ANCHOR_MM:
                        row["failed"].append("anchor")
        # start (every piece, whatever its move): the first stretch of its path goes into no part that stays it was not
        # already against shut (start_hits)
        sample = g["pts"][::max(1, len(g["pts"]) // 400)]
        moved = apply(mat(model.turn(h, START_SHARE)), sample)
        hits, first = start_hits(sample, moved, fixed_bvh, fowner, parts) if fixed_bvh is not None else (0, None)
        row["startPct"] = round(100.0 * hits / max(1, len(sample)), 1)
        if row["startPct"] > START_PCT:
            row["failed"].append("start")
            # where: the point that runs into what stays soonest, what it hits, and the frame it gets there
            reach, p, u, loc, name = first
            k = next((k for k, m in enumerate(g["open"]) if float((apply(mat(m), p[None])[0] - p) @ u) >= reach),
                     len(g["open"]) - 1)
            row["startHit"] = name
            row["startAt"] = [round(float(c), 3) for c in loc]
            row["startFrame"] = k + 1
        # body: the piece and its links
        if caps is not None:
            worst = (0.0, 0, None, None, None)
            stride = max(1, len(g["pts"]) // 600)
            sets = [(g["pts"][::stride], track, [g["pieces"][int(x)].name for x in g["owner"][::stride]])]
            sets += [(lp, lo + lc, [o.name] * len(lp)) for o, lp, _, lo, lc, _ in g["links"]]
            for sample, tr, names in sets:
                d0, _ = caps.depth(sample)
                for k, m in enumerate(tr):
                    if coarse and k % CLASH_EVERY and k != len(tr) - 1:
                        continue
                    posed = apply(mat(m), sample)
                    more, which = sunk(posed, d0)
                    i = int(more.argmax())
                    if more[i] > worst[0]:
                        worst = (float(more[i]), k, caps.name(int(which[i])), names[i], posed[i])
            row["bodyMm"] = round(worst[0] * 1000, 1)
            row["bodyFrame"] = worst[1] + 1
            row["bodyPart"] = worst[2]
            if worst[3] is not None:
                row["bodyObj"] = worst[3]           # the object of the piece (or link) whose point goes deepest
                row["bodyAt"] = [round(float(c), 3) for c in worst[4]]   # that point, at that frame (world)
            if row["bodyMm"] > BODY_MM:
                row["failed"].append("body")
            reg, side = region(h, j)
            row["region"] = reg + ("" if reg == "back" else "." + side)
            if reg not in ("back", "shoulder", "underarm") and model.turns_on_hinge(h) and par is None:
                hh, tt = body.seat(j, reg, side)
                limb = (tt - hh) / np.linalg.norm(tt - hh)
                row["axisDeg"] = round(math.degrees(math.acos(min(1.0, abs(float(limb @ a)) / np.linalg.norm(a)))), 1)
                if row["axisDeg"] > AXIS_DEG:
                    row["failed"].append("axis")
            way = direction(g, j)
            if way is not None:
                row["dirScore"], row["dirWant"], row["dirMove"] = way
                if row["dirScore"] < DIR_MIN:
                    row["failed"].append("dir")
        jump = jumps(g)
        if jump is not None:
            row["jumpMm"], row["jumpFrame"] = jump
            row["failed"].append("jump")
        if fixed_bvh is not None:
            hits = through(groups, g["index"], parts, fixed_bvh, caps, shell)
            for how, (d, frame, where, name) in hits.items():
                key = "through" + how.capitalize()          # throughCross..., throughUnder...
                row[key + "Mm"], row[key + "Frame"] = round(d * 1000, 1), frame
                row[key + "At"] = list(where) if where is not None else None
                if name is None and where is not None:
                    name = fowner[fixed_bvh.find_nearest(where)[2]]
                row[key + "Obj"] = name
            if hits:
                row["throughMm"] = max(row.get("throughCrossMm", 0.0), row.get("throughUnderMm", 0.0),
                                       row.get("throughLiningMm", 0.0))
                if row["throughMm"] > THROUGH_MM:
                    row["failed"].append("through")
        hold = held(g, by_name, fixed_bvh)
        if hold is None:
            gap, at = floating(g, by_name, fixed, fixed_bvh)
            where, overlap = None, None
        else:
            (gap, where, overlap), at = hold, None
        row["floatMm"] = _round(None if gap is None else gap * 1000)
        row["floatAt"], row["floatWhere"] = at, where
        row["railOverlapMm"] = _round(None if overlap is None else overlap * 1000)
        if (gap is not None and row["floatMm"] > FLOAT_MM) or (overlap is not None and row["railOverlapMm"] < RAIL_MM):
            row["failed"].append("float")
        rows[h.name] = row
    # clash: pieces going through each other on the way (more than they do shut)
    chosen = None if only is None else {i for i, g in enumerate(groups) if g["hinge"].name in only}
    for (i, k), (depth, frame, where) in clashes(groups, chosen, CLASH_EVERY * (2 if coarse else 1)).items():
        if depth * 1000 <= CLASH_MM:
            continue
        for a_, b_ in ((i, k), (k, i)):
            row = rows.get(groups[a_]["hinge"].name)
            if row is not None and depth * 1000 > row.get("clashMm", 0.0):
                row["clashMm"] = round(depth * 1000, 1)
                row["clashWith"] = int(groups[b_]["hinge"]["fh_no"])
                row["clashFrame"] = frame
                row["clashAt"] = list(where) if where is not None else None
                if "clash" not in row["failed"]:
                    row["failed"].append("clash")
    if coarse:
        for g in groups:
            row = rows.get(g["hinge"].name)
            if row is not None:
                ok = model.allowed(g["hinge"])
                row["coarse"] = True
                row["allowed"] = [f for f in row["failed"] if f in ok]
                row["failed"] = [f for f in row["failed"] if f not in ok]
        return rows, {"pieces": len(rows), "coarse": True}
    # what follows measures the suit as a whole (each piece's share of it): the same with `only`, filling only its rows
    summary = {"pieces": len(groups), "links": sum(len(g["links"]) for g in groups), "openFrames": open_frames,
               "openSeconds": round(max(g["f1"] for g in groups) / motion.FPS, 2)}
    # area share of the source object
    for g in groups:
        src = bpy.data.objects.get(g["source"])
        rest = sum(p.area for p in model.own(src)[1]) if src is not None and src.type == "MESH" else 0.0
        same = sum(x["area"] for x in groups if x["source"] == g["source"])
        if g["hinge"].name in rows:
            rows[g["hinge"].name]["areaPct"] = round(100 * g["area"] / max(rest + same, 1e-12), 1)
    if j is not None:
        # cover: the way in (inner-layer pieces shut) against every piece and link fully open, from behind
        box, way, floor = _way(groups, fixed)
        total = max(1, int(way.sum()))
        at = open_frames - 1
        covered = np.zeros_like(way)
        for g in groups:
            row = rows.get(g["hinge"].name)
            if row is None and only is not None:
                continue                         # a piece left out: its share is not asked for, nor the summary
            c, ys = raster(apply(mat(g["open"][at]), g["pts"]), g["tris"], box, depth="near")
            for _, lp, lt, lo, _, _ in g["links"]:
                lm, ly = raster(apply(mat(lo[at]), lp), lt, box, depth="near")
                c |= lm
                ys = np.maximum(ys, ly)
            c &= ys >= floor - COVER_BEHIND_M        # in front of the body (a front plate, a forearm hanging forward) hides nothing
            covered |= c
            if row is not None:
                pct = 100.0 * float((way & c).sum()) / total
                row["coverPct"] = round(pct, 1)
                if pct > COVER_PCT:
                    row["failed"].append("cover")
        if only is None:
            summary["wayInCoveredPct"] = round(100.0 * float((way & covered).sum()) / total, 1)
        # arc round each limb
        bins, keys = {}, {}
        for g in groups:
            seg, side = region(g["hinge"], j)
            keys[g["hinge"].name] = key = "%s/%s" % (seg if seg == "back" else seg + "." + side, g["layer"])
            if seg in ("back", "shoulder", "underarm"):
                continue
            hh, tt = body.seat(j, seg, side)
            bins.setdefault(key, set()).update(np.floor(body.angles(hh, tt, g["pts"]) / 2).astype(int).tolist())
        arcs = {k: 2 * len(v) for k, v in bins.items()}
        if only is None:
            summary["limbArcDeg"] = arcs
            summary["limbBackClearDeg"] = back_clear(groups, j, at)
        for g in groups:
            row, key = rows.get(g["hinge"].name), keys[g["hinge"].name]
            if row is not None and arcs.get(key, 0) > ARC_DEG.get(g["layer"], 120.0):
                row["arcDeg"] = arcs[key]
                row["failed"].append("arc")
    for g in groups:
        row = rows.get(g["hinge"].name)
        if row is None:
            continue
        ok = model.allowed(g["hinge"])
        row["allowed"] = [f for f in row["failed"] if f in ok]
        row["failed"] = [f for f in row["failed"] if f not in ok]
    return rows, summary


def describe(row, item):
    v = {"attach": row.get("attachMm"), "links": row.get("linksMm"), "lift": row.get("lift"), "start": row.get("startPct"),
         "body": row.get("bodyMm"), "cover": row.get("coverPct"), "arc": row.get("arcDeg"), "axis": row.get("axisDeg"),
         "float": row.get("floatMm"), "clash": row.get("clashMm"), "dir": row.get("dirScore"),
         "jump": row.get("jumpMm"), "anchor": row.get("anchorMm"), "through": row.get("throughMm")}[item]
    unit = {"attach": " mm", "links": " mm", "lift": "", "start": "%", "body": " mm", "cover": "%", "arc": "°", "axis": "°",
            "float": " mm", "clash": " mm", "dir": "", "jump": " mm", "anchor": " mm", "through": " mm"}[item]
    text = "%s %s" % (NAMES[item], "—" if v is None else "%s%s" % (v, unit))
    if item == "clash" and row.get("clashWith") is not None:
        text += "（与 %s 号，第 %s 帧）" % (row["clashWith"], row.get("clashFrame"))
    if item == "dir" and row.get("dirWant"):
        text += "（应%s）" % row["dirWant"]
    if item == "through":
        parts_ = ["%s %s mm（第 %s 帧，%s，点 %s）" % (label, row[key + "Mm"], row[key + "Frame"], row.get(key + "Obj") or "固定件",
                                                tuple(row.get(key + "At") or ()))
                  for key, label in (("throughCross", "表面刺穿"), ("throughUnder", "钻到壳下"), ("throughLining", "内衬刺穿"))
                  if row.get(key + "Mm") is not None]
        if parts_:
            text += "：" + "；".join(parts_)
    if item == "float" and row.get("floatWhere"):
        text += "（%s）" % row["floatWhere"]
    if item == "float" and row.get("railOverlapMm") is not None:
        text += "（滑出后与导轨重叠 %s mm）" % row["railOverlapMm"]
    if item == "jump" and row.get("jumpFrame") is not None:
        text += "（第 %s 帧）" % row["jumpFrame"]
    if item == "start" and row.get("startHit"):
        text += "（第 %s 帧撞上 %s，点 %s）" % (row.get("startFrame"), row["startHit"], tuple(row.get("startAt") or ()))
    if item == "body" and row.get("bodyObj"):
        text += "（第 %s 帧 %s 的点 %s 进入%s）" % (row.get("bodyFrame"), row["bodyObj"], tuple(row.get("bodyAt") or ()),
                                         row.get("bodyPart"))
    return text


def save(path, rows, summary):
    with open(path, "w", encoding="utf-8") as f:
        json.dump({"summary": summary, "hinges": rows}, f, ensure_ascii=False, indent=1)
