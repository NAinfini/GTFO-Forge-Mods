"""Bakes what the owner set in a cut file for the build (Blender 5.2, background):

  blender -b <model>_cut.blend --python tools/hinge_tool/bake.py -- <posed.blend> <checks.json>

Each hinge with its pieces becomes one part collection, each link a part collection of its own; every other mesh goes to
固定. Every part object carries its move as the custom property fh_motion (JSON), which build_frame.py expands with this
add-on's motion.py (the one implementation of the stroke):
  a piece  {"path": its segments (world, shut), "frames": [first, last], "parent": the collection of the piece it hangs on or
           null, "allow": the checks the owner allowed for its hinge (fh_allow)}
  a link   {"link": motion.link_pose's spec, "driver": the collection of the piece it carries}
The checks run first and are written to <checks.json>. The cut file itself is never written."""
import json
import os
import sys

import bpy

sys.path.insert(0, os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
from hinge_tool import checks, model, motion  # noqa: E402

FIXED = "固定"


def main():
    out, report = sys.argv[sys.argv.index("--") + 1:][:2]
    scene = bpy.context.scene
    model.shut_all(scene)
    rows, summary = checks.run(scene)
    checks.save(report, rows, summary)
    hs, ls, parts = model.parts(scene)
    if not hs:
        raise SystemExit("这个文件里还没有设铰链的部件")
    opened, _, _ = motion.sequence(parts)
    fixed = bpy.data.collections.get(FIXED) or bpy.data.collections.new(FIXED)
    if fixed.name not in scene.collection.children:
        scene.collection.children.link(fixed)
    coll_of = {}
    moving = set()

    def own(objs, name, motion_):
        coll = bpy.data.collections.new(name)
        scene.collection.children.link(coll)
        for o in objs:
            for c in list(o.users_collection):
                c.objects.unlink(o)
            coll.objects.link(o)
            o.matrix_world = model.base(o)
            o["fh_motion"] = json.dumps(motion_)
            moving.add(o)
        return coll.name

    pieces = {h.name: model.pieces_of(h, scene) for h in hs}
    for h, p in zip(hs, parts):
        coll_of[h.name] = own(pieces[h.name], pieces[h.name][0].name,
                              {"path": p["path"].to_json(), "frames": list(p["frames"]), "parent": None,
                               "allow": sorted(model.allowed(h))})
    for h in hs:     # the piece it hangs on, by its part collection's name (known once every part has one)
        for o in pieces[h.name]:
            m = json.loads(o["fh_motion"])
            m["parent"] = coll_of.get(h.get("fh_parent", ""))
            o["fh_motion"] = json.dumps(m)
    for o in ls:
        own([o], o.name, {"link": model.link_spec(o), "driver": coll_of[o["fh_link"]]})
    for o in list(scene.objects):
        if o.type == "MESH" and o not in moving:
            for c in list(o.users_collection):
                c.objects.unlink(o)
            fixed.objects.link(o)
        elif o.type == "EMPTY" and o.get("fh_hinge"):
            bpy.data.objects.remove(o, do_unlink=True)
    last = len(opened[0])
    scene.render.fps = motion.FPS
    scene.frame_start, scene.frame_end = 1, last
    scene.frame_set(1)
    bpy.ops.wm.save_as_mainfile(filepath=out)
    bad = {k: v["failed"] for k, v in rows.items() if v["failed"]}
    print("BAKE parts", len(hs), "links", len(ls), "frames 1..%d" % last, "checks failing", len(bad), bad, flush=True)


main()
