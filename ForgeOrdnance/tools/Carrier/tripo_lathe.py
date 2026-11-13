"""Inside Blender: a clean low-poly lathe of a body of revolution about Z whose source mesh is too broken for decimation, for lowpoly_pipeline.py --low.
blender -b --factory-startup --python tripo_lathe.py -- <high.glb> <low.fbx> <segments>"""
import math
import sys

import bpy
import numpy as np

high, low, segments = sys.argv[sys.argv.index("--") + 1:]
segments = int(segments)
for leftover in list(bpy.data.objects):
    bpy.data.objects.remove(leftover)
bpy.ops.import_scene.gltf(filepath=high)
points = []
for obj in bpy.context.scene.objects:
    if obj.type == "MESH":
        # Points all over the surface, not the vertices: long triangles leave heights with no vertex.
        obj.data.calc_loop_triangles()
        co = np.empty(len(obj.data.vertices) * 3)
        obj.data.vertices.foreach_get("co", co)
        matrix = np.array(obj.matrix_world)
        co = co.reshape(-1, 3) @ matrix[:3, :3].T + matrix[:3, 3]
        tris = np.empty(len(obj.data.loop_triangles) * 3, dtype=np.int32)
        obj.data.loop_triangles.foreach_get("vertices", tris)
        tris = co[tris.reshape(-1, 3)]
        area = np.linalg.norm(np.cross(tris[:, 1] - tris[:, 0], tris[:, 2] - tris[:, 0]), axis=1)
        rng = np.random.default_rng(1)
        chosen = tris[rng.choice(len(tris), 3000000, p=area / area.sum())]
        w = rng.random((3000000, 2))
        w[w.sum(1) > 1] = 1 - w[w.sum(1) > 1]
        points.append(chosen[:, 0] + w[:, :1] * (chosen[:, 1] - chosen[:, 0]) + w[:, 1:] * (chosen[:, 2] - chosen[:, 0]))
points = np.vstack(points)
centre = (points[:, :2].min(0) + points[:, :2].max(0)) / 2
radius = np.hypot(*(points[:, :2] - centre).T)
bottom, top = points[:, 2].min(), points[:, 2].max()
height = top - bottom
bins = 120
index = np.minimum(((points[:, 2] - bottom) / height * bins).astype(int), bins - 1)
outline = np.zeros(bins)
np.maximum.at(outline, index, radius)
for i in np.flatnonzero(outline == 0):
    outline[i] = outline[i - 1]
profile = [(0.0, bottom)] + [(outline[i], bottom + (i + 0.5) * height / bins) for i in range(bins)] + [(0.0, top)]


def simplify(line, tolerance):
    """Douglas-Peucker."""
    a, b = np.array(line[0]), np.array(line[-1])
    if len(line) < 3:
        return line
    d = b - a
    pts = np.array(line[1:-1])
    distance = np.abs(d[0] * (pts[:, 1] - a[1]) - d[1] * (pts[:, 0] - a[0])) / max(np.hypot(*d), 1e-9)
    worst = int(np.argmax(distance))
    if distance[worst] <= tolerance:
        return [line[0], line[-1]]
    return simplify(line[:worst + 2], tolerance)[:-1] + simplify(line[worst + 1:], tolerance)


rings = simplify(profile, height * 0.003)
lengths = np.cumsum([0.0] + [math.dist(rings[i], rings[i + 1]) for i in range(len(rings) - 1)])
verts, faces, uvs = [], [], []
for i, (r, z) in enumerate(rings):
    for s in range(segments + 1):
        angle = 2 * math.pi * (s % segments) / segments
        verts.append((centre[0] + r * math.cos(angle), centre[1] + r * math.sin(angle), z))
        uvs.append((0.01 + 0.98 * s / segments, 0.01 + 0.98 * lengths[i] / lengths[-1]))
for i in range(len(rings) - 1):
    for s in range(segments):
        a, b = i * (segments + 1) + s, (i + 1) * (segments + 1) + s
        if rings[i][0] > 0:
            faces.append((a, a + 1, b + 1))
        if rings[i + 1][0] > 0:
            faces.append((a, b + 1, b))
mesh = bpy.data.meshes.new("lathe")
mesh.from_pydata(verts, [], faces)
mesh.update()
layer = mesh.uv_layers.new(name="UVMap")
for loop in mesh.loops:
    layer.data[loop.index].uv = uvs[loop.vertex_index]
for obj in list(bpy.context.scene.objects):
    bpy.data.objects.remove(obj)
out = bpy.data.objects.new("lathe", mesh)
bpy.context.scene.collection.objects.link(out)
bmesh_faces = len(mesh.polygons)
out.select_set(True)
bpy.ops.export_scene.fbx(filepath=low, use_selection=True, mesh_smooth_type="FACE")
print("LATHE", len(rings), bmesh_faces)
