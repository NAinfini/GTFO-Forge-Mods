"""What the owner set, as plain custom properties in the .blend (read by the add-on, the bake and the checks; nothing guessed):

  hinge   an empty; its world Z axis is the hinge line through its origin.
          fh_hinge = 1, fh_angle (degrees, signed: the sign is the way it turns about +Z), fh_layer ("outer"/"inner"),
          fh_batch (int), fh_row ("", "top", "mid", "low"), fh_template (the preset it was cut with, or ""),
          fh_part ("shoulder": a shoulder cap, not a limb piece; "underarm": a piece under the arm, opening down and
          forward; "back" or a limb with its side, "forearm.R": the body part it sits on, instead of judging it from its
          main body), fh_dir_want (JSON {"words", "vec": unit world vector}: the way this piece should open, declared by
          the owner, over its body part's rule), fh_no (the piece number the owner refers to), fh_lines (JSON:
          {"cut": the cut lines as clicked, [[x, y, z], ...] each, "hinge": the two ends of the hinge axis along the piece},
          world space).
          The move: without fh_path the piece turns fh_angle about the hinge line. fh_path (JSON: motion.py's segments, world,
          as the piece stands shut) replaces that; fh_move (JSON {"kind": the preset, its settings}) is how it was made.
          fh_parent (the name of the hinge it hangs on: it moves with that piece, a fold's leaf), fh_delay (frames added to its
          start), fh_length (frames its move takes; 0: its layer's), fh_rests (JSON list of the hinges whose pieces it rests
          on when open).
  piece   a mesh object that opens: fh_hinge = the name of its hinge empty (several pieces on one hinge move together),
          fh_source = the object it was cut from, fh_allow = JSON list of checks the owner allowed for it, fh_base = its shut
          transform.
  link    a mesh object the add-on made (a bar of a parallel-link swing or a scissor): fh_link = the name of the hinge whose
          pieces it carries, fh_link_spec = JSON motion.link_pose's spec.
  still   a mesh object that must never be cut or opened: fh_still = 1 (feet, helmet).
Everything else stays fixed.

A piece's lining (faces with the material LINING: the inside a plate is thickened with, grown inward from its own faces, which
keep their place and texture) is not the piece's surface: the checks, the presets and the main body read a piece by own()
(every face but the lining's, and every vertex but those only the lining uses), so a lining of any thickness never moves
their sample points or readings. checks.through measures the lining on its own (lining())."""
import json
import math

import bmesh
import bpy
import numpy as np
from mathutils import Matrix, Vector

from . import body, motion

COLL = "开合铰链"
LINING = "frame-lining"


def lining_slots(o):
    return {i for i, s in enumerate(o.material_slots)
            if s.material is not None and (s.material.name == LINING or s.material.name.startswith(LINING + "."))}


def own(o):
    """(vertex indices, polygons) of a mesh's own surface: every polygon but its lining's, every vertex but those only the
    lining uses (all of them, in order, without a lining)."""
    me = o.data
    slots = lining_slots(o)
    if not slots:
        return list(range(len(me.vertices))), list(me.polygons)
    polys = [p for p in me.polygons if p.material_index not in slots]
    used = {v for p in polys for v in p.vertices}
    only = {v for p in me.polygons if p.material_index in slots for v in p.vertices} - used
    return [i for i in range(len(me.vertices)) if i not in only], polys


def lining(o):
    """The vertex indices of a mesh's lining that only the lining uses (its inner skin; the rim joining it to the plate's
    own vertices uses those too). Empty without a lining."""
    me = o.data
    slots = lining_slots(o)
    if not slots:
        return []
    used = {v for p in me.polygons if p.material_index not in slots for v in p.vertices}
    return sorted({v for p in me.polygons if p.material_index in slots for v in p.vertices} - used)


def hinges(scene=None):
    scene = scene or bpy.context.scene
    return [o for o in scene.objects if o.type == "EMPTY" and o.get("fh_hinge")]


def pieces_of(hinge, scene=None):
    scene = scene or bpy.context.scene
    return [o for o in scene.objects if o.type == "MESH" and o.get("fh_hinge") == hinge.name]


def pieces(scene=None):
    scene = scene or bpy.context.scene
    return [o for o in scene.objects if o.type == "MESH" and o.get("fh_hinge")]


def links_of(hinge, scene=None):
    scene = scene or bpy.context.scene
    return [o for o in scene.objects if o.type == "MESH" and o.get("fh_link") == hinge.name]


def links(scene=None):
    scene = scene or bpy.context.scene
    return [o for o in scene.objects if o.type == "MESH" and o.get("fh_link")]


def link_spec(link):
    return json.loads(link["fh_link_spec"])


def hinge_of(obj):
    if obj is None:
        return None
    if obj.type == "EMPTY" and obj.get("fh_hinge"):
        return obj
    name = (obj.get("fh_hinge") or obj.get("fh_link")) if obj.type == "MESH" else None
    return bpy.data.objects.get(name) if name else None


def line(hinge):
    """(point, unit axis) of a hinge, in world space."""
    m = hinge.matrix_world
    return m.translation.copy(), (m.to_3x3() @ Vector((0, 0, 1))).normalized()


def path(hinge):
    """The piece's own move (motion.Path), as it stands shut."""
    if hinge.get("fh_path"):
        return motion.Path(json.loads(hinge["fh_path"]))
    p, a = line(hinge)
    return motion.Path.hinge(p, a, hinge["fh_angle"])


def set_path(hinge, segments, move=None):
    """Give the piece a path (motion segments); None goes back to turning on its hinge."""
    if segments is None:
        for k in ("fh_path", "fh_move"):
            if k in hinge:
                del hinge[k]
        return
    hinge["fh_path"] = json.dumps(segments)
    hinge["fh_move"] = json.dumps(move or {"kind": "path"}, ensure_ascii=False)


def move_of(hinge):
    try:
        return json.loads(hinge.get("fh_move", "")) if hinge.get("fh_move") else {"kind": "hinge"}
    except ValueError:
        return {"kind": "path"}


def turns_on_hinge(hinge):
    """A plain hinged piece (one turn about its hinge line), as every piece was before paths."""
    return not hinge.get("fh_path")


def angle_of(hinge):
    """The turn the piece really makes (degrees, signed): fh_angle on its hinge, or on a path its biggest turn (or link
    swing) that stays (not a flare's out-and-back); None for a path without one."""
    if turns_on_hinge(hinge):
        return float(hinge["fh_angle"])
    turns = [float(s.get("turn", s.get("arc"))) for s in path(hinge).segments
             if motion.kind(s) in ("turn", "arc") and not s.get("back")]
    return max(turns, key=abs) if turns else None


def turn_line(hinge):
    """(point, unit axis) of the line the piece really turns about (angle_of's turn), where it lies on the piece shut (a
    turn after a slide or another turn is taken back through them); None for a path without one."""
    if turns_on_hinge(hinge):
        return line(hinge)
    segs = sorted(path(hinge).segments, key=lambda s: s["at"][0])
    stay = [s for s in segs if motion.kind(s) == "turn" and not s.get("back")]
    if not stay:
        return None
    seg = max(stay, key=lambda s: abs(s["turn"]))
    before = Matrix.Identity(4)
    for s in segs:
        if s is seg:
            break
        if not s.get("back"):
            before = motion.seg_pose(s, 1.0) @ before
    back = before.inverted()
    return back @ Vector(seg["point"]), (back.to_3x3() @ Vector(seg["axis"])).normalized()


def expected_dir(hinge):
    """(words, unit world vector) of the way the owner declared this piece should open (fh_dir_want), or None."""
    try:
        d = json.loads(hinge.get("fh_dir_want", "")) if hinge.get("fh_dir_want") else None
    except ValueError:
        return None
    return (d["words"], d["vec"]) if d else None


def parent(hinge):
    name = hinge.get("fh_parent", "")
    return bpy.data.objects.get(name) if name else None


def chain(hinge):
    """The hinge and those it hangs on, the hinge first."""
    out, h = [], hinge
    while h is not None and h not in out:
        out.append(h)
        h = parent(h)
    return out


def turn(hinge, fraction=1.0):
    """The piece's world move `fraction` (0..1) of the way along its path (no stroke), carried by the pieces it hangs on."""
    m = Matrix.Identity(4)
    for h in chain(hinge):
        m = path(h).at(fraction) @ m
    return m


def make_hinge(centre, axis, angle, layer, batch=0, row="", template="", name="铰链"):
    coll = bpy.data.collections.get(COLL)
    if coll is None:
        coll = bpy.data.collections.new(COLL)
        bpy.context.scene.collection.children.link(coll)
    e = bpy.data.objects.new(name, None)
    e.empty_display_type = "SINGLE_ARROW"
    e.empty_display_size = 0.08
    z = Vector((0, 0, 1))
    e.matrix_world = Matrix.Translation(centre) @ z.rotation_difference(axis).to_matrix().to_4x4()
    e["fh_hinge"] = 1
    e["fh_angle"] = float(angle)
    e["fh_layer"] = layer
    e["fh_batch"] = int(batch)
    e["fh_row"] = row
    e["fh_template"] = template
    e["fh_no"] = max((int(h.get("fh_no", 0)) for h in hinges()), default=0) + 1
    coll.objects.link(e)
    return e


def by_no(no):
    return next((h for h in hinges() if int(h.get("fh_no", 0)) == int(no)), None)


def set_lines(hinge, cut, hinge_line):
    hinge["fh_lines"] = json.dumps({"cut": [[[round(c, 5) for c in p] for p in l] for l in cut],
                                    "hinge": [[round(c, 5) for c in p] for p in hinge_line]})


def lines_of(hinge):
    try:
        d = json.loads(hinge.get("fh_lines", "{}"))
    except ValueError:
        d = {}
    return [[Vector(p) for p in l] for l in d.get("cut", [])], [Vector(p) for p in d.get("hinge", [])]


def attach(piece, hinge, source=""):
    piece["fh_hinge"] = hinge.name
    if source:
        piece["fh_source"] = source


def is_right(hinge):
    ps = pieces_of(hinge)
    if not ps:
        return hinge.matrix_world.translation.x < 0
    xs = []
    for o in ps:
        keep = own(o)[0]
        xs += [(o.matrix_world @ o.data.vertices[i].co).x for i in keep[::max(1, len(keep) // 50)]]
    return sum(xs) / max(1, len(xs)) < 0


def frames(hinge):
    return motion.frames_of(hinge.get("fh_layer", "outer"), int(hinge.get("fh_batch", 0)), hinge.get("fh_row", ""),
                            is_right(hinge), int(hinge.get("fh_delay", 0)), int(hinge.get("fh_length", 0)))


def parts(scene=None):
    """The scene's moving things for motion.sequence: (hinges with pieces, their links, the parts list)."""
    scene = scene or bpy.context.scene
    hs = [h for h in hinges(scene) if pieces_of(h, scene)]
    index = {h.name: i for i, h in enumerate(hs)}
    out = [{"path": path(h), "frames": frames(h), "parent": index.get(h.get("fh_parent", ""))} for h in hs]
    ls = [o for o in links(scene) if o["fh_link"] in index]
    out += [{"link": link_spec(o), "driver": index[o["fh_link"]]} for o in ls]
    return hs, ls, out


def allowed(piece):
    try:
        return set(json.loads(piece.get("fh_allow", "[]")))
    except ValueError:
        return set()


def rests_on(hinge):
    """The names of the hinges whose pieces this piece is said to rest on when open (fh_rests; the float check measures to
    them too)."""
    try:
        return list(json.loads(hinge.get("fh_rests", "[]")))
    except ValueError:
        return []


def set_allowed(piece, items):
    piece["fh_allow"] = json.dumps(sorted(items), ensure_ascii=False)


def shut_all(scene=None):
    """Every piece and link back in its place, animation cleared."""
    for o in pieces(scene) + links(scene):
        o.animation_data_clear()
        base = o.get("fh_base")
        o.matrix_world = Matrix(json.loads(base)) if base else o.matrix_world


def remember_base(obj):
    """Store a piece's shut transform so previews can always put it back."""
    obj["fh_base"] = json.dumps([list(r) for r in obj.matrix_world])


def base(obj):
    b = obj.get("fh_base")
    return Matrix(json.loads(b)) if b else obj.matrix_world.copy()


_MAIN = {}


def main_island(hinge, scene=None):
    """(points, rim points) of the piece's main body, world, shut: the connected part with the most area over all its
    objects, and the vertices on its open border (edges with one face). Trims, rivets and stray islands riding on the piece
    are left out, so they neither decide the body part it sits on nor its edges. Its own surface only (own()): a lining
    neither joins it nor closes its outline."""
    ps = pieces_of(hinge, scene)
    key = (hinge.name,) + tuple((o.name, len(o.data.vertices), len(o.data.polygons), o.get("fh_base", "")) for o in ps)
    if key in _MAIN:
        return _MAIN[key]
    best = None
    for o in ps:
        bm = bmesh.new()
        bm.from_mesh(o.data)
        slots = lining_slots(o)
        seen = {f for f in bm.faces if f.material_index in slots}

        def faces_of(e):
            return [k for k in e.link_faces if k.material_index not in slots]
        for f in bm.faces:
            if f in seen:
                continue
            seen.add(f)
            stack, comp = [f], []
            while stack:
                g = stack.pop()
                comp.append(g)
                for e in g.edges:
                    for k in faces_of(e):
                        if k not in seen:
                            seen.add(k)
                            stack.append(k)
            area = sum(g.calc_area() for g in comp)
            if best is None or area > best[0]:
                m = base(o)
                vs = {v for g in comp for v in g.verts}
                rim = {v for g in comp for e in g.edges if len(faces_of(e)) == 1 for v in e.verts}
                best = (area, np.array([tuple(m @ v.co) for v in vs]).reshape(-1, 3),
                        np.array([tuple(m @ v.co) for v in rim]).reshape(-1, 3))
        bm.free()
    out = (best[1], best[2]) if best else (np.zeros((0, 3)), np.zeros((0, 3)))
    if len(_MAIN) > 512:
        _MAIN.clear()
    _MAIN[key] = out
    return out


def body_part(hinge, j, scene=None):
    """(region, side, over the shoulders) of the body part a piece sits on: fh_part when it names one ("back", or a limb
    with its side, "forearm.R"), else judged from the piece's main body (main_island), not from every point it carries."""
    part = hinge.get("fh_part", "")
    pts = main_island(hinge, scene)[0]
    if part == "back":
        c = pts.mean(axis=0)
        return "back", ("L" if c[0] > 0.03 else "R" if c[0] < -0.03 else "C"), c[2] > j["neck"][2] + 0.05
    if "." in part:
        reg, side = part.split(".", 1)
        return reg, side, False
    return body.region(j, pts)


def degrees_between(a, b):
    return math.degrees(a.angle(b, 0))
