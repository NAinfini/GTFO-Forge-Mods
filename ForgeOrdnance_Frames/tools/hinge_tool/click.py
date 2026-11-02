"""点选开合: the one click tool, in Object mode. Click a plate, hover and click its lines (the cut, then the hinge), drag to set
the angle (the piece swings live, its far edge following the cursor), Enter. F flips, digits type the angle, wheel offers the next line, Esc steps back,
Ctrl+Z takes the last piece back. The preset (模板) only sets which lines are asked for, how
many pieces come out and the default angle and layer."""
import math

import bpy
import numpy as np
from bpy_extras import view3d_utils
from mathutils import Vector

from . import body, draw, geom, model, motion, moves

# Each preset: the lines it asks for (name, colour role), the layer, and how the pieces are made.
TEMPLATES = {
    "strip": {"label": "四肢外甲条", "lines": (("切口", "cut"), ("铰链", "hinge")), "layer": "outer"},
    "inner3": {"label": "内层三线", "lines": (("中线切口", "cut"), ("铰链一", "hinge"), ("铰链二", "hinge")), "layer": "inner"},
    "back6": {"label": "背部6块", "lines": (("中线", "cut"), ("行线一", "cut"), ("行线二", "cut")), "layer": "inner"},
    "top": {"label": "上背上翻", "lines": (), "layer": "outer"},
    "whole": {"label": "整块开合", "lines": (("铰链", "hinge"),), "layer": "outer"},
}
COLOURS = {"cut": (1.0, 0.15, 0.1, 1.0), "hinge": (0.15, 0.45, 1.0, 1.0), "hover": (1.0, 0.95, 0.1, 1.0)}
PLATE_TINT = (1.0, 1.0, 1.0, 0.18)
PIECE_TINT = (1.0, 0.55, 0.1, 0.45)
BATCH = {"back": 0, "upperarm": 1, "forearm": 2, "thigh": 3, "shin": 4}
BACK_FACING = 0.2       # the back preset works on the clicked shell's faces turned at least this much backward (+Y)
BACK_MARGIN = 0.02      # m: and no farther than this past the ends of its lines


def strokes_world():
    out = []
    for a in bpy.data.annotations:
        for l in a.layers:
            if l.annotation_hide or l.is_ruler:
                continue
            for fr in l.frames[:1]:
                for s in fr.strokes:
                    pts = [p.co.copy() for p in s.points]
                    if len(pts) >= 2:
                        out.append(pts)
    return out


class Group:
    """Pieces made together (one preset use): [(piece, hinge, source name)]."""

    def __init__(self, items):
        self.items = items


class FH_OT_click(bpy.types.Operator):
    bl_idname = "fh.click"
    bl_label = "点选开合"
    bl_options = {"REGISTER", "UNDO"}

    def invoke(self, context, event):
        if context.area.type != "VIEW_3D":
            return {"CANCELLED"}
        if context.object and context.object.mode != "OBJECT":
            bpy.ops.object.mode_set(mode="OBJECT")
        self.template = context.scene.fh_template
        self.plate = None
        self.lines = []          # chosen lines (lists of bmesh edges)
        self.hover = []          # offered lines at the cursor
        self.hover_k = 0
        self.group = None        # the pieces being angled
        self.done = []           # confirmed groups (for Ctrl+Z and M)
        self.drag = None
        self.typed = ""
        self.strokes = strokes_world()
        self.j = body.joints(context.scene)
        context.window_manager.modal_handler_add(self)
        self.status(context)
        return {"RUNNING_MODAL"}

    # ------------------------------------------------------------------------------------------------------------ text
    def status(self, context, note=""):
        t = TEMPLATES[self.template]
        if self.group:
            text = "拖动设角度  F 翻转  数字输入角度  回车确定  Esc 取消"
            if self.typed:
                text = "角度 %s°  回车确定" % self.typed
        elif self.plate is None:
            text = "%s：点甲片" % t["label"]
        else:
            name = t["lines"][len(self.lines)][0] if len(self.lines) < len(t["lines"]) else ""
            text = "点%s  滚轮换线  Esc 退一步" % name
        extra = "  Ctrl+Z 撤回" if self.done and not self.group else ""
        context.area.header_text_set(text + extra + ("    " + note if note else ""))

    # ------------------------------------------------------------------------------------------------------------ input
    def modal(self, context, event):
        try:
            return self.step(context, event)
        except Exception as e:   # never leave the viewport stuck in the tool
            self.finish(context)
            self.report({"ERROR"}, "点选开合出错：%s" % e)
            raise

    def step(self, context, event):
        if event.type in {"MIDDLEMOUSE", "TRACKPADPAN", "TRACKPADZOOM"} or (
                event.type in {"WHEELUPMOUSE", "WHEELDOWNMOUSE"} and (event.ctrl or event.shift or not self.plate)):
            return {"PASS_THROUGH"}
        if self.group:
            return self.angle_step(context, event)
        if event.type == "MOUSEMOVE":
            self.on_hover(context, event)
        elif event.type in {"WHEELUPMOUSE", "WHEELDOWNMOUSE"} and event.value == "PRESS" and self.hover:
            self.hover_k = (self.hover_k + (1 if event.type == "WHEELDOWNMOUSE" else -1)) % len(self.hover)
            self.paint()
        elif event.type == "LEFTMOUSE" and event.value == "PRESS":
            self.on_click(context, event)
        elif event.type == "Z" and event.ctrl and event.value == "PRESS":
            self.undo_last(context)
        elif event.type in {"ESC", "RIGHTMOUSE"} and event.value == "PRESS":
            if self.lines:
                self.lines.pop()
            elif self.plate is not None:
                self.drop_plate()
            else:
                self.finish(context)
                return {"FINISHED"} if self.done else {"CANCELLED"}
            self.paint()
        self.status(context)
        return {"RUNNING_MODAL"}

    def ray(self, context, event):
        region, rv3d = context.region, context.region_data
        co = (event.mouse_region_x, event.mouse_region_y)
        origin = view3d_utils.region_2d_to_origin_3d(region, rv3d, co)
        direction = view3d_utils.region_2d_to_vector_3d(region, rv3d, co)
        dg = context.evaluated_depsgraph_get()
        hit, loc, nrm, idx, obj, _ = context.scene.ray_cast(dg, origin, direction)
        if not hit or obj.type != "MESH":
            return None, None, None
        return obj.original, loc, idx

    def on_hover(self, context, event):
        obj, loc, idx = self.ray(context, event)
        if self.plate is None:
            draw.set_layer("fh_plate", [("tris", draw.mesh_tris(obj), PLATE_TINT)] if self.usable(obj) else [])
            return
        need = TEMPLATES[self.template]["lines"]
        if obj is not self.plate.obj or len(self.lines) >= len(need):
            self.hover = []
        else:
            p = obj.matrix_world.inverted() @ loc
            inv = obj.matrix_world.inverted()
            self.hover = [self.clip(l, p) for _, l in geom.candidates(self.plate, p, [[inv @ q for q in s] for s in self.strokes])]
            self.hover = [l for l in self.hover if l]
            self.hover_k = 0
        self.paint()

    def usable(self, obj):
        return obj is not None and not obj.get("fh_still") and not obj.get("fh_hinge") and not obj.get("fh_link")

    def clip(self, edges, p):
        """With joints: only the run of the line along the body part the cursor is on (a seam running on down the leg stops
        at the knee)."""
        if self.j is None or not edges:
            return edges
        m = self.plate.obj.matrix_world
        reg, side, _ = body.region(self.j, np.array([tuple(m @ p)]))
        h, t = body.seat(self.j, reg, side if side in ("L", "R") else "L")
        d = t - h
        ok = [-0.03 <= float((np.array(tuple(m @ ((e.verts[0].co + e.verts[1].co) / 2))) - h) @ d / (d @ d)) <= 1.03
              for e in edges]
        k = min(range(len(edges)), key=lambda i: ((edges[i].verts[0].co + edges[i].verts[1].co) / 2 - p).length)
        if not ok[k]:
            return edges
        a = b = k
        while a > 0 and ok[a - 1]:
            a -= 1
        while b < len(edges) - 1 and ok[b + 1]:
            b += 1
        return edges[a:b + 1]

    def on_click(self, context, event):
        obj, loc, idx = self.ray(context, event)
        if self.plate is None:
            if not self.usable(obj):
                return
            if self.template == "back6":
                self.plate = BackPlate(obj, idx)
            else:
                self.plate = geom.Plate(obj, idx)
            if not TEMPLATES[self.template]["lines"]:
                self.make(context)
            self.paint()
            return
        need = TEMPLATES[self.template]["lines"]
        if self.hover and len(self.lines) < len(need):
            self.lines.append(self.hover[self.hover_k])
            self.hover = []
            if len(self.lines) == len(need):
                self.make(context)
            self.paint()

    def drop_plate(self):
        if self.plate is not None:
            self.plate.free()
        self.plate, self.lines, self.hover = None, [], []
        draw.clear("fh_plate")

    # ------------------------------------------------------------------------------------------------------------ pieces
    def make(self, context):
        """Cut the pieces the preset makes from the chosen lines and start angling them."""
        try:
            group = cut(self.plate, self.lines, self.template, self.j)
        except Refused as r:
            if self.lines:
                self.lines.pop()
            self.status(context, str(r))
            return
        self.plate = None
        self.group = Group(group)
        first = group[0][0]
        pts = [model.base(first) @ v.co for v in first.data.vertices]
        p, a = model.line(group[0][1])
        dist = [((q - p) - a * (q - p).dot(a)).length for q in pts]
        cut_at = sorted(dist)[int(0.8 * (len(dist) - 1))]
        far = [q for q, d in zip(pts, dist) if d >= cut_at]
        self.far = sum(far, Vector()) / len(far)        # the far edge of the first piece, shut
        self.lines, self.hover = [], []
        self.drag = None
        self.typed = ""
        self.pose_group()
        draw.clear("fh_plate")

    def pose_group(self, fraction=1.0):
        for piece, h, _ in self.group.items:
            piece.matrix_world = model.turn(h, fraction=fraction) @ model.base(piece)
        self.paint()

    # ------------------------------------------------------------------------------------------------------------ angle
    def angle_step(self, context, event):
        hs = [h for _, h, _ in self.group.items]
        if event.type == "LEFTMOUSE":
            self.drag = event.value == "PRESS"
            if self.drag:
                self.follow(context, event)
        elif event.type == "MOUSEMOVE" and self.drag:
            self.follow(context, event)
        elif event.type == "F" and event.value == "PRESS":
            for h in hs:
                h["fh_angle"] = -h["fh_angle"]
            self.pose_group()
        elif event.unicode and event.unicode in "0123456789." and event.value == "PRESS":
            self.typed += event.unicode
        elif event.type == "BACK_SPACE" and event.value == "PRESS":
            self.typed = self.typed[:-1]
        elif event.type in {"RET", "NUMPAD_ENTER"} and event.value == "PRESS":
            if self.typed:
                try:
                    v = float(self.typed)
                    for h in hs:
                        h["fh_angle"] = math.copysign(v, h["fh_angle"] or 1)
                except ValueError:
                    pass
            self.confirm()
        elif event.type == "ESC" and event.value == "PRESS":
            self.take_back(self.group)
            self.group = None
        self.status(context)
        return {"RUNNING_MODAL"}

    def follow(self, context, event):
        """The first piece turns so its far edge comes nearest the cursor on screen; the others turn with it in proportion."""
        piece, h0, _ = self.group.items[0]
        far, base = self.far, h0["fh_angle"] or 1.0
        ratios = [h["fh_angle"] / base for _, h, _ in self.group.items]
        mouse = Vector((event.mouse_region_x, event.mouse_region_y))
        best = None
        for a in range(-180, 181, 2):
            p = view3d_utils.location_3d_to_region_2d(context.region, context.region_data,
                                                      motion.hinge_turn(*model.line(h0), a) @ far)
            if p is not None and (best is None or (p - mouse).length < best[0]):
                best = ((p - mouse).length, a)
        if best is None:
            return
        for (_, h, _), r in zip(self.group.items, ratios):
            h["fh_angle"] = float(best[1]) * r if best[1] else 0.0
        self.pose_group()

    def confirm(self):
        self.pose_group(0.0)
        self.done.append(self.group)
        self.group = None
        self.typed = ""
        self.paint()

    def take_back(self, group):
        take_back(group)
        self.paint()

    def undo_last(self, context):
        if self.done:
            self.take_back(self.done.pop())
            self.status(context, "已撤回")

    # ------------------------------------------------------------------------------------------------------------ draw
    def paint(self):
        items = []
        if self.plate is not None:
            m = self.plate.obj.matrix_world
            items.append(("tris", draw.face_tris(self.plate.obj, self.plate.faces), PLATE_TINT))
            roles = TEMPLATES[self.template]["lines"]
            for l, (_, role) in zip(self.lines, roles):
                items.append(("lines", draw.polyline([m @ p for p in geom.chain_points(l)]), COLOURS[role], True, 5.0))
            if self.hover:
                items.append(("lines", draw.polyline([m @ p for p in geom.chain_points(self.hover[self.hover_k])]),
                              COLOURS["hover"], True, 5.0))
        if self.group:
            for piece, h, _ in self.group.items:
                items.append(("tris", draw.mesh_tris(piece), PIECE_TINT))
        draw.set_layer("fh_click", items)
        overlay_hinges()

    def finish(self, context):
        if self.group:
            self.confirm()
        if self.plate is not None:
            self.plate.free()
            self.plate = None
        draw.clear("fh_click")
        draw.clear("fh_plate")
        context.area.header_text_set(None)
        overlay_hinges()


# ---------------------------------------------------------------------------------------------------------------- helpers
class Refused(Exception):
    pass


def cut(plate, lines, t, j):
    """The pieces preset `t` makes of the plate with the chosen lines, split off, each with its hinge (default turn: the way
    its far edge lifts off the plate) and order. Frees the plate. Returns [(piece, hinge, source name)]."""
    obj = plate.obj
    m = obj.matrix_world
    layer = TEMPLATES[t]["layer"]
    made = []   # (faces, hinge points (object space), row)
    if t == "strip":
        faces = geom.between(plate, lines[0], lines[1])
        if not faces:
            raise Refused("这两条线围不出一块，换一条")
        made.append((faces, geom.chain_points(lines[1]), ""))
    elif t == "whole":
        made.append((set(plate.faces), geom.chain_points(lines[0]), ""))
    elif t == "top":
        zs = sorted((m @ v.co).z for v in plate.verts)
        crest = max(zs[-1] - 0.01, zs[int(0.98 * (len(zs) - 1))])     # the top ridge: within 1 cm of the top
        made.append((set(plate.faces), [v.co.copy() for v in plate.verts if (m @ v.co).z >= crest], ""))
    elif t == "inner3":
        for k in (1, 2):
            faces = geom.between(plate, lines[0], lines[k])
            if not faces:
                raise Refused("中线和铰链%d围不出一块" % k)
            made.append((faces, geom.chain_points(lines[k]), ""))
    elif t == "back6":
        made = plate.blocks(lines)
        if not made:
            raise Refused("三条线没把背分成块")
    seen = set()
    for faces, _, _ in made:
        if not faces:
            raise Refused("有一块是空的，换一条线")
        if seen & faces:
            raise Refused("两块重叠了：两条铰链在切口同一侧，换一条")
        seen |= faces
    absorb_slivers(plate, made)
    items = []
    base_name = obj.name.split(".")[0]
    first_no = max((int(h.get("fh_no", 0)) for h in model.hinges()), default=0) + 1    # pieces are named by their number
    for k, (faces, hinge_pts, row) in enumerate(made):
        fl = list(faces)
        on_piece = geom.kd([m @ v.co for f in fl for v in f.verts])
        pts = [m @ p for p in hinge_pts]
        along = [p for p in pts if on_piece.find(p)[2] <= HINGE_ON_PIECE]    # only the run of the line along the piece
        centre, axis = geom.axis_of(along if len(along) >= 2 else pts)
        if t == "top":          # the top ridge: level
            axis = Vector((axis.x, axis.y, 0)).normalized()
        elif t == "back6":      # each block opens like a door on its outer side: upright
            axis = Vector((0, 0, 1))
        made[k] = (faces, [m.inverted() @ p for p in (along if len(along) >= 2 else pts)], row)
        centre = touching(centre, axis, [m @ v.co for f in fl for v in f.verts], along or pts)
        face = ([tuple(m @ f.calc_center_median()) for f in fl], [tuple(m.to_3x3() @ f.normal) for f in fl],
                [f.calc_area() for f in fl])
        up = geom.lift(*face, centre, axis, 1) >= geom.lift(*face, centre, axis, -1)
        piece_pts = [tuple(m @ v.co) for v in {v for f in fl for v in f.verts}]
        batch = 0
        if j is not None:
            reg, side, over = body.region(j, np.array(piece_pts))
            batch = 0 if over else BATCH.get(reg, 0)
        items.append(({f.index for f in faces}, centre, axis, (1 if up else -1) * motion.ANGLE[layer], batch, row,
                      "%d号-%s" % (first_no + k, base_name)))
    roles = [r for _, r in TEMPLATES[t]["lines"]]
    cuts = [[m @ p for p in geom.chain_points(l)] for l, r in zip(lines, roles) if r == "cut"]
    ends = []     # each hinge drawn as what turns: its straight axis, as long as its points reach
    for (_, centre, axis, _, _, _, _), (_, pts, _) in zip(items, made):
        ts = [(m @ p - centre).dot(axis) for p in pts]
        ends.append([centre + axis * min(ts), centre + axis * max(ts)])
    plate.free()
    pieces = split_many(obj, [it[0] for it in items], [it[6] for it in items])
    group = []
    for piece, (_, centre, axis, angle, batch, row, name), hinge_pts in zip(pieces, items, ends):
        h = model.make_hinge(centre, axis, angle, layer, batch, row, t, name="铰链-" + piece.name)
        model.set_lines(h, cuts, hinge_pts)
        model.attach(piece, h, obj.name)
        model.remember_base(piece)
        group.append((piece, h, obj.name))
    return group


SLIVER_FACES = 30       # what a cut leaves of the plate this small (and under SLIVER_SHARE of the pieces' area) joins the
SLIVER_SHARE = 0.03     # piece it touches most, so no loose crumbs stay behind


HINGE_ON_PIECE = 0.015  # m: hinge line points this close to the piece are the ones it turns on


def touching(centre, axis, piece_pts, hinge_pts):
    """The hinge axis moved sideways onto the piece: through the piece point nearest it within the hinge's reach (a curved
    line's best straight axis floats off the surface otherwise)."""
    ts = [(p - centre).dot(axis) for p in hinge_pts]
    lo, hi = min(ts), max(ts)
    best = None
    for p in piece_pts:
        r = p - centre
        s = r.dot(axis)
        if lo <= s <= hi:
            off = r - axis * s
            if best is None or off.length < best.length:
                best = off
    return centre + best if best is not None else centre


def absorb_slivers(plate, made):
    owner = {f: k for k, (faces, _, _) in enumerate(made) for f in faces}
    total = sum(geom.area(faces) for faces, _, _ in made)
    rest = set(plate.island) - owner.keys()
    while rest:
        comp = geom.grow({next(iter(rest))}, rest)
        rest -= comp
        if len(comp) > SLIVER_FACES or geom.area(comp) > SLIVER_SHARE * total:
            continue
        votes = {}
        for f in comp:
            for e in f.edges:
                for g in e.link_faces:
                    if g in owner:
                        votes[owner[g]] = votes.get(owner[g], 0) + 1
        if votes:
            made[max(votes, key=votes.get)][0].update(comp)


def take_back(group):
    """Put a group's pieces back into what they were cut from (seam welded) and remove their hinges."""
    for piece, h, source in reversed(group.items):
        src = bpy.data.objects.get(source)
        piece.matrix_world = model.base(piece)
        if src is not None:
            geom.merge_back(piece, src)
        else:
            bpy.data.objects.remove(piece, do_unlink=True)
        if h is not None and h.name in bpy.data.objects and not model.pieces_of(h):
            bpy.data.objects.remove(h, do_unlink=True)


class BackPlate(geom.Plate):
    """The clicked shell's back: its faces linked to the clicked one and turned backward (the sides and shoulders, where the
    shell turns away, close it off). blocks(): the six blocks the middle line and two row lines make, each hinged on its own
    outer side."""

    def __init__(self, obj, face_index):
        super().__init__(obj, face_index)
        m3 = obj.matrix_world.to_3x3()
        facing = {f for f in self.faces if (m3 @ f.normal).normalized().y >= BACK_FACING}
        seed = self.bm.faces[face_index]
        keep, stack = {seed}, [seed]
        while stack:
            for e in stack.pop().edges:
                for g in e.link_faces:
                    if g in facing and g not in keep:
                        keep.add(g)
                        stack.append(g)
        self.faces = keep
        self.index()

    def blocks(self, lines):
        """Left/right of the middle line and above/between/below the two row lines; each block hinged on its outer rim."""
        m = self.obj.matrix_world
        mid = geom.chain_points(lines[0])
        rows = sorted(lines[1:], key=lambda l: -sum((m @ p).z for p in geom.chain_points(l)))   # upper row line first
        up, low = [sum(geom.chain_points(l), Vector()) / (len(l) + 1) for l in rows]
        side = geom.side_field(self, lines[0], mid[0] + Vector((1, 0, 0)))          # positive: +x (the suit's left)
        above = geom.side_field(self, rows[0], up + (up - low))                       # positive: above the upper row line
        below = geom.side_field(self, rows[1], low - (up - low))                      # positive: below the lower one
        # Only the back the lines span: from the middle line's top to its bottom, out to the row lines' ends.
        mz = [(m @ p).z for p in mid]
        mid_x = sum((m @ p).x for p in mid) / len(mid)
        reach = max(abs((m @ p).x - mid_x) for l in rows for p in geom.chain_points(l)) + BACK_MARGIN
        lo, hi = min(mz) - BACK_MARGIN, max(mz) + BACK_MARGIN
        out = {}
        for f in self.faces:
            c = m @ f.calc_center_median()
            if not (lo <= c.z <= hi and abs(c.x - mid_x) <= reach):
                continue
            row = "top" if above[f][0] > 0 else "low" if below[f][0] > 0 else "mid"
            out.setdefault(("L" if side[f][0] > 0 else "R", row), set()).add(f)
        made = []
        for (s, row), faces in sorted(out.items(), key=lambda kv: (("top", "mid", "low").index(kv[0][1]), kv[0][0])):
            rim = [e for f in faces for e in f.edges if sum(1 for g in e.link_faces if g in faces) == 1]
            far = max(abs((m @ v.co).x - mid_x) for e in rim for v in e.verts) if rim else 0
            outer = [e for e in rim if min(abs((m @ v.co).x - mid_x) for v in e.verts) >= 0.8 * far]
            pts = [v.co.copy() for e in (outer if len(outer) >= 2 else rim) for v in e.verts]
            if len(pts) >= 2:
                made.append((faces, pts, row))
        return made

def split_many(obj, index_sets, names):
    """Split several face sets (indices into obj's mesh as it is now) off obj, as new objects named `names`."""
    attr = obj.data.attributes.new("_fh_piece", "INT", "FACE")
    for k, idx in enumerate(index_sets, start=1):
        for i in idx:
            attr.data[i].value = k
    pieces = []
    for k, name in enumerate(names, start=1):
        a = obj.data.attributes["_fh_piece"]
        pieces.append(geom.split_off(obj, [i for i, x in enumerate(a.data) if x.value == k], name))
    for o in [obj] + pieces:
        a = o.data.attributes.get("_fh_piece")
        if a is not None:
            o.data.attributes.remove(a)
    return pieces


def overlay_hinges():
    """Every hinge: its line (blue) and the way its pieces start to open (a big arrow from the piece's middle); a piece on a
    path instead shows the path its middle takes (green, an arrow head at its end)."""
    items = []
    for h in model.hinges():
        p, a = model.line(h)
        ps = model.pieces_of(h)
        if not ps:
            continue
        if not model.turns_on_hinge(h) or model.parent(h) is not None:
            trail = moves.path_points(h)
            items.append(("lines", draw.polyline(trail), (0.2, 1.0, 0.35, 1.0), True, 3.0))
            d = trail[-1] - trail[-2]
            if d.length > 1e-6:
                items.append(("lines", draw.arrow(trail[-1] - d.normalized() * 0.04, d, 0.04, 0.02), (0.2, 1.0, 0.35, 1.0),
                              True, 3.0))
            continue
        items.append(("lines", [tuple(p - a * 0.15), tuple(p + a * 0.15)], COLOURS["hinge"], True, 4.0))
        pts = [o.matrix_world @ v.co for o in ps for v in list(o.data.vertices)[::max(1, len(o.data.vertices) // 200)]]
        c = sum(pts, Vector()) / len(pts)
        r = c - p
        r -= a * r.dot(a)
        d = a.cross(r) * (1 if h["fh_angle"] >= 0 else -1)
        if d.length > 1e-9:
            items.append(("lines", draw.arrow(c, d, 0.18, 0.05), (0.2, 1.0, 0.35, 1.0), True, 4.0))
    draw.set_layer("fh_hinges", items)
