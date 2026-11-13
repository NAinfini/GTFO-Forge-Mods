"""Renders one built exosuit opening for boarding, one picture per view (Blender 5.2, background):

  blender --background --factory-startup --disable-autoexec <model>_rigged.blend --python preview_opening.py -- \
      <model>_rig.json <out folder>

writes <model>_<state>_<view>.png: the whole suit shut, half open and open from behind and from behind and to the side, and
the open suit close up from the same two sides on the shoulders and neck, the left arm and the legs; and <model>_frames.png,
the opening frame by frame (FRAMES pictures from behind and to the side, in reading order).

Each pose is the rig's clip "open" at that point, the clip the game plays (FrameVisual samples it forwards to open and
backwards to close). The script checks the parts stay rigid: within every part the distances between its vertices stay what
they were in every pose (the largest change is printed).
"""
import json
import math
import os
import sys

import bpy
import numpy as np
from mathutils import Matrix, Vector

HALF = 0.4     # the opening's half-way picture
FRAMES = 12    # pictures in the frame-by-frame sheet
COLUMNS = 6

argv = sys.argv[sys.argv.index("--") + 1:]
rig = json.load(open(argv[0], encoding="utf-8"))
out = argv[1]
os.makedirs(out, exist_ok=True)
model = rig["model"]

body = next(o for o in bpy.data.objects if o.type == "MESH")
armature = next(o for o in bpy.data.objects if o.type == "ARMATURE")
armature.hide_render = True
for track in armature.animation_data.nla_tracks:
    track.mute = True
# The rest of the body at its bind pose (the clip moves the parts only).
for pb in armature.pose.bones:
    pb.matrix_basis = Matrix.Identity(4)
armature.animation_data.action = bpy.data.actions["open"]
first, last = rig["clips"]["open"]
me = body.data
group = {g.index: g.name for g in body.vertex_groups}
names = [p["node"] for p in rig["plates"]]
owner = np.full(len(me.vertices), -1)
for v in me.vertices:
    for g in v.groups:
        n = group.get(g.group)
        if n in names and g.weight >= 0.5:
            owner[v.index] = names.index(n)


def posed(open_):
    """The suit's vertices at `open_` (0..1) of the "open" clip."""
    bpy.context.scene.frame_set(round(first + open_ * (last - first)))
    ev = body.evaluated_get(bpy.context.evaluated_depsgraph_get())
    m = ev.to_mesh()
    co = np.empty(len(m.vertices) * 3)
    m.vertices.foreach_get("co", co)
    ev.to_mesh_clear()
    return co.reshape(-1, 3)


rest = posed(0.0)
# Rigid: within each part, the distances from its first vertex to every other stay what they were.
worst = 0.0
for open_ in [k / (last - first) for k in range(last - first + 1)]:
    co = posed(open_)
    for i in range(len(names)):
        idx = np.where(owner == i)[0]
        if len(idx) < 2:
            continue
        d0 = np.linalg.norm(rest[idx] - rest[idx[0]], axis=1)
        d1 = np.linalg.norm(co[idx] - co[idx[0]], axis=1)
        worst = max(worst, float(np.abs(d1 - d0).max()))
if worst > 1e-4:
    raise SystemExit("a part changes shape while opening: %.5f m" % worst)

scene = bpy.context.scene
try:
    scene.render.engine = "BLENDER_EEVEE_NEXT"
except TypeError:
    scene.render.engine = "BLENDER_EEVEE"
scene.render.resolution_x, scene.render.resolution_y = 900, 1100
world = bpy.data.worlds.new("w")
world.use_nodes = True
bg = next(n for n in world.node_tree.nodes if n.type == "BACKGROUND")
bg.inputs[0].default_value = (0.18, 0.19, 0.21, 1)
bg.inputs[1].default_value = 1.6
scene.world = world
for rot, energy in (((math.radians(50), 0, math.radians(150)), 4.0), ((math.radians(60), 0, math.radians(-40)), 2.5),
                    ((math.radians(70), 0, math.radians(20)), 2.0)):
    light = bpy.data.objects.new("sun", bpy.data.lights.new("sun", "SUN"))
    light.data.energy = energy
    light.rotation_euler = rot
    scene.collection.objects.link(light)
H = rig["height"]
J = {k: Vector(v) for k, v in rig["joints"].items()}
cam = bpy.data.objects.new("cam", bpy.data.cameras.new("cam"))
scene.collection.objects.link(cam)
scene.camera = cam

BACK, SIDEBACK = Vector((0, 1, 0.12)).normalized(), Vector((0.75, 0.75, 0.15)).normalized()
whole = Vector((0, 0, H * 0.48))
views = [("shut", 0.0, "back", whole, BACK, 3.4), ("shut", 0.0, "sideback", whole, SIDEBACK, 3.4),
         ("half", HALF, "back", whole, BACK, 3.4), ("half", HALF, "sideback", whole, SIDEBACK, 3.4),
         ("open", 1.0, "back", whole, BACK, 3.4), ("open", 1.0, "sideback", whole, SIDEBACK, 3.4)]
upper = (J["neck"] + J["chest"]) / 2
arm = (J["upperarm.L"] + J["hand.L"]) / 2
legs = (J["thigh.L"] + J["thigh.R"] + J["foot.L"] + J["foot.R"]) / 4
for name, target, size in (("shoulders", upper, 1.5), ("arm", arm, 1.3), ("legs", legs, 2.1)):
    views += [("open", 1.0, name + "-back", target, BACK, size), ("open", 1.0, name + "-sideback", target, SIDEBACK, size)]

def shoot(open_, target, direction, distance, path):
    bpy.context.scene.frame_set(round(first + open_ * (last - first)))
    cam.data.lens = 40
    cam.location = target + direction * distance
    cam.rotation_euler = (target - cam.location).to_track_quat("-Z", "Y").to_euler()
    scene.render.filepath = path
    bpy.ops.render.render(write_still=True)


for state, open_, view, target, direction, distance in views:
    shoot(open_, target, direction, distance, os.path.join(out, "%s_%s_%s.png" % (model, state, view)))

# The opening frame by frame, one sheet: smaller pictures from behind and to the side, left to right, top to bottom.
scene.render.resolution_x, scene.render.resolution_y = 360, 440
tiles = []
for k in range(FRAMES):
    path = os.path.join(out, "_frame%02d.png" % k)
    shoot(k / (FRAMES - 1), whole, SIDEBACK, 3.4, path)
    im = bpy.data.images.load(path)
    px = np.array(im.pixels[:], dtype=np.float32).reshape(im.size[1], im.size[0], 4)
    tiles.append(px)
    bpy.data.images.remove(im)
    os.remove(path)
rows = (FRAMES + COLUMNS - 1) // COLUMNS
h, w = tiles[0].shape[:2]
sheet = np.ones((rows * h, COLUMNS * w, 4), dtype=np.float32)
for k, px in enumerate(tiles):
    r, c = divmod(k, COLUMNS)
    # Image pixels run bottom to top: the first row of pictures goes at the top.
    sheet[(rows - 1 - r) * h:(rows - r) * h, c * w:(c + 1) * w] = px
im = bpy.data.images.new("frames", COLUMNS * w, rows * h, alpha=True)
im.pixels.foreach_set(sheet.reshape(-1))
im.filepath_raw = os.path.join(out, "%s_frames.png" % model)
im.file_format = "PNG"
im.save()
print("PREVIEW", model, "parts", len(names), "rigid within %.6f m" % worst, "pictures", len(views), "frames", FRAMES)
