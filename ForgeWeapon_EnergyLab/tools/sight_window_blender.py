"""Shared 5x5 sight-aperture and central full-gun line-of-sight checks (Blender metres)."""
import numpy as np
import bpy
from mathutils import Vector
from mathutils.bvhtree import BVHTree


def check_sight_window(mode, look, window_mm, opaque, sight):
    eye = look + Vector((0, .08, 0))
    trees = [BVHTree.FromObject(o, bpy.context.evaluated_depsgraph_get()) for o in opaque]
    front = min((o.matrix_world @ v.co).y for o in sight for v in o.data.vertices)
    for x in np.linspace(-.3, .3, 5) * window_mm[0] / 1000:
        for z in np.linspace(-.3, .3, 5) * window_mm[1] / 1000:
            target = look + Vector((float(x), -.03, float(z)))
            direction = (target - eye).normalized()
            # Test through the housing. An off-axis ray can legitimately meet the barrel
            # after leaving the window; the full-gun central ray below checks the bore line.
            distance = (eye.y - front + .001) / -direction.y
            for obj, tree in zip(opaque, trees):
                inverse = obj.matrix_world.inverted()
                if tree.ray_cast(inverse @ eye, inverse.to_3x3() @ direction, distance)[0] is not None:
                    raise RuntimeError(f"{mode}: sight-window ray ({x},{z}) blocked by {obj.name}")
    for obj, tree in zip(opaque, trees):
        inverse = obj.matrix_world.inverted()
        if tree.ray_cast(inverse @ eye, inverse.to_3x3() @ Vector((0, -1, 0)), 2)[0] is not None:
            raise RuntimeError(f"{mode}: central sight line blocked by {obj.name}")
    print(f"ENERGY_SIGHT_WINDOW {mode} 5x5 25/25 clear")
