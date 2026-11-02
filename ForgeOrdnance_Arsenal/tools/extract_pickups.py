# Reads the game's own supply pickups out of its asset files (UnityPy) into --out as <kind>.obj + <kind>.json + the albedo pngs, for
# tools/render_icon.py's supply layout. Kinds follow the item data blocks Arsenal pods deliver: ammo (101), medi (102), tool (127),
# disinfect (132), cfoam (115). The files are local working copies of game assets: they stay out of Git and out of every package.
# Run by build_icons.ps1 with PYTHONPATH set to a folder holding UnityPy:  python extract_pickups.py --game <GTFO_Data> --out <dir>
#
# Every active mesh renderer under the pickup prefab is taken with its whole transform chain and every sub-mesh with its own material
# (one `usemtl` group and one albedo png each; <kind>.json lists the pngs in group order). Inactive objects (the collision proxy) are skipped.
# The OBJ is in Blender axes (Unity x, z, y), centred on the pickup. One rule stands every item the same way up: the prefab's LeftHand
# and RightHand marks are where the game's hands take the pack, which is its handle side, so a pickup whose hands lie below its middle is
# turned over (the tool pack's prefab is authored upside down).
import argparse
import json
import os

import numpy as np
import UnityPy
from UnityPy.helpers.MeshHelper import MeshHandler

args = argparse.ArgumentParser()
args.add_argument("--game", required=True)
args.add_argument("--out", required=True)
a = args.parse_args()
root, out = a.game, a.out
os.makedirs(out, exist_ok=True)


def quat_matrix(q):
    x, y, z, w = q
    return np.array([[1 - 2 * (y * y + z * z), 2 * (x * y - z * w), 2 * (x * z + y * w)],
                     [2 * (x * y + z * w), 1 - 2 * (x * x + z * z), 2 * (y * z - x * w)],
                     [2 * (x * z - y * w), 2 * (y * z + x * w), 1 - 2 * (x * x + y * y)]])


def local_matrix(t):
    m = np.eye(4)
    p, r, s = t.m_LocalPosition, t.m_LocalRotation, t.m_LocalScale
    m[:3, :3] = quat_matrix((r.x, r.y, r.z, r.w)) * np.array([s.x, s.y, s.z])
    m[:3, 3] = [p.x, p.y, p.z]
    return m


def collect(go, parent, renderers, hands):
    transform = mesh_filter = renderer = None
    for c in go.m_Components:
        try:
            cc = c.component.read() if hasattr(c, "component") else c.read()
        except Exception:
            continue  # scripts the game's types do not describe
        kind = cc.object_reader.type.name
        if kind == "Transform":
            transform = cc
        elif kind == "MeshFilter":
            mesh_filter = cc
        elif kind == "MeshRenderer":
            renderer = cc
    world = parent @ local_matrix(transform)
    if go.m_Name in ("LeftHand", "RightHand"):
        hands.append(world[:3, 3])
    if go.m_IsActive and mesh_filter is not None and renderer is not None and mesh_filter.m_Mesh.path_id:
        renderers.append((mesh_filter.m_Mesh.read(), renderer.m_Materials, world))
    for child in transform.m_Children:
        collect(child.read().m_GameObject.read(), world, renderers, hands)


def albedo(material_ptr):
    """The material's _MainTex texture, or None for a material without one."""
    try:
        material = material_ptr.read()
        for key, env in material.m_SavedProperties.m_TexEnvs:
            if key == "_MainTex" and env.m_Texture.path_id:
                return env.m_Texture.read()
    except Exception:
        pass
    return None


def export(name, asset, path_id):
    env = UnityPy.load(os.path.join(root, asset))
    prefab = next(o for o in env.objects if o.path_id == path_id).read()
    renderers, hands = [], []
    collect(prefab, np.eye(4), renderers, hands)
    groups = []  # (png name, vertices in Unity axes, uvs, triangles)
    for mesh, materials, world in renderers:
        mesh_name = mesh.m_Name
        handler = MeshHandler(mesh)
        handler.process()
        verts = np.array(handler.m_Vertices, dtype=float).reshape(handler.m_VertexCount, -1)[:, :3]
        uvs = np.array(handler.m_UV0, dtype=float).reshape(len(verts), -1)[:, :2]
        placed = (world[:3, :3] @ verts.T).T + world[:3, 3]
        for index, triangles in enumerate(handler.get_triangles()):
            texture = albedo(materials[index]) if index < len(materials) else None
            if texture is None:
                continue
            png = f"{name}_{mesh_name}_{index}_{texture.m_Name}.png".replace(" ", "_")
            texture.image.convert("RGBA").save(os.path.join(out, png))
            groups.append((png, placed, uvs, np.array(list(triangles), dtype=int).reshape(-1, 3)))
    everything = np.concatenate([g[1] for g in groups])
    centre = (everything.min(0) + everything.max(0)) / 2
    flip = bool(hands and np.mean([h[1] for h in hands]) < centre[1])  # hands below the middle: turn the pickup over (Unity y is up)
    lines, offset = [], 1
    for index, (png, placed, uvs, triangles) in enumerate(groups):
        points = placed - centre
        if flip:
            points = points * np.array([-1.0, -1.0, 1.0])  # half turn about Unity z: y up becomes y down
        lines.append(f"usemtl {index}")
        lines += ["v %.6f %.6f %.6f" % (p[0], p[2], p[1]) for p in points]
        lines += ["vt %.6f %.6f" % tuple(t) for t in uvs]
        lines += [f"f {i + offset}/{i + offset} {j + offset}/{j + offset} {k + offset}/{k + offset}" for i, j, k in triangles]
        offset += len(points)
    with open(os.path.join(out, name + ".obj"), "w") as handle:
        handle.write("\n".join(lines))
    with open(os.path.join(out, name + ".json"), "w") as handle:
        json.dump({"textures": [g[0] for g in groups], "turnedOver": flip}, handle)
    print(name, "parts", len(groups), "turned over" if flip else "as authored")


export("medi", "sharedassets14.assets", 92)  # MediPackPickup
export("ammo", "sharedassets16.assets", 181)  # AmmoPackWeaponPickup
export("tool", "sharedassets16.assets", 138)  # AmmoPackToolPickup
export("disinfect", "sharedassets16.assets", 137)  # DisinfectonPackPickup
export("cfoam", "sharedassets7.assets", 120)  # Consumable_GlueGrenade_Pickup: grenade body, lug, pin, switch, trigger and the foam payload canister
