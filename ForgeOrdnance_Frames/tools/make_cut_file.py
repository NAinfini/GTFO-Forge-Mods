"""Writes a model's first cut file, for the owner to split (Blender 5.2, background):

  blender --background --factory-startup --disable-autoexec --python-exit-code 1 --python make_cut_file.py -- \
      --model M15 --src <model file> --textures <dir> --out <dir> [--replace]

Steps: import (no package scripts run), stand the arms down with the model's own skeleton and bake that pose, join the parts,
face -Y with the origin between the feet, scale to the model's height, weld and tidy the source and reduce it to SHELL_TRIS,
map the source skin onto the 17-bone suit rig, bake all source materials into one 2048 texture set on a repacked copy of the
source UVs (albedo; R metal, G and A smoothness; tangent normal; emission colour; written beside the cut file).

<out>/<model>_cut.blend then holds the whole shell, shut and not yet thickened, as one object in the collection FIXED. The
timeline runs frames 1..30. Nothing is split or chosen: the owner may draw guide lines on it with Blender's Annotate tool; the
hinge add-on (Tools/Blender/hinge_tool) cuts the parts and sets their hinges, and its bake.py writes the file build_frame.py builds
from (build_assets.ps1 bakes each model's <model>_cut.draft.blend). An existing cut file is the owner's work and is never overwritten without --replace."""
import argparse
import json
import math
import os
import re
import sys

import bmesh
import bpy
import numpy as np
from mathutils import Matrix, Vector

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from build_frame import FIXED, MODELS, TILE, TILE_V, log, pieces

SIZE = 2048


# Source skeleton -> suit bone. Rules are tried in order on the bone name; a bone no rule names takes its parent's bone.
RIGS = {
    "helios": {
        "side": lambda n: "L" if re.search(r"(_L$|_L_|^b_l_)", n) else ("R" if re.search(r"(_R$|_R_|^b_r_)", n) else None),
        "rules": [(r"pedestal|pelvis|spine_0|spine1_twist", "pelvis"), (r"spine_1|spine2_twist", "spine"), (r"spine_2|collar", "chest"),
                  (r"neck|head", "head"), (r"upperarm|shoulder_helper", "upperarm"), (r"lowerarm|forearm_helper|elbow", "forearm"),
                  (r"hand|index|middle|ring|pinky|thumb", "hand"), (r"hip_|thigh", "thigh"), (r"knee", "shin"), (r"foot|toe", "foot")],
        "joints": {"pelvis": "bip_pelvis", "spine": "bip_spine_1", "chest": "bip_spine_2", "neck": "bip_neck",
                   "upperarm": "bip_upperArm_{S}", "forearm": "bip_lowerArm_{S}", "hand": "bip_hand_{S}", "handtip": "bip_middle_2_{S}:tail",
                   "thigh": "bip_hip_{S}", "shin": "bip_knee_{S}", "foot": "bip_foot_{S}", "toe": "bip_toe_{S}:tail"},
    },
    "halo5": {
        "side": lambda n: "L" if (n.startswith("Left") or re.search(r"_L(_|$)", n)) else ("R" if (n.startswith("Right") or re.search(r"_R(_|$)", n)) else None),
        "rules": [(r"^hips", "pelvis"), (r"^spine", "spine"), (r"^chest|shoulder", "chest"), (r"^neck|^head", "head"),
                  (r"wrist|palm|finger|thumb", "hand"), (r"elbow", "forearm"), (r" arm$", "upperarm"), (r"knee", "shin"),
                  (r"ankle|toe", "foot"), (r" leg$", "thigh")],
        "joints": {"pelvis": "Hips", "spine": "Spine", "chest": "Chest", "neck": "Neck",
                   "upperarm": "{Side} arm", "forearm": "{Side} elbow", "hand": "{Side} wrist", "handtip": "MiddleFinger3_{S}:tail",
                   "thigh": "{Side} leg", "shin": "{Side} knee", "foot": "{Side} ankle", "toe": "{Side} toe:tail"},
    },
}


SUIT_BONES = ["root", "pelvis", "spine", "chest", "head"] + [b + "." + s for s in ("L", "R") for b in
                                                              ("thigh", "shin", "foot", "upperarm", "forearm", "hand")]


ATLAS_SCALE = 0.955


SHELL_TRIS = 27000    # the source is reduced to this before the shell is given thickness


def import_model(src):
    bpy.ops.wm.read_factory_settings(use_empty=True)
    if src.lower().endswith((".glb", ".gltf")):
        bpy.ops.import_scene.gltf(filepath=src)
    else:
        bpy.ops.import_scene.fbx(filepath=src, use_anim=False)
    for o in list(bpy.data.objects):
        if o.type not in ("MESH", "ARMATURE") or (o.type == "MESH" and not any(m.type == "ARMATURE" for m in o.modifiers)):
            log("drop", o.type, o.name)
            bpy.data.objects.remove(o, do_unlink=True)
    arms = [o for o in bpy.data.objects if o.type == "ARMATURE"]
    if len(arms) != 1:
        raise SystemExit("Expected one armature, found %d" % len(arms))
    arm = arms[0]
    arm.animation_data_clear()
    for pb in arm.pose.bones:
        pb.matrix_basis = Matrix.Identity(4)
    bpy.context.view_layer.update()
    return arm, [o for o in bpy.data.objects if o.type == "MESH"]


def joint_bone(spec, side):
    s = {"L": "l", "R": "r"}[side] if side else ""
    full = {"L": "Left", "R": "Right"}[side] if side else ""
    return spec.replace("{S}", side or "").replace("{s}", s).replace("{Side}", full)


def bone_point(arm, spec):
    name, _, end = spec.partition(":")
    pb = arm.pose.bones[name]
    return arm.matrix_world @ (pb.tail if end == "tail" else pb.head)


def stand_arms(arm, rig, drop):
    """Swings each upper arm (with everything below it) about its shoulder so the arm hangs `drop` degrees below horizontal in
    the body's frontal plane."""
    A = arm.matrix_world
    R3 = A.to_3x3().normalized()
    for side, sign in (("L", 1.0), ("R", -1.0)):
        up = joint_bone(rig["joints"]["upperarm"], side)
        lo = joint_bone(rig["joints"]["forearm"], side)
        pb = arm.pose.bones[up]
        h = A @ pb.head
        e = A @ arm.pose.bones[lo].head
        d = (e - h).normalized()
        target = Vector((sign * math.cos(math.radians(drop)), 0.0, -math.sin(math.radians(drop)))).normalized()
        q = d.rotation_difference(target).to_matrix()
        local = (R3.inverted() @ q @ R3).to_4x4()
        pb.matrix = Matrix.Translation(pb.head) @ local @ Matrix.Translation(-pb.head) @ pb.matrix
        bpy.context.view_layer.update()
        log("arm", side, "from %.0f deg to %d deg below horizontal" % (math.degrees(math.asin(max(-1, min(1, -d.z)))), drop))


def bake_pose_and_join(arm, meshes):
    for o in meshes:
        if o.data.shape_keys:
            o.shape_key_clear()
        for m in list(o.modifiers):
            with bpy.context.temp_override(object=o, active_object=o):
                bpy.ops.object.modifier_apply(modifier=m.name)
    bpy.ops.object.select_all(action="DESELECT")
    for o in meshes:
        mw = o.matrix_world.copy()
        o.parent = None
        o.matrix_world = mw
    with bpy.context.temp_override(active_object=meshes[0], selected_editable_objects=meshes, object=meshes[0]):
        bpy.ops.object.join()
    body = meshes[0]
    body.name = "body"
    body.data.name = "body"
    with bpy.context.temp_override(active_object=body, selected_editable_objects=[body], object=body):
        bpy.ops.object.transform_apply(location=True, rotation=True, scale=True)
    return body


def map_bones(arm, rig):
    """Original bone name -> suit bone name."""
    out = {}

    def resolve(b):
        if b.name in out:
            return out[b.name]
        low = b.name.lower()
        target = None
        for pattern, t in rig["rules"]:
            if re.search(pattern, low):
                target = t
                break
        side = rig["side"](b.name)
        if target is None:
            if b.parent is None:
                target = "pelvis"
            else:
                out[b.name] = resolve(b.parent)
                return out[b.name]
        if target in ("upperarm", "forearm", "hand", "thigh", "shin", "foot"):
            if side is None:
                side = "L" if (arm.matrix_world @ b.head_local).x > 0 else "R"
            target += "." + side
        out[b.name] = target
        return target

    for b in arm.data.bones:
        resolve(b)
    return out


def remap_weights(body, mapping):
    me = body.data
    names = {g.index: g.name for g in body.vertex_groups}
    n = len(me.vertices)
    acc = {b: np.zeros(n) for b in SUIT_BONES}
    for v in me.vertices:
        for g in v.groups:
            t = mapping.get(names.get(g.group))
            if t:
                acc[t][v.index] += g.weight
    for g in list(body.vertex_groups):
        body.vertex_groups.remove(g)
    total = sum(acc.values())
    empty = np.where(total < 1e-6)[0]
    log("unweighted vertices", len(empty))
    return acc, empty


def normalise(body, joints, height):
    me = body.data
    co = np.empty(len(me.vertices) * 3)
    me.vertices.foreach_get("co", co)
    v = co.reshape(-1, 3)
    # Face -Y: toes are in front of the ankles.
    fwd = sum(joints["toe." + s].y - joints["foot." + s].y for s in "LR")
    if fwd > 0:
        v[:, 0] *= -1
        v[:, 1] *= -1
        for k in joints:
            joints[k] = Vector((-joints[k].x, -joints[k].y, joints[k].z))
        log("turned 180 degrees to face -Y")
    zmin, zmax = v[:, 2].min(), v[:, 2].max()
    s = height / (zmax - zmin)
    cx = (joints["foot.L"].x + joints["foot.R"].x) / 2
    cy = (joints["foot.L"].y + joints["foot.R"].y) / 2
    off = np.array([cx, cy, zmin])
    v[:] = (v - off) * s
    me.vertices.foreach_set("co", v.reshape(-1))
    me.update()
    for k in joints:
        joints[k] = (joints[k] - Vector(off)) * s
    joints["headtip"] = Vector((0, joints["neck"].y, height))
    log("scaled x%.3f to %.2f m; width %.2f depth %.2f" % (s, height, np.ptp(v[:, 0]), np.ptp(v[:, 1])))


def tidy(bm):
    """Welds coincident vertices (UVs are per corner and survive), drops degenerate, duplicate and loose geometry and makes
    each connected piece consistently oriented, keeping the source's outward side."""
    bmesh.ops.remove_doubles(bm, verts=bm.verts, dist=1e-5)
    bmesh.ops.dissolve_degenerate(bm, edges=bm.edges, dist=1e-6)
    bm.verts.index_update()
    seen, dup = set(), []
    for f in bm.faces:
        k = frozenset(v.index for v in f.verts)
        if k in seen:
            dup.append(f)
        else:
            seen.add(k)
    bmesh.ops.delete(bm, geom=dup, context="FACES_ONLY")
    # An edge shared by more than two faces keeps the two largest.
    extra = set()
    for e in bm.edges:
        if len(e.link_faces) > 2:
            extra.update(sorted(e.link_faces, key=lambda f: f.calc_area())[:-2])
    bmesh.ops.delete(bm, geom=list(extra), context="FACES_ONLY")
    bmesh.ops.delete(bm, geom=[v for v in bm.verts if not v.link_faces], context="VERTS")
    bm.faces.ensure_lookup_table()
    flipped = 0
    for piece in pieces(bm.faces):
        if all(e.is_contiguous for f in piece for e in f.edges if len(e.link_faces) == 2):
            continue
        before = sum(f.normal.dot(f.calc_center_median() - Vector((0, 0, f.calc_center_median().z))) * f.calc_area() for f in piece)
        bmesh.ops.recalc_face_normals(bm, faces=piece)
        after = sum(f.normal.dot(f.calc_center_median() - Vector((0, 0, f.calc_center_median().z))) * f.calc_area() for f in piece)
        if (after < 0) != (before < 0):
            bmesh.ops.reverse_faces(bm, faces=piece)
        flipped += 1
    return len(dup), len(extra), flipped


def reduce_source(body, target):
    me = body.data
    log("before tidy: %d triangles in %d faces" % (sum(len(p.vertices) - 2 for p in me.polygons), len(me.polygons)))
    bm = bmesh.new()
    bm.from_mesh(me)
    dup, extra, flipped = tidy(bm)
    bm.to_mesh(me)
    bm.free()
    tris = sum(len(p.vertices) - 2 for p in me.polygons)
    if tris > target:
        mod = body.modifiers.new("reduce", "DECIMATE")
        mod.ratio = target / tris
        with bpy.context.temp_override(object=body, active_object=body):
            bpy.ops.object.modifier_apply(modifier=mod.name)
    bm = bmesh.new()
    bm.from_mesh(me)
    tidy(bm)
    bm.to_mesh(me)
    bm.free()
    log("source tidied (%d duplicate faces, %d extra faces on shared edges, %d pieces reoriented), %d -> %d triangles" % (
        dup, extra, flipped, tris, sum(len(p.vertices) - 2 for p in me.polygons)))


def tex_sets(model, cfg, texdir, mat):
    """Image paths for one source material: base, metal, rough, normal, emissive (any may be None)."""
    files = {f.lower(): os.path.join(texdir, f) for f in os.listdir(texdir)}
    get = lambda n: files.get(n.lower())
    name = re.sub(r"\.\d+$", "", mat.name)
    if cfg["tex"] == "substance":
        p = cfg["sets"].get(name)
        if not p:
            return None
        return {"base": get(p + "_Base_color.png"), "metal": get(p + "_Metallic.png"), "rough": get(p + "_Roughness.png"),
                "normal": get(p + "_Normal_OpenGL.png"), "emit": get(p + "_Emissive.png")}
    if cfg["tex"] == "wrath":
        raise SystemExit("M16 (Wrath) is made by Source/mods/ordnance-assets/models/M16/source/make_wrath_cut.py, which supplies its own materials")
    return None


def build_material(mat, files, uv):
    mat.use_nodes = True
    nt = mat.node_tree
    nt.nodes.clear()
    out = nt.nodes.new("ShaderNodeOutputMaterial")
    p = nt.nodes.new("ShaderNodeBsdfPrincipled")
    nt.links.new(p.outputs[0], out.inputs["Surface"])
    uvn = nt.nodes.new("ShaderNodeUVMap")
    uvn.uv_map = uv

    def img(path, data):
        n = nt.nodes.new("ShaderNodeTexImage")
        n.image = bpy.data.images.load(path, check_existing=True)
        n.image.colorspace_settings.name = "Non-Color" if data else "sRGB"
        nt.links.new(uvn.outputs[0], n.inputs[0])
        return n

    if files["base"]:
        nt.links.new(img(files["base"], False).outputs[0], p.inputs["Base Color"])
    if files["metal"]:
        nt.links.new(img(files["metal"], True).outputs[0], p.inputs["Metallic"])
    if files["rough"]:
        nt.links.new(img(files["rough"], True).outputs[0], p.inputs["Roughness"])
    if files["normal"]:
        nm = nt.nodes.new("ShaderNodeNormalMap")
        nm.uv_map = uv
        nt.links.new(img(files["normal"], True).outputs[0], nm.inputs["Color"])
        nt.links.new(nm.outputs[0], p.inputs["Normal"])
    if files["emit"]:
        nt.links.new(img(files["emit"], False).outputs[0], p.inputs["Emission Color"])
        p.inputs["Emission Strength"].default_value = 1.0


def prepare_materials(body, model, cfg, texdir, uv):
    for mat in body.data.materials:
        if mat is None:
            continue
        files = tex_sets(model, cfg, texdir, mat)
        if files is not None:
            missing = [k for k in ("base", "normal") if not files[k]]
            if missing:
                raise SystemExit("Material %s lacks %s" % (mat.name, missing))
            build_material(mat, files, uv)
        else:
            mat.use_nodes = True
            for n in mat.node_tree.nodes:
                if n.type == "TEX_IMAGE" and not n.inputs[0].is_linked:
                    u = mat.node_tree.nodes.new("ShaderNodeUVMap")
                    u.uv_map = uv
                    mat.node_tree.links.new(u.outputs[0], n.inputs[0])
                if n.type == "NORMAL_MAP":
                    n.uv_map = uv
        p = next((n for n in mat.node_tree.nodes if n.type == "BSDF_PRINCIPLED"), None)
        if p is not None and "visor" in mat.name.lower() and not p.inputs["Emission Color"].is_linked:
            # Helios: the black visor glows faintly amber (its only light).
            p.inputs["Emission Color"].default_value = (1.0, 0.45, 0.12, 1.0)
            p.inputs["Emission Strength"].default_value = 0.35
        log("material", mat.name, "textures" if files else "own nodes")


def make_atlas(body):
    me = body.data
    src = me.uv_layers.active
    src.active_render = True
    uvname = src.name
    atlas = me.uv_layers.new(name="atlas")
    me.uv_layers.active = atlas
    bpy.context.view_layer.objects.active = body
    body.select_set(True)
    bpy.context.scene.tool_settings.use_uv_select_sync = True
    bpy.ops.object.mode_set(mode="EDIT")
    bpy.ops.mesh.select_all(action="SELECT")
    bpy.ops.uv.average_islands_scale()
    bpy.ops.uv.pack_islands(rotate=True, margin=0.002)
    bpy.ops.object.mode_set(mode="OBJECT")
    # UVs go through the float32 attribute API: a float64 buffer on MeshUVLoop.uv is not read or written as such.
    a = np.empty(len(me.loops) * 2, dtype=np.float32)
    me.attributes["atlas"].data.foreach_get("vector", a)
    a = a.reshape(-1, 2).astype(np.float64)
    log("atlas as packed: u %.3f..%.3f, v %.3f..%.3f" % (a[:, 0].min(), a[:, 0].max(), a[:, 1].min(), a[:, 1].max()))
    # The packer can leave the layout slightly past 0..1; fit it into the area below the reserved strip.
    lo = a.min(axis=0)
    a = (a - lo) * (ATLAS_SCALE / max(float((a.max(axis=0) - lo).max()), 1e-9))
    me.attributes["atlas"].data.foreach_set("vector", a.astype(np.float32).reshape(-1))
    me.update()
    b = np.empty(len(me.loops) * 2, dtype=np.float32)
    me.attributes["atlas"].data.foreach_get("vector", b)
    if b.max() > ATLAS_SCALE + 1e-4:
        raise SystemExit("The atlas UVs did not take the fitted layout (max %.3f)" % b.max())
    log("atlas packed; source UV", uvname)
    return uvname


def setup_cycles():
    sc = bpy.context.scene
    sc.render.engine = "CYCLES"
    try:
        prefs = bpy.context.preferences.addons["cycles"].preferences
        for kind in ("OPTIX", "CUDA"):
            try:
                prefs.compute_device_type = kind
                prefs.get_devices()
                if any(d.type == kind for d in prefs.devices):
                    for d in prefs.devices:
                        d.use = d.type == kind
                    sc.cycles.device = "GPU"
                    break
            except TypeError:
                continue
    except Exception as e:  # CPU bake works too, only slower
        log("GPU setup failed", e)
    sc.cycles.samples = 4
    sc.render.bake.margin = 8
    sc.render.bake.use_clear = True


def bake_textures(body):
    setup_cycles()
    mats = [m for m in body.data.materials if m]
    imgs = {}
    for key, data in (("base", False), ("metal", True), ("rough", True), ("emit", False), ("normal", True)):
        im = bpy.data.images.new("bake_" + key, SIZE, SIZE, alpha=False, float_buffer=False)
        im.colorspace_settings.name = "Non-Color" if data else "sRGB"
        imgs[key] = im
    state = []
    for m in mats:
        nt = m.node_tree
        out = next(n for n in nt.nodes if n.type == "OUTPUT_MATERIAL" and n.is_active_output) if any(
            n.type == "OUTPUT_MATERIAL" and n.is_active_output for n in nt.nodes) else next(n for n in nt.nodes if n.type == "OUTPUT_MATERIAL")
        surf = out.inputs["Surface"].links[0].from_socket if out.inputs["Surface"].is_linked else None
        p = next((n for n in nt.nodes if n.type == "BSDF_PRINCIPLED"), None)
        em = nt.nodes.new("ShaderNodeEmission")
        tgt = nt.nodes.new("ShaderNodeTexImage")
        nt.nodes.active = tgt
        state.append((m, nt, out, surf, p, em, tgt))

    def source(p, name):
        if p is None:
            return None, {"Base Color": (0.5, 0.5, 0.5, 1), "Metallic": 0.0, "Roughness": 0.6, "Emission Color": (0, 0, 0, 1)}[name]
        s = p.inputs[name]
        if s.is_linked:
            return s.links[0].from_socket, None
        return None, s.default_value

    # A material whose colour is no texture and a plain grey is an unpainted default: refuse it rather than bake grey.
    used = {body.data.materials[p.material_index].name for p in body.data.polygons if body.data.materials[p.material_index]}
    for m, nt, out, surf, p, em, tgt in state:
        sock, val = source(p, "Base Color")
        if m.name in used and sock is None and max(val[:3]) - min(val[:3]) < 0.02 and 0.2 < val[0] < 0.95:
            raise SystemExit("Material %s has no colour texture, only default grey %s" % (m.name, tuple(round(c, 2) for c in val[:3])))
    for key, chan in (("base", "Base Color"), ("metal", "Metallic"), ("rough", "Roughness"), ("emit", "Emission Color")):
        for m, nt, out, surf, p, em, tgt in state:
            tgt.image = imgs[key]
            for l in list(em.inputs["Color"].links):
                nt.links.remove(l)
            sock, val = source(p, chan)
            if sock is not None:
                nt.links.new(sock, em.inputs["Color"])
            else:
                em.inputs["Color"].default_value = tuple(val) if hasattr(val, "__len__") else (val, val, val, 1.0)
            strength = 1.0
            if key == "emit" and p is not None:
                strength = p.inputs["Emission Strength"].default_value
            em.inputs["Strength"].default_value = strength
            nt.links.new(em.outputs[0], out.inputs["Surface"])
        bpy.ops.object.bake(type="EMIT")
        log("baked", key)
    for m, nt, out, surf, p, em, tgt in state:
        tgt.image = imgs["normal"]
        if surf is not None:
            nt.links.new(surf, out.inputs["Surface"])
    bpy.ops.object.bake(type="NORMAL", normal_space="TANGENT")
    log("baked normal")
    for m, nt, out, surf, p, em, tgt in state:
        nt.nodes.remove(em)
        nt.nodes.remove(tgt)
    return imgs


def pixels(im):
    a = np.empty(SIZE * SIZE * 4, dtype=np.float32)
    im.pixels.foreach_get(a)
    return a.reshape(SIZE, SIZE, 4)


def tile_box(name):
    u0, u1 = TILE[name]
    v0, v1 = TILE_V
    return slice(int(v0 * SIZE) - 6, int(v1 * SIZE) + 6), slice(int(u0 * SIZE) - 6, int(u1 * SIZE) + 6)


def write_textures(imgs, out, model, tint=None):
    base, metal, rough, emit, normal = (pixels(imgs[k]) for k in ("base", "metal", "rough", "emit", "normal"))
    if tint:
        # Paint the light, unsaturated armor (the source's colour-change areas) in the model's colour; dark parts stay.
        rgb = base[..., :3]
        lum = rgb.mean(axis=2)
        sat = rgb.max(axis=2) - rgb.min(axis=2)
        t = np.clip((lum - 0.25) / 0.35, 0, 1) * np.clip(1 - sat * 4, 0, 1)
        painted = np.clip(rgb * np.array(tint) * 1.25, 0, 1)
        base[..., :3] = rgb * (1 - t[..., None]) + painted * t[..., None]
    # Strip tiles: the cavity (near black, non-metal, rough, flat, no light) on the shell's inner wall and the trim (dark satin
    # metal) on cut edges.
    r, c = tile_box("cavity")
    base[r, c, :3] = (0.035, 0.035, 0.04)  # display (sRGB) values
    metal[r, c, 0] = 0.0
    rough[r, c, 0] = 0.95
    emit[r, c, :3] = 0.0
    normal[r, c, :3] = (0.5, 0.5, 1.0)
    r, c = tile_box("trim")
    base[r, c, :3] = (0.16, 0.16, 0.17)
    metal[r, c, 0] = 0.5
    rough[r, c, 0] = 0.6
    emit[r, c, :3] = 0.0
    normal[r, c, :3] = (0.5, 0.5, 1.0)

    def save(arr, name, data):
        im = bpy.data.images.new(name, SIZE, SIZE, alpha=True)
        im.colorspace_settings.name = "Non-Color" if data else "sRGB"
        arr = arr.copy()
        arr[..., 3] = 1.0 if arr.shape[-1] == 4 else arr[..., 3]
        im.pixels.foreach_set(arr.reshape(-1))
        im.filepath_raw = os.path.join(out, name)
        im.file_format = "PNG"
        im.save()
        return im

    mr = np.zeros_like(metal)
    mr[..., 0] = metal[..., 0]
    mr[..., 1] = 1.0 - rough[..., 0]
    mr[..., 2] = 0.0
    a = mr.copy()
    sm = (1.0 - rough[..., 0])
    a[..., 3] = 1.0
    saved = {
        "albedo": save(base, model + "_albedo.png", False),
        "normal": save(normal, model + "_normal.png", True),
        "emission": save(emit, model + "_emission.png", False),
    }
    # Metallic: R metal, G and A smoothness (the gear shader reads one of them).
    im = bpy.data.images.new(model + "_metallic.png", SIZE, SIZE, alpha=True)
    im.colorspace_settings.name = "Non-Color"
    mr[..., 3] = sm
    im.pixels.foreach_set(mr.reshape(-1))
    im.filepath_raw = os.path.join(out, model + "_metallic.png")
    im.file_format = "PNG"
    im.save()
    saved["metallic"] = im
    log("emission texels lit", int((emit[..., :3].max(axis=2) > 0.02).sum()))
    return saved


def single_material(body, saved, uvname):
    me = body.data
    mat = bpy.data.materials.new("frame-body")
    mat.use_nodes = True
    mat.use_backface_culling = True
    nt = mat.node_tree
    p = next(n for n in nt.nodes if n.type == "BSDF_PRINCIPLED")

    def img(im, data):
        n = nt.nodes.new("ShaderNodeTexImage")
        n.image = im
        n.image.colorspace_settings.name = "Non-Color" if data else "sRGB"
        return n

    nt.links.new(img(saved["albedo"], False).outputs[0], p.inputs["Base Color"])
    mt = img(saved["metallic"], True)
    sep = nt.nodes.new("ShaderNodeSeparateColor")
    nt.links.new(mt.outputs[0], sep.inputs[0])
    nt.links.new(sep.outputs[0], p.inputs["Metallic"])
    inv = nt.nodes.new("ShaderNodeMath")
    inv.operation = "SUBTRACT"
    inv.inputs[0].default_value = 1.0
    nt.links.new(sep.outputs[1], inv.inputs[1])
    nt.links.new(inv.outputs[0], p.inputs["Roughness"])
    nm = nt.nodes.new("ShaderNodeNormalMap")
    nt.links.new(img(saved["normal"], True).outputs[0], nm.inputs["Color"])
    nt.links.new(nm.outputs[0], p.inputs["Normal"])
    nt.links.new(img(saved["emission"], False).outputs[0], p.inputs["Emission Color"])
    p.inputs["Emission Strength"].default_value = 1.0
    me.materials.clear()
    me.materials.append(mat)
    for poly in me.polygons:
        poly.material_index = 0
    for l in [l for l in me.uv_layers if l.name != "atlas"]:
        me.uv_layers.remove(l)
    me.uv_layers["atlas"].name = "UVMap"
    me.uv_layers["UVMap"].active_render = True


def surface(args):
    """Import to the baked single-material body: posed, joined, scaled, reduced, skinned and textured. Returns the body, the
    joints and the source's triangle count."""
    cfg = MODELS[args.model]
    rig_cfg = RIGS[cfg["rig"]]
    H = cfg["height"]
    os.makedirs(args.out, exist_ok=True)
    arm, meshes = import_model(args.src)
    tris0 = sum(sum(len(p.vertices) - 2 for p in o.data.polygons) for o in meshes)
    log("source", len(meshes), "meshes", tris0, "triangles", len(arm.data.bones), "bones")
    stand_arms(arm, rig_cfg, cfg["drop"])
    joints = {}
    for k, spec in rig_cfg["joints"].items():
        if "{" in spec:
            for side in ("L", "R"):
                joints[k + "." + side] = bone_point(arm, joint_bone(spec, side))
        else:
            joints[k] = bone_point(arm, spec)
    mapping = map_bones(arm, rig_cfg)
    body = bake_pose_and_join(arm, meshes)
    bpy.data.objects.remove(arm, do_unlink=True)
    normalise(body, joints, H)
    reduce_source(body, SHELL_TRIS)
    acc, empty = remap_weights(body, mapping)

    uvname = body.data.uv_layers.active.name
    prepare_materials(body, args.model, cfg, args.textures, uvname)
    make_atlas(body)
    imgs = bake_textures(body)
    # Every corner's atlas UV lies inside the baked atlas area (the materials were checked for a colour source in the bake).
    a = np.empty(len(body.data.loops) * 2, dtype=np.float32)
    body.data.attributes["atlas"].data.foreach_get("vector", a)
    a = a.reshape(-1, 2)
    outside = int(((a < 0) | (a > ATLAS_SCALE) | ~np.isfinite(a)).any(axis=1).sum())
    log("atlas check: %d corners, %d outside the atlas (u %.3f..%.3f, v %.3f..%.3f)" % (
        len(a), outside, a[:, 0].min(), a[:, 0].max(), a[:, 1].min(), a[:, 1].max()))
    if outside:
        raise SystemExit("%d UV corners outside the atlas" % outside)
    saved = write_textures(imgs, args.out, args.model, cfg.get("tint"))
    single_material(body, saved, uvname)
    # Skin groups (vertex groups named after the bones).
    me = body.data
    co = np.array([v.co[:] for v in me.vertices])
    if len(empty):
        # Nearest joint's bone for the few vertices the source left unweighted.
        heads = {b: np.array(joints[b][:]) for b in SUIT_BONES if b in joints}
        for i in empty:
            b = min(heads, key=lambda n: np.linalg.norm(co[i] - heads[n]))
            acc[b][i] = 1.0
    for name in SUIT_BONES[1:]:
        body.vertex_groups.new(name=name)
    total = sum(acc[b] for b in SUIT_BONES)
    for b in SUIT_BONES:
        w = acc[b] / np.maximum(total, 1e-9)
        idx = np.where(w > 1e-3)[0]
        if b == "root" or not len(idx):
            continue
        g = body.vertex_groups[b]
        for i in idx:
            g.add([int(i)], float(w[i]), "REPLACE")
    return body, joints, tris0


# ------------------------------------------------------------------------------------------------------------- cut file
def arguments():
    argv = sys.argv[sys.argv.index("--") + 1:]
    p = argparse.ArgumentParser()
    p.add_argument("--model", required=True)
    p.add_argument("--src", required=True)
    p.add_argument("--textures", required=True)
    p.add_argument("--out", required=True)
    p.add_argument("--replace", action="store_true", help="overwrite an existing cut file (the owner's work)")
    return p.parse_args(argv)


def main():
    args = arguments()
    path = os.path.join(args.out, args.model + "_cut.blend")
    if os.path.exists(path) and not args.replace:
        raise SystemExit("%s exists (the owner's cut); pass --replace to start it again" % path)
    body, joints, tris0 = surface(args)
    cut_file(args.model, body, joints, tris0, path)


def collection(name, parent=None):
    coll = bpy.data.collections.new(name)
    (parent or bpy.context.scene.collection).children.link(coll)
    return coll


def cut_file(model, body, joints, tris0, path):
    for o in list(bpy.data.objects):
        if o is not body:
            bpy.data.objects.remove(o, do_unlink=True)
    body.name = "外壳"
    body.data.name = "外壳"
    fixed = collection(FIXED)
    for c in body.users_collection:
        c.objects.unlink(body)
    fixed.objects.link(body)
    bpy.context.scene["frame"] = json.dumps({"model": model, "joints": {k: [round(c, 5) for c in v] for k, v in joints.items()},
                                             "tris0": tris0})
    scene = bpy.context.scene
    scene.render.fps = 30
    scene.frame_start, scene.frame_end, scene.frame_current = 1, 30, 1
    # Show the textures in the solid view (the seams show in the paint).
    for screen in bpy.data.screens:
        for area in screen.areas:
            for space in area.spaces:
                if space.type == "VIEW_3D":
                    space.shading.color_type = "TEXTURE"
    bpy.ops.file.make_paths_relative()
    bpy.ops.wm.save_as_mainfile(filepath=path)
    log("cut file", path, "faces", len(body.data.polygons))


if __name__ == "__main__":
    main()
