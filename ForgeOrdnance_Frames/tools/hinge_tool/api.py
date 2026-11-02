"""战甲开合 from scripts (the Blender MCP or the Python console): the same cuts, hinges and checks as the click tool. Pieces are
named by number (fh_no on the hinge); every function takes and returns those numbers. Points are world (x, y, z).

  lines(obj, at, near)          the lines the tool offers at `near` on the plate under `at` (drawn yellow in the viewport)
  make(tpl, obj, at, picks)     cut a preset's pieces and hinge them; returns their numbers
  info(no=None)                 every piece (or one): number, objects, angle, way it opens, layer, batch, row, timing, lines
  edit(no, ...)                 angle / flip / layer / batch / row / part / delay / length of a piece
  move(no, kind, ..., then=)    how a piece opens (moves.py: hinge, slide, offset, stepped, edge, plug, twist, flare, nest,
                                scissor_door, zlink, scissor, turn); `then` = (kind, {settings}) runs after it
  move_group(nos, kind, ...)    a group: louver, petal, iris, fold, spine
  chain(no, parent)             piece `no` hangs on piece `parent` (it moves with it, then on its own hinge); parent=None frees it
  shoulder(cap, inner, ...)     the shoulder: the cap pushes out then turns up off the neck, the inner layer slides up under
                                the raised upper back
  remove(no, into)              put a piece back where it was cut from (refused when that is gone; `into` names where)
  give_back(no|obj, into, faces|pred)   faces (or all) of a piece go back to the shell or a still part, welded on
  renumber(no, new) / rest_on(no, nos)  a piece's number / the pieces it lies on when open (the float check counts them)
  limb_arc(limb, lo, hi)        a face test for carve / give_back: round a limb between two angles (0 straight back)
  join(no, into)                piece `no` moves with piece `into` (one hinge)
  merge(obj) / delete(obj) / still(obj)   fragments and parts that never open
  pose(t) / play() / shut()     open everything to fraction t of its path / play the sequence / back shut
  reveal()                      every mesh shown and rendered (the owner's files keep most parts hidden)
  carve(src, name, ...)         cut a new object out of `src`: by planes, by a test on the faces' centres, an arc round a
                                limb (limb, arc), or by face indices
  split(no, planes, count, along)   cut a hinged piece into strips, each with its own hinge on the cut it hangs from
  strip(src, cut, hinge, name, zr, ...)   a limb's outer strip between two drawn lines (radial planes), hinged on the second
  attach(objs, no) / new(objs, point, axis, angle, layer, ...)   objects riding on piece `no`'s hinge / objects made a piece
  auto_strip(src, layer, limb) / auto_back6(src, rows)   the owner's drawn lines cut into strips / the back's six blocks
  auto_attach(no) / orphans(fix)   small loose parts folded into a door / fragments nobody took in (fix joins them)
  unregistered() / attach_loose()   what is in no piece and not marked still (pink in the review) / fold every part that sits
                                on a door into that door; still(obj, at=point) marks one island fixed
  check() / allow(no, item)     run the checks (failures turn red) / accept one result
  flip_dir(no) / fix_dirs()     turn a piece that opens the wrong way (check "dir") the other way round its hinge
  clash_shot(path, no)          a close-up of piece `no`'s worst clash (both pieces see-through, the poking point marked)
  fit(no, lo, hi, param) / fit_all()   the largest angle (or setting) that still passes body, start, float and clash
  sweep(no, {param: [values]})  every combination by the quick look, the best few by the full check
  quick(scene, names)           the coarse check a search uses (single piece under a second or so), never what is delivered
  report(rows)                  one line per piece that fails something, to read in the console
  review(out, name)             back and side-back GIFs, numbered overview, table
  sheet(path, ...)              frames x views of the opening in one picture, pieces coloured, some framed or marked red
  summary_sheet(out, name)      review's pack, the shoulder and back sheets and the checks as text
  bake(posed, report)           the file the build reads (pose.ps1 does this from the command line)
"""
import colorsys
import inspect
import json
import math
import os

import bmesh
import bpy
import numpy as np
from bpy_extras.object_utils import world_to_camera_view
from mathutils import Matrix, Vector
from mathutils.bvhtree import BVHTree

from . import body, checks, click, draw, geom, model, motion, moves

ROLE_COLOUR = {0: (1.0, 0.95, 0.1, 1.0)}
OTHER_COLOUR = (1.0, 0.95, 0.1, 0.35)
HOT_COLOUR, GREY = (0.9, 0.1, 0.1, 1.0), (0.78, 0.78, 0.78, 1.0)
UNREG_COLOUR = (1.0, 0.0, 0.85, 1.0)     # in no piece and not marked still
RAIL_WARN_DEG = 20.0      # move(): a slide whose real rail is this far from the direction asked is reported


def _obj(obj):
    return bpy.data.objects[obj] if isinstance(obj, str) else obj


def _hinge(no):
    h = model.by_no(no)
    if h is None:
        raise KeyError("没有 %s 号" % no)
    return h


def _plate(obj, at, template=""):
    o = _obj(obj)
    ok, _, _, face = o.closest_point_on_mesh(o.matrix_world.inverted() @ Vector(at))
    if not ok:
        raise ValueError("%s 上找不到这个点" % o.name)
    return click.BackPlate(o, face) if template == "back6" else geom.Plate(o, face)


def _strokes(o):
    inv = o.matrix_world.inverted()
    return [[inv @ q for q in s] for s in click.strokes_world()]


def _pick(plate, near, k=0):
    o = plate.obj
    if isinstance(near, (tuple, list)) and near and isinstance(near[0], str) and near[0] == "stroke":
        layer, i = near[1], near[2]
        stroke = next(l for a in bpy.data.annotations for l in a.layers if l.info == layer).frames[0].strokes[i]
        inv = o.matrix_world.inverted()
        return "stroke", geom.snap_stroke(plate, [inv @ p.co for p in stroke.points])
    if isinstance(near, (tuple, list)) and near and isinstance(near[0], str) and near[0] == "points":
        inv = o.matrix_world.inverted()
        return "stroke", geom.snap_stroke(plate, [inv @ Vector(tuple(p)) for p in near[1]])
    cands = geom.candidates(plate, o.matrix_world.inverted() @ Vector(near), _strokes(o))
    if not cands:
        return None, []
    return cands[min(k, len(cands) - 1)]


def _describe(o, kind, edges):
    pts = [o.matrix_world @ p for p in geom.chain_points(edges)]
    length = sum((b - a).length for a, b in zip(pts, pts[1:]))
    return {"kind": kind, "edges": len(edges), "length": round(length, 3),
            "ends": [[round(c, 3) for c in pts[0]], [round(c, 3) for c in pts[-1]]] if pts else []}


def lines(obj, at, near, show=True):
    """The lines offered at world point `near` on the plate under `at`, best first: [{k, kind (stroke / seam / edge / run),
    edges, length, ends}]. The first is drawn bright yellow, the others faint."""
    plate = _plate(obj, at)
    o = plate.obj
    cands = geom.candidates(plate, o.matrix_world.inverted() @ Vector(near), _strokes(o))
    out = [dict(k=k, **_describe(o, kind, e)) for k, (kind, e) in enumerate(cands)]
    if show:
        draw.set_layer("fh_api", [("lines", draw.polyline([o.matrix_world @ p for p in geom.chain_points(e)]),
                                   ROLE_COLOUR.get(k, OTHER_COLOUR), True, 5.0 if k == 0 else 2.0)
                                  for k, (_, e) in enumerate(cands)])
    plate.free()
    return out


def make(template, obj, at, picks=(), angle=None, layer=None, batch=None, row=None):
    """Cut preset `template` (strip / inner3 / back6 / top / whole) on the plate of `obj` under world point `at`. `picks`
    are its lines in the preset's order (strip: cut, hinge; inner3: middle cut, hinge 1, hinge 2; back6: middle line, two
    row lines; whole: hinge; top: none), each a world point (the first line offered there), (point, k) for the k-th offer,
    or ("stroke", annotation layer, stroke index). Returns the new piece numbers."""
    need = click.TEMPLATES[template]["lines"]
    if len(picks) != len(need):
        raise ValueError("%s 要 %d 条线" % (click.TEMPLATES[template]["label"], len(need)))
    plate = _plate(obj, at, template)
    chosen = []
    for p in picks:
        near, k = (p[0], p[1]) if len(p) == 2 and not isinstance(p[0], str) else (p, 0)
        _, edges = _pick(plate, near, k)
        if not edges:
            plate.free()
            raise ValueError("%s 附近没有可用的线" % (p,))
        chosen.append(edges)
    try:
        group = click.cut(plate, chosen, template, body.joints(bpy.context.scene))
    except click.Refused as r:
        plate.free()
        raise ValueError(str(r))
    nos = []
    for piece, h, _ in group:
        if angle is not None:       # the size; the way it turns stays the one that lifts it off (edit(no, flip=True))
            h["fh_angle"] = math.copysign(abs(angle), h["fh_angle"])
        _apply(h, layer, batch, row)
        nos.append(int(h["fh_no"]))
    _refresh()
    return nos


def _apply(h, layer=None, batch=None, row=None):
    if layer is not None:
        h["fh_layer"] = layer
    if batch is not None:
        h["fh_batch"] = int(batch)
    if row is not None:
        h["fh_row"] = row


def info(no=None):
    """[{no, pieces, source, angle, layer, batch, row, part, expect (the declared way it opens, edit expect=), frames,
    template, hinge (point, axis), cut lines, hinge line}]."""
    out = []
    for h in sorted(model.hinges(), key=lambda h: int(h.get("fh_no", 0))):
        if no is not None and int(h.get("fh_no", 0)) != int(no):
            continue
        p, a = model.line(h)
        cut, hl = model.lines_of(h)
        ps = model.pieces_of(h)
        par = model.parent(h)
        out.append({"no": int(h.get("fh_no", 0)), "pieces": [o.name for o in ps],
                    "source": ps[0].get("fh_source", "") if ps else "",
                    "angle": None if model.angle_of(h) is None else round(model.angle_of(h), 1),
                    "move": model.move_of(h), "links": [o.name for o in model.links_of(h)],
                    "parent": int(par["fh_no"]) if par is not None else None,
                    "layer": h.get("fh_layer"), "batch": h.get("fh_batch"), "row": h.get("fh_row"),
                    "part": h.get("fh_part", ""), "expect": (model.expected_dir(h) or (None,))[0],
                    "delay": int(h.get("fh_delay", 0)), "frames": list(model.frames(h)), "template": h.get("fh_template", ""),
                    "hinge": [[round(c, 4) for c in p], [round(c, 4) for c in a]],
                    "cut": [[[round(c, 3) for c in q] for q in l[:: max(1, len(l) // 6)]] for l in cut],
                    "hingeLine": [[round(c, 3) for c in q] for q in hl[:: max(1, len(hl) // 6)]]})
    return out


def edit(no, angle=None, flip=False, layer=None, batch=None, row=None, part=None, delay=None, length=None, expect=None):
    """Change a piece: angle (degrees, signed: the sign is the way it turns on its hinge), flip (the other way), layer (outer
    / inner), batch (order within the layer), row ("", top, mid, low), part ("shoulder": a shoulder cap, not a limb piece;
    "underarm": a piece under the arm, whose opening the dir check wants down and forward; "back" or a limb with its side,
    "forearm.R": the body part it sits on, for the checks and the presets, instead of judging it from its main body; ""
    clears), delay (frames added to its start, may be negative), length (frames its move takes; 0: its layer's), expect (the
    way this one piece should open, for the dir check over its body part's rule: direction words joined by "+", e.g.
    "down+front" (moves.py's words, as the piece stands shut), or a vector (x, y, z); "" clears). On a piece with a path (it
    slides, plugs, twists ... first) angle and flip change the turn its path really makes (_turn_on_path): the hinge turn
    that follows, or the preset's own deg; info() and the tables show that turn."""
    h = _hinge(no)
    if part is not None:
        reg = part.split(".")[0]
        if part not in ("", "shoulder", "back", "underarm") and (reg not in body.REACH_R or
                                                                 part.split(".")[-1] not in ("L", "R")):
            raise ValueError("part 是 shoulder、underarm、back 或 部位.侧（upperarm/forearm/thigh/shin 加 .L/.R），不是 %s" % part)
        h["fh_part"] = part
    if expect is not None:
        if isinstance(expect, str) and expect == "":
            if "fh_dir_want" in h:
                del h["fh_dir_want"]
        else:
            shape = moves.Shape(h)
            words = expect.split("+") if isinstance(expect, str) else [tuple(expect)]
            v = sum((shape.direction(w.strip() if isinstance(w, str) else w) for w in words), Vector())
            if v.length < 1e-6:
                raise ValueError("expect 的几个方向相互抵消了：%s" % (expect,))
            h["fh_dir_want"] = json.dumps({"words": expect if isinstance(expect, str) else str(tuple(expect)),
                                           "vec": [round(c, 4) for c in v.normalized()]}, ensure_ascii=False)
    if angle is not None or flip:
        now = model.angle_of(h)
        want = float(angle) if angle is not None else now
        if flip and want is not None:
            want = -want
        if model.turns_on_hinge(h):
            h["fh_angle"] = want
        elif now is None:
            raise ValueError("%s 号的开法里没有转动，angle / flip 没有意义；用它开法里的参数（如 distance）" % no)
        else:
            _turn_on_path(h, want, flip=flip)
    if delay is not None:
        h["fh_delay"] = int(delay)
    if length is not None:
        h["fh_length"] = int(length)
    _apply(h, layer, batch, row)
    _refresh()
    return info(no)[0]


def move(no, kind, then=None, **settings):
    """How piece `no` opens: preset `kind` with its settings (moves.py), then `then` = (kind, {settings}). Replaces its path
    and its links (made anew for zlink and scissor). kind "hinge" without `then` puts it back on its hinge."""
    h = _hinge(no)
    if kind == "hinge" and settings.get("deg") is not None:
        h["fh_angle"] = math.copysign(abs(settings["deg"]), h["fh_angle"] or 1)
    model.shut_all(bpy.context.scene)
    segs, links = moves.build(h, kind, then=then, **settings)
    moves.clear_links(h)
    record = {"kind": kind, **{k: (list(v) if isinstance(v, (tuple, Vector)) else v) for k, v in settings.items()}}
    if then:
        record["then"] = [then[0], dict(then[1])]
    rails = [Vector(s["slide"]) for s in (segs or []) if "slide" in s and Vector(s["slide"]).length > 1e-6]
    if rails and settings.get("direction") is not None:
        # the way it really slides (a direction word runs along the plate): recorded, and said when it is far from the word
        rail = max(rails, key=lambda v: v.length).normalized()
        record["rail"] = [round(c, 3) for c in rail]
        want = moves.Shape(h).direction(settings["direction"])
        off = math.degrees(rail.angle(want, 0.0))
        if off > RAIL_WARN_DEG:
            record["railOffDeg"] = round(off, 1)
            print("%s 号：沿板面滑的实际方向 %s 与给的方向 %s 差 %.0f°（给向量 (x, y, z) 就按它走）" % (
                no, record["rail"], settings["direction"], off))
    model.set_path(h, segs, record)
    for k, spec in enumerate(links):
        moves.make_link(h, spec, k)
    air = [s["seatMm"] for s in links if s.get("seatMm") is not None and s["seatMm"] > checks.ANCHOR_MM]
    if air:
        print("%s 号：连杆固定端离不动的部分还有 %s mm（%.0f cm 内找不到可落的壳）" % (
            no, "、".join("%.0f" % x for x in air), moves.ANCHOR_REACH * 100))
    _refresh()
    return info(no)[0]


def chain(no, parent):
    """Piece `no` hangs on piece `parent` (a fold's next leaf): it moves with it, then on its own path. None frees it."""
    h = _hinge(no)
    if parent is None:
        h["fh_parent"] = ""
    else:
        p = _hinge(parent)
        if h in model.chain(p):
            raise ValueError("%s 号已经挂在 %s 号上，不能互挂" % (parent, no))
        h["fh_parent"] = p.name
    _refresh()
    return info(no)[0]


def move_group(nos, kind, step=2, **settings):
    """A group of pieces opening together, one after another `step` frames apart (in the order given):
      louver(direction, distance, tilt=6, stack=0.022)  each slides `direction` and stacks over the one before it (the first
                                                        given leads and moves least), tilting a little
      petal(deg)          each turns on its own hinge, one after another
      iris(deg, pivot)    each turns in its own plane (a scissor door), one after another
      fold(deg)           an accordion: the first turns `deg` on its hinge, each next hangs on the one before and folds
                          2 x deg (at most 170) about the edge they share, back over it, the next the other way
      spine(distance, lag=6)  the first backs straight out `distance` first; the others start `lag` frames later
    Returns the numbers."""
    hs = [_hinge(n) for n in nos]
    model.shut_all(bpy.context.scene)
    if kind == "louver":
        direction = settings.get("direction", "up")
        first = moves.Shape(hs[0])
        d = first.along(direction)
        lead = first.centre.dot(d)
        tilt, stack = settings.get("tilt", 6.0), settings.get("stack", 0.022)
        base_dist = settings.get("distance") or 0.3 * first.extent(d)
        for r, h in enumerate(hs):
            sh = moves.Shape(h)
            t = sh.along(direction)
            dist = base_dist + max(0.0, lead - sh.centre.dot(d))
            segs = [motion.slide(sh.n * stack * r, at=(0.0, 0.3))] if r else []
            if tilt:
                p, a = sh.edge(direction)
                segs.append(motion.turn(p, a, sh.lifts(p, a) * abs(tilt), at=(0.0, 0.4)))
            segs.append(motion.slide(t * dist, at=(0.15 if r else 0.0, 1.0)))
            _set(h, segs, {"kind": "louver", "rank": r, **settings}, r * step)
    elif kind in ("petal", "iris"):
        for r, h in enumerate(hs):
            if kind == "petal":
                if settings.get("deg") is not None:
                    h["fh_angle"] = math.copysign(abs(settings["deg"]), h["fh_angle"] or 1)
                _set(h, None, None, r * step)
            else:
                segs, _ = moves.build(h, "scissor_door", deg=settings.get("deg", 40.0), pivot=settings.get("pivot", "hinge"))
                _set(h, segs, {"kind": "iris", **settings}, r * step)
    elif kind == "fold":
        deg = settings.get("deg", abs(hs[0]["fh_angle"]) or 60.0)
        for r, h in enumerate(hs):
            if r == 0:
                h["fh_angle"] = math.copysign(deg, h["fh_angle"] or 1)
                _set(h, None, None, 0)
                h["fh_parent"] = ""
                continue
            prev = moves.Shape(hs[r - 1])
            sh = moves.Shape(h)
            tree = geom.kd(prev.pts)
            near = [q for q in sh.pts if tree.find(q)[2] < 0.004]
            if len(near) < 2:
                raise ValueError("%s 号和 %s 号没有共用的边，折不起来" % (nos[r], nos[r - 1]))
            c, a = geom.axis_of(near)
            # The second leaf folds back over the first (its far side lifting off), the third the other way, and so on.
            sign = sh.lifts(c, a) * (1 if r % 2 else -1)
            _set(h, [motion.turn(c, a, sign * min(170.0, 2 * deg))], {"kind": "fold", "deg": deg}, 0)
            h["fh_parent"] = hs[r - 1].name
    elif kind == "spine":
        segs, _ = moves.build(hs[0], "slide", direction="out", distance=settings.get("distance", 0.03))
        _set(hs[0], segs, {"kind": "spine", **settings}, 0)
        for h in hs[1:]:
            h["fh_delay"] = int(settings.get("lag", 6))
    else:
        raise ValueError("没有这种成组开法：%s（louver、petal、iris、fold、spine）" % kind)
    _refresh()
    return [int(h["fh_no"]) for h in hs]


def _set(h, segs, record, delay):
    moves.clear_links(h)
    model.set_path(h, segs, record)
    h["fh_delay"] = int(delay)


def shoulder(cap=None, inner=None, ring=None, push=0.03, lift_deg=50.0, inner_dir=(0.0, 1.0, 0.3), inner_lift=0.02,
             inner_distance=None, ring_deg=None):
    """The shoulder, so the arm goes in from behind: the cap (shoulder top) pushes `push` straight out off the trapezius, then
    turns `lift_deg` up and out about its neck-side upper edge; the inner layer lifts `inner_lift` off the shell (clear of
    the shoulder armour round it) and slides back and up (`inner_dir`) into the room the raised upper back left, on the
    inner layer's time; the ring under the shoulder keeps its single-sided hinge (ring_deg sets its size). Each argument is
    a piece number or None. Returns info() of the pieces set."""
    out = []
    if cap is not None:
        move(cap, "plug", push=push, edge="neck", deg=lift_deg)
        edit(cap, layer="outer", batch=0, part="shoulder")
        out.append(int(cap))
    if inner is not None:
        move(inner, "slide", direction=inner_dir, distance=inner_distance, under=1, depth=inner_lift)
        edit(inner, layer="inner", part="shoulder")
        out.append(int(inner))
    if ring is not None:
        move(ring, "hinge", deg=ring_deg)
        out.append(int(ring))
    return [info(n)[0] for n in out]


def remove(no, into=None):
    """Put the piece back into what it was cut from and drop its hinge. When what it was cut from is gone, nothing is
    removed: it says so, and `into` (an object or its name) names where its faces go back instead (give_back)."""
    h = _hinge(no)
    if into is not None:
        return give_back(no, into)
    gone = sorted({o.get("fh_source", "") for o in model.pieces_of(h) if bpy.data.objects.get(o.get("fh_source", "")) is None})
    if gone:
        raise ValueError("%s 号切自的物体 %s 已不在，没有删；用 remove(%s, into=物体) 把它并回指定的物体" % (
            no, "、".join(g or "（未记录）" for g in gone), no))
    moves.clear_links(h)
    click.take_back(click.Group([(o, h, o.get("fh_source", "")) for o in model.pieces_of(h)]))
    _refresh()


def give_back(what, into, faces=None, pred=None):
    """Faces go back from a piece to what stays (the shell, a still part) or to any other mesh `into` (an object or its name),
    welded on at the seam: `what` is a piece number (all its objects) or one object (or its name); `faces` (indices into that
    object's mesh) or `pred` (pred(world centre of a face) -> bool, e.g. by carve_arc()) picks some of its faces, else all of
    it goes. A piece left with nothing loses its hinge and links. Returns {"into": name, "faces": how many went back}."""
    target = _obj(into)
    if target.type != "MESH" or target.get("fh_link"):
        raise ValueError("%s 不是可以并回的网格" % target.name)
    objs = model.pieces_of(_hinge(what)) if isinstance(what, int) else [_obj(what)]
    if faces is not None and len(objs) != 1:
        raise ValueError("%s 号有 %d 个物体，faces 只能对一个物体给；给物体名" % (what, len(objs)))
    if target in objs:
        raise ValueError("不能并回到它自己")
    model.shut_all(bpy.context.scene)
    hs = {model.hinge_of(o) for o in objs} - {None}
    moved = 0
    for o in objs:
        if faces is None and pred is None:
            moved += len(o.data.polygons)
            for k in ("fh_hinge", "fh_base"):
                if k in o:
                    del o[k]
            _absorb(o, target)
            continue
        try:
            part = carve(o, "_还回", faces=faces, pred=pred)
        except ValueError:
            continue                     # none of this object's faces were picked
        moved += len(part.data.polygons)
        _absorb(part, target)
        if not o.data.polygons:
            me = o.data
            bpy.data.objects.remove(o, do_unlink=True)
            if me.users == 0:
                bpy.data.meshes.remove(me)
    if not moved:
        raise ValueError("没有符合的面")
    for h in hs:
        if not model.pieces_of(h):
            moves.clear_links(h)
            bpy.data.objects.remove(h, do_unlink=True)
    _refresh()
    return {"into": target.name, "faces": moved}


def renumber(no, new):
    """Give piece `no` the number `new` (free, or it is refused): its hinge, its objects and links are renamed to match."""
    h = _hinge(no)
    if model.by_no(new) is not None:
        raise ValueError("%s 号已有件；先给它换个号" % new)
    old = "%d号-" % int(no)
    h["fh_no"] = int(new)
    mine = model.pieces_of(h) + model.links_of(h)
    for o in mine:
        if o.name.startswith(old):
            o.name = "%d号-%s" % (int(new), o.name[len(old):])
    if h.name.startswith("铰链-" + old):
        was = h.name
        h.name = "铰链-%d号-%s" % (int(new), was[len("铰链-" + old):])
        for o in mine:
            o["fh_hinge" if o.get("fh_hinge") else "fh_link"] = h.name
        for o in model.hinges():
            if o.get("fh_parent") == was:
                o["fh_parent"] = h.name
            if was in model.rests_on(o):
                o["fh_rests"] = json.dumps([h.name if n == was else n for n in model.rests_on(o)], ensure_ascii=False)
    _refresh()
    return info(new)[0]


def rest_on(no, on=()):
    """Piece `no`, open, rests on pieces `on` (numbers; () clears): the float check measures its gap to them too, so a plate
    that lies on a neighbour that opened first does not count as hanging in the air."""
    h = _hinge(no)
    h["fh_rests"] = json.dumps([_hinge(n).name for n in on], ensure_ascii=False)
    _refresh()
    return [int(_hinge(n)["fh_no"]) for n in on]


def join(no, into):
    """Piece `no` moves with piece `into` (it sits on it): its objects take `into`'s hinge; its own hinge goes."""
    h, target = _hinge(no), _hinge(into)
    for o in model.pieces_of(h):
        model.attach(o, target)
    bpy.data.objects.remove(h, do_unlink=True)
    _refresh()


def _nearest_to(pts, skip=()):
    """(distance, object) of the mesh object nearest the world points, leaving out `skip`; None when there is none."""
    best = None
    for o in bpy.context.scene.objects:
        if o in skip or o.type != "MESH" or not o.data.vertices:
            continue
        t = geom.kd([o.matrix_world @ v.co for v in o.data.vertices])
        d = min(t.find(p)[2] for p in pts[:: max(1, len(pts) // 100)])
        if best is None or d < best[0]:
            best = (d, o)
    return best


def _nearest_mesh(frag, skip=()):
    return _nearest_to([frag.matrix_world @ v.co for v in frag.data.vertices], tuple(skip) + (frag,))


def _join_into(frag, target):
    old = model.hinge_of(frag)
    with bpy.context.temp_override(active_object=target, selected_editable_objects=[target, frag]):
        bpy.ops.object.join()
    if old is not None and old is not model.hinge_of(target) and not model.pieces_of(old):
        bpy.data.objects.remove(old, do_unlink=True)


def merge(obj):
    """Join a fragment into the piece or part it touches (nearest)."""
    frag = _obj(obj)
    best = _nearest_mesh(frag)
    if best is None:
        raise ValueError("旁边没有可并入的件")
    _join_into(frag, best[1])
    _refresh()
    return best[1].name


def delete(obj):
    o = _obj(obj)
    h = model.hinge_of(o)
    bpy.data.objects.remove(o, do_unlink=True)
    if h is not None and not model.pieces_of(h):
        bpy.data.objects.remove(h, do_unlink=True)
    _refresh()


def still(obj, on=True, at=None):
    """Mark a part as never opening (feet, helmet, body, the front plates that stay), or clear the mark. With `at` (a world
    point) only the island of `obj` nearest it is marked: it is cut off into an object of its own ("<obj>-固定"), the rest
    of `obj` keeps showing as unregistered. Returns the marked object's name."""
    o = _obj(obj)
    if at is not None and on:
        model.shut_all(bpy.context.scene)
        parts = _islands_of(o)
        q = np.array(tuple(at))
        faces = min(parts, key=lambda p: float(np.min(np.linalg.norm(p[1] - q, axis=1))))[0]
        if len(faces) < len(o.data.polygons):
            o = click.split_many(o, [faces], ["%s-固定" % o.name])[0]
    if on:
        o["fh_still"] = 1
    elif "fh_still" in o:
        del o["fh_still"]
    _refresh()
    return o.name


# --------------------------------------------------------------------------------------------------------------------- reveal
def _show_all(scene):
    """Show and render every mesh (collections, viewport and render switches, hide). Returns how many were hidden and a function
    that puts every switch back as it was."""
    layers = []

    def walk(lc):
        layers.append((lc, lc.exclude, lc.hide_viewport))
        for c in lc.children:
            walk(c)
    for c in bpy.context.view_layer.layer_collection.children:
        walk(c)
    colls = [(c, c.hide_viewport, c.hide_render) for c in scene.collection.children_recursive]
    for lc, _, _ in layers:
        lc.exclude = False
        lc.hide_viewport = False
    for c, _, _ in colls:
        c.hide_viewport = False
        c.hide_render = False
    saved, hidden = [], 0
    for o in scene.objects:
        if o.type != "MESH":
            continue
        try:
            was = o.hide_get()
        except RuntimeError:        # not in this view layer
            was = False
        saved.append((o, was, o.hide_viewport, o.hide_render))
        hidden += bool(was or o.hide_viewport or o.hide_render)
        o.hide_viewport = False
        o.hide_render = False
        try:
            o.hide_set(False)
        except RuntimeError:
            pass

    def restore():
        for o, was, viewport, render in saved:
            o.hide_viewport, o.hide_render = viewport, render
            try:
                o.hide_set(was)
            except RuntimeError:
                pass
        for c, viewport, render in colls:
            c.hide_viewport, c.hide_render = viewport, render
        for lc, exclude, viewport in layers:
            lc.hide_viewport, lc.exclude = viewport, exclude
    return hidden, restore


def reveal():
    """Show and render every mesh in the scene: the hide switches of its objects, the viewport and render switches, and the
    collections they sit in (the owner's files keep most parts hidden). Returns how many meshes were hidden."""
    return _show_all(bpy.context.scene)[0]


# ----------------------------------------------------------------------------------------------------------------------- cutting
def _objs(objs):
    out = [_obj(o) for o in ([objs] if isinstance(objs, (str, bpy.types.Object)) else objs)]
    for o in out:
        if o.type != "MESH":
            raise ValueError("%s 不是网格" % o.name)
        if o.get("fh_link"):
            raise ValueError("%s 是连杆，不能再挂到别的件上" % o.name)
    return out


def _world_points(o):
    co = np.empty(len(o.data.vertices) * 3)
    o.data.vertices.foreach_get("co", co)
    return checks.apply(np.array(o.matrix_world), co.reshape(-1, 3))


def _islands(faces):
    """The faces of a bmesh as groups joined through shared edges (only the given faces count), in the order they come."""
    faces = list(faces)
    inside, seen, out = set(faces), set(), []
    for f in faces:
        if f in seen:
            continue
        seen.add(f)
        stack, comp = [f], []
        while stack:
            g = stack.pop()
            comp.append(g)
            for e in g.edges:
                for h in e.link_faces:
                    if h in inside and h not in seen:
                        seen.add(h)
                        stack.append(h)
        out.append(comp)
    return out


def _bisect(bm, planes, m, tag=None):
    """Cut the mesh in `bm` (of an object standing at world matrix `m`) along world planes [(point, normal)], so each border is
    straight; only the faces with int layer `tag` set when there is one."""
    mi, m3 = m.inverted(), m.to_3x3().transposed()
    for point, normal in planes:
        pool = [f for f in bm.faces if tag is None or f[tag]]
        geometry = list({v for f in pool for v in f.verts}) + list({e for f in pool for e in f.edges}) + pool
        bmesh.ops.bisect_plane(bm, geom=geometry, plane_co=mi @ Vector(point), plane_no=(m3 @ Vector(normal)).normalized(),
                               dist=1e-5)
        bm.faces.ensure_lookup_table()


def _limb_frame(limb):
    """(head, unit axis, back, outer side, length) of limb "thigh.R": the joint axis, the way straight back round it and the
    way toward its outer side (away from the suit's middle), both square to the axis."""
    j = body.joints(bpy.context.scene)
    if j is None:
        raise ValueError("这个文件没有关节数据，不知道四肢在哪")
    part, side = limb.split(".")
    head, tail = body.seat(j, part, side)
    unit = (tail - head) / np.linalg.norm(tail - head)
    back = np.array([0.0, 1.0, 0.0]) - unit[1] * unit
    back /= np.linalg.norm(back)
    lateral = np.array([1.0 if side == "L" else -1.0, 0.0, 0.0])
    lateral -= unit * (lateral @ unit) + back * (lateral @ back)
    return head, unit, back, lateral / np.linalg.norm(lateral), float(np.linalg.norm(tail - head))


def limb_arc(limb, lo, hi, along=(-0.05, 1.05)):
    """pred(world point) -> bool for carve() / give_back(): the point lies round limb `limb` ("thigh.R") between angles lo and
    hi (degrees about the joint axis: 0 straight back, positive toward the limb's outer side, away from the suit's middle,
    negative toward its inner side; -180..180), and along the limb between shares `along` of its length (0 its joint, 1 the
    next)."""
    head, unit, back, lateral, length = _limb_frame(limb)

    def pred(p):
        r = np.array(tuple(p)) - head
        s = float(r @ unit) / length
        r -= unit * (r @ unit)
        a = math.degrees(math.atan2(float(r @ lateral), float(r @ back)))
        return along[0] <= s <= along[1] and lo <= a <= hi
    return pred


def carve(src, name, faces=None, planes=(), pred=None, largest=False, limb=None, arc=None):
    """A new object `name` made of faces of `src` (an object or its name); they leave `src`. Done on a bmesh copy of the mesh
    with the faces deleted from each side, never bpy.ops.mesh.separate. Ways to say which faces, together or alone:
      planes  [(point, normal)] in world space: the mesh is cut along each first, so the border runs straight
      pred    pred(world centre of a face) -> bool: the faces that pass
      limb, arc   the faces round limb `limb` ("thigh.R") between angles arc = (lo, hi), as limb_arc() measures them
      faces   indices into src's mesh as it is now: only these faces are candidates (the planes cut only them)
    Needs `faces`, `pred` or `limb` and `arc`. largest=True keeps only the biggest connected group of the chosen faces (by
    area). The new object stands where src stands, with its materials and attributes and fh_source = src; it is
    not a piece yet (new() / attach() / split()). Returns it."""
    o = _obj(src)
    if (limb is None) != (arc is None):
        raise ValueError("limb 和 arc 要一起给")
    if limb is not None:
        ring, more = limb_arc(limb, *arc), pred
        pred = (lambda p: ring(p) and more(p)) if more is not None else ring
    if faces is None and pred is None:
        raise ValueError("要给 faces、pred 或 limb+arc，说明切哪些面")
    model.shut_all(bpy.context.scene)
    m = o.matrix_world
    bm = bmesh.new()
    bm.from_mesh(o.data)
    bm.faces.ensure_lookup_table()
    tag = None
    if faces is not None:
        tag = bm.faces.layers.int.new("_fh_pick")
        for i in faces:
            bm.faces[i][tag] = 1
    _bisect(bm, planes, m, tag)
    pick = [f for f in bm.faces if (tag is None or f[tag]) and (pred is None or pred(m @ f.calc_center_median()))]
    if largest and pick:
        pick = max(_islands(pick), key=lambda comp: sum(f.calc_area() for f in comp))
    bm.faces.index_update()
    chosen = {f.index for f in pick}
    if not chosen:
        bm.free()
        raise ValueError("%s 上没有符合的面" % o.name)
    if tag is not None:
        bm.faces.layers.int.remove(tag)
    bm.to_mesh(o.data)
    bm.free()
    o.data.update()
    piece = geom.split_off(o, chosen, name)
    piece["fh_source"] = o.name
    return piece


def split(no, planes=None, count=None, along=None):
    """Cut piece `no` into strips (a folding door's leaves, louver slats, iris blades). Either `planes` [(point, normal)] in
    world space, or `count` strips of equal width along `along` (a direction word or vector; the way from the piece's hinge
    across its widest side when not given: strips parallel to the hinge). The strip nearest the hinge keeps the piece's hinge
    and number; every other strip is a piece of its own, numbered next, with a hinge on the cut it hangs from (the cut it
    shares with the strip nearer the hinge, fitted to that edge, same turn) and the piece's layer, batch, row and part. A
    piece that opens another way than its hinge gives its strips plain hinges: set theirs afterwards (move, move_group,
    chain). Returns the numbers, nearest the hinge first."""
    h = _hinge(no)
    scene = bpy.context.scene
    model.shut_all(scene)
    objs = model.pieces_of(h, scene)
    if not objs:
        raise ValueError("%s 号没有物体" % no)
    p, a = model.line(h)
    pa = np.array(a)
    points = np.concatenate([_world_points(o) for o in objs])
    if planes is None:
        if count is None or count < 2:
            raise ValueError("要给 planes，或 count（至少 2 条）")
        if along is None:
            r = points - np.array(p)
            r -= np.outer(r @ pa, pa)
            n = Vector(r[int(np.argmax((r ** 2).sum(axis=1)))]).normalized()
        else:
            n = moves.Shape(h).direction(along)
        s = points @ np.array(n)
        lo, hi = float(s.min()), float(s.max())
        planes = [(n * (lo + (hi - lo) * k / count), n) for k in range(1, count)]
    planes = [(Vector(q), Vector(v).normalized()) for q, v in planes]
    cells, centres = {}, {}      # which side of every plane -> {object: face indices}, and the faces' centres
    for o in objs:
        bm = bmesh.new()
        bm.from_mesh(o.data)
        _bisect(bm, planes, o.matrix_world)
        bm.to_mesh(o.data)
        bm.free()
        o.data.update()
        m = o.matrix_world
        for poly in o.data.polygons:
            c = m @ poly.center
            sig = tuple((c - q).dot(v) > 0 for q, v in planes)
            cells.setdefault(sig, {}).setdefault(o, []).append(poly.index)
            centres.setdefault(sig, []).append(tuple(c))
    if len(cells) < 2:
        raise ValueError("这些平面没有把 %s 号切开" % no)
    mid = {sig: np.mean(cs, axis=0) for sig, cs in centres.items()}

    def off_hinge(sig):
        r = mid[sig] - np.array(p)
        return float(np.linalg.norm(r - pa * (r @ pa)))
    ranked = sorted(cells, key=off_hinge)       # the first stays on the hinge it has
    made = {sig: [] for sig in ranked[1:]}
    for o in objs:
        tail = o.name.split("号-", 1)[-1]
        source = o.get("fh_source") or o.name
        todo = [sig for sig in ranked[1:] if o in cells[sig]]
        if todo:
            parts = click.split_many(o, [cells[sig][o] for sig in todo],
                                     ["%s-%d" % (tail, ranked.index(sig) + 1) for sig in todo])
            for sig, piece in zip(todo, parts):
                made[sig].append((piece, source))
        if o not in cells[ranked[0]]:
            me = o.data
            bpy.data.objects.remove(o, do_unlink=True)
            if me.users == 0:
                bpy.data.meshes.remove(me)
    nos = [int(h["fh_no"])]
    for sig in ranked[1:]:
        # the cut it hangs from: the plane shared with the cell nearest the hinge
        def hangs(i):
            other = sig[:i] + (not sig[i],) + sig[i + 1:]
            return (ranked.index(other) if other in cells else len(ranked),
                    abs(float((mid[sig] - np.array(planes[i][0])) @ np.array(planes[i][1]))))
        q, v = planes[min(range(len(planes)), key=hangs)]
        verts = np.concatenate([_world_points(piece) for piece, _ in made[sig]])
        on = verts[np.abs((verts - np.array(q)) @ np.array(v)) < 1e-4]
        if len(on) >= 2:
            c = on.mean(axis=0)
            d = Vector(np.linalg.svd(on - c)[2][0]).normalized()
        else:
            c, d = mid[sig] - np.array(v) * float((mid[sig] - np.array(q)) @ np.array(v)), a
        if d.dot(a) < 0:
            d = -d
        c = Vector(c)
        reach = [float((x - np.array(c)) @ np.array(d)) for x in (on if len(on) >= 2 else verts)]
        nh = model.make_hinge(c, d, h["fh_angle"], h.get("fh_layer", "outer"), int(h.get("fh_batch", 0)), h.get("fh_row", ""),
                              name="铰链")
        if h.get("fh_part"):
            nh["fh_part"] = h["fh_part"]
        new_no = int(nh["fh_no"])
        for piece, _ in made[sig]:
            piece.name = "%d号-%s" % (new_no, piece.name.split("号-", 1)[-1])
        nh.name = "铰链-" + made[sig][0][0].name
        for piece, source in made[sig]:
            model.attach(piece, nh, source)
            model.remember_base(piece)
        model.set_lines(nh, [], [c + d * min(reach), c + d * max(reach)])
        nos.append(new_no)
    _refresh()
    return nos


def strip(src, cut, hinge, name, zr, arc=None, layer="外部装甲", color=(1, 0.3, 0.1, 1), minf=40, angle=None, SEARCH=15,
          hinge_pt=None, skip=None, limb=None, widen=(0.0, 0.0)):
    """A limb's outer strip between two lines drawn in annotation layer `layer` (strokes `cut` and `hinge`, by index, or
    each a list of world points), cut out
    of `src` by two radial planes about the limb's axis and made a piece hinged on the hinge line; returns its number.
    The axis is the limb's joint axis (`limb`, "thigh.R", or the limb its faces sit on; a file without joints fits one to
    the faces' centres). widen = (cut side, hinge side) in degrees takes the strip that much farther round the limb past
    each line (the hinge line moves with its side).
    The limb is the faces of `src` on the stroke's side (|x| > 0.03) in groups of at least `minf` faces (`skip(faces, x, z)`
    drops a group): those between the limb's two joints, or without joints the groups centred between heights zr = (low,
    high). Each line's plane is turned up to `SEARCH` degrees to the one that crosses
    the fewest faces (it follows an existing seam); `hinge_pt` (a world point) puts the hinge line there instead, unturned.
    `arc` (degrees) is the strip's width round the limb (default: the angle between the two lines, the way round that faces
    the back). The hinge is fitted to the strip's edge on the hinge plane (the stroke's own line when it has none); the turn is
    the one that lifts it, the outer layer's size, or `angle` (signed). The strip is coloured `color`."""
    L = {l.info: l for a in bpy.data.annotations for l in a.layers}

    def pts(i):
        if not isinstance(i, (int, np.integer)):      # the line's own points (world), e.g. a stroke clipped to the limb
            return [Vector(tuple(p)) for p in i]
        return [Vector(p.co) for p in L[layer].frames[0].strokes[i].points]
    o = _obj(src)
    M = o.matrix_world
    Mi = M.inverted()
    side = 1 if sum(p.x for p in pts(hinge)) > 0 else -1
    bm = bmesh.new()
    bm.from_mesh(o.data)
    bm.faces.ensure_lookup_table()

    def wc(f):
        return M @ f.calc_center_median()

    frame = None

    def limb_faces():
        """The limb's faces: of the groups of at least `minf` faces on the stroke's side, with the limb's joint axis the
        faces between its two joints, without it the groups whose centre lies between the heights zr."""
        res = []
        for fs in _islands(bm.faces):
            if len(fs) < minf:
                continue
            cx = sum(wc(f).x for f in fs) / len(fs)
            cz = sum(wc(f).z for f in fs) / len(fs)
            if skip and skip(fs, cx, cz):
                continue
            if frame is not None:
                h0, u, length = frame
                res += [f for f in fs if side * wc(f).x > 0.03 and 0.0 <= (wc(f) - h0).dot(u) <= length]
            elif side * cx > 0.03 and zr[0] < cz < zr[1]:
                res += fs
        return res
    j = body.joints(bpy.context.scene)
    if limb is None and j is not None:
        faces = limb_faces()
        reg, sd, _ = body.region(j, np.array([tuple(wc(f)) for f in faces]).reshape(-1, 3)) if faces else ("back", "", 0)
        limb = "%s.%s" % (reg, sd) if reg != "back" else None
    if limb is not None:          # the limb's own joint axis (the shell's face centres lean with its shape)
        head, unit, _, _, length = _limb_frame(limb)
        A0, axis = Vector(tuple(head)), Vector(tuple(unit))
        frame = (A0, axis, length)
    faces = limb_faces()
    if not faces:
        bm.free()
        raise ValueError("%s 上找不到这段肢体的面（%s）" % (o.name, limb or "高度 %s" % (zr,)))
    near = geom.kd([wc(f) for f in faces])
    for which, i in (("切口", cut), ("铰链", hinge)):
        off = float(np.median([near.find(p)[2] for p in pts(i)]))
        if off > STROKE_ON_SRC:
            bm.free()
            raise ValueError("%s线离 %s 的这段肢体 %.0f mm，不是画在它上面；src 给线所在的物体" % (which, o.name, off * 1000))

    def centre(z):
        near = [wc(f) for f in faces if abs(wc(f).z - z) < 0.03]
        xs = [c.x for c in near]
        ys = [c.y for c in near]
        return Vector(((min(xs) + max(xs)) / 2, (min(ys) + max(ys)) / 2, z))
    if limb is None:
        zs = sorted(wc(f).z for f in faces)
        za, zb = zs[len(zs) // 10], zs[len(zs) * 9 // 10]
        A0, A1 = centre(za), centre(zb)
        axis = (A1 - A0).normalized()

    def at_ax(p):
        return A0 + axis * (p - A0).dot(axis)

    def radial(v):
        r = v - at_ax(v)
        r -= axis * r.dot(axis)
        return r.normalized()

    def plane(i):
        P = pts(i)
        m = sum(P, Vector()) / len(P)
        r = radial(m)
        return m, axis.cross(r).normalized(), r
    hco, hno, hrad = plane(hinge)
    cco0, cno0, crad0 = plane(cut)
    if hinge_pt is not None:
        hco = Vector(hinge_pt)
        hrad = radial(hco)
        hno = axis.cross(hrad).normalized()
        SEARCH = 0
    # refine each line: within +-SEARCH deg, the radial plane that crosses the fewest faces (follows existing seams/edges)
    vw = {v: M @ v.co for f in faces for v in f.verts}

    def crossings(rad):
        n = axis.cross(rad).normalized()
        c0 = A0
        tot = 0.0
        for f in faces:
            ds = [(vw[v] - c0).dot(n) for v in f.verts]
            if min(ds) < -1e-4 and max(ds) > 1e-4:
                fc = wc(f)
                r = fc - at_ax(fc)
                r -= axis * r.dot(axis)
                if r.normalized().dot(rad) > 0.5:
                    tot += f.calc_area()
        return tot

    def refine(rad):
        best = None
        for d in range(-SEARCH, SEARCH + 1, 1):
            r = (Matrix.Rotation(math.radians(d), 3, axis) @ rad).normalized()
            s = crossings(r) * (1 + 0.01 * abs(d))
            if best is None or s < best[0]:
                best = (s, d, r)
        return best
    if SEARCH:
        sh, dh, hrad2 = refine(hrad)
        sc, dc, crad2 = refine(crad0)
        print(f"refine hinge {dh:+d} deg, cut {dc:+d} deg")
        hrad = hrad2
        crad0 = crad2
        hco = at_ax(hco) + hrad * (hco - at_ax(hco)).length
        hno = axis.cross(hrad).normalized()
    b = Vector((0, 1, 0))
    b = (b - axis * b.dot(axis)).normalized()
    owner = arc is None
    if owner:
        a = math.degrees(hrad.angle(crad0))
        arc = a if (hrad + crad0).normalized().dot(b) > 0 else 360 - a
    cand = [((Matrix.Rotation(math.radians(arc) * s, 3, axis) @ hrad).normalized(), s) for s in (1, -1)]
    crad, sgn = max(cand, key=lambda t: t[0].dot(crad0 if owner else b))
    w_cut, w_hinge = widen
    if w_hinge:      # the hinge line moves out round the limb, away from the strip
        hrad = (Matrix.Rotation(-math.radians(w_hinge) * sgn, 3, axis) @ hrad).normalized()
        hco = at_ax(hco) + hrad * (hco - at_ax(hco)).length
        hno = axis.cross(hrad).normalized()
    if w_cut:
        crad = (Matrix.Rotation(math.radians(w_cut) * sgn, 3, axis) @ crad).normalized()
    arc += w_cut + w_hinge
    mid = (Matrix.Rotation(math.radians(arc / 2) * sgn, 3, axis) @ hrad).normalized()
    for co, no in ((at_ax(hco) + crad * 0.1, axis.cross(crad).normalized()), (hco, hno)):
        geometry = list({v for f in faces for v in f.verts}) + list({e for f in faces for e in f.edges}) + faces
        bmesh.ops.bisect_plane(bm, geom=geometry, plane_co=Mi @ co, plane_no=(Mi.to_3x3() @ no).normalized(), dist=1e-5)
        bm.faces.ensure_lookup_table()
        faces = limb_faces()
    sel = {f.index for f in faces if math.degrees(radial(wc(f)).angle(mid)) <= arc / 2 + 0.5}
    bm.to_mesh(o.data)
    o.data.update()
    bm.free()
    fresh = carve(o, name, faces=sel)
    # the hinge: the strip's boundary edges on the hinge plane, fitted to one line (the stroke's own line when there are none)
    Mw = fresh.matrix_world
    verts = [Mw @ v.co for v in fresh.data.vertices]
    bb = bmesh.new()
    bb.from_mesh(fresh.data)

    def on_hinge(p):
        r = p - at_ax(p)
        r -= axis * r.dot(axis)
        return abs((p - hco).dot(hno)) < 0.004 and r.normalized().dot(hrad) > 0.5
    near = [ed for ed in bb.edges if ed.is_boundary and on_hinge(Mw @ ((ed.verts[0].co + ed.verts[1].co) / 2))]
    hp = [Mw @ v.co for ed in near for v in ed.verts]
    bb.free()
    if len(hp) >= 4:
        c = sum(hp, Vector()) / len(hp)
        d = Vector(np.linalg.svd(np.array([list(p - c) for p in hp]))[2][0]).normalized()
    elif hinge_pt is not None:
        c, d = Vector(hco), axis
    else:
        P = pts(hinge)
        tree = geom.kd(verts)
        along = [p for p in P if tree.find(p)[2] <= click.HINGE_ON_PIECE]
        c, d = geom.axis_of(along or P)
        c = click.touching(c, d, verts, along or P)
    if frame is not None:
        d = axis.copy()       # hinges run along the limb (its joint axis), through the strip's edge
    if d.dot(axis) < 0:
        d = -d
    polys = fresh.data.polygons
    face = ([tuple(Mw @ p.center) for p in polys], [tuple(Mw.to_3x3() @ p.normal) for p in polys], [p.area for p in polys])
    up = geom.lift(*face, c, d, 1) >= geom.lift(*face, c, d, -1)
    batch = 0
    if j is not None:
        reg, _, over = body.region(j, np.array([tuple(v) for v in verts]))
        batch = 0 if over else click.BATCH.get(reg, 0)
    no = new(fresh, c, d, (1 if up else -1) * motion.ANGLE["outer"], "outer", batch)
    fresh.color = color
    if angle is not None:
        edit(no, angle=angle)
    print(f"{no}号 {name}: arc {round(arc)} faces {len(fresh.data.polygons)} src left {len(o.data.polygons)}")
    return no


def attach(objs, no):
    """Objects (or their names) ride on piece `no`'s hinge: they move with it, as one piece. Their place now is their shut
    place. For accessories (a lamp, a rivet strip, a bracket). An object that was a piece leaves its old hinge, which stays
    even with nothing on it. Returns their names."""
    h = _hinge(no)
    out = _objs(objs)
    model.shut_all(bpy.context.scene)
    for o in out:
        model.attach(o, h)
        model.remember_base(o)
    _refresh()
    return [o.name for o in out]


def new(objs, point, axis, angle, layer, batch=0, row="", part=None):
    """Make objects (or their names) one piece of its own, hinged on the line through world `point` along `axis`, turning
    `angle` degrees (signed) in `layer` ("outer" / "inner") with `batch`, `row` and `part` as edit() takes them. They are
    renamed with the new number. Their place now is their shut place. Returns the number."""
    if layer not in motion.LAYERS:
        raise ValueError("层是 %s" % " 或 ".join(motion.LAYERS))
    out = _objs(objs)
    model.shut_all(bpy.context.scene)
    point, axis = Vector(point), Vector(axis).normalized()
    h = model.make_hinge(point, axis, angle, layer, batch, row, "whole", name="铰链")
    no = int(h["fh_no"])
    reach = []
    for o in out:
        o.name = "%d号-%s" % (no, o.name.split("号-", 1)[-1])
    h.name = "铰链-" + out[0].name
    for o in out:
        model.attach(o, h, o.get("fh_source") or o.name)
        model.remember_base(o)
        reach += ((_world_points(o) - np.array(point)) @ np.array(axis)).tolist()
    if part:
        h["fh_part"] = part
    model.set_lines(h, [], [point + axis * (min(reach) if reach else -0.03), point + axis * (max(reach) if reach else 0.03)])
    _refresh()
    return no


# ---------------------------------------------------------------------------------------------------------------- automatic cuts
LIMB_ZH = {"upperarm": "上臂", "forearm": "前臂", "thigh": "大腿", "shin": "小腿"}
ROW_ZH = {"top": "上", "mid": "中", "low": "下"}
STROKE_REACH = 4.5          # a stroke belongs to a limb when it stays within this many body-part radii of its axis (the outer layer stands 3-4 out)
STROKE_SHARE = 0.4          # ... with at least this share of its points on that limb
STROKE_ALONG_DEG = 35.0     # ... running within this of the limb's axis
STROKE_SPAN = 0.3           # ... over at least this share of the limb's length
ATTACH_TOUCH, ATTACH_SMALL, ATTACH_SHARE = 0.004, 600, 0.3
ORPHAN_FACES, ORPHAN_AWAY = 60, 0.02
ORPHAN_TOUCH_MM = 2.0       # an island of a piece this close to a part that stays is a scrap of that part
STROKE_ON_SRC = 0.03        # strip(): a line whose points lie (median) farther than this from the limb's faces is not on src


def _annotation_layer(layer):
    lay = next((l for a in bpy.data.annotations for l in a.layers if l.info == layer), None)
    if lay is None or not lay.frames:
        raise ValueError("没有画了线的标注层「%s」" % layer)
    return lay


def _limb_strokes(layer, limb):
    """([(stroke index, angle round the limb in degrees: 0 straight back, positive toward the limb's outer side (away from
    the suit's middle), its points on this limb)], limb axis head, tail): the strokes of annotation layer `layer` that run
    along limb "thigh.R" (a body part and its side, as body.py names them). A stroke is the limb's when at least
    STROKE_SHARE of its points lie nearest this limb of all the limbs on that side (the torso does not count, so a line
    down the inside of the thigh by the crotch still counts) and within STROKE_REACH of its radius; it must run along the
    limb (its long direction within STROKE_ALONG_DEG of the axis, over at least STROKE_SPAN of the limb's length) on its side of
    the suit (|x| > 3 cm), so a
    ring cut round the limb's end is not taken. A line drawn down two limbs at once (thigh and shin) is clipped to this
    limb's part."""
    j = body.joints(bpy.context.scene)
    head, unit, back, lateral, _ = _limb_frame(limb)
    part, side = limb.split(".")
    tail = body.seat(j, part, side)[1]
    axis = tail - head
    limbs =[(n, s, h, t) for n, s, h, t in body.segments(j) if s == side]
    me = next(k for k, (n, _, _, _) in enumerate(limbs) if n == part)
    found = []
    for i, s in enumerate(_annotation_layer(layer).frames[0].strokes):
        pts = np.array([tuple(p.co) for p in s.points])
        if len(pts) < 2:
            continue
        cost = np.stack([body.from_axis(h, t, pts)[0] / body.REACH_R[n] for n, _, h, t in limbs], axis=1)
        along = ((pts - head) @ axis) / (axis @ axis)
        mine = ((cost.argmin(axis=1) == me) & (cost[:, me] < STROKE_REACH) & (along > -0.05) & (along < 1.05)
                & (pts[:, 0] * (1 if side == "L" else -1) > 0.03))
        if np.mean(mine) < STROKE_SHARE or mine.sum() < 2:
            continue
        own = pts[mine]
        c = own - own.mean(axis=0)
        long_dir = np.linalg.svd(c, full_matrices=False)[2][0] if len(own) > 2 else own[-1] - own[0]
        span = float(np.ptp(own @ unit)) / float(np.linalg.norm(axis))
        if abs(float(long_dir @ unit)) / max(np.linalg.norm(long_dir), 1e-12) < math.cos(math.radians(STROKE_ALONG_DEG)) \
                or span < STROKE_SPAN:
            continue
        r = own.mean(axis=0) - head
        r -= unit * (r @ unit)
        found.append((i, math.degrees(math.atan2(float(r @ lateral), float(r @ back))), own))
    return found, head, tail


def auto_strip(src, layer, limb, outer=None, strokes=None):
    """Cut the strips of limb `limb` ("thigh.R", "upperarm.L", "forearm.R", "shin.L": body part, side) out of `src` along the
    lines the owner drew on it in annotation layer `layer`, and hinge them. The limb's own strokes are found by _limb_strokes()
    (a line drawn down thigh and shin at once is clipped to this limb); `strokes` (stroke indices) picks them by hand
    instead. `outer` says whether it is the outer layer (default: the layer's name starts with 外). The rule (任务书 4, 5):
    the outermost layer of the upper arm and the thigh is one strip cut by two lines — the inner line (nearer the suit's
    middle) is the cut, the outer line the hinge, so it opens outward (strip()); everything else (the other limbs' outer
    layer, every inner layer) is cut by three lines into two halves, the middle line (between the other two round the limb,
    the one on the back) cut, each half hinged on its own outer line (the 内层三线 preset). Raises, listing the lines it
    found and where they lie round the limb, when the limb has not the number of lines the rule needs. Returns the new
    piece numbers."""
    found, head, tail = _limb_strokes(layer, limb)
    if strokes is not None:
        found = [f for f in found if f[0] in strokes]
        missing = [i for i in strokes if i not in [f[0] for f in found]]
        if missing:
            raise ValueError("线 %s 不在 %s 上（不顺着它或不在这一侧）" % (missing, limb))
    part, side = limb.split(".")
    outer = layer.startswith("外") if outer is None else outer
    two = outer and part in ("upperarm", "thigh")
    need = 2 if two else 3
    found.sort(key=lambda f: f[1])      # round the limb from its inner side (toward the suit's middle) to its outer side
    if len(found) != need:
        raise ValueError("%s 在「%s」上有 %d 条线（%s），%s要 %d 条；用 strokes= 指定" % (
            limb, layer, len(found), "、".join("%d 号线在背后%s %d°" % (f[0], "外侧" if f[1] >= 0 else "内侧", abs(f[1]))
                                              for f in found) or "无", "两刀单开" if two else "三刀两半开", need))
    name = "%s%s%s" % ("右" if side == "R" else "左", LIMB_ZH[part], "外甲" if outer else "内层")
    if two:
        return [strip(src, found[0][2], found[1][2], name, (min(head[2], tail[2]), max(head[2], tail[2])), layer=layer,
                      limb=limb)]
    mid = found[1]
    nos = make("inner3", src, tuple(mid[2][len(mid[2]) // 2]),
               [("points", mid[2]), ("points", found[0][2]), ("points", found[2][2])],
               layer="outer" if outer else "inner")
    if outer:
        for no in nos:
            edit(no, angle=math.copysign(motion.ANGLE["outer"], _hinge(no)["fh_angle"]))
    return nos


def auto_back6(src, rows, layer="内部装甲"):
    """The back's inner layer of `src` cut into six blocks by the owner's middle line (the long upright stroke near the suit's
    middle in annotation layer `layer`, taken as the plane x = its mean x) and two horizontal lines at the heights rows = (z1,
    z2), each block hinged upright on its outer side and given its row (上 above the higher line, 中, 下 below the lower). The
    blocks are the faces turned backward (normal +Y at least click.BACK_FACING) joined to the one nearest the middle line, no
    farther up or down than the line reaches plus click.BACK_MARGIN. Returns the numbers, top row first, left (+X) then right."""
    o = _obj(src)
    z_hi, z_lo = max(rows), min(rows)
    best = None
    for i, s in enumerate(_annotation_layer(layer).frames[0].strokes):
        pts = np.array([tuple(p.co) for p in s.points])
        if len(pts) < 2:
            continue
        reach = float(pts[:, 2].max() - pts[:, 2].min())
        if abs(pts[:, 0].mean()) < 0.06 and pts[:, 1].mean() > 0 and reach > 0.15 and (best is None or reach > best[0]):
            best = (reach, pts)
    if best is None:
        raise ValueError("「%s」上找不到背部的中线（竖直、靠近中间、在背后的长线）" % layer)
    line = best[1]
    mid_x = float(line[:, 0].mean())
    lo, hi = float(line[:, 2].min()) - click.BACK_MARGIN, float(line[:, 2].max()) + click.BACK_MARGIN
    planes = [(Vector((mid_x, 0, 0)), Vector((1, 0, 0))), (Vector((0, 0, z_hi)), Vector((0, 0, 1))),
              (Vector((0, 0, z_lo)), Vector((0, 0, 1)))]
    model.shut_all(bpy.context.scene)
    m, m3 = o.matrix_world, o.matrix_world.to_3x3()
    bm = bmesh.new()
    bm.from_mesh(o.data)
    _bisect(bm, planes, m)
    bm.faces.index_update()
    facing = {f for f in bm.faces if (m3 @ f.normal).normalized().y >= click.BACK_FACING and lo <= (m @ f.calc_center_median()).z <= hi}
    centre = Vector(line[len(line) // 2])
    if not facing:
        bm.free()
        raise ValueError("%s 上没有朝后的面" % o.name)
    seed = min(facing, key=lambda f: ((m @ f.calc_center_median()) - centre).length)
    keep, stack = {seed}, [seed]
    while stack:
        for e in stack.pop().edges:
            for g in e.link_faces:
                if g in facing and g not in keep:
                    keep.add(g)
                    stack.append(g)
    blocks = {}
    for f in keep:
        c = m @ f.calc_center_median()
        row = "top" if c.z > z_hi else "low" if c.z <= z_lo else "mid"
        blocks.setdefault(("L" if c.x > mid_x else "R", row), []).append(f.index)
    bm.to_mesh(o.data)
    bm.free()
    o.data.update()
    order = sorted(blocks, key=lambda k: (("top", "mid", "low").index(k[1]), k[0]))
    names = ["背部%s%s块" % ("左" if s == "L" else "右", ROW_ZH[r]) for s, r in order]
    pieces = click.split_many(o, [blocks[k] for k in order], names)
    nos = []
    for (side, row), piece in zip(order, pieces):
        piece["fh_source"] = o.name
        pts = _world_points(piece)
        dx = np.abs(pts[:, 0] - mid_x)
        c = Vector(pts[dx >= 0.8 * dx.max()].mean(axis=0))
        axis = Vector((0, 0, 1))
        polys, pm = piece.data.polygons, piece.matrix_world
        face = ([tuple(pm @ p.center) for p in polys], [tuple(pm.to_3x3() @ p.normal) for p in polys], [p.area for p in polys])
        up = geom.lift(*face, c, axis, 1) >= geom.lift(*face, c, axis, -1)
        nos.append(new(piece, c, axis, (1 if up else -1) * motion.ANGLE["inner"], "inner", 0, row))
    return nos


def _islands_of(o):
    """[(face indices, world points)] of the connected parts of a mesh object."""
    bm = bmesh.new()
    bm.from_mesh(o.data)
    bm.faces.ensure_lookup_table()
    m = o.matrix_world
    out = [([f.index for f in comp], np.array([tuple(m @ v.co) for v in {v for f in comp for v in f.verts}]))
           for comp in _islands(bm.faces)]
    bm.free()
    return out


def _excluded(o, exclude):
    return any(x in o.name for x in exclude)


def auto_attach(no, exclude=(), min_faces=1):
    """Fold the small loose parts that belong to door `no` into it: islands (connected parts of min_faces to ATTACH_SMALL
    faces) of what stays, standing inside the door's box and touching the door's surface (within ATTACH_TOUCH, at least
    ATTACH_SHARE of their points, and more of them than touch the rest of what stays) are cut off their object and attach()ed
    to the door. Islands that sit on what stays are left, and so are the objects whose name contains any of `exclude`.
    Returns the names of the new objects."""
    h = _hinge(no)
    scene = bpy.context.scene
    model.shut_all(scene)
    door = model.pieces_of(h, scene)
    box = np.concatenate([_world_points(o) for o in door])
    lo, hi = box.min(axis=0) - 0.01, box.max(axis=0) + 0.01

    def surface(objs):
        verts, polys, owner = [], [], []
        for k, o in enumerate(objs):
            off = len(verts)
            verts += [tuple(o.matrix_world @ v.co) for v in o.data.vertices]
            for p in o.data.polygons:
                polys.append(tuple(off + i for i in p.vertices))
                owner.append((k, p.index))
        return BVHTree.FromPolygons(verts, polys), owner
    still = [o for o in scene.objects if o.type == "MESH" and not o.get("fh_hinge") and not o.get("fh_link")
             and not o.get("fh_still") and len(o.data.polygons)]
    door_tree, _ = surface(door)
    rest_tree, owner = surface(still)
    which = {}
    for k, o in enumerate(still):
        for n, (faces, _) in enumerate(_islands_of(o)):
            for f in faces:
                which[(k, f)] = n
    take = {}
    for k, o in enumerate(still):
        for n, (faces, pts) in enumerate(_islands_of(o)):
            c = pts.mean(axis=0)
            if not min_faces <= len(faces) <= ATTACH_SMALL or _excluded(o, exclude) or np.any(c < lo) or np.any(c > hi):
                continue
            sample = pts[::max(1, len(pts) // 150)]
            on_door =np.mean([door_tree.find_nearest(tuple(p), ATTACH_TOUCH)[0] is not None for p in sample])
            on_rest = np.mean([any(which[owner[i[2]]] != n or owner[i[2]][0] != k
                                   for i in rest_tree.find_nearest_range(tuple(p), ATTACH_TOUCH)) for p in sample])
            if on_door >= ATTACH_SHARE and on_door > on_rest:
                take.setdefault(k, []).append(faces)
    tail = door[0].name.split("号-", 1)[-1]
    names = []
    for k, sets in take.items():
        o = still[k]
        pieces = click.split_many(o, sets, ["%d号-%s-挂件%d" % (int(no), tail, len(names) + i + 1) for i in range(len(sets))])
        for piece in pieces:
            piece["fh_source"] = o.name
        names += attach(pieces, no)
        if not o.data.polygons:
            me = o.data
            bpy.data.objects.remove(o, do_unlink=True)
            if me.users == 0:
                bpy.data.meshes.remove(me)
    return names


def _surface_of(objs):
    """(BVH of the objects' faces as they stand, [(object index, face index)] per triangle-tree face)."""
    verts, polys, owner = [], [], []
    for k, o in enumerate(objs):
        off = len(verts)
        verts += [tuple(o.matrix_world @ v.co) for v in o.data.vertices]
        for p in o.data.polygons:
            polys.append(tuple(off + i for i in p.vertices))
            owner.append((k, p.index))
    return BVHTree.FromPolygons(verts, polys), owner


def _loose_parts(scene):
    """Every island of every mesh that is neither a piece, a link nor a still part (feet, helmet, body: still()): [{obj,
    island (face indices), faces, pts, centre, door (the number of the door it sits on, or None), share (how much of it
    touches that door)}]. An island sits on a door as auto_attach() judges it (at most ATTACH_SMALL faces, inside the
    door's box, at least ATTACH_SHARE of its points within ATTACH_TOUCH of the door and more than touch the rest); touching
    several, it goes to the one it touches most."""
    model.shut_all(scene)
    rest = [o for o in scene.objects if o.type == "MESH" and not o.get("fh_hinge") and not o.get("fh_link")
            and not o.get("fh_still") and len(o.data.polygons)]
    if not rest:
        return []
    rest_tree, owner = _surface_of(rest)
    which, parts = {}, []
    for k, o in enumerate(rest):
        for n, (faces, pts) in enumerate(_islands_of(o)):
            for f in faces:
                which[(k, f)] = n
            parts.append({"obj": o, "k": k, "n": n, "island": faces, "faces": len(faces), "pts": pts,
                          "centre": pts.mean(axis=0), "door": None, "share": 0.0})
    doors = []
    for h in model.hinges(scene):
        ps = model.pieces_of(h, scene)
        if ps:
            box = np.concatenate([_world_points(o) for o in ps])
            doors.append((int(h["fh_no"]), box.min(axis=0) - 0.01, box.max(axis=0) + 0.01, _surface_of(ps)[0]))
    for p in parts:
        if p["faces"] > ATTACH_SMALL:
            continue
        sample = p["pts"][::max(1, len(p["pts"]) // 150)]
        near = [d for d in doors if not (np.any(p["centre"] < d[1]) or np.any(p["centre"] > d[2]))]
        if not near:
            continue
        on_rest = np.mean([any(which[owner[i[2]]] != p["n"] or owner[i[2]][0] != p["k"]
                               for i in rest_tree.find_nearest_range(tuple(q), ATTACH_TOUCH)) for q in sample])
        for no, _, _, tree in near:
            on = float(np.mean([tree.find_nearest(tuple(q), ATTACH_TOUCH)[0] is not None for q in sample]))
            if on >= ATTACH_SHARE and on > on_rest and on > p["share"]:
                p["door"], p["share"] = no, on
    return parts


def unregistered():
    """What is in no piece and not marked still (feet, helmet, body: still()): one entry per island of those meshes, the ones
    sitting on a door first (attach_loose() folds those in), [{name (the object), faces, centre (world), door (the number of
    the door it sits on, or None)}]. The review pictures show these in UNREG_COLOUR and still parts in light grey."""
    parts = _loose_parts(bpy.context.scene)
    parts.sort(key=lambda p: (p["door"] is None, p["obj"].name, -p["faces"]))
    return [{"name": p["obj"].name, "faces": p["faces"], "centre": [round(float(c), 3) for c in p["centre"]],
             "door": p["door"]} for p in parts]


def attach_loose(exclude=(), min_faces=1):
    """Fold every loose part that sits on a door (unregistered() lists it with its door) into that door, each to the one it
    touches most: cut off its object and attach()ed. Parts of fewer than `min_faces` faces, and of objects whose name
    contains any of `exclude`, are left. Returns {door number: [new object names]}."""
    scene = bpy.context.scene
    parts = [p for p in _loose_parts(scene) if p["door"] is not None and p["faces"] >= min_faces
             and not _excluded(p["obj"], exclude)]
    out = {}
    for o in {p["obj"] for p in parts}:
        mine = [p for p in parts if p["obj"] is o]
        made = click.split_many(o, [p["island"] for p in mine],
                                ["%d号-%s-挂件%d" % (p["door"], o.name, i + 1) for i, p in enumerate(mine)])
        for p, piece in zip(mine, made):
            piece["fh_source"] = o.name
            out.setdefault(p["door"], []).extend(attach([piece], p["door"]))
        if not o.data.polygons:
            me = o.data
            bpy.data.objects.remove(o, do_unlink=True)
            if me.users == 0:
                bpy.data.meshes.remove(me)
    _refresh()
    return out


def _absorb(frag, target):
    """Weld a fragment into `target` (its seam joined) and remove it."""
    geom.merge_back(frag, target)


def orphans(fix=False):
    """The fragments no piece or part has taken in: loose small objects (at most ORPHAN_FACES faces, not a piece, link or still
    part: kind "碎片物体"), empty objects ("空"), and islands of at most ORPHAN_FACES faces inside a piece that lie more than
    ORPHAN_AWAY from the rest of it ("碎片岛"). Returns [{name, kind, faces, near (nearest other object), mm, stays}]. With
    fix=True the loose objects are joined into the nearest other object (empty ones are left) and each has `merged` = that
    object; an island of a piece stays in its piece (stays=True: a trim or rivet that rides on it) unless it touches (within
    ORPHAN_TOUCH_MM) a part that stays, of which it is then a scrap the cut caught and goes back to it. For the small parts of
    what stays that sit on a door, see unregistered() / attach_loose()."""
    scene = bpy.context.scene
    found = []
    for o in [o for o in scene.objects if o.type == "MESH" and not o.get("fh_link")]:
        n = len(o.data.polygons)
        if not o.get("fh_hinge") and not o.get("fh_still"):
            if n == 0:
                found.append({"name": o.name, "kind": "空", "faces": 0, "obj": o, "island": None})
            elif n <= ORPHAN_FACES:
                found.append({"name": o.name, "kind": "碎片物体", "faces": n, "obj": o, "island": None})
        elif o.get("fh_hinge") and n > ORPHAN_FACES:
            parts = _islands_of(o)
            main = max(parts, key=lambda p: len(p[0]))
            tree = geom.kd([tuple(p) for p in main[1]])
            for part in parts:
                if part is not main and len(part[0]) <= ORPHAN_FACES and min(tree.find(tuple(p))[2] for p in part[1]) > ORPHAN_AWAY:
                    found.append({"name": o.name, "kind": "碎片岛", "faces": len(part[0]), "obj": o, "island": part[0]})
    for f in found:
        o = f["obj"]
        if f["island"] is not None:
            pts = _world_points(o)
            keep = sorted({v for i in f["island"] for v in o.data.polygons[i].vertices})
            near = _nearest_to([Vector(pts[v]) for v in keep], skip=(o,))
        else:
            near = _nearest_mesh(o) if o.data.vertices else None
        f["target"] = near[1] if near is not None else None
        f["near"], f["mm"] = (near[1].name, round(near[0] * 1000, 1)) if near is not None else (None, None)
    for f in found:
        if f["island"] is not None and not (f["target"] is not None and not f["target"].get("fh_hinge")
                                            and f["mm"] is not None and f["mm"] <= ORPHAN_TOUCH_MM):
            # an island of a piece (a trim, a rivet) stays with its piece: only a scrap of what stays that the cut caught
            # (touching that part) goes back to it
            f["target"], f["stays"] = None, True
    if fix:
        model.shut_all(scene)
        for o in {f["obj"] for f in found if f["island"] is not None and f["target"] is not None}:
            mine = [f for f in found if f["obj"] is o and f["island"] is not None and f["target"] is not None]
            for f, probe in zip(mine, click.split_many(o, [f["island"] for f in mine], ["_碎片"] * len(mine))):
                _absorb(probe, f["target"])
                f["merged"] = f["target"].name
        for f in found:
            if f["island"] is None and f["kind"] != "空" and f["target"] is not None:
                f["merged"] = f["target"].name
                _absorb(f["obj"], f["target"])
        _refresh()
    return [{k: v for k, v in f.items() if k not in ("obj", "island", "target")} for f in found]


def shut():
    model.shut_all(bpy.context.scene)
    _refresh()


def pose(t=1.0):
    """Every piece fraction t of the way along its path (0 shut, 1 fully open), its links with it, for looking."""
    pose_scene(bpy.context.scene, t)
    _refresh()


def pose_scene(scene, t):
    model.shut_all(scene)
    for h in model.hinges(scene):
        m = model.turn(h, t)
        for o in model.pieces_of(h, scene):
            o.matrix_world = m @ model.base(o)
        for o in model.links_of(h, scene):
            o.matrix_world = motion.link_pose(model.link_spec(o), m) @ model.base(o)


def keyframes(scene=None):
    """Key the whole open-and-close sequence (unlatch, staggered moves, overshoot, settle, the closing) on every frame,
    pieces and links. Returns (frame count, last opening frame)."""
    scene = scene or bpy.context.scene
    hs, ls, parts = model.parts(scene)
    if not hs:
        raise ValueError("还没有开合件")
    model.shut_all(scene)
    opened, closed, _ = motion.sequence(parts)
    objs = [model.pieces_of(h, scene) for h in hs] + [[o] for o in ls]
    for group, o_track, c_track in zip(objs, opened, closed):
        track = o_track + c_track
        for o in group:
            b = model.base(o)
            o.rotation_mode = "QUATERNION"
            for k, m in enumerate(track):
                o.matrix_world = m @ b
                o.keyframe_insert("location", frame=k + 1)
                o.keyframe_insert("rotation_quaternion", frame=k + 1)
    n = len(opened[0]) + len(closed[0])
    scene.render.fps = motion.FPS
    scene.frame_start, scene.frame_end = 1, n
    scene.frame_set(1)
    return n, len(opened[0])


def play():
    n, open_frames = keyframes()
    for win in bpy.context.window_manager.windows:
        area = next((a for a in win.screen.areas if a.type == "VIEW_3D"), None)
        if area is not None:
            with bpy.context.temp_override(window=win, area=area):
                if not bpy.context.screen.is_animation_playing:
                    bpy.ops.screen.animation_play()
            break
    return {"frames": n, "openSeconds": round(open_frames / motion.FPS, 2), "seconds": round(n / motion.FPS, 2)}


def check():
    """Run every check; failing pieces turn red in the viewport. Returns {no: row} and the summary (pieces, open time,
    entry cover, arc per limb, limbBackClearDeg: the arc straight behind each arm and leg segment left clear fully open).
    Row items: attachMm, lift, bodyMm/bodyPart/bodyFrame/bodyObj/bodyAt (the deepest point into the
    body: its object, where, when), startPct/startHit/startAt/startFrame (the first point of what stays it runs into),
    anchorMm, areaPct, coverPct, arcDeg, floatMm/floatWhere/railOverlapMm (the worst gap from a hinge line or rail start
    to what stays, what it hangs on and what it rests on, the segment it is on, and the least a slide leaves of the piece
    over its rail; a path with neither: the worst gap at floatAt of its path), failed, allowed."""
    scene = bpy.context.scene
    model.shut_all(scene)
    rows, summary = checks.run(scene)
    scene["fh_check"] = json.dumps({"summary": summary, "hinges": rows}, ensure_ascii=False)
    _refresh()
    out = {}
    for name, row in rows.items():
        h = bpy.data.objects.get(name)
        out[int(h["fh_no"]) if h is not None else name] = row
    return out, summary


def allow(no, item):
    """Accept one check result for a piece (attach / links / anchor / lift / start / body / cover / arc / axis / float / clash
    / dir / jump); it is no longer listed."""
    h = _hinge(no)
    ok = model.allowed(h)
    ok.add(item)
    model.set_allowed(h, ok)
    return check()[0].get(int(no))


FIT_ITEMS = ("body", "start", "float", "clash")
FIT_KEYS = ("bodyMm", "startPct", "floatMm", "clashMm")


def _snapshot(h):
    return {k: h[k] for k in ("fh_angle", "fh_path", "fh_move") if k in h}, [model.link_spec(o) for o in model.links_of(h)]


def _restore(h, snap):
    props, specs = snap
    moves.clear_links(h)
    for k in ("fh_angle", "fh_path", "fh_move"):
        if k in props:
            h[k] = props[k]
        elif k in h:
            del h[k]
    for k, spec in enumerate(specs):
        moves.make_link(h, spec, k)


def _params(kind, settings):
    """`settings` of preset `kind` as its function takes them: what a record only notes (note, what, the rail it got, ...)
    left out, a `then` cleaned the same way."""
    takes = set(inspect.signature(moves.PRESETS[kind]).parameters) - {"shape"}
    out = {k: v for k, v in settings.items() if k in takes}
    if settings.get("then"):
        k2, s2 = settings["then"][0], dict(settings["then"][1])
        out["then"] = [k2, _params(k2, s2) if k2 in moves.PRESETS else s2]
    return out


def _preset_settings(h, rec=None):
    """The settings (with "then") the piece's recorded preset (fh_move) is built from, as move() takes them (_params). A
    zlink recorded without its direction gets it back from its swing: the way it goes is against the swing's radius, less
    the plate's normal."""
    rec = model.move_of(h) if rec is None else rec
    kind = rec.get("kind", "hinge")
    if kind not in moves.PRESETS:
        raise ValueError("%s 号的开法 %s 不是预设，没有可改的参数" % (h.get("fh_no"), kind))
    settings = _params(kind, {k: v for k, v in rec.items() if k != "kind"})
    if kind == "zlink" and settings.get("direction") is None:
        arc = next((s for s in model.path(h).segments if motion.kind(s) == "arc"), None)
        if arc is None:
            raise ValueError("%s 号记作 Z 形连杆，路径里却没有摆动段，读不出方向" % h.get("fh_no"))
        n, r = moves.Shape(h).n, Vector(arc["radius"])
        settings["direction"] = [round(c, 4) for c in (-(r - n * r.dot(n))).normalized()]
    return settings


NOTE_KEYS = ("note", "what")      # what a record says in words: kept when a setting is changed


def _move_keeping_notes(h, kind, settings):
    """move() the piece with `settings` (with "then"), keeping the record's notes."""
    notes = {k: v for k, v in model.move_of(h).items() if k in NOTE_KEYS}
    settings = dict(settings)
    then = settings.pop("then", None)
    move(int(h["fh_no"]), kind, then=tuple(then) if then else None, **settings)
    if notes:
        h["fh_move"] = json.dumps({**model.move_of(h), **notes}, ensure_ascii=False)


def _hinge_turn_rebuild(h):
    """(kind, then, settings) of a preset whose turn is the hinge's own (fh_angle): hinge, anything then hinge, a twist or
    flare alone (its turn is the hinge's); None for other ways."""
    rec = model.move_of(h)
    kind = rec.get("kind", "hinge")
    then = tuple(rec["then"]) if rec.get("then") else None
    if kind in moves.PRESETS and (kind == "hinge" or (then and then[0] == "hinge") or (kind in ("twist", "flare") and not then)):
        settings = _preset_settings(h, rec)
        then = settings.pop("then", None)
        return kind, tuple(then) if then else None, settings
    return None


TURN_DEG_PRESETS = {"edge": "lift", "plug": "lift", "scissor_door": "signed", "turn": "signed", "zlink": "signed",
                    "twist": "signed", "lift_turn": "signed"}     # presets whose `deg` is a turn (lift: its size; the way is the lifting one)


def _rebuilds(h):
    """Whether the piece's recorded preset (fh_move) still builds the path it has (a path set by hand under a preset's name
    does not)."""
    rec = model.move_of(h)
    try:
        settings = _preset_settings(h, rec)
        then = settings.pop("then", None)
        segs, _ = moves.build(h, rec.get("kind", "hinge"), then=tuple(then) if then else None, **settings)
    except Exception:
        return False
    have = model.path(h).segments
    built = motion.Path(segs).segments if segs else motion.Path.hinge(*model.line(h), h["fh_angle"]).segments
    return len(built) == len(have) and all(
        set(a) == set(b) and all(np.allclose(np.asarray(a[k], float), np.asarray(b[k], float), atol=1e-4)
                                 for k in a if k != "back") for a, b in zip(built, have))


def _turn_on_path(h, deg, flip=False):
    """Make the turn a piece's path really makes `deg` (signed) and rebuild the path: a hinge turn in it (hinge, anything then
    hinge) is fh_angle; else the `deg` of the preset that turns (the last one in the `then` chain that turns), as that
    preset takes it (plug and edge take its size and turn the lifting way: flip refuses). A path its preset no longer
    builds (set by hand) gets its last segment's turn set in place when that turn ends the path. A path with no turn
    refuses."""
    no = int(h["fh_no"])
    if deg is None:
        raise ValueError("%s 号的开法里没有转动，没有角度可改" % no)
    rec = model.move_of(h)
    kind = rec.get("kind", "hinge")
    if not _rebuilds(h):
        segs = model.path(h).to_json()
        stay = [s for s in segs if motion.kind(s) == "turn" and not s.get("back")]
        last = max(stay, key=lambda s: abs(s["turn"])) if stay else None
        if last is None or any(s["at"][0] >= last["at"][1] - 1e-6 for s in segs if s is not last):
            raise ValueError("%s 号是手设的路径（%s），转动后面还有别的段，改角度会让后面的段错位；用 move() 或 path() 重设"
                             % (no, moves.LABEL.get(kind, kind)))
        last["turn"] = float(deg)
        if _hinge_turn_rebuild(h) is not None:   # its preset turns by the hinge's own angle: keep that the same
            h["fh_angle"] = float(deg)
        model.set_path(h, segs, rec)
        _refresh()
        return
    again = _hinge_turn_rebuild(h)
    then = [rec["then"][0], dict(rec["then"][1])] if rec.get("then") else None
    if again is not None and (kind == "hinge" or (then and then[0] == "hinge")):
        h["fh_angle"] = float(deg)
        kind, then_, settings = again
        if kind == "hinge":
            settings.pop("deg", None)
        _move_keeping_notes(h, kind, {**settings, **({"then": list(then_)} if then_ else {})})
        return
    settings = json.loads(json.dumps(_preset_settings(h, rec)))
    levels, kd, sets = [], kind, settings      # the presets it chains, outermost first (each `then` inside the one before)
    while True:
        levels.append((kd, sets))
        if not sets.get("then"):
            break
        kd, sets = sets["then"][0], sets["then"][1]
    for kd, sets in reversed(levels):         # the last turn is the one that stays (a flare's is out and back)
        if kd in TURN_DEG_PRESETS and (kd != "plug" or sets.get("edge") or sets.get("deg")):
            if flip and TURN_DEG_PRESETS[kd] == "lift":
                raise ValueError("%s 号的%s总是朝外抬的方向转，不能翻；要反向用 move() 换开法" % (no, moves.LABEL[kd]))
            sets["deg"] = abs(deg) if TURN_DEG_PRESETS[kd] == "lift" else float(deg)
            _move_keeping_notes(h, kind, settings)
            return
    raise ValueError("%s 号的开法（%s）里的转动不是一个角度参数能改的；用 move() 重设" % (no, moves.LABEL.get(kind, kind)))


def _set_param(h, param, value):
    no = int(h["fh_no"])
    if param == "angle":
        if model.turns_on_hinge(h):
            h["fh_angle"] = math.copysign(abs(value), h["fh_angle"] or 1)
            return
        now = model.angle_of(h)
        if now is None:
            raise ValueError("%s 号的开法里没有转动，angle 没有意义；用它开法里的参数名（如 distance）" % no)
        _turn_on_path(h, math.copysign(abs(value), now or 1))
        return
    rec = model.move_of(h)
    kind = rec.get("kind", "hinge")
    if kind not in moves.PRESETS:
        raise ValueError("%s 号的开法 %s 不是单件预设，没有参数 %s" % (no, kind, param))
    takes = set(inspect.signature(moves.PRESETS[kind]).parameters) - {"shape"}
    if param not in takes:
        raise ValueError("%s 号的开法 %s 没有参数 %s（有：%s）" % (no, kind, param, "、".join(sorted(takes))))
    if not _rebuilds(h):
        print("%s 号的路径是在%s预设上手调过的；改 %s 会按预设重建整条路径（手调的部分不再保留）" % (no, moves.LABEL.get(kind, kind), param))
    settings = _preset_settings(h, rec)
    settings[param] = value
    _move_keeping_notes(h, kind, settings)


def quick(scene=None, names=None):
    """The quick look a search uses (never what is delivered): for the pieces `names` (hinge names or numbers; default every
    piece), their crossing into what stays and the body, clashes with the pieces near them, direction, float and start,
    body and clashes on a coarser frame step; no area, cover or arc. Body, start, float and clash come out no higher than
    the full check's (it looks at fewer of the same frames), so a piece that fails those here fails them there too;
    through reads exactly as the full check (checks.through). Returns {hinge name: row}, each row marked "coarse"."""
    scene = scene or bpy.context.scene
    if names is not None:
        names = {_hinge(n).name if isinstance(n, int) or str(n).isdigit() else n for n in names}
    return checks.run(scene, names, coarse=True)[0]


FIT_LIMITS = {"bodyMm": checks.BODY_MM, "startPct": checks.START_PCT, "floatMm": checks.FLOAT_MM, "clashMm": checks.CLASH_MM}


def _try(h, scene, items, exact, record):
    """Check piece `h` as it now is (quick unless `exact`) on `items`; appends {ok, failed, exact, readings} to `record`."""
    model.shut_all(scene)
    row = checks.run(scene, {h.name}, coarse=not exact)[0][h.name]
    failed = [k for k in row["failed"] if k in items]
    record.append({"ok": not failed, "failed": failed, "exact": exact,
                   **{k: row[k] for k in FIT_KEYS if row.get(k) is not None}})
    return record[-1]


def fit(no, lo, hi, param="angle", items=FIT_ITEMS, tol=None, apply=False, top=3):
    """The largest value of `param` between `lo` and `hi` for piece `no` that passes `items` (default: body, start, float, clash;
    the other checks are not looked at). `param` is "angle" (degrees, the size of its hinge turn; the sign stays) or a setting of
    the way it opens (distance, deg, lift, ...; move() takes them). Searches by halving with the quick look (it assumes larger
    is worse), to within `tol` (default a 32nd of the range), then runs the full check on the `top` largest values the quick
    look passed, largest first, and halves with the full check between the largest of them it passes and the smallest it
    fails (from `lo` when it passes none), so the answer is where the full check alone would put it. `top=0` uses the full check throughout. The answer always passed the full check. The piece is left as it was
    unless `apply` is true and a value passed. Returns {no, param, best (None if even `lo` fails), candidates: [{value, ok,
    exact, failed, bodyMm, startPct, floatMm, clashMm}] by value}."""
    h = _hinge(no)
    scene = bpy.context.scene
    snap = _snapshot(h)
    cands = []

    def test(value, exact):
        _set_param(h, param, value)
        c = _try(h, scene, items, exact, cands)
        c["value"] = value
        return c["ok"]

    def halve(a, b, exact):
        """a passes, b fails: the largest passing value to within tol."""
        while b - a > (tol or (hi - lo) / 32):
            mid = (a + b) / 2
            if test(mid, exact):
                a = mid
            else:
                b = mid
        return a
    try:
        exact = not top
        best = None if not test(lo, exact) else hi if test(hi, exact) else halve(lo, hi, exact)
        if top and best is not None:    # the quick look never fails what the full check passes: the answer is below its first fail
            passed = sorted({c["value"] for c in cands if c["ok"]}, reverse=True)[:top]
            best = next((v for v in passed if test(v, True)), None)
            above = [c["value"] for c in cands if c["exact"] and not c["ok"] and (best is None or c["value"] > best)]
            if best is None:
                best = None if min(passed) == lo or not test(lo, True) else halve(lo, min(above), True)
            elif above:                 # a larger value passed the quick look but not the full check: halve between them
                best = halve(best, min(above), True)
    finally:
        _restore(h, snap)
    if apply and best is not None:
        _set_param(h, param, best)
    _refresh()
    for c in cands:
        c["value"] = round(c["value"], 3)
    return {"no": int(no), "param": param, "best": None if best is None else round(best, 2),
            "candidates": sorted(cands, key=lambda c: (c["value"], c["exact"]))}


def sweep(no, grid, items=FIT_ITEMS, top=3, apply=False):
    """Try every combination of settings in `grid` ({param: [values]}, params as fit() takes them, set in the order given) on
    piece `no` with the quick look, rank them (fewest of `items` failed, then the most room under the limits: the largest
    reading over its limit, lowest first) and run the full check on the best `top`. The best is the first of those the full
    check passes, ranked by its own readings. The piece is left as it was unless `apply` is true and one passed. Returns {no,
    tried, best ({param: value} or None), confirmed: [{settings, ok, failed, readings}] full checks, ranked, quick: [the same]
    every combination by rank}."""
    import itertools
    h = _hinge(no)
    scene = bpy.context.scene
    snap = _snapshot(h)
    params = list(grid)
    quick_, full = [], []

    def put(values):
        for p, v in zip(params, values):
            _set_param(h, p, v)

    def rank(c):
        return (len(c["failed"]), max([c.get(k, 0.0) / lim for k, lim in FIT_LIMITS.items() if k in c] or [0.0]))
    try:
        for values in itertools.product(*(grid[p] for p in params)):
            put(values)
            _try(h, scene, items, False, quick_)["settings"] = dict(zip(params, values))
        quick_.sort(key=rank)
        for c in quick_[:top]:
            put([c["settings"][p] for p in params])
            _try(h, scene, items, True, full)["settings"] = c["settings"]
        full.sort(key=rank)
    finally:
        _restore(h, snap)
    best = next((c["settings"] for c in full if c["ok"]), None)
    if apply and best is not None:
        put([best[p] for p in params])
    _refresh()
    return {"no": int(no), "tried": len(quick_), "best": best, "confirmed": full, "quick": quick_}


def fit_all(lo=5.0, hi=None, items=FIT_ITEMS):
    """fit() for every piece that turns on its hinge (alone, or after a twist, push or flare): its angle from `lo` to `hi`
    (default: the angle it has now, so the answer is how much of it passes). Nothing is changed. Prints and returns one line
    per piece, [{no, now, best, failed at the next step}]; pieces on other paths are left out (fit them by their own
    setting)."""
    out = []
    for h in sorted(model.hinges(), key=lambda x: int(x.get("fh_no", 0))):
        no = int(h["fh_no"])
        if not (model.turns_on_hinge(h) or _hinge_turn_rebuild(h)) or not model.pieces_of(h):
            continue
        now = abs(h["fh_angle"])
        r = fit(no, lo, hi if hi is not None else now, items=items)
        bad = next((c["failed"] for c in r["candidates"] if not c["ok"]), [])
        out.append({"no": no, "now": round(now, 1), "best": r["best"], "failed": bad})
    for r in out:
        print("%d号  现在 %.0f°  最大可用 %s  %s" % (r["no"], r["now"], "（没有）" if r["best"] is None else "%.1f°" % r["best"],
                                                   "再大会：" + "、".join(checks.NAMES[k] for k in r["failed"]) if r["failed"] else ""))
    return out


def flip_dir(no):
    """Turn piece `no` the other way round its hinge, keeping the change only when its opening direction (the check "dir")
    gets better: a plain hinge, or a preset whose turn is its hinge's (hinge, twist then hinge, flare then hinge, ...: rebuilt
    with the hinge's sign flipped). Other ways (slides, pushes, links) cannot be flipped by sign and raise. Returns {no,
    before, after, kept}."""
    h = _hinge(no)
    scene = bpy.context.scene
    kind = model.move_of(h).get("kind", "hinge")
    on_hinge = model.turns_on_hinge(h)
    again = None if on_hinge else _hinge_turn_rebuild(h)
    if not on_hinge and again is None:
        raise ValueError("%s 号的开法（%s）不是绕铰链转，不能一键翻转；换方向参数重设 move()" % (no, moves.LABEL.get(kind, kind)))

    def score():
        model.shut_all(scene)
        return quick(scene, {h.name})[h.name].get("dirScore")     # direction reads the same in the quick look
    before = score()
    snap = _snapshot(h)
    h["fh_angle"] = -h["fh_angle"]
    if again is not None:
        kind, then, settings = again
        if kind == "hinge":
            settings.pop("deg", None)
        move(no, kind, then=then, **settings)
    after = score()
    kept = before is None or (after is not None and after > before)
    if not kept:
        _restore(h, snap)
    _refresh()
    return {"no": int(no), "before": before, "after": after, "kept": kept}


def fix_dirs():
    """flip_dir() every piece the check finds opening the wrong way ("dir"); those that cannot be flipped are listed with
    the reason. Returns [{no, before, after, kept} or {no, error}]."""
    rows, _ = check()
    out = []
    for no, row in sorted(rows.items(), key=lambda kv: kv[0] if isinstance(kv[0], int) else 0):
        if "dir" in row.get("failed", []):
            try:
                out.append(flip_dir(no))
            except ValueError as e:
                out.append({"no": int(no), "error": str(e)})
    check()
    return out


def _back_clear_text(clear):
    return "全开后肢体正后方空出：" + "  ".join(
        "%s%s %d°" % ({"L": "左", "R": "右"}[k.split(".")[1]], LIMB_ZH[k.split(".")[0]], v) for k, v in clear.items())


def report(rows=None):
    """Print one line per piece that fails a check, only the items it fails ("N号 piece way angle layer/batch: item value; ...")
    under a header, and return the text. `rows` is what check() returned (its first item, or the pair); None runs check()."""
    summary = None
    if rows is None:
        rows, summary = check()
    elif isinstance(rows, tuple):
        rows, summary = rows
    bad = {no: r for no, r in rows.items() if r.get("failed")}
    head = "%d 件，%d 件有没过的项" % (len(rows), len(bad))
    if summary:
        head += "；打开 %.2f 秒，入口挡 %s%%" % (summary.get("openSeconds", 0), summary.get("wayInCoveredPct", "—"))
    lines_ = [head]
    if summary and summary.get("limbBackClearDeg"):
        lines_.append(_back_clear_text(summary["limbBackClearDeg"]))
    for no in sorted(bad, key=lambda k: (not isinstance(k, int), k if isinstance(k, int) else 0, str(k))):
        r = bad[no]
        h = model.by_no(no) if isinstance(no, int) else None
        way = "铰链" if r.get("move", "hinge") == "hinge" else moves.LABEL.get(r["move"], r["move"])
        if r.get("angle") is not None:
            way += " %+.0f°" % r["angle"]
        where = "%s %s批" % (LAYER_LABEL.get(h.get("fh_layer"), ""), h.get("fh_batch")) if h is not None else ""
        lines_.append("%s号 %s  %s  %s  ：%s" % (no, "、".join(r.get("pieces", [])), way, where,
                                              "；".join(checks.describe(r, k) for k in r["failed"])))
    text = "\n".join(lines_)
    print(text)
    return text


def bake(posed, report):
    """Write the file the build reads (one collection per hinge keyed shut and open, the rest fixed) as a copy; the open
    file is left as it is."""
    import subprocess
    import sys
    path = bpy.data.filepath
    if not path:
        raise ValueError("先保存这个文件")
    bpy.ops.wm.save_mainfile()
    script = os.path.join(os.path.dirname(os.path.abspath(__file__)), "bake.py")
    r = subprocess.run([bpy.app.binary_path, "-b", path, "--python", script, "--", posed, report],
                       capture_output=True, text=True, encoding="utf-8", errors="replace")
    tail = [l for l in r.stdout.splitlines() if l.startswith("BAKE") or "Error" in l]
    if r.returncode != 0 or not any(l.startswith("BAKE") for l in tail):
        raise RuntimeError("\n".join(tail[-10:]) or r.stderr[-2000:])
    return tail[-1]


def _refresh():
    try:
        from . import panel
        panel.refresh_overlays()
    except Exception:   # no viewport (background): nothing to draw
        pass


# ----------------------------------------------------------------------------------------------------------------- review
VIEWS = {"背面": Vector((0, 1, 0.12)), "侧后": Vector((0.8, 1, 0.2)), "右侧后": Vector((-0.8, 1, 0.2)),
         "左侧": Vector((1, 0.15, 0.1)), "右侧": Vector((-1, 0.15, 0.1)), "顶": Vector((0, 0.35, 1)),
         "正面": Vector((0, -1, 0.1))}
GIF_VIEWS = ("背面", "侧后")


class _Shot:
    """A temporary orthographic camera and Workbench settings; everything is put back afterwards."""

    def __init__(self, scene, w, h):
        self.scene = scene
        r = scene.render
        self.saved = (scene.camera, r.engine, r.resolution_x, r.resolution_y, r.resolution_percentage, r.filepath,
                      r.film_transparent, scene.frame_current)
        pts = [o.matrix_world @ Vector(c) for o in scene.objects if o.type == "MESH" and o.visible_get()
               for c in o.bound_box]
        lo = Vector([min(p[i] for p in pts) for i in range(3)])
        hi = Vector([max(p[i] for p in pts) for i in range(3)])
        self.centre, self.height = (lo + hi) / 2, hi.z - lo.z
        self.cam = bpy.data.objects.new("_fh_review_cam", bpy.data.cameras.new("_fh_review_cam"))
        self.cam.data.type = "ORTHO"
        self.cam.data.ortho_scale = self.height * 1.12
        scene.collection.objects.link(self.cam)
        scene.camera = self.cam
        r.engine = "BLENDER_WORKBENCH"
        r.resolution_x, r.resolution_y, r.resolution_percentage = w, h, 100
        r.film_transparent = True
        # one colour per opening piece, light grey for the parts marked still, UNREG_COLOUR for what is in no piece and not
        # marked still (a plate or rivet nobody registered)
        sh = scene.display.shading
        self.shading = sh.color_type
        self.colors = {o: tuple(o.color) for o in scene.objects if o.type == "MESH"}
        sh.color_type = "OBJECT"
        for o in self.colors:
            o.color = GREY if o.get("fh_still") else UNREG_COLOUR
        for i, h in enumerate(model.hinges(scene)):
            c = colorsys.hsv_to_rgb(((i * 0.618034) % 1) * 0.72, 0.75, 0.95)    # hues short of UNREG_COLOUR's magenta
            for o in model.pieces_of(h, scene):
                o.color = (*c, 1)
            for o in model.links_of(h, scene):
                o.color = (c[0] * 0.55, c[1] * 0.55, c[2] * 0.55, 1)

    def aim(self, d):
        d = d.normalized()
        self.cam.location = self.centre + d * 6
        self.cam.rotation_euler = (-d).to_track_quat("-Z", "Y").to_euler()
        bpy.context.view_layer.update()

    def render(self, path):
        """Render to `path` and return it as an RGB image on white."""
        from PIL import Image
        self.scene.render.filepath = path
        bpy.ops.render.render(write_still=True)
        im = Image.open(path).convert("RGBA")
        white = Image.new("RGBA", im.size, (255, 255, 255, 255))
        return Image.alpha_composite(white, im).convert("RGB")

    def to_px(self, p):
        v = world_to_camera_view(self.scene, self.cam, Vector(p))
        r = self.scene.render
        return v.x * r.resolution_x, (1 - v.y) * r.resolution_y

    def close(self):
        s, r = self.scene, self.scene.render
        (s.camera, r.engine, r.resolution_x, r.resolution_y, r.resolution_percentage, r.filepath, r.film_transparent,
         frame) = self.saved
        s.display.shading.color_type = self.shading
        for o, c in self.colors.items():
            o.color = c
        cam = self.cam.data
        bpy.data.objects.remove(self.cam, do_unlink=True)
        bpy.data.cameras.remove(cam)
        s.frame_set(frame)


def _font(size):
    from PIL import ImageFont
    for f in ("C:/Windows/Fonts/msyhbd.ttc", "C:/Windows/Fonts/msyh.ttc"):
        if os.path.exists(f):
            return ImageFont.truetype(f, size)
    return ImageFont.load_default()


def review(out, name):
    """The review pack in folder `out`: <name>_背面.gif and <name>_侧后.gif (open, hold, close), <name>_编号.png (the shut
    suit from behind and both back corners: every piece numbered, its cut red, its hinge blue (on a path, the line it
    really turns about, model.turn_line; none for a path that only slides), an arrow the way it opens)
    and <name>_表.md (number, piece, layer/batch, angle, checks). Returns the paths."""
    from PIL import Image, ImageDraw
    os.makedirs(out, exist_ok=True)
    scene = bpy.context.scene
    rows, summary = check()
    paths = []
    tmp = os.path.join(out, "_frames")
    os.makedirs(tmp, exist_ok=True)
    shot = _Shot(scene, 520, 640)
    try:
        n, open_frames = keyframes(scene)
        font = _font(18)
        for view in GIF_VIEWS:
            shot.aim(VIEWS[view])
            seq = []
            for f in range(1, n + 1):
                scene.frame_set(f)
                p = os.path.join(tmp, "%s_%03d.png" % (view, f))
                im = shot.render(p)
                ImageDraw.Draw(im).text((8, 6), "%s %s %.2f s" % (name, view, (f - 1) / motion.FPS), fill=(20, 20, 20),
                                        font=font)
                seq.append(im)
                if f == open_frames:
                    seq += [im] * motion.FPS
            seq += [seq[-1]] * motion.FPS
            path = os.path.join(out, "%s_%s.gif" % (name, view))
            seq[0].save(path, save_all=True, append_images=seq[1:], duration=int(1000 / motion.FPS), loop=0)
            paths.append(path)
        model.shut_all(scene)
        scene.frame_set(1)
        loose = _loose_parts(scene)
        paths.append(_overview(shot, out, name, rows, loose))
    finally:
        shot.close()
        model.shut_all(scene)
        for f in os.listdir(tmp):
            os.remove(os.path.join(tmp, f))
        os.rmdir(tmp)
    paths.append(_table(out, name, rows, summary, loose))
    _refresh()
    return paths


def _overview(shot, out, name, rows, loose=()):
    from PIL import Image, ImageDraw
    scene = shot.scene
    W, H = 900, 1100
    r = scene.render
    r.resolution_x, r.resolution_y = W, H
    panels = []
    font, small = _font(30), _font(22)
    for view in ("侧后", "背面", "右侧后"):
        d = VIEWS[view].normalized()
        shot.aim(VIEWS[view])
        p = os.path.join(out, "_ov_%s.png" % view)
        im = shot.render(p)
        os.remove(p)
        g = ImageDraw.Draw(im)
        for h in model.hinges(scene):
            ps = model.pieces_of(h, scene)
            if not ps:
                continue
            pts = np.array([tuple(o.matrix_world @ o.data.vertices[i].co) for o in ps for i in model.own(o)[0]])
            nrm = np.array([tuple((o.matrix_world.to_3x3() @ f.normal) * f.area) for o in ps for f in model.own(o)[1]])
            if np.sum(nrm, axis=0) @ np.array(d) <= 0:     # facing away from this camera
                continue
            no = int(h["fh_no"])
            bad = rows.get(no, {}).get("failed")
            cut, hl = model.lines_of(h)
            for l in cut:
                g.line([shot.to_px(q) for q in l], fill=(230, 30, 30), width=4)
            if not model.turns_on_hinge(h):      # a path: the line it really turns about, over the piece's reach along it
                tl = model.turn_line(h)
                if tl is None:
                    hl = []
                else:
                    pa, ax = np.array(tuple(tl[0])), np.array(tuple(tl[1]))
                    s = (pts - pa) @ ax
                    hl = [Vector(pa + ax * s.min()), Vector(pa + ax * s.max())]
            if hl:
                g.line([shot.to_px(q) for q in hl], fill=(30, 90, 240), width=5)
            c = Vector(pts.mean(axis=0))
            trail = [shot.to_px(model.turn(h, k / 16) @ c) for k in range(17)]
            if len(trail) > 1:
                g.line(trail, fill=(20, 170, 60), width=5)
                a, b = trail[-2], trail[-1]
                v = Vector((b[0] - a[0], b[1] - a[1]))
                if v.length > 1:
                    v.normalize()
                    side = Vector((-v.y, v.x))
                    tip = Vector(b)
                    g.polygon([tuple(tip), tuple(tip - v * 16 + side * 9), tuple(tip - v * 16 - side * 9)],
                              fill=(20, 170, 60))
            x, y = shot.to_px(c)
            g.ellipse([x - 20, y - 20, x + 20, y + 20], fill=(255, 80, 80) if bad else (255, 255, 255),
                      outline=(0, 0, 0), width=2)
            g.text((x, y), str(no), fill=(0, 0, 0), font=small, anchor="mm")
        for p in loose:
            if p["door"] is None:
                continue
            x, y = shot.to_px(Vector(p["centre"]))
            g.ellipse([x - 14, y - 14, x + 14, y + 14], outline=(255, 140, 0), width=4)
            g.text((x + 16, y), "→%d" % p["door"], fill=(200, 90, 0), font=small, anchor="lm")
        g.text((14, 10), "%s %s" % (name, view), fill=(20, 20, 20), font=font)
        panels.append(im)
    sheet = Image.new("RGB", (W * len(panels), H), (255, 255, 255))
    for k, im in enumerate(panels):
        sheet.paste(im, (k * W, 0))
    ImageDraw.Draw(sheet).text((14, H - 40), "红=切口  蓝=铰链  绿箭头=打开方向  红圈=检查没过  灰=固定  粉=未登记  "
                               "橙圈→号=贴在该号门上没并入的零件", fill=(20, 20, 20), font=small)
    path = os.path.join(out, "%s_编号.png" % name)
    sheet.save(path)
    return path


LAYER_LABEL = {"outer": "外层", "inner": "内层"}
ROW_LABEL = {"": "", "top": "上", "mid": "中", "low": "下"}


def _table(out, name, rows, summary, loose=()):
    lines_ = ["# %s 开合件" % name, "",
              "打开 %.2f 秒；入口被挡 %s%%" % (summary.get("openSeconds", 0), summary.get("wayInCoveredPct", "—")), ""]
    if summary.get("limbBackClearDeg"):
        lines_ += [_back_clear_text(summary["limbBackClearDeg"]), ""]
    if loose:
        on_door = [p for p in loose if p["door"] is not None]
        objs = sorted({p["obj"].name for p in loose})
        lines_ += ["未登记（不在任何件里、也没标固定）：%d 块，在 %s；其中 %d 块贴在门上没并入（%s），attach_loose() 一键并入。" % (
            len(loose), "、".join(objs), len(on_door),
            "、".join("%s→%d号" % (p["obj"].name, p["door"]) for p in on_door[:12]) or "无"), ""]
    lines_ += ["| 号 | 件 | 层/批次 | 开法 | 检查 |", "|---|---|---|---|---|"]
    for it in info():
        row = rows.get(it["no"], {})
        bad = "；".join(checks.describe(row, k) for k in row.get("failed", [])) or "通过"
        if row.get("allowed"):
            bad += "（已允许：%s）" % "、".join(checks.NAMES[k] for k in row["allowed"])
        kind = it["move"].get("kind", "hinge")
        way = "铰链" if kind == "hinge" else moves.LABEL.get(kind, kind)
        if it["move"].get("then"):
            way += " → " + moves.LABEL.get(it["move"]["then"][0], it["move"]["then"][0])
        if it["angle"] is not None:
            way += " %+.0f°" % it["angle"]      # the turn it really makes
        if it["parent"]:
            way += "（挂在 %d 号上）" % it["parent"]
        if it["links"]:
            way += "（%d 根连杆）" % len(it["links"])
        lines_.append("| %d | %s | %s %s%s%s | %s | %s |" % (
            it["no"], "、".join(it["pieces"]), LAYER_LABEL.get(it["layer"], it["layer"]), it["batch"],
            ROW_LABEL.get(it["row"] or "", ""), " +%d 帧" % it["delay"] if it["delay"] else "", way, bad))
    path = os.path.join(out, "%s_表.md" % name)
    with open(path, "w", encoding="utf-8") as f:
        f.write("\n".join(lines_) + "\n")
    return path


def _frame(nos, views):
    """(centre, size) that frames pieces `nos` and their links through the whole of their paths from every view."""
    scene = bpy.context.scene
    pts = []
    for no in nos:
        h = _hinge(no)
        for t in (0.0, 0.25, 0.5, 0.75, 1.0):
            m = model.turn(h, t)
            for o in model.pieces_of(h, scene):
                co = np.empty(len(o.data.vertices) * 3)
                o.data.vertices.foreach_get("co", co)
                pts.append(checks.apply(np.array(m @ model.base(o)), co.reshape(-1, 3)))
            for o in model.links_of(h, scene):
                co = np.empty(len(o.data.vertices) * 3)
                o.data.vertices.foreach_get("co", co)
                pts.append(checks.apply(np.array(motion.link_pose(model.link_spec(o), m) @ model.base(o)), co.reshape(-1, 3)))
    pts = np.concatenate(pts)
    centre = (pts.min(axis=0) + pts.max(axis=0)) / 2
    half = 0.0
    for v in views:
        q = (-(VIEWS[v] if isinstance(v, str) else Vector(v)).normalized()).to_track_quat("-Z", "Y")
        for axis in (q @ Vector((1, 0, 0)), q @ Vector((0, 1, 0))):
            half = max(half, float(np.abs((pts - centre) @ np.array(axis)).max()))
    return Vector(centre), max(0.12, 2 * half * 1.12)


def sheet(path, centre=None, size=None, nos=None, fracs=(0, .25, .5, 1), views=("背面", "侧后", "右侧后"), highlight=None,
          px=420, label="", still=None):
    """One picture of the opening in `path` (.png): a column per frame, a row per view (VIEWS: 背面 侧后 右侧后 左侧 右侧 顶
    正面, or a direction (x, y, z)), each cell marked with its view and frame number. The frames are `fracs` of the opening
    sequence (what play() keys: pieces start at their own times). Each opening piece has its own colour, what stays is
    grey (review's colours). `nos` frames those pieces through their whole paths; `centre` (x, y, z) and `size` (the width
    of a cell in metres) frame it by hand, over the framing from `nos`; with neither it is the whole suit. `highlight` (piece
    numbers) draws those red and everything else grey. `still` shows what does not open, to find parts left behind:
    "highlight" draws every part that is in no piece or link red (marked still or not) and the opening ones light grey,
    "only" draws only those parts (pieces and links left out). `px` is a cell's side, `label` goes first in each mark.
    Hidden parts are shown while it renders and hidden again after. Returns `path`."""
    from PIL import Image, ImageDraw
    if still not in (None, "highlight", "only"):
        raise ValueError("still 是 highlight（不动的部分标红）或 only（只画不动的部分），不是 %s" % (still,))
    scene = bpy.context.scene
    hot = None if highlight is None else {int(n) for n in highlight}
    moving = set(model.pieces(scene)) | set(model.links(scene))
    hidden = {}
    os.makedirs(os.path.dirname(os.path.abspath(path)), exist_ok=True)
    _, restore = _show_all(scene)
    shot = None
    tmp = path + "_tmp.png"
    try:
        n, open_frames = keyframes(scene)
        shot = _Shot(scene, px, px)
        if nos:
            shot.centre, shot.cam.data.ortho_scale = _frame(nos, views)
        if centre is not None:
            shot.centre = Vector(centre)
        if size is not None:
            shot.cam.data.ortho_scale = size
        if hot is not None:
            for o in shot.colors:
                o.color = GREY
            for h in model.hinges(scene):
                if int(h["fh_no"]) in hot:
                    for o in model.pieces_of(h, scene):
                        o.color = HOT_COLOUR
                    for o in model.links_of(h, scene):
                        o.color = tuple(c * 0.55 for c in HOT_COLOUR[:3]) + (1.0,)
        if still is not None:
            for o in shot.colors:
                if o not in moving:
                    o.color = HOT_COLOUR
                elif still == "highlight":
                    o.color = (0.85, 0.85, 0.85, 1.0)
                else:
                    hidden[o] = o.hide_render
                    o.hide_render = True
        font = _font(max(14, px // 24))
        frames = [max(1, min(n, round(1 + f * (open_frames - 1)))) for f in fracs]
        cells = []
        for v in views:
            shot.aim(VIEWS[v] if isinstance(v, str) else Vector(v))
            row = []
            for f in frames:
                scene.frame_set(f)
                im = shot.render(tmp)
                ImageDraw.Draw(im).text((6, 4), "%s%s 第%d帧 %.2f s" % (label + " " if label else "", v if isinstance(v, str)
                                                                    else "视角", f, (f - 1) / motion.FPS),
                                        fill=(20, 20, 20), font=font)
                row.append(im)
            cells.append(row)
    finally:
        for o, was in hidden.items():
            o.hide_render = was
        if shot is not None:
            shot.close()
        model.shut_all(scene)
        restore()
        if os.path.exists(tmp):
            os.remove(tmp)
    out = Image.new("RGB", (px * len(frames), px * len(views)), (255, 255, 255))
    for r, row in enumerate(cells):
        for c, im in enumerate(row):
            out.paste(im, (c * px, r * px))
    out.save(path)
    return path


def clash_shot(path, no, size=0.12, px=420):
    """A close-up of piece `no`'s worst clash (what check() reported: the other piece, the frame, the poking point) in `path`
    (.png): only the two pieces, `no` red and the other blue, see-through, the poking point a yellow dot, from behind, both
    sides and above, `size` metres across. To see whether a reported clash shows. Returns `path`, or None when it has none."""
    from PIL import Image, ImageDraw
    scene = bpy.context.scene
    rows, _ = check()
    row = rows.get(int(no), {})
    if not row.get("clashAt"):
        return None
    other = model.by_no(row["clashWith"])
    keep = set(model.pieces_of(_hinge(no), scene) + model.pieces_of(other, scene))
    os.makedirs(os.path.dirname(os.path.abspath(path)), exist_ok=True)
    _, restore = _show_all(scene)
    shot, mark, tmp = None, None, path + "_tmp.png"
    sh = scene.display.shading
    xray = (sh.show_xray, sh.xray_alpha)
    hidden = {o: o.hide_render for o in scene.objects if o.type == "MESH"}
    cells = []
    try:
        keyframes(scene)
        scene.frame_set(row["clashFrame"])
        shot = _Shot(scene, px, px)
        for o in hidden:
            o.hide_render = o not in keep
        for o in model.pieces_of(_hinge(no), scene):
            o.color = (0.95, 0.25, 0.2, 1)
        for o in model.pieces_of(other, scene):
            o.color = (0.2, 0.45, 0.95, 1)
        me = bpy.data.meshes.new("_fh_mark")
        bm = bmesh.new()
        bmesh.ops.create_icosphere(bm, subdivisions=2, radius=size / 50)
        bm.to_mesh(me)
        bm.free()
        mark = bpy.data.objects.new("_fh_mark", me)
        scene.collection.objects.link(mark)
        mark.location = Vector(row["clashAt"])
        mark.color = (1, 0.9, 0, 1)
        sh.show_xray, sh.xray_alpha = True, 0.55
        shot.centre = Vector(row["clashAt"])
        shot.cam.data.ortho_scale = size
        font = _font(max(14, px // 26))
        for view in ("背面", "左侧", "右侧", "顶"):
            shot.aim(VIEWS[view])
            im = shot.render(tmp)
            ImageDraw.Draw(im).text((6, 4), "%s号红 / %s号蓝  第%s帧  %s mm  %s" % (no, row["clashWith"], row["clashFrame"],
                                                                            row["clashMm"], view), fill=(0, 0, 0), font=font)
            cells.append(im)
    finally:
        sh.show_xray, sh.xray_alpha = xray
        for o, h in hidden.items():
            o.hide_render = h
        if mark is not None:
            me = mark.data
            bpy.data.objects.remove(mark, do_unlink=True)
            bpy.data.meshes.remove(me)
        if shot is not None:
            shot.close()
        model.shut_all(scene)
        restore()
        if os.path.exists(tmp):
            os.remove(tmp)
    out = Image.new("RGB", (px * len(cells), px), (255, 255, 255))
    for k, im in enumerate(cells):
        out.paste(im, (k * px, 0))
    out.save(path)
    return path


def summary_sheet(out, name):
    """The whole review pack in folder `out`: everything review() makes (back and side-back GIFs, numbered overview, table), the
    shoulder pieces and the back pieces each in a four-frame, three-view sheet (<name>_肩部.png, <name>_背部.png: the shoulder
    caps, inner shoulder layers and rings; the back blocks and plates; a part with no pieces is left out), and the checks as text
    (<name>_检查摘要.txt: report()). Returns the paths."""
    paths = review(out, name)
    rows, summary = check()

    def is_(no, kind):
        row, h = rows[no], model.by_no(no)
        region = row.get("region")
        if region is None:
            return (h.get("fh_part") == "shoulder") if kind == "shoulder" else h.get("fh_template") in ("back6", "top")
        return region.startswith("shoulder") if kind == "shoulder" else region == "back"
    for kind, label in (("shoulder", "肩部"), ("back", "背部")):
        nos = [no for no in rows if isinstance(no, int) and is_(no, kind)]
        if nos:
            paths.append(sheet(os.path.join(out, "%s_%s.png" % (name, label)), nos=nos, label=label))
    path = os.path.join(out, "%s_检查摘要.txt" % name)
    with open(path, "w", encoding="utf-8") as f:
        f.write(report((rows, summary)) + "\n")
    paths.append(path)
    return paths
