"""Shared by export_parts_blender.py and render_icons_blender.py (runs inside Blender).

assemble(src, mode) rebuilds one energy gun from the parts library in an empty scene: the receiver root at the origin,
front / stock / mag roots at the receiver's align_Front / align_Stock / align_Magazine, the sight root at align_Sight,
all rotations identity (Blender frame: muzzle -Y, up +Z, shooter's right -X, metres). Every manifest number is checked
against the align empties of the blends.
"""
import json
import os
import re
import sys
from pathlib import Path
from types import SimpleNamespace

import bpy
from mathutils import Vector

MODES = ("beam", "arc", "plasma-blast", "plasma-arc", "blast", "disc", "flame", "hole")
TOL = 2e-5                                   # manifests hold 5 decimals


def args():
    return sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else []


def read_json(path):
    return json.loads(Path(path).read_text(encoding="utf-8"))


def load(path):
    """append every object of a blend; image paths stay valid against the source blend's folder"""
    before = set(bpy.data.images)
    with bpy.data.libraries.load(str(path), link=False) as (src, dst):
        dst.objects = list(src.objects)
    objs = [o for o in dst.objects if o is not None]
    for o in objs:
        bpy.context.scene.collection.objects.link(o)
    bpy.context.view_layer.update()
    for img in set(bpy.data.images) - before:
        if img.filepath:
            img.filepath = os.path.normpath(bpy.path.abspath(img.filepath, start=str(Path(path).parent)))
    return objs


def base(o):
    """name without the .001 suffix Blender adds when parts from several blends share one scene"""
    return re.sub(r"\.\d{3}$", "", o.name)


def split(objs, what):
    roots = [o for o in objs if o.type == "EMPTY" and base(o).endswith("_root")]
    if len(roots) != 1 or roots[0].parent is not None:
        raise RuntimeError(f"{what}: expected exactly one root empty, found {[o.name for o in roots]}")
    root = roots[0]
    if any(abs(v) > 1e-9 for v in root.location) or any(abs(v) > 1e-9 for v in root.rotation_euler) or any(abs(v - 1) > 1e-9 for v in root.scale):
        raise RuntimeError(f"{what}: root must sit at the origin with identity transform")
    aligns = {base(o)[len("align_"):]: o for o in objs if o.type == "EMPTY" and base(o).startswith("align_")}
    meshes = {base(o): o for o in objs if o.type == "MESH"}
    return root, aligns, meshes


def check_aligns(what, aligns, manifest):
    for key, want in manifest.items():
        if key not in aligns:
            raise RuntimeError(f"{what}: missing align_{key}")
        got = aligns[key].matrix_world.translation
        if max(abs(got[i] - want[i]) for i in range(3)) > TOL:
            raise RuntimeError(f"{what}: align_{key} {tuple(got)} differs from the manifest {want}")


def principled(obj):
    mat = obj.data.materials[0] if len(obj.data.materials) == 1 else None
    node = next((n for n in mat.node_tree.nodes if n.type == "BSDF_PRINCIPLED"), None) if mat and mat.node_tree else None
    if node is None:
        raise RuntimeError(f"{obj.name}: needs exactly one Principled BSDF material")
    return node


def base_color_path(obj):
    links = principled(obj).inputs["Base Color"].links
    if not links or links[0].from_node.type != "TEX_IMAGE" or links[0].from_node.image is None:
        raise RuntimeError(f"{obj.name}: body material needs one Principled BSDF with a Base Color image")
    return Path(os.path.normpath(bpy.path.abspath(links[0].from_node.image.filepath)))


def assemble(src, mode):
    src = Path(src)
    bpy.ops.wm.read_factory_settings(use_empty=True)
    gun = src / "guns" / mode
    man = read_json(gun / "manifest.json")
    if man["mode"] != mode:
        raise RuntimeError(f"{mode}: manifest names mode {man['mode']}")
    rail_id, sight_id = man["rail"], man["sight"]
    rail_dir, sight_dir = src / "library" / "rail" / rail_id[len("rail-"):], src / "library" / "sight" / sight_id[len("sight-"):]
    rail_man, sight_man = read_json(rail_dir / "manifest.json"), read_json(sight_dir / "manifest.json")
    sight_name, rail_name = sight_man["name"], rail_man["name"]
    if rail_man["id"] != rail_id or sight_man["id"] != sight_id:
        raise RuntimeError(f"{mode}: library manifests do not match {rail_id} / {sight_id}")

    def textures(folder, listed):
        # Rebuilt parts also carry a normal map; parts without one render with the shader's flat normal.
        out = {k: (folder / listed[k]).resolve() for k in ("BaseColor", "MetallicSmoothness", "Emission", "Normal") if k in listed}
        if len(out) < 3:
            raise RuntimeError(f"{folder}: BaseColor, MetallicSmoothness and Emission are required")
        for p in out.values():
            if not p.is_file():
                raise FileNotFoundError(p)
        return out

    kinds = {}
    for kind, mesh_name, align in (("receiver", "Receiver", None), ("front", "Front", "Front"), ("stock", "Stock", "Stock"), ("mag", "Mag", "Magazine")):
        pm = man["parts"][kind]
        root, aligns, meshes = split(load(gun / kind / f"{mode}-{kind}.blend"), f"{mode}/{kind}")
        check_aligns(f"{mode}/{kind}", aligns, pm["aligns"])
        kinds[kind] = dict(root=root, aligns=aligns, meshes=meshes, mesh=mesh_name, align=align, tex=textures(gun, pm["textures"]))
    sroot, saligns, smeshes = split(load(sight_dir / f"sight-{sight_name}.blend"), f"{mode}/{sight_id}")
    check_aligns(f"{mode}/{sight_id}", saligns, sight_man["aligns"])
    if any(abs(v) > TOL for v in saligns["Sight"].matrix_world.translation):
        raise RuntimeError(f"{sight_id}: align_Sight must be the sight root origin")

    receiver_aligns = {k: v.matrix_world.translation.copy() for k, v in kinds["receiver"]["aligns"].items()}
    if any(abs(v) > TOL for v in receiver_aligns["RightHand"]):
        raise RuntimeError(f"{mode}: receiver align_RightHand must be the origin")
    for kind in ("front", "stock", "mag"):
        kinds[kind]["root"].location = receiver_aligns[kinds[kind]["align"]]
    sroot.location = receiver_aligns["Sight"]
    bpy.context.view_layer.update()

    rail_mesh = rail_name
    rcv = kinds["receiver"]["meshes"]
    unknown = set(rcv) - {"Receiver", "Trigger", rail_mesh}
    if unknown or "Receiver" not in rcv or rail_mesh not in rcv:
        raise RuntimeError(f"{mode}/receiver: unexpected mesh objects {sorted(rcv)} (rail {rail_mesh})")
    for kind in ("front", "stock", "mag"):
        if set(kinds[kind]["meshes"]) != {kinds[kind]["mesh"]}:
            raise RuntimeError(f"{mode}/{kind}: unexpected mesh objects {sorted(kinds[kind]['meshes'])}")
    glass = [n for n in smeshes if n == f"{sight_name}_Glass"]
    reticles = sorted(n for n in smeshes if n.startswith(f"{sight_name}_Reticle"))
    if set(smeshes) != {sight_name, *glass, *reticles} or sight_name not in smeshes or len(reticles) > 2:
        raise RuntimeError(f"{sight_id}: unexpected mesh objects {sorted(smeshes)}")

    receiver_tex, rail_tex, sight_tex = kinds["receiver"]["tex"], textures(rail_dir, rail_man["textures"]), textures(sight_dir, sight_man["textures"])
    zero, sight_at = Vector((0, 0, 0)), receiver_aligns["Sight"]
    parts = [
        dict(name="Receiver", surface="body", textureSet=f"{mode}-Receiver", objects=[rcv["Receiver"]] + ([rcv["Trigger"]] if "Trigger" in rcv else []), pivot=zero, textures=receiver_tex),
        dict(name="Rail", surface="body", textureSet=rail_id, objects=[rcv[rail_mesh]], pivot=zero, textures=rail_tex),
        dict(name="Front", surface="body", textureSet=f"{mode}-Front", objects=[kinds["front"]["meshes"]["Front"]], pivot=receiver_aligns["Front"], textures=kinds["front"]["tex"]),
        dict(name="Stock", surface="body", textureSet=f"{mode}-Stock", objects=[kinds["stock"]["meshes"]["Stock"]], pivot=receiver_aligns["Stock"], textures=kinds["stock"]["tex"]),
        dict(name="Magazine", surface="body", textureSet=f"{mode}-Magazine", objects=[kinds["mag"]["meshes"]["Mag"]], pivot=receiver_aligns["Magazine"], textures=kinds["mag"]["tex"]),
        dict(name="Sight", surface="body", textureSet=sight_id, objects=[smeshes[sight_name]], pivot=sight_at, textures=sight_tex),
    ]
    parts += [dict(name="SightGlass", surface="glass", textureSet="", objects=[smeshes[n]], pivot=sight_at, textures=None) for n in glass]
    parts += [dict(name=f"SightReticle{i + 1}", surface="reticle", textureSet="", objects=[smeshes[n]], pivot=sight_at, textures=None) for i, n in enumerate(reticles)]
    for p in parts:
        if p["surface"] == "body":
            for o in p["objects"]:
                got, want = base_color_path(o), p["textures"]["BaseColor"]
                if os.path.normcase(str(got)) != os.path.normcase(str(want)):
                    raise RuntimeError(f"{mode}/{p['name']}: {o.name} is textured with {got}, expected {want}")
    assembly = SimpleNamespace(mode=mode, sight=sight_name, rail=rail_name, parts=parts,
                               left_hand=receiver_aligns["LeftHand"],
                               muzzle=kinds["front"]["root"].location + kinds["front"]["aligns"]["Muzzle"].location,
                               sight_look=sroot.location + saligns["SightLook"].location)
    bpy.context.view_layer.update()
    return assembly
