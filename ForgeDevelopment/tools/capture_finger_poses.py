"""Measures the finger poses of the game's grip animations on a landed drive session and writes them as JSON.

    capture_finger_poses.py --session <session.forge> --output <hand-shapes.json>

One animation at a time through FingerPoseCapture (Names, Joints, Play, Read): the layer is put on the animation, and the pose is read
by later calls, after the game has evaluated it in its own frames, until two reads in a row agree; a hand layer without the state is
listed under "missing", an animation that never holds still under "moving" (its last read is kept). A gun must be in the hands (the
animation is set the game's way, through the wielded gun). The output, per hand: its 15 finger joints (bone, parent, rest position)
and, per animation, their local rotations (x, y, z, w) in joint order; ForgeSkin's backend keeps the rotations as hand-shapes.json."""
import argparse
import json
import subprocess
import sys
import time
from pathlib import Path

CLI = Path(__file__).resolve().parent / "drive_cli.py"
TARGET = "static:ForgeDevelopment.Native.FingerPoseCapture"


def call(session, *args):
    done = subprocess.run([sys.executable, str(CLI), "--session", str(session), "call", TARGET, *args], capture_output=True, text=True, encoding="utf-8")
    if done.returncode != 0:
        raise RuntimeError(" ".join(args) + ": " + done.stderr.strip())
    for line in reversed(done.stdout.splitlines()):
        row = json.loads(line)
        if "result" in row:
            return row["result"]
    raise RuntimeError(" ".join(args) + ": no result in " + done.stdout)


def numbers(text):
    return [round(float(v), 5) for v in text.split()]


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--session", required=True, type=Path)
    parser.add_argument("--output", required=True, type=Path)
    args = parser.parse_args()
    names = dict(part.split("=", 1) for part in call(args.session, "Names").split(";"))
    out = {}
    for side in ("left", "right"):
        joints = []
        for row in call(args.session, "Joints", side).strip(";").split(";"):
            bone, rest = row.split("<", 1)
            parent, at = rest.split("@", 1)
            joints.append({"bone": bone, "parent": parent, "at": numbers(at)})
        shapes, missing, moving, last = {}, [], [], None
        for name in filter(None, names[side].split(",")):
            if call(args.session, "Play", side, name).startswith("fingers:"):
                missing.append(name)
                continue
            for _ in range(20):
                time.sleep(0.4)
                state, pose = call(args.session, "Read", side).split(":", 1)
                if state.endswith(" still") and pose == last:
                    break
                last = pose
            else:
                moving.append(name)  # an animation that moves on its own (a melee idle): its last read is kept
            shapes[name] = [numbers(q) for q in pose.strip(";").split(";")]
            print(side, name, flush=True)
        out[side] = {"joints": joints, "shapes": shapes, "missing": missing, "moving": moving}
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(out, ensure_ascii=False, indent=1) + "\n", encoding="utf-8")
    print("wrote", args.output, {side: len(out[side]["shapes"]) for side in out})


if __name__ == "__main__":
    main()
