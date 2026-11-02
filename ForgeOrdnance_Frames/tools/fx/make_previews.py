"""Turns the Unity preview frames (tools/fx/preview_fx.ps1) into one GIF and one key-frame sheet per moment, and index.html.
Frame files are named f<milliseconds>.png. Usage: python make_previews.py <artifacts/work/vfx-frames>
"""
import glob
import html
import os
import re
import sys

from PIL import Image

# key, title, note, seconds the six sheet frames are taken at (None: evenly spaced)
MOMENTS = [
    ("01-arrival", "战甲空降", "背后两团向下的蓝白推进焰，越接近地面越强（高速下落时火焰留在身后成尾迹）。", [0.3, 0.55, 0.8, 0.95, 1.1, 1.2]),
    ("02-landing", "战甲落地", "尘环、升起的烟柱、碎石、火花、冲击环，脚下留下碎裂印记。", [0.0, 0.2, 0.5, 1.0, 1.8, 2.8]),
    ("03-charge", "冲锋", "背部蓝色推进焰，脚后拖起尘线。", [0.2, 0.6, 1.0, 1.5, 2.2, 2.9]),
    ("04-siege", "驻守", "支撑架落地：脚下一圈尘土和碎石（肩部红灯是点光源，预览里不渲染）。", [0.0, 0.2, 0.5, 0.9, 1.4, 1.9]),
    ("05-rockets", "火箭", "发射瞬间肩部后喷焰加烟团，飞行中尾焰与烟迹。", [0.0, 0.2, 0.5, 0.9, 1.4, 2.2]),
    ("07-push-pulse", "屏障推开敌人", "白色脉冲环从屏障壁向外扩散。", [0.0, 0.1, 0.2, 0.3, 0.45, 0.6]),
    ("08-destroyed", "被毁", "爆燃火球、黑烟、火花、碎石；残骸持续起火冒烟，最后三秒火势减弱，最后两秒沉入地面。", [0.1, 0.4, 1.0, 4.0, 9.5, 11.5]),
    ("09-eject", "驾驶员弹出", "背板爆开处喷出汽柱和火花。", [0.0, 0.2, 0.4, 0.7, 1.1, 1.8]),
    ("10-overheat", "冲刺过热", "肩部散热红色热气，越热越密（仅驾驶员本人）。", [0.4, 1.0, 1.6, 2.2, 3.0, 3.8]),
]


def millis(path):
    return int(re.search(r"f(\d+)\.png$", path).group(1))


def main():
    root = sys.argv[1]
    frames = os.path.join(root, "frames")
    rows = []
    for key, title, note, picks_at in MOMENTS:
        files = sorted(glob.glob(os.path.join(frames, key, "f*.png")), key=millis)
        if not files:
            continue
        times = [millis(f) for f in files]
        images = [Image.open(f).convert("RGB") for f in files]
        small = [im.resize((480, 270), Image.LANCZOS) for im in images]
        durations = [max(30, min(1000, (times[i + 1] - times[i]) if i + 1 < len(times) else 600)) for i in range(len(times))]
        small[0].save(os.path.join(root, key + ".gif"), save_all=True, append_images=small[1:], duration=durations, loop=0, optimize=True)
        chosen = [images[min(range(len(times)), key=lambda i: abs(times[i] - at * 1000))] for at in picks_at]
        sheet = Image.new("RGB", (3 * 427, 2 * 240))
        for i, im in enumerate(chosen):
            sheet.paste(im.resize((427, 240), Image.LANCZOS), ((i % 3) * 427, (i // 3) * 240))
        sheet.save(os.path.join(root, key + ".png"))
        rows.append((key, title, note))
    page = ["<!doctype html><meta charset=utf-8><meta name=viewport content='width=device-width,initial-scale=1'><title>战甲特效预览</title>",
            "<style>body{font-family:system-ui,sans-serif;background:#14161a;color:#e6e6e6;margin:0;padding:16px;max-width:1320px}"
            "h1{font-size:20px}section{margin:24px 0}h2{font-size:16px;margin:0 0 4px}p{margin:0 0 8px;color:#a8adb5}"
            ".row{display:flex;flex-wrap:wrap;gap:8px}img{max-width:100%;border:1px solid #2b2f36}</style>",
            "<h1>ForgeOrdnance_Frames 特效预览（Unity 2019.4.21f1 渲染，灰色人形为 2.1 米参照）</h1>"]
    for key, title, note in rows:
        page.append("<section><h2>%s</h2><p>%s</p><div class=row><img src='%s.gif' width=480 height=270 alt=''><img src='%s.png' width=640 alt=''></div></section>"
                    % (html.escape(title), html.escape(note), key, key))
    with open(os.path.join(root, "index.html"), "w", encoding="utf-8") as out:
        out.write("\n".join(page))
    print("wrote %d moments" % len(rows))


if __name__ == "__main__":
    main()
