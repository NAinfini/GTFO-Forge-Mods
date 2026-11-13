#!/usr/bin/env python3
"""Turns the repaired high-poly models (Source/mods/ordnance-assets/fixed-1104) into the low-poly masters the Carrier and Arsenal
build_assets.ps1 export (Source/mods/ordnance-assets/masters/<prefab>/<prefab>.blend). Every mesh node is reduced and baked on its own
by the ai-highpoly-to-game-ready-lowpoly pipeline, so a moving part never takes colour from its neighbour, then all nodes of a model share one
atlas and one material.

  python Mods/ForgeOrdnance/tools/Carrier/tripo_masters.py [prefab ...]     (no prefab: every one whose source exists)

A source that has not arrived yet (the gas canister, the new drop pod) is skipped and the build keeps the older model; run this again when it
arrives. Sources are only read. Node budgets are triangles. Thin parts and bodies of revolution take the pipeline's shape-aware "auto" method (decimation tears them); "lathe" builds the low-poly itself for a body of revolution whose source is too broken for either. A node with <source>-low/<node>.fbx beside the source uses that hand-built low-poly (hard surfaces: sharp edges and regular triangles, so no shading streaks). "tiles" sets a node's atlas tile.
"""
import hashlib
import json
import os
import subprocess
import sys
from concurrent.futures import ThreadPoolExecutor
from pathlib import Path

ROOT = Path(os.environ.get("FORGE_SOURCE_REPO", Path(__file__).resolve().parents[4]))
FIXED = ROOT / "Source/mods/ordnance-assets/fixed-1104"
MASTERS = ROOT / "Source/mods/ordnance-assets/masters"
WORK = ROOT / "artifacts/work/ordnance-next/models-integrated/work"
BLENDER = r"C:\Program Files\Blender Foundation\Blender 5.2\blender.exe"
PIPELINE = Path(os.environ["USERPROFILE"]) / ".claude/skills/ai-highpoly-to-game-ready-lowpoly/tools/lowpoly_pipeline.py"
TOOLS = Path(__file__).resolve().parent

# Blender frames: a model's front is -Y (the exporter turns that into the game's -Z). "turn" is the rotation about Z for a model built facing +Y;
# "nose" turns a munition whose nose is +Z so that its nose becomes the front; "lift" moves the model up.
MODELS = {
    # Carrier
    "turret-shell": dict(source="drill-base-tall", nodes={"body": 10000, "lid_a": 1500, "lid_b": 1500}, atlas=2048, tiles={"body": 1536, "lid_a": 512, "lid_b": 512}, well=(0.29, -0.0395)),
    "beacon": dict(source="召唤球", nodes=5000, atlas=2048, fit=0.0817),
    "drop-pod": dict(source="supply-pod", atlas=2048, require="door_1",
                     tiles={"body": 1024, "door_1": 680, "door_2": 680, "door_3": 680, "door_4": 680, "interior": 256, "shelf_1": 128, "shelf_2": 128, "cradle": 64},
                     nodes={"body": 12000, "cradle": 72, "door_1": 1000, "door_2": 1000, "door_3": 1000, "door_4": 1000, "interior": 1220, "shelf_1": 160, "shelf_2": 160}),
    # Arsenal: munitions
    "shell": dict(source="范围炮弹", nodes=3000, atlas=1024, method="auto", nose=True),
    "shell-precision": dict(source="精确炮弹A", nodes=3000, atlas=1024, nose=True),
    "shell-hunter": dict(source="追猎炮弹", nodes=3500, atlas=1024, nose=True),
    "shell-incendiary": dict(source="燃烧弹", nodes=3500, atlas=1024, method="auto", nose=True),
    "bomblet": dict(source="集束小弹", nodes=1500, atlas=512, method="auto", nose=True),
    "rocket": dict(source="火箭弹", nodes=2500, atlas=1024, nose=True),
    "grenade-40": dict(source="40毫米榴弹", nodes=2500, atlas=512, lathe=32, nose=True),
    "gas-canister": dict(source="毒气弹", nodes=4000, atlas=1024, stand=True, invert=True),
    "stasis-emitter": dict(source="静滞发生器", nodes=5000, atlas=1024, method="auto", stand=True),
    "mine": dict(source="地雷", nodes=3000, atlas=1024),
    "glue-mine": dict(source="胶雷", nodes=3500, atlas=1024),
    # Heavy weapon master: front -Y, up +Z, the pistol grip at the origin. x' = -x, y' = -z, z' = -y. A weapon with "sight" (x, y, length) gets a red-dot sight from fit_sight.py; the recoilless rifle's master has none, and the red-dot it wears comes from Arsenal's Data/gun-sights.json.
    "recoilless": dict(source="无后坐力炮", nodes=7000, atlas=2048, weapon=dict(grip=(0.0, -0.18, -0.13), left=(0.0, -0.26, 0.075), bore=0.128)),
    # Arsenal: turrets and devices
    "turret-burst": dict(source="turret-burst", nodes={"base": 3000, "yaw": 7000, "head": 12000}, atlas=2048, turn=True, flat=True),
    "turret-sniper": dict(source="turret-sniper", nodes={"base": 4000, "yaw": 6000, "head": 11000}, atlas=2048, turn=True, flat=True),
    "turret-shotgun": dict(source="turret-shotgun", nodes={"base": 2500, "yaw": 6000, "head": 11000}, atlas=2048, turn=True, flat=True),
    "dispenser": dict(source="dispenser", nodes={"ring": 1700, "column": 1100, **{f"plate{i}": 3000 for i in range(1, 7)}}, atlas=2048, turn=True, lift=-0.021),
    "radar": dict(source="radar", nodes={"base": 3000, "yaw": 3000, "head": 3000, **{f"petal{i}": 1500 for i in range(1, 9)}}, atlas=2048, turn=True, parents={"head": "yaw"}),
    "floodlight": dict(source="floodlight", nodes={"base": 6000, "yaw": 2500, "head": 6000, "fan": 2500}, atlas=2048, turn=True),
}


def run(args, log):
    with open(log, "w", encoding="utf-8", errors="replace") as out:
        code = subprocess.call(args, stdout=out, stderr=subprocess.STDOUT)
    if code != 0:
        raise SystemExit(f"Failed ({code}), see {log}")


def bake_node(prefab, source, node, budget, size, method, lathe=0):
    work = WORK / prefab
    out = work / node
    built = FIXED / (source + "-low") / (node + ".fbx")
    # A finished bake is reused only for the same source high model, hand-made low model, budget, texture size and method.
    key = hashlib.sha256()
    for path in (work / "split" / (node + ".glb"), built):
        key.update(path.read_bytes() if path.exists() else b"-")
    key.update(f"{budget}|{size}|{method}|{lathe}".encode())
    stamp = out / "bake.key"
    if (out / "report.json").exists() and stamp.exists() and stamp.read_text(encoding="utf-8") == key.hexdigest():
        return
    shape = []
    if built.exists():
        shape = ["--low", str(built), "--maps", "basecolor,normal,roughness,metallic"]  # no AO: the low lying on the high speckles it
    elif lathe:
        made = work / (node + "_lathe.fbx")
        run([BLENDER, "-b", "--factory-startup", "--python-exit-code", "1", "--python", str(TOOLS / "tripo_lathe.py"), "--", str(work / "split" / (node + ".glb")), str(made), str(lathe)], work / (node + "_lathe.log"))
        shape = ["--low", str(made)]
    run([BLENDER, "-b", "--factory-startup", "--python-exit-code", "1", "--python", str(PIPELINE), "--", "--high", str(work / "split" / (node + ".glb")), "--out", str(out), "--name", node,
         "--target-tris", str(budget), "--max-tris", str(int(budget * 1.1)), "--low-method", method, "--regularize", "off", "--high-clean", "none",
         "--repeats", "off", "--texture-size", str(size)] + (["--error-target", "0.003"] if method == "auto" else []) + shape, work / (node + ".log"))
    stamp.write_text(key.hexdigest(), encoding="utf-8")


def build(prefab, spec, jobs):
    work = WORK / prefab
    blend = FIXED / (spec["source"] + ".blend")
    work.mkdir(parents=True, exist_ok=True)
    run([BLENDER, "-b", str(blend), "--python-exit-code", "1", "--python", str(TOOLS / "tripo_split.py"), "--", str(work / "split")], work / "split.log")
    info = json.loads((work / "split" / "nodes.json").read_text(encoding="utf-8"))
    if spec.get("require") and spec["require"] not in info:
        print("waiting for the new", prefab, "(the source has no", spec["require"] + ")")
        return False
    nodes = spec["nodes"]
    meshes = [n for n, v in info.items() if v["mesh"]]
    if isinstance(nodes, int):
        nodes = {meshes[0]: nodes}
    missing = [n for n in nodes if n not in meshes]
    extra = [n for n in meshes if n not in nodes]
    if missing or extra:
        raise SystemExit(f"{prefab}: nodes differ from the source: missing {missing}, unbudgeted {extra}")
    # Share of the atlas by the node's surface area, as a power of two.
    total = sum(info[n]["area"] for n in nodes)
    atlas = spec["atlas"]
    tiles = {}
    for n in nodes:
        side = (info[n]["area"] / total) ** 0.5 * atlas
        tiles[n] = max(128, 2 ** int(round(__import__("math").log2(max(side, 1)))))
    while sum(s * s for s in tiles.values()) > atlas * atlas:
        big = max(tiles, key=lambda k: tiles[k])
        tiles[big] //= 2
    tiles.update(spec.get("tiles", {}))
    with ThreadPoolExecutor(jobs) as pool:
        for f in [pool.submit(bake_node, prefab, spec["source"], n, nodes[n], tiles[n], spec.get("method", "decimate"), spec.get("lathe", 0)) for n in nodes]:
            f.result()
    config = dict(prefab=prefab, source=str(blend), work=str(work), out=str(MASTERS / prefab), nodes=list(nodes), tiles=tiles, atlas=atlas, **{k: v for k, v in spec.items() if k not in ("source", "nodes", "atlas", "tiles", "require", "method", "lathe")})
    (work / "assemble.json").write_text(json.dumps(config, ensure_ascii=False), encoding="utf-8")
    run([BLENDER, "-b", "--factory-startup", "--python-exit-code", "1", "--python", str(TOOLS / "tripo_assemble.py"), "--", str(work / "assemble.json")], work / "assemble.log")
    sight = spec.get("weapon", {}).get("sight")
    if sight:
        master = MASTERS / prefab / (prefab + ".blend")
        fitted = master.with_name(prefab + "_sight.blend")
        run([BLENDER, "-b", "--factory-startup", "--disable-autoexec", str(master), "--python-exit-code", "1", "--python", str(TOOLS.parent / "Arsenal" / "fit_sight.py"), "--",
             "--gun", prefab, "--sight", str(MASTERS.parent / "downloads" / "red-dot-sight" / "red_dot.blend"), f"--mount={sight[0]},{sight[1]}", "--length", str(sight[2]), "--save", str(fitted)], work / "sight.log")
        os.replace(fitted, master)
    return True


def main():
    names = sys.argv[1:] or list(MODELS)
    for prefab in names:
        spec = MODELS[prefab]
        if not (FIXED / (spec["source"] + ".blend")).exists():
            print("waiting for", spec["source"], "; the build keeps the older", prefab)
            continue
        if build(prefab, spec, 3):
            print("master", prefab)


main()
