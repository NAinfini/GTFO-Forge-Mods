"""Stage the Unity project that builds the per-weapon model bundles from the parts library (format 4).

blender --background --factory-startup --python-exit-code 1 --python export_parts_blender.py -- --source <Rework folder> --out <Unity project folder>
Every gun is assembled from its parts (parts_assembly.py), converted from Blender (muzzle -Y, up +Z, right -X) to Unity
(unity = (-x, z, -y), +Z muzzle, winding reversed) and written as Geometry/<mode>/<Part>.meshbin plus Geometry/manifest.json;
textures land in Assets/Weapons/<set>/. Each body part that is large also gets Geometry/<mode>/<Part>.reduced.meshbin (lod_mesh.py),
the mesh every view but first person draws. The result holds exactly the current input.
"""
import argparse
import hashlib
import json
import re
import shutil
import struct
import sys
import tempfile
from pathlib import Path

import bpy
import numpy as np

sys.path.insert(0, str(Path(__file__).resolve().parent))
from lod_mesh import corners, low_corners
from parts_assembly import MODES, args, assemble, principled
from sight_window_blender import check_sight_window
from owned_outputs import reserve, clear

TOOLS = Path(__file__).resolve().parent
UNITY_VERSION = "m_EditorVersion: 2019.4.21f1\nm_EditorVersionWithRevision: 2019.4.21f1 (b76dac84db26)\n"
UNITY_MODULES = ("assetbundle", "imageconversion", "jsonserialize", "particlesystem", "physics")
TO_UNITY = np.array([[-1, 0, 0], [0, 0, 1], [0, -1, 0]], np.float64)
LENGTH = (0.5, 1.3)
# Every view but first person draws a reduced mesh of each body part; the whole gun stays within this many triangles.
REDUCED_BUDGET = 15000
REDUCED_FLOOR = 150                            # no mesh is cut below this many triangles
REDUCED_SHARE = 0.7                            # a mesh's share of the budget grows with its triangle count to this power
EMISSION_FLOOR = 2 / 255
WINDING_MIN = 0.9


def unity(v):
    return TO_UNITY @ np.array(v, np.float64)


def vec(v):
    return {"x": round(float(v[0]), 6), "y": round(float(v[1]), 6), "z": round(float(v[2]), 6)}


def sha256(path):
    return hashlib.sha256(Path(path).read_bytes()).hexdigest()


def build_mesh(parts, pivot):
    """per-corner (positions, normals, uvs) of one part's meshes, Blender frame
    -> (positions in Unity, relative to the pivot / normals / uvs / triangle indices), deduplicated, winding reversed"""
    pos, nrm, uv = (np.concatenate([p[i] for p in parts]) for i in range(3))
    pos, nrm = pos @ TO_UNITY.T - pivot, nrm @ TO_UNITY.T
    if not (np.isfinite(pos).all() and np.isfinite(nrm).all() and np.isfinite(uv).all()):
        raise RuntimeError("non-finite vertex data")
    key = np.concatenate([np.rint(pos * 1e6), np.rint(nrm * 1e4), np.rint(uv * 1e5)], axis=1).astype(np.int64)
    _, first, inverse = np.unique(key, axis=0, return_index=True, return_inverse=True)
    order = np.argsort(first)
    rank = np.empty(len(order), np.int64)
    rank[order] = np.arange(len(order))
    keep = first[order]
    index = rank[inverse.reshape(-1)].reshape(-1, 3)[:, [0, 2, 1]].reshape(-1)
    return pos[keep], nrm[keep], uv[keep], index


def winding_fraction(v, n, index):
    t = index.reshape(-1, 3)
    cross = np.cross(v[t[:, 1]] - v[t[:, 0]], v[t[:, 2]] - v[t[:, 0]])
    area = np.linalg.norm(cross, axis=1)
    avg = n[t[:, 0]] + n[t[:, 1]] + n[t[:, 2]]
    return float(area[(cross * avg).sum(1) > 0].sum() / area.sum())


def write_mesh(path, v, n, uv, index):
    path.parent.mkdir(parents=True, exist_ok=True)
    vertices = np.concatenate([v, n, uv], axis=1).astype("<f4")
    with open(path, "wb") as f:
        f.write(struct.pack("<ii", len(v), len(index)))
        f.write(vertices.tobytes())
        f.write(index.astype("<i4").tobytes())


def surface_color(obj, surface):
    node = principled(obj)
    if surface == "glass":
        c = node.inputs["Base Color"].default_value
        return {"r": round(c[0], 6), "g": round(c[1], 6), "b": round(c[2], 6), "a": round(node.inputs["Alpha"].default_value, 6)}
    c = node.inputs["Emission Color"].default_value
    return {"r": round(c[0], 6), "g": round(c[1], 6), "b": round(c[2], 6), "a": 1.0}


def has_alpha(path):
    d = Path(path).read_bytes()[:26]
    if d[:8] != b"\x89PNG\r\n\x1a\n" or d[24] != 8 or d[25] not in (4, 6):
        raise RuntimeError(f"{path}: smoothness lives in the alpha channel; expected an 8-bit PNG with alpha")


def emission_used(path):
    img = bpy.data.images.load(str(path))
    try:
        img.colorspace_settings.name = "Non-Color"
        px = np.empty(len(img.pixels), np.float32)
        img.pixels.foreach_get(px)
        return float(px.reshape(-1, img.channels)[:, :3].max()) > EMISSION_FLOOR
    finally:
        bpy.data.images.remove(img)


def stage_texture_set(root, name, tex):
    if not re.fullmatch(r"[A-Za-z0-9_-]+", name):
        raise ValueError("Unsafe texture-set name")
    has_alpha(tex["MetallicSmoothness"])
    emission = emission_used(tex["Emission"])
    normal = "Normal" in tex
    folder = root / "Assets" / "Weapons" / name
    wanted = {"albedo.png": tex["BaseColor"], "metallic.png": tex["MetallicSmoothness"]}
    if emission:
        wanted["emission.png"] = tex["Emission"]
    if normal:
        wanted["normal.png"] = tex["Normal"]
    folder.mkdir(parents=True, exist_ok=True)
    for stale in folder.iterdir():
        if stale.name not in wanted and stale.name[:-5] not in wanted:
            stale.unlink()
    for target, source in wanted.items():
        dst = folder / target
        if target == "metallic.png":
            write_gear_metallic(source, dst)
        elif not dst.is_file() or sha256(dst) != sha256(source):
            shutil.copyfile(source, dst)
    return {"emission": emission, "normal": normal}


def write_gear_metallic(source, dst):
    # The vanilla gear shader reads metalness from R and smoothness from G; the source keeps smoothness in A.
    img = bpy.data.images.load(str(source))
    try:
        img.colorspace_settings.name = "Non-Color"
        w, h = img.size
        px = np.empty(w * h * 4, np.float32)
        img.pixels.foreach_get(px)
        px = px.reshape(-1, 4)
        px[:, 1] = px[:, 3]
        px[:, 2] = px[:, 0]
        out = bpy.data.images.new("gear_metallic", w, h, alpha=True, float_buffer=False)
        try:
            out.colorspace_settings.name = "Non-Color"
            out.pixels.foreach_set(px.ravel())
            tmp = dst.with_name(dst.name + ".tmp.png")
            out.filepath_raw = str(tmp)
            out.file_format = "PNG"
            out.save()
        finally:
            bpy.data.images.remove(out)
    finally:
        bpy.data.images.remove(img)
    if dst.is_file() and sha256(dst) == sha256(tmp):
        tmp.unlink()
    else:
        tmp.replace(dst)


def reduced_targets(counts, spent):
    """-> {mesh name: triangles to keep, or 0 to keep the mesh whole}: REDUCED_BUDGET less the `spent` on other surfaces is shared
    out by count ** REDUCED_SHARE; a mesh that would keep most of its triangles anyway stays whole and frees its share"""
    whole, budget = {}, REDUCED_BUDGET - spent
    while True:
        rest = {k: n for k, n in counts.items() if k not in whole}
        if not rest:
            return whole
        weight = {k: n ** REDUCED_SHARE for k, n in rest.items()}
        share = {k: max(REDUCED_FLOOR, budget * weight[k] / sum(weight.values())) for k in rest}
        small = [k for k, n in rest.items() if share[k] >= 0.8 * n]
        if not small:
            return {**whole, **{k: int(share[k]) for k in rest}}
        for k in small:
            whole[k] = 0
            budget -= rest[k]


def stage_project(root):
    reserve(root, "project")
    # Verify the existing input setting before touching any generated output.
    settings = root / "ProjectSettings" / "ProjectSettings.asset"
    if settings.is_file() and len(re.findall(r"(?m)^\s*VertexChannelCompressionMask:\s*\d+", settings.read_text(encoding="utf-8"))) != 1:
        raise RuntimeError("Cannot locate Unity's vertex-channel compression setting")
    for rel in ("Assets/Generated", "Geometry"):
        clear(root, rel, "project")
    (root / "ProjectSettings").mkdir(parents=True, exist_ok=True)
    (root / "Packages").mkdir(parents=True, exist_ok=True)
    (root / "Assets" / "Editor").mkdir(parents=True, exist_ok=True)
    (root / "ProjectSettings" / "ProjectVersion.txt").write_text(UNITY_VERSION, encoding="utf-8")
    packages = {"dependencies": {f"com.unity.modules.{name}": "1.0.0" for name in UNITY_MODULES}}
    (root / "Packages" / "manifest.json").write_text(json.dumps(packages, indent=2) + "\n", encoding="utf-8")
    settings = root / "ProjectSettings" / "ProjectSettings.asset"
    if settings.is_file():
        original = settings.read_text(encoding="utf-8")
        # On unreadable meshes the global mask would silently turn precise UVs into Float16.
        updated, count = re.subn(r"(?m)^(\s*VertexChannelCompressionMask:)\s*\d+", r"\g<1> 0", original)
        if count != 1:
            raise RuntimeError("Cannot locate Unity's vertex-channel compression setting")
        if updated != original:
            settings.write_text(updated, encoding="utf-8")
    for name in ("BuildWeaponBundle.cs", "BuildDiscBundle.cs"):
        shutil.copyfile(TOOLS / name, root / "Assets" / "Editor" / name)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--source", required=True)
    ap.add_argument("--out", required=True)
    opt = ap.parse_args(args())
    src = Path(opt.source).resolve()
    destination = reserve(opt.out, "project", src)
    for required in (TOOLS / "BuildWeaponBundle.cs", TOOLS / "BuildDiscBundle.cs"):
        if not required.is_file():
            raise FileNotFoundError(required)
    # Build and validate EVERY input in a temporary staging project. The previous
    # project's generated folders survive a bad mesh, texture, sight or manifest.
    staging = Path(tempfile.mkdtemp(prefix="forge-energy-staging-"))
    try:
        export(src, reserve(staging / "project", "project", src), destination)
    finally:
        shutil.rmtree(staging, ignore_errors=True)


def export(src, root, destination):
    stage_project(root)
    weapons, sets = [], {}
    for mode in MODES:
        a = assemble(src, mode)
        sight_manifest = json.loads((src / "library/sight" / a.sight / "manifest.json").read_text(encoding="utf-8"))
        check_sight_window(mode, a.sight_look, sight_manifest["windowMm"],
                           [o for p in a.parts if p["surface"] == "body" for o in p["objects"]],
                           next(p["objects"] for p in a.parts if p["name"] == "Sight"))
        deps = bpy.context.evaluated_depsgraph_get()
        lo, hi, records = np.full(3, np.inf), np.full(3, -np.inf), []
        full = {o.name: corners(o, deps) for p in a.parts for o in p["objects"]}
        targets = reduced_targets({o.name: len(full[o.name][0]) // 3 for p in a.parts if p["surface"] == "body" for o in p["objects"]},
                                  sum(len(full[o.name][0]) // 3 for p in a.parts if p["surface"] != "body" for o in p["objects"]))
        for p in a.parts:
            if not re.fullmatch(r"[A-Za-z0-9_-]+", p["name"]):
                raise ValueError("Unsafe mesh part name")
            pivot = np.round(unity(p["pivot"]), 6)
            v, n, uv, index = build_mesh([full[o.name] for o in p["objects"]], pivot)
            record = {"name": p["name"], "surface": p["surface"], "textureSet": p["textureSet"], "pivot": vec(pivot)}
            if p["surface"] == "body":
                frac = winding_fraction(v, n, index)
                if frac < WINDING_MIN:
                    raise RuntimeError(f"{mode}/{p['name']}: only {frac:.3f} of the area is wound with the normals (Unity front face)")
                print(f"WINDING {mode}/{p['name']} {frac:.4f}")
                if any(targets[o.name] for o in p["objects"]):
                    rv, rn, ruv, rindex = build_mesh([low_corners(o, deps, targets[o.name]) if targets[o.name] else full[o.name] for o in p["objects"]], pivot)
                    frac = winding_fraction(rv, rn, rindex)
                    if frac < WINDING_MIN:
                        raise RuntimeError(f"{mode}/{p['name']}: only {frac:.3f} of the reduced area is wound with the normals (Unity front face)")
                    reduced_path = root / "Geometry" / mode / f"{p['name']}.reduced.meshbin"
                    write_mesh(reduced_path, rv, rn, ruv, rindex)
                    record.update(reducedMesh=f"{mode}/{p['name']}.reduced.meshbin", reducedSha256=sha256(reduced_path), reducedTriangles=len(rindex) // 3)
            mesh_path = root / "Geometry" / mode / f"{p['name']}.meshbin"
            write_mesh(mesh_path, v, n, uv, index)
            lo, hi = np.minimum(lo, (v + pivot).min(0)), np.maximum(hi, (v + pivot).max(0))
            if p["surface"] == "body":
                if p["textureSet"] not in sets:
                    sets[p["textureSet"]] = stage_texture_set(root, p["textureSet"], p["textures"])
                color = {"r": 0, "g": 0, "b": 0, "a": 0}
            else:
                color = surface_color(p["objects"][0], p["surface"])
            records.append({**record, "color": color, "mesh": f"{mode}/{p['name']}.meshbin", "sha256": sha256(mesh_path), "triangles": len(index) // 3})
        length = float(hi[2] - lo[2])
        if not LENGTH[0] <= length <= LENGTH[1]:
            raise RuntimeError(f"{mode}: length {length:.3f} m is outside {LENGTH}")
        weapons.append({"mode": mode, "sight": a.sight, "rail": a.rail, "length": round(length, 6), "rightHand": vec((0, 0, 0)),
                        "leftHand": vec(unity(a.left_hand)), "muzzle": vec(unity(a.muzzle)), "sightLook": vec(unity(a.sight_look)),
                        "boundsMin": vec(lo), "boundsMax": vec(hi), "parts": records})
        print(f"EXPORTED {mode} length {length:.3f} " + " ".join(f"{r['name']}={r['triangles']}/{r.get('reducedTriangles', r['triangles'])}" for r in records))
        print(f"TRIANGLES {mode} first person {sum(r['triangles'] for r in records)} other views {sum(r.get('reducedTriangles', r['triangles']) for r in records)}")
    used = set(sets)
    for folder in (root / "Assets" / "Weapons").iterdir():
        if folder.is_dir() and folder.name not in used:
            clear(root, folder.relative_to(root), "project")
    manifest = {"format": 4, "weapons": weapons, "textureSets": [{"name": k, **sets[k]} for k in sorted(sets)]}
    (root / "Geometry" / "manifest.json").write_text(json.dumps(manifest, indent=1, allow_nan=False), encoding="utf-8")
    stage_project(destination)
    clear(destination, "Assets/Weapons", "project")
    for rel in ("Geometry", "Assets/Weapons"):
        shutil.copytree(root / rel, destination / rel)
    print(f"STAGED {len(weapons)} weapons, {len(sets)} texture sets -> {destination}")


main()
