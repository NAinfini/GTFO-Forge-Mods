"""Turns a downloaded FBX gun with separate PNG textures into a glTF that import_body.py reads (it expects Principled
materials fed by a base colour image, one packed occlusion-roughness-metallic image and a normal image).
The download is untrusted data: Blender only imports the model file and reads the PNGs; nothing in it runs and the sources are never written.

blender -b --factory-startup --python fbx_to_glb.py -- --fbx <file.fbx> --tex <texture folder>
    --mat "Material=base.png,roughness.png,metallic.png,normal.png[,ao.png]" ... --out <new.glb>

- each --mat names a material of the FBX and its images (a metallic of "-" means 0, an ao of "-" or none means no occlusion);
- the three images are packed as R occlusion, G roughness, B metallic at the base colour's size;
- --metal-cap limits the metallic value: a map that is 1 everywhere (bare metal under the game's soft light) would render black;
- a material without a --mat keeps its flat colour.
"""
import argparse, os, sys
import bpy
import numpy as np

argv = sys.argv[sys.argv.index("--") + 1:]
p = argparse.ArgumentParser()
p.add_argument("--fbx", required=True)
p.add_argument("--tex", required=True)
p.add_argument("--mat", action="append", default=[])
p.add_argument("--out", required=True)
p.add_argument("--metal-cap", type=float, default=1.0)
a = p.parse_args(argv)

bpy.ops.wm.read_factory_settings(use_empty=True)
bpy.ops.import_scene.fbx(filepath=a.fbx)

def load(name, colour):
    img = bpy.data.images.load(os.path.join(a.tex, name), check_existing=True)
    img.colorspace_settings.name = "sRGB" if colour else "Non-Color"
    return img

def gray(img, size):
    w, h = img.size
    d = np.empty(w * h * 4, dtype=np.float32); img.pixels.foreach_get(d); d = d.reshape(h, w, 4)[..., 0]
    if (w, h) != tuple(size):
        t = bpy.data.images.new("t", w, h, alpha=False, float_buffer=True); t.colorspace_settings.name = "Non-Color"
        t.pixels.foreach_set(np.repeat(d[..., None], 4, axis=2).ravel().astype(np.float32)); t.scale(*size)
        o = np.empty(size[0] * size[1] * 4, dtype=np.float32); t.pixels.foreach_get(o); bpy.data.images.remove(t)
        d = o.reshape(size[1], size[0], 4)[..., 0]
    return d

for spec in a.mat:
    name, _, rest = spec.partition("=")
    names = rest.split(",")
    base_f, rough_f, metal_f, normal_f = names[:4]
    ao_f = names[4] if len(names) > 4 else "-"
    m = bpy.data.materials[name]
    m.use_nodes = True
    nt = m.node_tree; nt.nodes.clear()
    out = nt.nodes.new("ShaderNodeOutputMaterial"); b = nt.nodes.new("ShaderNodeBsdfPrincipled")
    nt.links.new(b.outputs[0], out.inputs["Surface"])
    base = load(base_f, True)
    t = nt.nodes.new("ShaderNodeTexImage"); t.image = base; nt.links.new(t.outputs["Color"], b.inputs["Base Color"])
    size = base.size
    packed = np.ones((size[1], size[0], 4), dtype=np.float32)
    packed[..., 0] = gray(load(ao_f, False), size) if ao_f != "-" else 1.0
    packed[..., 1] = gray(load(rough_f, False), size)
    packed[..., 2] = np.minimum(gray(load(metal_f, False), size), a.metal_cap) if metal_f != "-" else 0.0
    orm = bpy.data.images.new(name + "_orm", size[0], size[1], alpha=False); orm.colorspace_settings.name = "Non-Color"
    orm.pixels.foreach_set(packed.ravel()); orm.pack()
    t = nt.nodes.new("ShaderNodeTexImage"); t.image = orm
    sep = nt.nodes.new("ShaderNodeSeparateColor"); nt.links.new(t.outputs["Color"], sep.inputs["Color"])
    nt.links.new(sep.outputs["Green"], b.inputs["Roughness"]); nt.links.new(sep.outputs["Blue"], b.inputs["Metallic"])
    t = nt.nodes.new("ShaderNodeTexImage"); t.image = load(normal_f, False)
    nm = nt.nodes.new("ShaderNodeNormalMap"); nt.links.new(t.outputs["Color"], nm.inputs["Color"]); nt.links.new(nm.outputs["Normal"], b.inputs["Normal"])
    print("FBX material", name, "base", tuple(base.size), "normal", tuple(t.image.size))

bpy.ops.export_scene.gltf(filepath=a.out, export_format="GLB", export_image_format="AUTO", use_selection=False)
print("FBX saved", a.out)


