"""Viewport drawing (gpu): lines, tinted faces and arrows, from plain lists the tools fill in. One handler for the add-on."""
import bpy
import gpu
from gpu_extras.batch import batch_for_shader
from mathutils import Vector

_handle = None
# What to draw now: name -> list of (kind, data, colour); kinds: "lines" (pairs of points), "tris" (triples of points),
# "points".
layers = {}


def set_layer(name, items):
    layers[name] = items
    redraw()


def clear(name=None):
    if name is None:
        layers.clear()
    else:
        layers.pop(name, None)
    redraw()


def redraw():
    for w in bpy.context.window_manager.windows:
        for a in w.screen.areas:
            if a.type == "VIEW_3D":
                a.tag_redraw()


def _draw():
    shader = gpu.shader.from_builtin("UNIFORM_COLOR")
    gpu.state.blend_set("ALPHA")
    gpu.state.depth_test_set("LESS_EQUAL")
    for items in list(layers.values()):
        for kind, data, colour, *rest in items:
            if not data:
                continue
            on_top = rest and rest[0]
            gpu.state.depth_test_set("NONE" if on_top else "LESS_EQUAL")
            if kind == "lines":
                gpu.state.line_width_set(rest[1] if len(rest) > 1 else 3.0)
                batch = batch_for_shader(shader, "LINES", {"pos": data})
            elif kind == "tris":
                batch = batch_for_shader(shader, "TRIS", {"pos": data})
            else:
                gpu.state.point_size_set(8.0)
                batch = batch_for_shader(shader, "POINTS", {"pos": data})
            shader.uniform_float("color", colour)
            batch.draw(shader)
    gpu.state.line_width_set(1.0)
    gpu.state.depth_test_set("NONE")
    gpu.state.blend_set("NONE")


def register():
    global _handle
    if _handle is None:
        _handle = bpy.types.SpaceView3D.draw_handler_add(_draw, (), "WINDOW", "POST_VIEW")


def unregister():
    global _handle
    if _handle is not None:
        bpy.types.SpaceView3D.draw_handler_remove(_handle, "WINDOW")
        _handle = None
    layers.clear()


# ------------------------------------------------------------------------------------------------------------- helpers
def polyline(points):
    out = []
    for a, b in zip(points, points[1:]):
        out += [tuple(a), tuple(b)]
    return out


def face_tris(obj, faces, lift=0.0015):
    """World triangles of bmesh faces of obj (pushed a little along their normals so they show over the surface)."""
    m = obj.matrix_world
    nm = m.to_3x3()
    out = []
    for f in faces:
        n = (nm @ f.normal).normalized() * lift
        vs = [m @ v.co + n for v in f.verts]
        for k in range(1, len(vs) - 1):
            out += [tuple(vs[0]), tuple(vs[k]), tuple(vs[k + 1])]
    return out


def mesh_tris(obj, lift=0.0015):
    m = obj.matrix_world
    nm = m.to_3x3()
    me = obj.data
    out = []
    for p in me.polygons:
        n = (nm @ p.normal).normalized() * lift
        vs = [m @ me.vertices[i].co + n for i in p.vertices]
        for k in range(1, len(vs) - 1):
            out += [tuple(vs[0]), tuple(vs[k]), tuple(vs[k + 1])]
    return out


def arrow(start, direction, length=0.25, head=0.05):
    """Line segments of an arrow from `start` along `direction`."""
    d = direction.normalized()
    end = start + d * length
    side = d.orthogonal().normalized()
    other = d.cross(side).normalized()
    out = [tuple(start), tuple(end)]
    for s in (side, -side, other, -other):
        out += [tuple(end), tuple(end - d * head + s * head * 0.5)]
    return out


def capsule(a, b, r, n=12):
    """Wire lines of a capsule (two rings and four lines) from a to b, radius r."""
    import math
    d = (b - a)
    if d.length < 1e-6:
        return []
    ax = d.normalized()
    u = ax.orthogonal().normalized()
    v = ax.cross(u)
    out = []
    for c in (a, b, (a + b) / 2):
        ring = [c + (u * math.cos(2 * math.pi * k / n) + v * math.sin(2 * math.pi * k / n)) * r for k in range(n + 1)]
        out += polyline(ring)
    for k in range(4):
        w = u * math.cos(math.pi * k / 2) + v * math.sin(math.pi * k / 2)
        out += [tuple(a + w * r), tuple(b + w * r)]
    return out


def to_vec(p):
    return Vector(p)
