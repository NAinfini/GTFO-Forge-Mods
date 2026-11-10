"""Exports one .blend into the stage format OrdnanceBundleBuild.cs reads.

Run inside Blender:
  blender --background --factory-startup <file.blend> --python export_blender.py -- --prefab drop-pod --out <stage>/models/drop-pod
       [--require body,door_1,...] [--max-texture 2048] [--max-albedo 2048] [--glow M01_mat=1,0.6,0.12] [--double-sided]

Every mesh and empty object becomes one node of the prefab, keeping its name, its local transform and its parent (a top-level one is a child of
the prefab; names are unique in the file). Coordinates
change from Blender (right handed, Z up) to Unity (left handed, Y up) by swapping Y and Z; triangle winding is reversed to
match. --double-sided adds an inner copy of every triangle (reversed winding, flipped normal) for shells seen from inside.
Each mesh is split into one submesh per material slot. A material whose Principled BSDF reads image textures writes
albedo / metallic (R metallic, G smoothness, which the game's gear shader reads, A the same smoothness for Unity's Standard layout) / normal PNGs; a material without textures is recorded as flat
values the Unity step turns into small textures. An image feeding Emission Color is written as the emission map (the
surface glows where and in the colour it shows, scaled by Emission Strength). --glow marks the bright yellow-amber texels of a material's albedo (hue
40-65 degrees, saturation and value above 0.55) as an emission mask that glows in the given linear colour; without it a
material glows only through its own Emission Strength. Output: manifest.json, <object>.mesh, <surface>-<kind>.png.
"""
import argparse
import json
import os
import struct
import sys

import bpy
import numpy as np
from mathutils import Matrix


def arguments():
    argv = sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else []
    parser = argparse.ArgumentParser()
    parser.add_argument("--prefab", required=True)
    parser.add_argument("--out", required=True)
    parser.add_argument("--require", default="")
    parser.add_argument("--max-texture", type=int, default=2048)
    parser.add_argument("--max-albedo", type=int)
    parser.add_argument("--glow", action="append", default=[])
    parser.add_argument("--double-sided", action="store_true")
    args = parser.parse_args(argv)
    if args.max_texture < 1 or (args.max_albedo is not None and args.max_albedo < 1):
        raise SystemExit("Texture limits must be positive.")
    if args.max_albedo is None:
        args.max_albedo = args.max_texture
    glow = {}
    for entry in args.glow:
        name, _, colour = entry.partition("=")
        values = [float(v) for v in colour.split(",")]
        if not name or len(values) != 3:
            raise SystemExit("--glow takes material=r,g,b, got " + entry)
        glow[name] = values
    args.glow = glow
    return args


def unity_vector(v):
    return [float(v[0]), float(v[2]), float(v[1])]


def unity_quaternion(q):
    # Conjugating a rotation by the Y/Z swap (a reflection) keeps the angle and maps the axis to -(x, z, y).
    return [float(-q.x), float(-q.z), float(-q.y), float(q.w)]  # Unity order x, y, z, w


def upstream_image(socket, seen=None):
    """The image texture node feeding a socket, through any chain of nodes."""
    if seen is None:
        seen = set()
    for link in socket.links:
        node = link.from_node
        if node.name in seen:
            continue
        seen.add(node.name)
        if node.type == "TEX_IMAGE" and node.image is not None:
            return node.image
        for upstream in node.inputs:
            found = upstream_image(upstream, seen)
            if found is not None:
                return found
    return None


def pixels(image):
    width, height = image.size
    data = np.empty(width * height * 4, dtype=np.float32)
    image.pixels.foreach_get(data)
    return data.reshape(height, width, 4), width, height


def save_png(name, array, width, height, path, colour, limit):
    image = bpy.data.images.new(name, width, height, alpha=True)
    image.colorspace_settings.name = "sRGB" if colour else "Non-Color"
    image.pixels.foreach_set(np.ascontiguousarray(array, dtype=np.float32).ravel())
    if max(width, height) > limit:
        scale = limit / max(width, height)
        image.scale(max(1, int(width * scale)), max(1, int(height * scale)))
    image.filepath_raw = path
    image.file_format = "PNG"
    image.save()
    size = list(image.size)
    bpy.data.images.remove(image)
    return size


def amber_mask(data):
    """1 where a texel is bright yellow-amber, else 0 (RGBA float array, linear values from Blender)."""
    rgb = np.clip(data[..., :3], 0.0, 1.0) ** (1.0 / 2.2)
    top = rgb.max(axis=-1)
    low = rgb.min(axis=-1)
    span = np.maximum(top - low, 1e-6)
    r, g, b = rgb[..., 0], rgb[..., 1], rgb[..., 2]
    hue = np.where(top == r, ((g - b) / span) % 6.0, np.where(top == g, (b - r) / span + 2.0, (r - g) / span + 4.0)) / 6.0
    saturation = np.where(top > 0, span / np.maximum(top, 1e-6), 0.0)
    return ((hue >= 40.0 / 360.0) & (hue <= 65.0 / 360.0) & (saturation > 0.55) & (top > 0.55)).astype(np.float32)


def surface_record(material, out, prefab, texture_limit, albedo_limit, glow=None):
    record = {"name": material.name, "albedo": "", "metallic": "", "normal": "", "color": [1, 1, 1, 1],
              "metallicValue": 0.0, "smoothness": 0.5, "emission": [0, 0, 0]}
    bsdf = next((n for n in material.node_tree.nodes if n.type == "BSDF_PRINCIPLED"), None) if material.node_tree else None
    if bsdf is None:
        raise SystemExit("Material " + material.name + " has no Principled BSDF.")
    base = upstream_image(bsdf.inputs["Base Color"])
    metal_image = upstream_image(bsdf.inputs["Metallic"]) or upstream_image(bsdf.inputs["Roughness"])
    normal = upstream_image(bsdf.inputs["Normal"])
    if base is not None:
        data, w, h = pixels(base)
        file = prefab + "-" + material.name + "-albedo.png"
        record["albedoSize"] = save_png("albedo", data, w, h, os.path.join(out, file), True, albedo_limit)
        record["albedo"] = file
        if glow is not None:
            mask = amber_mask(data)
            if mask.sum() < 16:
                raise SystemExit("--glow found no yellow-amber texels in " + material.name)
            emission = np.ones_like(data)
            emission[..., 0] = emission[..., 1] = emission[..., 2] = mask
            save_png("emission", emission, w, h, os.path.join(out, prefab + "-" + material.name + "-emission.png"), True, texture_limit)
            record["emission"] = list(glow)
            record["emissionTexels"] = int(mask.sum())
    elif glow is not None:
        raise SystemExit("--glow needs an albedo texture on " + material.name)
    else:
        record["color"] = [float(c) for c in bsdf.inputs["Base Color"].default_value]
    if metal_image is not None:
        data, w, h = pixels(metal_image)
        # glTF metallic-roughness: G roughness, B metallic. Cell/Player/CustomGearShader's _MetallicGlossMap ("Metalness (R),
        # Smoothness (G)"): R metallic, G smoothness; A carries the smoothness too.
        packed = np.zeros_like(data)
        packed[..., 0] = data[..., 2]
        packed[..., 1] = 1.0 - data[..., 1]
        packed[..., 3] = packed[..., 1]
        file = prefab + "-" + material.name + "-metallic.png"
        record["metallicSize"] = save_png("metallic", packed, w, h, os.path.join(out, file), False, texture_limit)
        record["metallic"] = file
    else:
        record["metallicValue"] = float(bsdf.inputs["Metallic"].default_value)
        record["smoothness"] = 1.0 - float(bsdf.inputs["Roughness"].default_value)
    if normal is not None:
        data, w, h = pixels(normal)
        file = prefab + "-" + material.name + "-normal.png"
        record["normalSize"] = save_png("normal", data, w, h, os.path.join(out, file), False, texture_limit)
        record["normal"] = file
    strength_input = bsdf.inputs.get("Emission Strength")
    colour_input = bsdf.inputs.get("Emission Color") or bsdf.inputs.get("Emission")
    strength = float(strength_input.default_value) if strength_input is not None else 0.0
    emission_image = upstream_image(colour_input) if colour_input is not None else None
    if glow is None and emission_image is not None and strength > 0:
        # An emission texture says where and in which colour the surface glows; Emission Strength scales it.
        data, w, h = pixels(emission_image)
        if float(data[..., :3].max()) <= 0.0:
            raise SystemExit("The emission texture of " + material.name + " is black.")
        save_png("emission", data, w, h, os.path.join(out, prefab + "-" + material.name + "-emission.png"), True, texture_limit)
        record["emission"] = [strength, strength, strength]
    elif glow is None and colour_input is not None and strength > 0:
        record["emission"] = [float(c) * strength for c in list(colour_input.default_value)[:3]]
    return record


def surface_textured(material):
    bsdf = next((n for n in material.node_tree.nodes if n.type == "BSDF_PRINCIPLED"), None) if material and material.node_tree else None
    return bsdf is not None and any(upstream_image(bsdf.inputs[k]) is not None for k in ("Base Color", "Metallic", "Roughness", "Normal"))


def export_mesh(obj, path, double_sided=False):
    depsgraph = bpy.context.evaluated_depsgraph_get()
    evaluated = obj.evaluated_get(depsgraph)
    mesh = evaluated.to_mesh()
    try:
        mesh.calc_loop_triangles()
        count = len(mesh.loop_triangles)
        if count == 0:
            raise SystemExit("Mesh " + obj.name + " has no triangles.")
        corners = np.empty(count * 3, dtype=np.int32)
        mesh.loop_triangles.foreach_get("loops", corners)
        materials = np.empty(count, dtype=np.int32)
        mesh.loop_triangles.foreach_get("material_index", materials)
        normals = np.empty(count * 9, dtype=np.float32)
        mesh.loop_triangles.foreach_get("split_normals", normals)
        vertex_of_loop = np.empty(len(mesh.loops), dtype=np.int32)
        mesh.loops.foreach_get("vertex_index", vertex_of_loop)
        positions = np.empty(len(mesh.vertices) * 3, dtype=np.float32)
        mesh.vertices.foreach_get("co", positions)
        positions = positions.reshape(-1, 3)
        uvs = np.zeros(len(mesh.loops) * 2, dtype=np.float32)
        if mesh.uv_layers.active is not None:
            mesh.uv_layers.active.data.foreach_get("uv", uvs)
        elif any(surface_textured(slot.material) for slot in obj.material_slots):
            raise SystemExit("Mesh " + obj.name + " has a textured material but no UV map.")
        uvs = uvs.reshape(-1, 2)

        p = positions[vertex_of_loop[corners]]
        n = normals.reshape(-1, 3)
        uv = uvs[corners]
        # Blender (x, y, z) -> Unity (x, z, y).
        p = p[:, [0, 2, 1]]
        n = n[:, [0, 2, 1]]
        rows = np.concatenate([p, n, uv], axis=1)
        if double_sided:
            # The game's gear shader culls back faces: a shell seen from inside needs real inner faces, lit from inside.
            back = rows.copy()
            back[:, 3:6] *= -1.0
            rows = np.concatenate([rows, back], axis=0)
            materials = np.concatenate([materials, materials])
        unique, inverse = np.unique(np.round(rows, 6), axis=0, return_inverse=True)
        inverse = inverse.reshape(-1, 3)
        # Reversed winding for the mirrored space; the inner copies keep Blender's order, which faces the other way.
        inverse[:count] = inverse[:count][:, [0, 2, 1]]
        slots = max(1, len(obj.material_slots))
        submeshes = [inverse[materials == s].ravel().astype(np.int32) for s in range(slots)]
        with open(path, "wb") as f:
            f.write(struct.pack("<ii", len(unique), slots))
            for indices in submeshes:
                f.write(struct.pack("<i", len(indices)))
            f.write(unique.astype(np.float32).tobytes())
            for indices in submeshes:
                f.write(indices.tobytes())
        return int(len(inverse)), [int(len(s) // 3) for s in submeshes]
    finally:
        evaluated.to_mesh_clear()


def main():
    args = arguments()
    out = os.path.abspath(args.out)
    os.makedirs(out, exist_ok=True)
    objects, surfaces = [], {}
    for obj in sorted(bpy.context.scene.objects, key=lambda o: o.name):
        if obj.type not in ("MESH", "EMPTY"):
            continue
        if obj.parent is not None and obj.matrix_parent_inverse != Matrix.Identity(4):
            raise SystemExit("Object " + obj.name + " has a parent inverse; the stage format keeps plain local transforms.")
        entry = {"name": obj.name, "parent": obj.parent.name if obj.parent is not None else "", "position": unity_vector(obj.location),
                 "rotation": unity_quaternion(obj.matrix_basis.to_quaternion()), "scale": unity_vector(obj.scale)}
        if obj.type == "MESH":
            file = obj.name + ".mesh"
            triangles, per_slot = export_mesh(obj, os.path.join(out, file), args.double_sided)
            names = []
            for slot in obj.material_slots:
                if slot.material is None:
                    raise SystemExit("Object " + obj.name + " has an empty material slot.")
                names.append(slot.material.name)
                if slot.material.name not in surfaces:
                    surfaces[slot.material.name] = surface_record(slot.material, out, args.prefab, args.max_texture,
                                                                  args.max_albedo, args.glow.get(slot.material.name))
            if not names:
                raise SystemExit("Object " + obj.name + " has no material.")
            entry.update({"mesh": file, "triangles": triangles, "submeshTriangles": per_slot, "surfaces": names})
        objects.append(entry)
    unknown = [name for name in args.glow if name not in surfaces]
    if unknown:
        raise SystemExit("--glow names materials no exported object uses: " + ", ".join(unknown))
    missing = [name for name in args.require.split(",") if name and name not in {o["name"] for o in objects}]
    if missing:
        raise SystemExit("Missing objects: " + ", ".join(missing))
    manifest = {"format": 1, "prefab": args.prefab, "source": bpy.data.filepath, "objects": objects, "surfaces": list(surfaces.values())}
    with open(os.path.join(out, "manifest.json"), "w", encoding="utf-8") as f:
        json.dump(manifest, f, indent=1)
    print("ORDNANCE_EXPORT", args.prefab, len(objects), "objects", sum(o.get("triangles", 0) for o in objects), "triangles")


main()
