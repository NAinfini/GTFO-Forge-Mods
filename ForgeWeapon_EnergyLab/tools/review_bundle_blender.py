"""Render actual decoded bundle meshes/textures with the existing ADS review scene, plus a hip view.

blender --background --factory-startup --python-exit-code 1 --python review_bundle_blender.py --
    --source <Rework> --decoded <audit_bundle --extract output> --out <folder>
The camera is 0.3 m behind SightLook in ADS, or 0.35 m behind RightHand in hip view.
"""
import argparse
import json
import math
import runpy
import sys
from pathlib import Path
import bpy
import numpy as np
from mathutils import Vector
sys.path.insert(0, str(Path(__file__).resolve().parent))
from sight_window_blender import check_sight_window


def frame(v):
    a = np.asarray(v)
    if not a.size:
        return np.empty((0, 3))
    return np.stack([-a[..., 0], -a[..., 2], a[..., 1]], axis=-1)


def vector(v):
    return Vector(frame([v[k] for k in ("x", "y", "z")]))


def texture(folder, name, kind, normal=False):
    path = folder / "textures" / f"{name.lower()}-{kind}.png"
    image = bpy.data.images.load(str(path))
    image.colorspace_settings.name = "sRGB" if kind in ("albedo", "emission") else "Non-Color"
    if normal:
        # Unity's DXT5nm has X in A and Y in G; unpack it before Blender's normal node.
        pixels = np.empty(len(image.pixels), np.float32)
        image.pixels.foreach_get(pixels)
        p = pixels.reshape(-1, 4)
        x, y = p[:, 3] * 2 - 1, p[:, 1] * 2 - 1
        p[:, 0], p[:, 2], p[:, 3] = (x + 1) / 2, (np.sqrt(np.maximum(0, 1 - x*x - y*y)) + 1) / 2, 1
        image.pixels.foreach_set(pixels)
        image.pack()
    return image


def body_material(folder, part, flags):
    mat = bpy.data.materials.new(part["textureSet"])
    mat.use_nodes = True
    nt = mat.node_tree
    bsdf = nt.nodes.get("Principled BSDF")
    def node(kind, normal=False):
        n = nt.nodes.new("ShaderNodeTexImage")
        n.image = texture(folder, part["textureSet"], kind, normal)
        return n
    nt.links.new(node("albedo").outputs["Color"], bsdf.inputs["Base Color"])
    metal = node("metallic")
    separate = nt.nodes.new("ShaderNodeSeparateColor")
    nt.links.new(metal.outputs["Color"], separate.inputs["Color"])
    nt.links.new(separate.outputs["Red"], bsdf.inputs["Metallic"])
    invert = nt.nodes.new("ShaderNodeMath"); invert.operation = "SUBTRACT"; invert.inputs[0].default_value = 1
    nt.links.new(metal.outputs["Alpha"], invert.inputs[1])
    nt.links.new(invert.outputs[0], bsdf.inputs["Roughness"])
    if flags["normal"]:
        nm = nt.nodes.new("ShaderNodeNormalMap")
        nt.links.new(node("normal", True).outputs["Color"], nm.inputs["Color"])
        nt.links.new(nm.outputs["Normal"], bsdf.inputs["Normal"])
    if flags["emission"]:
        nt.links.new(node("emission").outputs["Color"], bsdf.inputs["Emission Color"])
        bsdf.inputs["Emission Strength"].default_value = 1
    return mat


def assemble_bundle(folder, weapon, sets):
    bpy.ops.wm.read_factory_settings(use_empty=True)
    for p in weapon["parts"]:
        data = np.load(folder / "meshes" / f"{weapon['mode']}-{p['name']}.npz")
        v, n, uv, tri = frame(data["vertices"]), frame(data["normals"]), data["uv"], data["indices"][:, [0, 2, 1]]
        mesh = bpy.data.meshes.new(p["name"])
        mesh.from_pydata(v.tolist(), [], tri.tolist())
        # Unity tolerates zero-area export triangles; Blender's custom-normal setter does not.
        mesh.validate(clean_customdata=False)
        mesh.update()
        loops = np.empty(len(mesh.loops), np.int32)
        mesh.loops.foreach_get("vertex_index", loops)
        for poly in mesh.polygons:
            poly.use_smooth = True
        if len(n):
            mesh.normals_split_custom_set(n[loops].tolist())
        if len(uv):
            layer = mesh.uv_layers.new(name="UVMap")
            layer.data.foreach_set("uv", uv[loops, :2].reshape(-1))
        obj = bpy.data.objects.new(p["name"], mesh)
        bpy.context.scene.collection.objects.link(obj)
        obj.location = vector(p["pivot"])
        if p["surface"] == "body":
            mat = body_material(folder, p, sets[p["textureSet"]])
        else:
            mat = bpy.data.materials.new(p["name"]); mat.use_nodes = True; mat["role"] = p["surface"]
            bsdf = mat.node_tree.nodes.get("Principled BSDF")
            c = p["color"]
            bsdf.inputs["Base Color"].default_value = (c["r"], c["g"], c["b"], 1)
            if p["surface"] == "glass":
                bsdf.inputs["Alpha"].default_value = c["a"]
                bsdf.inputs["Roughness"].default_value = .15
                mat.surface_render_method = "DITHERED"
            else:
                bsdf.inputs["Emission Color"].default_value = (c["r"], c["g"], c["b"], 1)
                bsdf.inputs["Emission Strength"].default_value = 1
        mesh.materials.append(mat)
    look = bpy.data.objects.new("align_SightLook", None)
    bpy.context.scene.collection.objects.link(look); look.location = vector(weapon["sightLook"])
    bpy.context.view_layer.update()


def check_window(weapon, source):
    sight = json.loads((source / "library/sight" / weapon["sight"] / "manifest.json").read_text())
    look = vector(weapon["sightLook"])
    objects = [o for o in bpy.context.scene.objects if o.type == "MESH" and
               not any(m and m.get("role") in ("glass", "reticle") for m in o.data.materials)]
    check_sight_window(weapon["mode"], look, sight["windowMm"], objects, [o for o in objects if o.name == "Sight"])


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--source", required=True, type=Path)
    ap.add_argument("--decoded", required=True, type=Path)
    ap.add_argument("--out", required=True, type=Path)
    opt = ap.parse_args(sys.argv[sys.argv.index("--") + 1:])
    source, folder, out = opt.source.resolve(), opt.decoded.resolve(), opt.out.resolve()
    out.mkdir(parents=True, exist_ok=True)
    profile = json.loads((folder / "profile.json").read_text())
    sets = {s["name"]: s for s in profile["textureSets"]}
    for w in profile["weapons"]:
        assemble_bundle(folder, w, sets)
        check_window(w, source)
        blend = out / f"{w['mode']}.blend"
        bpy.ops.wm.save_as_mainfile(filepath=str(blend))
        sys.argv = ["ads_view.py", "--", str(blend), str(out / f"{w['mode']}-ads.png"), "0.3", "0.05"]
        state = runpy.run_path(str(source / "cad/blender/ads_view.py"))
        state["img"].save_render(str(out / f"{w['mode']}-ads-mask.png"))
        state["env"].hide_render = False
        for obj in state["glass"]:
            obj.hide_render = False
        sc, cam = state["sc"], state["cam"]
        sc.view_settings.look = "Medium High Contrast"
        sc.view_settings.exposure = 2
        sc.render.film_transparent = False
        sc.render.filepath = str(out / f"{w['mode']}-ads-detail.png")
        bpy.ops.render.render(write_still=True)
        cam.location = Vector((.14, .35, .25))
        cam.data.angle = math.radians(45)
        sc.render.filepath = str(out / f"{w['mode']}-hip.png")
        bpy.ops.render.render(write_still=True)
        print("HIP", sc.render.filepath)
        state["env"].hide_render = True
        for obj in state["glass"]:
            obj.hide_render = True
        sc.render.film_transparent = True
        sc.render.filepath = str(out / f"{w['mode']}-hip-mask.png")
        bpy.ops.render.render(write_still=True)


if __name__ == "__main__":
    main()
