"""Render the eight loadout icons: the assembled gun (sight and rail included, real textures and emission), transparent 512x512 PNG.

blender --background --factory-startup --python-exit-code 1 --python render_icons_blender.py -- --source <Rework folder> --out <folder> [--samples N]
One camera rule for every gun, as the vanilla icons: orthographic, square on to the gun's left side, level, muzzle on the image's left.
Vanilla icon sprites are 512x512 and the widest frame the game requests is 380x180, so the gun is centred inside that middle band
(90 % of it): square frames show the whole icon and wide frames crop the empty top and bottom.
"""
import argparse
import sys
from pathlib import Path

import bpy
import numpy as np
from mathutils import Vector

sys.path.insert(0, str(Path(__file__).resolve().parent))
from parts_assembly import MODES, args, assemble

WIDTH, HEIGHT, FILL, BAND = 512, 512, 0.9, 180 / 380
VIEW = Vector((1, 0, 0))      # target -> camera; the gun's left side is +X, its muzzle -Y (image left)
MIN_COVERAGE, EDGE = 0.06, 4


def vertices(objs):
    pts = []
    for o in objs:
        co = np.empty(len(o.data.vertices) * 3, np.float32)
        o.data.vertices.foreach_get("co", co)
        m = np.array(o.matrix_world, np.float64)
        pts.append(co.reshape(-1, 3).astype(np.float64) @ m[:3, :3].T + m[:3, 3])
    return np.concatenate(pts)


def light(scene, name, position, target, energy, size):
    data = bpy.data.lights.new(name, "AREA")
    data.energy, data.size = energy, size
    obj = bpy.data.objects.new(name, data)
    scene.collection.objects.link(obj)
    obj.location = position
    obj.rotation_euler = (target - position).to_track_quat("-Z", "Y").to_euler()


def set_up(scene, pts, samples):
    forward = -VIEW
    right = forward.cross(Vector((0, 0, 1))).normalized()
    up = right.cross(forward).normalized()
    xs, ys = pts @ np.array(right), pts @ np.array(up)
    width, height = xs.max() - xs.min(), ys.max() - ys.min()
    target = right * float((xs.max() + xs.min()) / 2) + up * float((ys.max() + ys.min()) / 2) + forward * float((pts @ np.array(forward)).mean())
    cam = bpy.data.cameras.new("camera")
    cam.type, cam.ortho_scale, cam.clip_start, cam.clip_end = "ORTHO", max(width / FILL, height / (FILL * BAND)), 0.1, 20
    obj = bpy.data.objects.new("camera", cam)
    scene.collection.objects.link(obj)
    obj.location = target - forward * 5
    obj.rotation_euler = forward.to_track_quat("-Z", "Y").to_euler()
    scene.camera = obj
    light(scene, "key", target - forward * 2.5 + up * 2.0 - right * 1.5, target, 420, 2.0)
    light(scene, "fill", target - forward * 2.5 - up * 0.3 + right * 2.0, target, 200, 2.0)
    light(scene, "rim", target + forward * 2.0 + up * 1.5 - right * 0.5, target, 260, 1.5)
    world = bpy.data.worlds.new("studio")
    if world.node_tree is None:
        world.use_nodes = True
    bg = next(n for n in world.node_tree.nodes if n.type == "BACKGROUND")
    bg.inputs["Color"].default_value, bg.inputs["Strength"].default_value = (0.8, 0.8, 0.8, 1.0), 1.0
    scene.world = world
    r = scene.render
    r.engine, r.resolution_x, r.resolution_y, r.resolution_percentage, r.film_transparent = "CYCLES", WIDTH, HEIGHT, 100, True
    r.image_settings.file_format, r.image_settings.color_mode, r.image_settings.color_depth = "PNG", "RGBA", "8"
    r.filter_size = 1.2
    scene.cycles.device, scene.cycles.samples, scene.cycles.use_denoising = "CPU", samples, True
    scene.view_settings.view_transform = "Standard"


def check(path, mode):
    img = bpy.data.images.load(str(path))
    try:
        if tuple(img.size) != (WIDTH, HEIGHT):
            raise RuntimeError(f"{mode}: icon is {tuple(img.size)}")
        a = np.empty(WIDTH * HEIGHT * 4, np.float32)
        img.pixels.foreach_get(a)
        alpha = a.reshape(HEIGHT, WIDTH, 4)[..., 3]
    finally:
        bpy.data.images.remove(img)
    solid = alpha > 0.5
    coverage = float(solid.mean())
    cols, rows = np.nonzero(solid.any(0))[0], np.nonzero(solid.any(1))[0]
    if coverage < MIN_COVERAGE:
        raise RuntimeError(f"{mode}: the gun covers only {coverage:.3f} of the icon")
    top = round(HEIGHT * (1 - BAND) / 2)
    if cols[0] < EDGE or cols[-1] >= WIDTH - EDGE or rows[0] < top or rows[-1] >= HEIGHT - top:
        raise RuntimeError(f"{mode}: the gun leaves the 380x180 band (columns {cols[0]}-{cols[-1]}, rows {rows[0]}-{rows[-1]})")
    print(f"ICON {mode} coverage {coverage:.3f} columns {cols[0]}-{cols[-1]} rows {rows[0]}-{rows[-1]}")


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--source", required=True)
    ap.add_argument("--out", required=True)
    ap.add_argument("--samples", type=int, default=128)
    opt = ap.parse_args(args())
    src, out = Path(opt.source).resolve(), Path(opt.out).resolve()
    out.mkdir(parents=True, exist_ok=True)
    for mode in MODES:
        a = assemble(src, mode)
        scene = bpy.context.scene
        set_up(scene, vertices([o for p in a.parts for o in p["objects"]]), opt.samples)
        scene.render.image_settings.compression = 100
        path = out / f"{mode}.png"
        scene.render.filepath = str(path)
        bpy.ops.render.render(write_still=True)
        check(path, mode)


main()
