"""Renders a built suit from its <model>_rigged.blend with the given texture set, for judging the paint (wear.py) and the lining
by eye. Run in background Blender:
  blender -b <model>_rigged.blend --python preview_look.py -- <texture dir> <model> <out prefix> [samples]
The material is a principled shader fed like the game's gear shader reads the maps (metallic R, smoothness G/A). The light is the
game's: a dark room, a soft low ambient and the player's lamp from the camera. Environment: POSE an action to pose the suit at its last
frame (open: the suit fully open, as a pilot boarding from behind sees it), VIEWS a comma list of the views to render (default: all)."""
import math
import os
import sys

import bpy
from mathutils import Vector

tex_dir, model, out_prefix = sys.argv[sys.argv.index("--") + 1:][:3]
rest = sys.argv[sys.argv.index("--") + 4:]
samples = int(rest[0]) if rest else 64
res_x, res_y = 1000, 1300

def image(name, colour):
    path = os.path.join(tex_dir, name)
    img = bpy.data.images.load(path, check_existing=False)
    img.colorspace_settings.name = "sRGB" if colour else "Non-Color"
    return img


def material(name, prefix, emission=False, albedo=None):
    mat = bpy.data.materials.new(name)
    mat.use_nodes = True
    nt = mat.node_tree
    nt.nodes.clear()
    out = nt.nodes.new("ShaderNodeOutputMaterial")
    bsdf = nt.nodes.new("ShaderNodeBsdfPrincipled")
    nt.links.new(bsdf.outputs["BSDF"], out.inputs["Surface"])

    def tex(img):
        n = nt.nodes.new("ShaderNodeTexImage")
        n.image = img
        n.interpolation = "Linear"
        return n

    a = tex(image(albedo or prefix + "albedo.png", True))
    nt.links.new(a.outputs["Color"], bsdf.inputs["Base Color"])
    m = tex(image(prefix + "metallic.png", False))
    sep = nt.nodes.new("ShaderNodeSeparateColor")
    nt.links.new(m.outputs["Color"], sep.inputs["Color"])
    nt.links.new(sep.outputs["Red"], bsdf.inputs["Metallic"])
    inv = nt.nodes.new("ShaderNodeMath")
    inv.operation = "SUBTRACT"
    inv.inputs[0].default_value = 1.0
    nt.links.new(sep.outputs["Green"], inv.inputs[1])
    nt.links.new(inv.outputs["Value"], bsdf.inputs["Roughness"])
    n = tex(image(prefix + "normal.png", False))
    nm = nt.nodes.new("ShaderNodeNormalMap")
    nt.links.new(n.outputs["Color"], nm.inputs["Color"])
    nt.links.new(nm.outputs["Normal"], bsdf.inputs["Normal"])
    if emission:
        e = tex(image(prefix + "emission.png", True))
        nt.links.new(e.outputs["Color"], bsdf.inputs["Emission Color"])
        bsdf.inputs["Emission Strength"].default_value = 2.0
    return mat


body = max((o for o in bpy.data.objects if o.type == "MESH"), key=lambda o: len(o.data.polygons))
POSE = os.environ.get("POSE", "")
for o in bpy.data.objects:
    if o.type == "ARMATURE":
        if not o.animation_data:
            o.animation_data_create()
        o.animation_data.action = None
        if POSE and bpy.data.actions.get(POSE):
            o.animation_data.action = bpy.data.actions[POSE]
            bpy.context.scene.frame_set(int(bpy.data.actions[POSE].frame_range[1]))
armour = material("armour", model + "_", True)
lining = material("lining", "lining_", albedo=model + "_lining_albedo.png")
# the face slots stay as built (clearing the slots would fold every face into the armour): fill them in place, armour first
slots = [armour, lining] + [material("lining_" + k, "lining_%s_" % k) for k in ("metal", "rubber")]
for i, mat in enumerate(slots):
    if i < len(body.data.materials):
        body.data.materials[i] = mat
    else:
        body.data.materials.append(mat)
scene = bpy.context.scene
scene.render.engine = "CYCLES"
prefs = bpy.context.preferences.addons["cycles"].preferences
prefs.compute_device_type = "OPTIX"
prefs.get_devices()
for d in prefs.devices:
    d.use = d.type == "OPTIX"
scene.cycles.device = "GPU"
scene.cycles.samples = samples
scene.cycles.use_denoising = True
scene.render.resolution_x, scene.render.resolution_y = res_x, res_y
scene.render.image_settings.file_format = "PNG"
scene.view_settings.view_transform = "Standard"
world = bpy.data.worlds.new("w")
world.use_nodes = True
world.node_tree.nodes["Background"].inputs["Color"].default_value = (0.045, 0.05, 0.06, 1.0)
world.node_tree.nodes["Background"].inputs["Strength"].default_value = 0.35
scene.world = world
bpy.context.view_layer.update()
depsgraph = bpy.context.evaluated_depsgraph_get()
evaluated = body.evaluated_get(depsgraph)
bb = [evaluated.matrix_world @ Vector(c) for c in evaluated.bound_box]
lo = Vector((min(v.x for v in bb), min(v.y for v in bb), min(v.z for v in bb)))
hi = Vector((max(v.x for v in bb), max(v.y for v in bb), max(v.z for v in bb)))
mid = (lo + hi) / 2
height = hi.z - lo.z
floor = bpy.data.objects.new("floor", bpy.data.meshes.new("floor"))
floor.data.from_pydata([(-6, -6, lo.z), (6, -6, lo.z), (6, 6, lo.z), (-6, 6, lo.z)], [], [(0, 1, 2, 3)])
floor.data.materials.append(bpy.data.materials.new("floor"))
floor.data.materials[0].diffuse_color = (0.03, 0.03, 0.03, 1)
scene.collection.objects.link(floor)
# The player's lamp: a warm soft cone beside the camera; a dim cool room light from above.
lamp = bpy.data.objects.new("lamp", bpy.data.lights.new("lamp", "SPOT"))
lamp.data.spot_size = math.radians(75)
lamp.data.spot_blend = 0.8
lamp.data.shadow_soft_size = 0.12
lamp.data.color = (1.0, 0.92, 0.82)
scene.collection.objects.link(lamp)
room = bpy.data.objects.new("room", bpy.data.lights.new("room", "AREA"))
room.data.energy = 30
room.data.size = 5
room.data.color = (0.75, 0.85, 1.0)
room.location = mid + Vector((0, 0.5, 2.6))
scene.collection.objects.link(room)

cam = bpy.data.objects.new("cam", bpy.data.cameras.new("cam"))
cam.data.lens = 40
cam.data.clip_start = 0.05
scene.collection.objects.link(cam)
scene.camera = cam
# name: azimuth about the suit (0 front, 180 back), height share of the suit looked at, camera distance (m), camera lift (m)
views = {
    "front": (35, 0.52, 3.6, 0.1),
    "back": (215, 0.52, 3.6, 0.1),
    "detail": (60, 0.7, 1.5, 0.1),
    "in": (180, 0.55, 2.3, 0.0),
    "in_l": (150, 0.6, 2.0, 0.1),
    "in_r": (210, 0.6, 2.0, 0.1),
    "in_chest": (180, 0.68, 0.95, 0.0),
    "in_chest_l": (140, 0.68, 0.95, 0.05),
    "in_waist": (195, 0.45, 0.9, -0.05),
    "in_legs": (170, 0.28, 1.1, 0.05),
}
for name, (azimuth, share, dist, lift) in views.items():
    if os.environ.get("VIEWS") and name not in os.environ["VIEWS"].split(","):
        continue
    a = math.radians(azimuth)
    target = Vector((0, (lo.y + hi.y) * 0 , lo.z + height * share))
    cam.location = target + Vector((math.sin(a) * dist, -math.cos(a) * dist, lift))
    cam.rotation_euler = (target - cam.location).to_track_quat("-Z", "Y").to_euler()
    lamp.location = cam.location + Vector((0.12, 0, 0.18))
    lamp.data.energy = 40 * dist * dist
    lamp.rotation_euler = (target - lamp.location).to_track_quat("-Z", "Y").to_euler()
    scene.render.filepath = "%s_%s.png" % (out_prefix, name)
    bpy.ops.render.render(write_still=True)
print("PREVIEW DONE", out_prefix)
