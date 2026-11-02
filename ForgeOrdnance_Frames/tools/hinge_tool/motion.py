"""How a piece moves (Blender's mathutils only, no bpy). The add-on's preview, its checks, the bake and build_frame.py all use
this one module.

A piece's move is a Path: segments run one after another (or partly together), each a rigid motion in world space (metres,
degrees) given as the piece stands when that segment starts:
  {"turn": deg, "point": [x, y, z], "axis": [x, y, z]}     turn about a line (a hinge)
  {"slide": [x, y, z]}                                       a straight slide (a rail)
  {"arc": deg, "axis": [x, y, z], "radius": [x, y, z]}      swing without turning on parallel links: every point moves as
                                                             `radius` (link pivot to piece pivot) does turning `deg` about `axis`
  {"screw": [[4x4]]}                                         the screw motion from rest to a rigid transform (a hand-keyed part)
each with "at": [a, b], when it runs as a share (0..1) of the piece's move (default: one after another, by length), and
"back": true for a segment that goes out and comes back (a flare before the opening). Between two segments that do not
overlap the piece stops at the corner (or waits, for a gap); where they overlap it rounds the corner.

The stroke (Stroke) is the same for every path: it unlatches POP_M along the first segment's axis or rail within POP_S, runs
the path easing in and out (EASE, a curve of EASES), passes its end by OVERSHOOT_DEG (turns and swings) or SLIDE_OVER_M (slides;
both 0: no rebound) and settles within SETTLE_S. Closing runs the pieces in reverse order at CLOSE_PACE of their opening times: each runs back speeding up, latches in
and rebounds REBOUND_M out and back within REBOUND_S. The easing (and the closing's speed-up) shape only the path's final
run, from its last stop on; the segments before it (an unlatch twist, a pause, a push, a flare) keep their own share of the
time both ways (closing gives them as long as opening did, at most CLOSE_PRE of the run), so no segment is squeezed into a
frame.

A piece can hang on another (a chain: a fold's leaf on the leaf before it): its world move is its parent's world move after
its own. A link (a bar of a parallel-link swing or a scissor) follows the piece it carries: link_pose() places it from that
piece's world move, its ends pinned (or sliding on a rail) on the piece and on what stays."""
import math

from mathutils import Matrix, Quaternion, Vector

FPS = 30
POP_M, POP_S = 0.003, 0.08
OVERSHOOT_DEG, SLIDE_OVER_M, SETTLE_S = 3.0, 0.002, 0.1
EASE = "smoothstep"   # the opening's speed over its final run, one of EASES
CLOSE_PACE = 0.7
CLOSE_PRE = 0.45   # at most this share of a closing goes to what came before the final run (an unlatch, a push)
REBOUND_M, REBOUND_S = 0.0015, 0.1

# When a piece moves, in frames at FPS: the outer layer from frame 1, the inner layer from INNER_START; each batch BATCH_STEP
# later, each row down ROW_STEP later, the right side (x < 0) SIDE_STEP after the left, then its own delay. Its move takes
# LENGTH frames (or its own length).
LAYERS = ("outer", "inner")
ANGLE = {"outer": 100.0, "inner": 90.0}
START = {"outer": 0, "inner": 15}
LENGTH = {"outer": 16, "inner": 23}
BATCH_STEP, ROW_STEP, SIDE_STEP = 2, 3, 1
ROWS = ("", "top", "mid", "low")
TURN_REACH = 0.15   # m: a turn's length, for sharing time between segments, is its angle (radians) times this


def frames_of(layer, batch, row, right, delay=0, length=0):
    """(first, last) frame of a piece's opening move."""
    f0 = 1 + START[layer] + BATCH_STEP * batch + ROW_STEP * max(0, ROWS.index(row) - 1) + (SIDE_STEP if right else 0) + delay
    return max(1, f0), max(1, f0) + (length or LENGTH[layer])


def screw_parts(d):
    """The screw motion of the rigid transform `d` (from rest): unit axis, a point on it, angle (radians) and slide along the
    axis; a transform that does not turn is a pure slide (axis along it, angle 0)."""
    axis, angle = d.to_quaternion().to_axis_angle()
    if angle > math.pi:
        axis, angle = -axis, 2 * math.pi - angle
    t = d.translation
    if angle < 1e-6:
        return (t.normalized() if t.length > 1e-9 else Vector((0, 0, 1))), Vector(), 0.0, t.length
    along = t.dot(axis)
    across = t - axis * along
    centre = (across + axis.cross(across) / math.tan(angle / 2)) / 2
    return axis, centre, angle, along


def screw(d, u):
    """The pose `u` (0..1) of the way along the screw motion from rest to `d`: one turn about a fixed axis with a slide along it."""
    axis, centre, angle, along = screw_parts(d)
    if angle == 0.0:
        return Matrix.Translation(axis * along * u)
    return Matrix.Translation(centre + axis * along * u) @ Matrix.Rotation(angle * u, 4, axis) @ Matrix.Translation(-centre)


def smoothstep(u):
    u = min(1.0, max(0.0, u))
    return u * u * (3 - 2 * u)


# 0..1 over 0..1, starting and ending at rest
EASES = {
    "smoothstep": smoothstep,
    "smootherstep": lambda u: (lambda c: c * c * c * (c * (6 * c - 15) + 10))(min(1.0, max(0.0, u))),
    "sine": lambda u: 0.5 - 0.5 * math.cos(math.pi * min(1.0, max(0.0, u))),
}


def hinge_turn(point, axis, degrees):
    """The rigid transform turning about the line (point, axis) by `degrees`."""
    point, axis = Vector(point), Vector(axis)
    return Matrix.Translation(point) @ Matrix.Rotation(math.radians(degrees), 4, axis) @ Matrix.Translation(-point)


# ------------------------------------------------------------------------------------------------------------- segments
def turn(point, axis, deg, at=None, back=False):
    return _seg({"turn": float(deg), "point": list(point), "axis": list(Vector(axis).normalized())}, at, back)


def slide(by, at=None, back=False):
    return _seg({"slide": list(by)}, at, back)


def arc(axis, radius, deg, at=None):
    return _seg({"arc": float(deg), "axis": list(Vector(axis).normalized()), "radius": list(radius)}, at, False)


def _seg(s, at, back):
    if at is not None:
        s["at"] = [float(at[0]), float(at[1])]
    if back:
        s["back"] = True
    return s


def kind(s):
    return next(k for k in ("turn", "slide", "arc", "screw") if k in s)


def seg_pose(s, u):
    """The rigid transform of segment `s` at `u` (0..1, beyond 1 for the overshoot)."""
    k = kind(s)
    if k == "turn":
        return hinge_turn(Vector(s["point"]), Vector(s["axis"]), s["turn"] * u)
    if k == "slide":
        return Matrix.Translation(Vector(s["slide"]) * u)
    if k == "arc":
        r = Vector(s["radius"])
        return Matrix.Translation(Matrix.Rotation(math.radians(s["arc"] * u), 3, Vector(s["axis"])) @ r - r)
    return screw(Matrix(s["screw"]), u)


def reach(s):
    """How far a segment carries the piece (metres), for sharing time."""
    k = kind(s)
    if k == "turn":
        return abs(math.radians(s["turn"])) * TURN_REACH
    if k == "slide":
        return Vector(s["slide"]).length
    if k == "arc":
        return abs(math.radians(s["arc"])) * Vector(s["radius"]).length
    _, _, angle, along = screw_parts(Matrix(s["screw"]))
    return max(angle * TURN_REACH, abs(along))


def overshoot(s):
    """The overshoot past the segment's end, as a share of the segment."""
    k = kind(s)
    if s.get("back"):
        return 0.0
    if k in ("turn", "arc"):
        return OVERSHOOT_DEG / abs(s[k]) if abs(s[k]) > 1e-3 else 0.0
    if k == "slide":
        n = Vector(s["slide"]).length
        return SLIDE_OVER_M / n if n > 1e-6 else 0.0
    _, _, angle, along = screw_parts(Matrix(s["screw"]))
    if angle > 1e-3:
        return math.radians(OVERSHOOT_DEG) / angle
    return SLIDE_OVER_M / along if along > 1e-6 else 0.0


def first_axis(s):
    """The line a segment starts along: a turn's or swing's axis (the way it turns about), a slide's rail."""
    k = kind(s)
    if k in ("turn", "arc"):
        return Vector(s["axis"]).normalized() * (1 if s[k] >= 0 else -1)
    if k == "slide":
        v = Vector(s["slide"])
        return v.normalized() if v.length > 1e-9 else Vector((0, 0, 1))
    return screw_parts(Matrix(s["screw"]))[0].normalized()


def _hermite(u, m0, m1):
    """0..1 over 0..1 with end slopes m0, m1 (1: straight on, 0: stops there); straight on past 1."""
    if u >= 1:
        return 1 + (u - 1) * m1
    u2, u3 = u * u, u * u * u
    return (u3 - 2 * u2 + u) * m0 + (-2 * u3 + 3 * u2) + (u3 - u2) * m1


class Path:
    """A piece's move: its segments, with their times filled in (`at`). Times past 1 are scaled so the last ends at 1; a
    path whose segments all end before 1 keeps them where they are written and holds still after the last (a piece
    hanging on another pushes out early, then rides on it)."""

    def __init__(self, segments):
        segs = [dict(s) for s in segments]
        if any("at" not in s for s in segs):
            total = sum(max(reach(s), 1e-4) for s in segs)
            t = 0.0
            for s in segs:
                share = max(reach(s), 1e-4) / total
                s.setdefault("at", [t, t + share])
                t += share
        end = max((s["at"][1] for s in segs), default=1.0)
        if end > 1.0 + 1e-9:
            for s in segs:
                s["at"] = [s["at"][0] / end, s["at"][1] / end]
        self.segments = segs
        eps = 1e-6
        self.slopes = []
        for s in segs:
            a, b = s["at"]
            open_a = any(o is not s and o["at"][0] < a - eps and o["at"][1] > a + eps for o in segs)
            open_b = any(o is not s and o["at"][0] < b - eps and o["at"][1] > b + eps for o in segs)
            self.slopes.append((0.0 if a > eps and not open_a else 1.0, 0.0 if b < 1 - eps and not open_b else 1.0))
        last = [s for s in segs if s["at"][1] >= 1 - eps and not s.get("back")]
        self.over = overshoot(last[0]) * (last[0]["at"][1] - last[0]["at"][0]) if last else 0.0
        self.out = first_axis(min(segs, key=lambda s: s["at"][0])) if segs else Vector((0, 0, 1))
        # The final run: from the last stop (a time no segment runs across) before the segments that end the path. The
        # stroke's easing (and on closing its speed-up) shapes only this run; what comes before it (an unlatch, a
        # pause, a push) keeps its own share of the time, so it is never squeezed into a frame or two.
        ends = [s["at"][0] for s in last] or [0.0]
        cut = min(ends)
        while cut > eps:
            across = [o["at"][0] for o in segs if o["at"][0] < cut - eps and o["at"][1] > cut + eps]
            if not across:
                break
            cut = min(across)
        self.final = cut if cut > eps else 0.0

    @classmethod
    def hinge(cls, point, axis, deg):
        return cls([turn(point, axis, deg)])

    def to_json(self):
        return [dict(s) for s in self.segments]

    def at(self, e):
        """The rigid transform `e` (0..1, a little past 1 in the overshoot) of the way along the path."""
        m = Matrix.Identity(4)
        for s, (m0, m1) in zip(self.segments, self.slopes):
            a, b = s["at"]
            u = (e - a) / max(b - a, 1e-9)
            if u <= 0:
                continue
            if s.get("back"):
                u = math.sin(math.pi * min(1.0, u))
            elif u < 1 or b >= 1 - 1e-6:
                u = _hermite(u, m0, m1)
            else:
                u = 1.0
            m = seg_pose(s, u) @ m
        return m

    def end(self):
        return self.at(1.0)


class Stroke:
    """One piece's opening and closing on its Path over `length` seconds of opening: `opening(t)` and `closing(t)`, its own
    move t seconds after its opening (closing) starts."""

    def __init__(self, path, length):
        self.path, self.length = path, max(length, 1.0 / FPS)
        # Unlatching moves the piece along its first axis or rail, so a hinge never leaves what it hangs on.
        self.out = Vector(path.out).normalized()
        self.pop = min(POP_S, 0.3 * self.length)
        self.over = path.over
        self.settle = min(SETTLE_S, 0.4 * (self.length - self.pop))
        self.close_length = CLOSE_PACE * self.length
        self.latch = min(POP_S, 0.3 * self.close_length) * CLOSE_PACE
        self.final = getattr(path, "final", 0.0)

    def open_share(self, v):
        """How far along the path (e, 0..1 and the overshoot) the opening is at `v` (0..1) of its run after the unlatch: what
        comes before the final run at its own pace, the final run easing in and out (EASE), past its end and settling back."""
        a = self.final
        if v < a:
            return v
        run = (self.length - self.pop) * (1 - a)
        x = (v - a) / (1 - a)
        main = 1 - min(self.settle, 0.4 * run) / run
        over = self.over / (1 - a)
        if x < main:
            w = (1 + over) * EASES[EASE](x / main)
        else:
            w = 1 + over * (1 - smoothstep((x - main) / (1 - main)))
        return a + (1 - a) * w

    def close_share(self, v):
        """How far along the path the closing is at `v` (0..1) of its run before the latch: the final run back speeding up,
        then what came before it back at its own pace."""
        if self.final <= 0:
            return 1 - v * v
        # what comes before the final run takes back as long as it took opening (it is short: an unlatch, a push), at
        # most CLOSE_PRE of the run, so it is not squeezed by the faster closing
        run = self.close_length - self.latch
        q = 1 - min(CLOSE_PRE, self.final * (self.length - self.pop) / run)
        if v < q:
            x = v / q
            return 1 - q * x * x
        return self.final * (1 - (v - q) / (1 - q))

    def opening(self, t):
        if t <= 0:
            return Matrix.Identity(4)
        if t < self.pop:
            return Matrix.Translation(self.out * POP_M * smoothstep(t / self.pop))
        run = self.length - self.pop
        v = min(1.0, (t - self.pop) / run)
        return Matrix.Translation(self.out * POP_M * (1 - smoothstep(v / 0.5))) @ self.path.at(self.open_share(v))

    def closing(self, t):
        run = self.close_length - self.latch
        if t <= 0:
            return self.path.end()
        if t < run:
            v = t / run
            return Matrix.Translation(self.out * POP_M * smoothstep(v / 0.3)) @ self.path.at(self.close_share(v))
        if t < self.close_length:
            return Matrix.Translation(self.out * POP_M * (1 - (t - run) / self.latch))
        w = (t - self.close_length) / REBOUND_S
        return Matrix.Translation(self.out * REBOUND_M * math.sin(math.pi * w)) if w < 1 else Matrix.Identity(4)


# ---------------------------------------------------------------------------------------------------------------- links
def link_pose(spec, carried):
    """Where a link is (its rigid move from shut) when the piece it carries has moved by `carried`. spec (world, shut):
    "a" its end on what stays, "b" its end on the piece, "axis" its pins' axis; "aRail" (world) or "bRail" (on the piece) a
    rail that end slides on, the other end pinned. The link keeps its length; its pins slide along their axis with the piece."""
    a0, b0, n = Vector(spec["a"]), Vector(spec["b"]), Vector(spec["axis"]).normalized()
    length = (b0 - a0).length
    if spec.get("bRail"):
        r = (carried.to_3x3() @ Vector(spec["bRail"])).normalized()
        b = _on_rail(carried @ b0, r, a0, length)
        a = a0
    elif spec.get("aRail"):
        b = carried @ b0
        a = _on_rail(a0, Vector(spec["aRail"]).normalized(), b, length)
    else:
        b = carried @ b0
        a = a0 + n * (b - b0).dot(n)
    d0, d = b0 - a0, b - a
    p0, p = d0 - n * d0.dot(n), d - n * d.dot(n)
    angle = p0.angle(p, 0.0) if p0.length > 1e-9 and p.length > 1e-9 else 0.0
    if p0.cross(p).dot(n) < 0:
        angle = -angle
    return Matrix.Translation(a) @ Matrix.Rotation(angle, 4, n) @ Matrix.Translation(-a0)


def _on_rail(p, r, centre, length):
    """The point p + r s nearest p that lies `length` from `centre`."""
    q = p - centre
    bq = r.dot(q)
    disc = bq * bq - (q.dot(q) - length * length)
    if disc < 0:
        return p - r * bq
    root = math.sqrt(disc)
    s = min((-bq + root, -bq - root), key=abs)
    return p + r * s


# ------------------------------------------------------------------------------------------------------------- sequence
def close_starts(spans):
    """When each piece starts closing (seconds), given its opening (begin, done) in seconds: the last to finish opening starts
    closing first, every start CLOSE_PACE of its distance from the end of the opening."""
    end = max((s[1] for s in spans), default=0.0)
    return [(end - done) * CLOSE_PACE for _, done in spans]


def sequence(parts):
    """The whole open-then-close sequence. `parts`: dicts, a piece {"path": Path, "frames": (first, last), "parent": index of
    the piece it hangs on or None} or a link {"link": spec, "driver": index of the piece it carries}. Returns (open tracks,
    close tracks, events): per part one world move a frame (the open clip from the shut pose, the close clip from the open
    pose to shut), and its events in seconds (unlock, turn, open, closeStart, latch; a link has its piece's)."""
    own = {}
    for i, p in enumerate(parts):
        if "path" in p:
            f0, f1 = p["frames"]
            own[i] = (Stroke(p["path"], (f1 - f0) / FPS), (f0 - 1) / FPS)
    keys = list(own)
    starts = dict(zip(keys, close_starts([(b, b + s.length) for s, b in (own[i] for i in keys)])))
    open_frames = max((parts[i]["frames"][1] for i in keys), default=1) + int(math.ceil(SETTLE_S * FPS))
    total = max((starts[i] + own[i][0].close_length for i in keys), default=0.0) + REBOUND_S
    close_frames = int(math.ceil(total * FPS)) + 1
    opened, closed = {}, {}

    def piece(i):
        if i not in opened:
            s, begin = own[i]
            o = [s.opening(k / FPS - begin) for k in range(open_frames)]
            c = [s.closing(k / FPS - starts[i]) for k in range(close_frames)]
            c[-1] = Matrix.Identity(4)
            parent = parts[i].get("parent")
            if parent is not None:
                po, pc = piece(parent)
                o = [a @ b for a, b in zip(po, o)]
                c = [a @ b for a, b in zip(pc, c)]
            opened[i], closed[i] = o, c
        return opened[i], closed[i]

    events = {}
    for i in keys:
        piece(i)
        s, begin = own[i]
        events[i] = {"unlock": begin, "turn": begin + s.pop, "open": begin + s.length, "closeStart": starts[i],
                     "latch": starts[i] + s.close_length}
    out_open, out_close, out_events = [], [], []
    for i, p in enumerate(parts):
        if "path" in p:
            out_open.append(opened[i])
            out_close.append(closed[i])
            out_events.append(events[i])
        else:
            d = p["driver"]
            out_open.append([link_pose(p["link"], m) for m in opened[d]])
            out_close.append([link_pose(p["link"], m) for m in closed[d]])
            out_events.append(dict(events[d]))
    return out_open, out_close, out_events
