"""Summarise the native probe's GPU timelines (gpu-timeline-N.csv): median GPU milliseconds per stage over the captured frames.

python Mods/ForgePerformance/tools/analyze_gpu_timeline.py <gpu-timeline-N.csv>... [--cameras "1=FPSLookCamera;2=GizmoCamera"] [--detail]

A row is the stretch from one label (a camera event of one camera, or a render-target change when the Direct3D entries are
hooked) to the next; its time is the difference of two GPU timestamps, so in a CPU-limited frame it also holds any time the
GPU waited for the next commands. Stage "frame start" runs from the frame mark to the first camera label (everything drawn
before the first camera: shadow maps, the HUD camera's own render), "after <last label>" runs to the next frame mark and
holds the present and the wait for the next frame.
"""
import argparse
import collections
import csv
import statistics

EVENTS = ["BeforeDepthTexture", "AfterDepthTexture", "BeforeDepthNormalsTexture", "AfterDepthNormalsTexture", "BeforeGBuffer", "AfterGBuffer",
          "BeforeLighting", "AfterLighting", "BeforeFinalPass", "AfterFinalPass", "BeforeForwardOpaque", "AfterForwardOpaque",
          "BeforeImageEffectsOpaque", "AfterImageEffectsOpaque", "BeforeSkybox", "AfterSkybox", "BeforeForwardAlpha", "AfterForwardAlpha",
          "BeforeImageEffects", "AfterImageEffects", "AfterEverything", "BeforeReflections", "AfterReflections", "BeforeHaloAndLensFlares",
          "AfterHaloAndLensFlares"]


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("files", nargs="+")
    parser.add_argument("--cameras", default="")
    parser.add_argument("--detail", action="store_true")
    args = parser.parse_args()
    cameras = dict(part.split("=", 1) for part in args.cameras.split(";") if "=" in part)

    def name(label):
        if label == 0:
            return "frame start"
        camera, event = divmod(label, 100)
        return "{} after {}".format(cameras.get(str(camera), "camera " + str(camera)), EVENTS[event] if event < len(EVENTS) else event)

    for path in args.files:
        rows = [r for r in csv.DictReader(open(path, encoding="utf-8", newline="")) if r["label"] != "4294967295"]
        frames = collections.defaultdict(list)
        for r in rows:
            frames[r["frame"]].append(r)
        ids = sorted(frames, key=int)[1:-1] or sorted(frames, key=int)  # the first and last captured frames carry the arming cost
        stage = collections.defaultdict(list)
        draws = collections.defaultdict(list)
        order = []
        totals, busy = [], []
        for frame in ids:
            per = collections.OrderedDict()
            count = collections.Counter()
            for r in frames[frame]:
                key = name(int(r["label"]))
                if args.detail and r["width"] != "0":
                    key += " | {}x{} fmt {} x{}{}".format(r["width"], r["height"], r["format"], r["targets"], " +depth" if r["depth"] == "1" else "")
                value = float(r["gpu_us"])
                if value < 0:
                    continue
                per[key] = per.get(key, 0) + value
                count[key] += int(r["draws"]) + int(r["dispatches"])
            for key, value in per.items():
                if key not in order:
                    order.append(key)
                stage[key].append(value / 1000)
                draws[key].append(count[key])
            totals.append(sum(per.values()) / 1000)
            last = list(per)[-1] if per else None
            busy.append(sum(v for k, v in per.items() if k != last) / 1000)
        print("## {}: {} frames, median frame {:.2f} ms, median without the last stretch (present and wait) {:.2f} ms".format(
            path.replace("\\", "/").split("/")[-1], len(ids), statistics.median(totals) if totals else 0, statistics.median(busy) if busy else 0))
        print("| stage | median ms | min | max | draws+dispatches |")
        print("|---|---|---|---|---|")
        for key in order:
            values = stage[key]
            if statistics.median(values) < 0.02 and max(values) < 0.1:
                continue
            print("| {} | {:.2f} | {:.2f} | {:.2f} | {:.0f} |".format(key, statistics.median(values), min(values), max(values), statistics.median(draws[key])))
        print()


if __name__ == "__main__":
    main()
