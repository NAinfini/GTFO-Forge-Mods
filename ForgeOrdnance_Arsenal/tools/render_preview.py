"""Renders review pictures of a heavy weapon master .blend (front -Y, up +Z, grip at the origin) with Workbench: the left side, the top, the
rear, the first person hip view and the aim view through the sight (the camera sits 0.3 m behind the `SightLook` empty, as the game's aim camera does).
The master is only read.

blender -b --factory-startup <gun.blend> --python render_preview.py -- --out <folder> --tag M08 [--size 1000]

Writes <folder>/<tag>-side.png, -top.png, -rear.png, -hip.png and -ads.png. Hands (`RightHand`, `LeftHand`) and the magazine are shown too:
the empties are drawn as large coloured spheres by the extra objects this script adds and removes again in memory only.
"""
import argparse, math, os, sys
import bpy
from mathutils import Vector

argv = sys.argv[sys.argv.index("--") + 1:]
p = argparse.ArgumentParser()
p.add_argument("--out", required=True)
p.add_argument("--tag", required=True)
p.add_argument("--size", type=int, default=1000)
a = p.parse_args(argv)
os.makedirs(a.out, exist_ok=True)
scene = bpy.context.scene
scene.render.engine = "BLENDER_WORKBENCH"
scene.render.resolution_x = scene.render.resolution_y = a.size
scene.render.film_transparent = False
shading = scene.display.shading
shading.light = "STUDIO"; shading.color_type = "TEXTURE"; shading.show_cavity = False; shading.show_object_outline = False
world = bpy.data.worlds.new("w"); world.color = (0.62, 0.64, 0.68); scene.world = world

markers = {"RightHand": (1, 0.1, 0.1, 1), "LeftHand": (0.1, 0.4, 1, 1), "SightLook": (0.1, 1, 0.2, 1), "muzzle": (1, 0.9, 0.1, 1)}
spheres = []
for name, colour in markers.items():
    e = bpy.data.objects.get(name)
    if e is None: continue
    me = bpy.data.meshes.new("m_" + name)
    import bmesh
    bm = bmesh.new(); bmesh.ops.create_uvsphere(bm, u_segments=12, v_segments=8, radius=0.012); bm.to_mesh(me); bm.free()
    mat = bpy.data.materials.new("m_" + name); mat.use_nodes = False; mat.diffuse_color = colour
    me.materials.append(mat)
    o = bpy.data.objects.new("m_" + name, me); o.location = e.matrix_world.translation
    scene.collection.objects.link(o); spheres.append(o)

body = [o for o in scene.objects if o.type == "MESH" and not o.name.startswith("m_")]
pts = [o.matrix_world @ Vector(c) for o in body for c in o.bound_box]
lo = Vector((min(q.x for q in pts), min(q.y for q in pts), min(q.z for q in pts)))
hi = Vector((max(q.x for q in pts), max(q.y for q in pts), max(q.z for q in pts)))
centre = (lo + hi) / 2
span = max(hi.x - lo.x, hi.y - lo.y, hi.z - lo.z)

def shoot(name, location, look_at, ortho=None, lens=None, up=(0, 0, 1)):
    cam = bpy.data.cameras.new(name); obj = bpy.data.objects.new(name, cam); scene.collection.objects.link(obj)
    if ortho: cam.type = "ORTHO"; cam.ortho_scale = ortho
    else: cam.lens = lens or 50; cam.sensor_width = 36; cam.clip_start = 0.01; cam.clip_end = 20
    obj.location = location
    direction = Vector(look_at) - Vector(location)
    obj.rotation_euler = direction.to_track_quat("-Z", "Y").to_matrix().to_4x4().to_euler()
    if up != (0, 0, 1):
        q = direction.to_track_quat("-Z", "Y"); obj.rotation_euler = q.to_euler()
    scene.camera = obj
    scene.render.filepath = os.path.join(a.out, "%s-%s.png" % (a.tag, name))
    bpy.ops.render.render(write_still=True)
    bpy.data.objects.remove(obj)

d = span * 3
shoot("side", centre + Vector((d, 0, 0)), centre, ortho=span * 1.25)
shoot("top", centre + Vector((0, 0, d)), centre, ortho=span * 1.25)
shoot("rear", centre + Vector((0, d, 0)), centre, ortho=span * 1.0)
shoot("hip", Vector((0.32, 0.45, 0.2)), Vector((-0.02, -0.25, -0.03)), lens=38)
mag = bpy.data.objects.get("Magazine")
if mag is not None:
    # The reload: the magazine object alone moves, 0.12 m down.
    mag.location.z -= 0.12
    shoot("reload", centre + Vector((d, 0, 0)), centre, ortho=span * 1.25)
    mag.location.z += 0.12
sight = bpy.data.objects.get("SightLook")
if sight is not None:
    for o in spheres: o.hide_render = True
    at = sight.matrix_world.translation
    shoot("sight-side", at + Vector((0.5, 0, -0.02)), at + Vector((0, 0, -0.02)), ortho=0.16)
    shoot("sight-top", at + Vector((0, 0, 0.5)), at, ortho=0.16)
    shoot("ads", at + Vector((0, 0.3, 0)), at, lens=60)
print("PREVIEW done", a.tag)
