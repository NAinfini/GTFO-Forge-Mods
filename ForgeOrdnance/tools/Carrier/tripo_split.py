"""Inside Blender with a repaired high-poly .blend open: writes every mesh node as its own welded .glb in its own local frame and nodes.json with the
hierarchy (world matrix, parent, surface area). blender -b <file.blend> --python tripo_split.py -- <folder>"""
import json
import os
import sys

import bmesh
import bpy

out = sys.argv[sys.argv.index("--") + 1]
os.makedirs(out, exist_ok=True)
info = {}
for o in list(bpy.context.scene.objects):
    if o.type not in ("MESH", "EMPTY"):
        continue
    entry = {"mesh": o.type == "MESH", "parent": o.parent.name if o.parent else None, "world": [list(r) for r in o.matrix_world]}
    if o.type == "MESH":
        me = o.data.copy()
        bm = bmesh.new()
        bm.from_mesh(me)
        bmesh.ops.remove_doubles(bm, verts=bm.verts, dist=1e-5)
        entry["area"] = sum(f.calc_area() for f in bm.faces)
        entry["tris"] = sum(len(f.verts) - 2 for f in bm.faces)
        bm.to_mesh(me)
        bm.free()
        ob = bpy.data.objects.new("X" + o.name, me)
        bpy.context.scene.collection.objects.link(ob)
        bpy.ops.object.select_all(action="DESELECT")
        ob.select_set(True)
        bpy.context.view_layer.objects.active = ob
        bpy.ops.export_scene.gltf(filepath=os.path.join(out, o.name + ".glb"), use_selection=True, export_format="GLB", export_animations=False, export_skins=False)
        bpy.data.objects.remove(ob)
    info[o.name] = entry
with open(os.path.join(out, "nodes.json"), "w", encoding="utf-8") as f:
    json.dump(info, f, ensure_ascii=False)
