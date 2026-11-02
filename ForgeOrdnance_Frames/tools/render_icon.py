# Renders one exosuit's lobby picker icon: a transparent 256 x 256 PNG of the rigged model in its rest pose, textured
# with its surface (albedo, metallic, normal, lights), three-quarter front view, orthographic. Run by tools/build_icons.ps1:
#   blender -b <Mxx_rigged.blend> --factory-startup --python-exit-code 1 --python render_icon.py -- --textures <dir> --model Mxx --out <png>
import argparse
import os
import sys

import bpy
from mathutils import Matrix, Vector

args = argparse.ArgumentParser()
args.add_argument("--textures", required=True)
args.add_argument("--model", required=True)
args.add_argument("--out", required=True)
args.add_argument("--size", type=int, default=256)
a = args.parse_args(sys.argv[sys.argv.index("--") + 1:])
LIGHT = (0.8, 0.8, 0.8)  # the bundle's gain on the lights, which emission.png holds in grey (the light strips white)
ICON_SPAN = 3.2  # metres the square icon shows, the same for every suit

scene = bpy.context.scene
for obj in list(scene.objects):
    if obj.type in {"CAMERA", "LIGHT"}:
        bpy.data.objects.remove(obj, do_unlink=True)
mesh = next(o for o in scene.objects if o.type == "MESH")
for rig in (o for o in scene.objects if o.type == "ARMATURE"):
    if rig.animation_data is not None:
        for track in rig.animation_data.nla_tracks:
            track.mute = True
        rig.animation_data.action = None
    # The shut suit in its rest pose, as it stands empty in the game.
    for bone in rig.pose.bones:
        bone.matrix_basis = Matrix.Identity(4)
scene.frame_set(1)

# The surface, the way the game's gear shader reads it: R metal, G smoothness, the lights times the gain.
material = bpy.data.materials.new("icon")
material.use_nodes = True
nt = material.node_tree
bsdf = next(n for n in nt.nodes if n.type == "BSDF_PRINCIPLED")


def texture(kind, colourspace):
    path = os.path.join(a.textures, a.model + "_" + kind + ".png")
    if not os.path.isfile(path):
        sys.exit("render_icon: missing " + path)
    node = nt.nodes.new("ShaderNodeTexImage")
    node.image = bpy.data.images.load(path)
    node.image.colorspace_settings.name = colourspace
    return node


nt.links.new(texture("albedo", "sRGB").outputs[0], bsdf.inputs["Base Color"])
split = nt.nodes.new("ShaderNodeSeparateColor")
nt.links.new(texture("metallic", "Non-Color").outputs[0], split.inputs[0])
nt.links.new(split.outputs[0], bsdf.inputs["Metallic"])
rough = nt.nodes.new("ShaderNodeMath")
rough.operation = "SUBTRACT"
rough.inputs[0].default_value = 1.0
nt.links.new(split.outputs[1], rough.inputs[1])
nt.links.new(rough.outputs[0], bsdf.inputs["Roughness"])
normal = nt.nodes.new("ShaderNodeNormalMap")
nt.links.new(texture("normal", "Non-Color").outputs[0], normal.inputs["Color"])
nt.links.new(normal.outputs[0], bsdf.inputs["Normal"])
glow = nt.nodes.new("ShaderNodeMix")
glow.data_type = "RGBA"
glow.blend_type = "MULTIPLY"
glow.inputs[0].default_value = 1.0
nt.links.new(texture("emission", "sRGB").outputs[0], glow.inputs[6])
glow.inputs[7].default_value = (*LIGHT, 1)
nt.links.new(glow.outputs[2], bsdf.inputs["Emission Color"])
bsdf.inputs["Emission Strength"].default_value = 2.0
mesh.data.materials.clear()
mesh.data.materials.append(material)

bpy.context.view_layer.update()
evaluated = mesh.evaluated_get(bpy.context.evaluated_depsgraph_get())
points = [mesh.matrix_world @ v.co for v in evaluated.data.vertices]
lo = Vector((min(p.x for p in points), min(p.y for p in points), min(p.z for p in points)))
hi = Vector((max(p.x for p in points), max(p.y for p in points), max(p.z for p in points)))
centre = (lo + hi) / 2
span = (hi - lo).length

# Front is -Y: a three-quarter view from the front right, a little above.
view = Vector((0.55, -1.0, 0.35)).normalized()
camera = bpy.data.objects.new("IconCamera", bpy.data.cameras.new("IconCamera"))
scene.collection.objects.link(camera)
camera.data.type = "ORTHO"
camera.location = centre + view * (span * 10 + 10)
camera.rotation_euler = (-view).to_track_quat("-Z", "Y").to_euler()
camera.data.clip_end = span * 40 + 40
scene.camera = camera
bpy.context.view_layer.update()
right = camera.matrix_world.to_3x3() @ Vector((1, 0, 0))
up = camera.matrix_world.to_3x3() @ Vector((0, 1, 0))
# Centre the suit's outline as the camera sees it (the 3D box centre is off-centre in a three-quarter view).
xs = [(p - centre).dot(right) for p in points]
ys = [(p - centre).dot(up) for p in points]
camera.location += right * (min(xs) + max(xs)) / 2 + up * (min(ys) + max(ys)) / 2
extent = max(max(xs) - min(xs), max(ys) - min(ys)) / 2
# One camera for all four suits, so their sizes compare: the largest (the tank) keeps an 8% margin on every side.
camera.data.ortho_scale = ICON_SPAN
if extent * 2 > ICON_SPAN * (1 - 2 * 0.08):
    sys.exit("render_icon: %s spans %.2f m in view, more than the icon's %.2f m less its margins" % (a.model, extent * 2, ICON_SPAN))
print("ICON_EXTENT %s %.3f m of %.3f" % (a.model, extent * 2, ICON_SPAN))


def light(name, energy, direction):
    data = bpy.data.lights.new(name, "SUN")
    data.energy = energy
    obj = bpy.data.objects.new(name, data)
    scene.collection.objects.link(obj)
    obj.rotation_euler = (-Vector(direction)).to_track_quat("-Z", "Y").to_euler()


light("Key", 4.0, (0.6, -0.8, 0.9))
light("Fill", 1.2, (-0.9, -0.4, 0.3))
light("Rim", 3.0, (-0.3, 0.9, 0.6))
world = scene.world or bpy.data.worlds.new("World")
scene.world = world
world.use_nodes = True
background = next(n for n in world.node_tree.nodes if n.type == "BACKGROUND")
background.inputs["Color"].default_value = (0.05, 0.05, 0.06, 1)
background.inputs["Strength"].default_value = 0.6

for engine in ("BLENDER_EEVEE", "BLENDER_EEVEE_NEXT"):
    try:
        scene.render.engine = engine
        break
    except TypeError:
        continue
scene.render.film_transparent = True
scene.render.resolution_x = scene.render.resolution_y = a.size
scene.render.resolution_percentage = 100
scene.render.image_settings.file_format = "PNG"
scene.render.image_settings.color_mode = "RGBA"
scene.render.filepath = a.out
bpy.ops.render.render(write_still=True)
print("ICON_RENDERED " + a.out)
