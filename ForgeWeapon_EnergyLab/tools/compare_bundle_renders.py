"""Compare the gun pixels, save full-size before/after pairs and legible review crops."""
import argparse
import json
from pathlib import Path
import numpy as np
from PIL import Image, ImageDraw, ImageFont


def box_mean(a):
    a = np.pad(a, ((4, 3), (4, 3)), mode="reflect").cumsum(0).cumsum(1)
    return (a[7:, 7:] - a[:-7, 7:] - a[7:, :-7] + a[:-7, :-7]) / 49


def main():
    ap = argparse.ArgumentParser(description=__doc__)
    ap.add_argument("--root", required=True, type=Path)
    opt = ap.parse_args()
    root, rows = opt.root.resolve(), []
    (root / "pairs").mkdir(exist_ok=True)
    font = ImageFont.truetype("C:/Windows/Fonts/msyh.ttc", 22)
    for mode in ("beam", "arc", "plasma-blast", "plasma-arc", "blast", "disc", "flame", "hole"):
        crops = []
        for view in ("hip", "ads", "ads-detail"):
            a_image = Image.open(root / f"before/renders/{mode}-{view}.png").convert("RGB")
            b_image = Image.open(root / f"after/renders/{mode}-{view}.png").convert("RGB")
            a, b = np.array(a_image, dtype=float), np.array(b_image, dtype=float)
            mask_view = view.split("-")[0]
            mask = np.array(Image.open(root / f"before/renders/{mode}-{mask_view}-mask.png"))[:, :, 3] > 127
            mask |= np.array(Image.open(root / f"after/renders/{mode}-{mask_view}-mask.png"))[:, :, 3] > 127
            gray_a, gray_b = (a * [.2126, .7152, .0722]).sum(2), (b * [.2126, .7152, .0722]).sum(2)
            ma, mb = box_mean(gray_a), box_mean(gray_b)
            va, vb = box_mean(gray_a * gray_a) - ma * ma, box_mean(gray_b * gray_b) - mb * mb
            cov = box_mean(gray_a * gray_b) - ma * mb
            ssim = ((2 * ma * mb + 6.5025) * (2 * cov + 58.5225)) / ((ma * ma + mb * mb + 6.5025) * (va + vb + 58.5225))
            delta = np.abs(a - b)[mask]
            row = dict(mode=mode, view=view, meanAbsoluteRgbError=float(delta.mean()),
                       p99MaxChannelError=float(np.quantile(delta.max(1), .99)),
                       ssimOnGun=float(ssim[mask].mean()), pixelsOver8Fraction=float((delta.max(1) > 8).mean()))
            rows.append(row)
            pair = Image.new("RGB", (a_image.width * 2, a_image.height + 36), (23, 26, 29))
            pair.paste(a_image, (0, 36)); pair.paste(b_image, (a_image.width, 36))
            draw = ImageDraw.Draw(pair)
            draw.text((12, 3), f"{mode} / {view}  Before", font=font, fill="white")
            draw.text((a_image.width + 12, 3), "After", font=font, fill="white")
            pair.save(root / f"pairs/{mode}-{view}.png")
            if view != "ads":
                yy, xx = np.nonzero(mask)
                bounds = (max(0, int(xx.min()) - 16), max(0, int(yy.min()) - 16),
                          min(a_image.width, int(xx.max()) + 17), min(a_image.height, int(yy.max()) + 17))
                crops.append((view, a_image.crop(bounds), b_image.crop(bounds)))
            print(mode, view, round(row["meanAbsoluteRgbError"], 4), round(row["ssimOnGun"], 6))
        width = max(a.width for _, a, _ in crops)
        sheet = Image.new("RGB", (width * 2, sum(a.height + 36 for _, a, _ in crops)), (23, 26, 29))
        draw, y = ImageDraw.Draw(sheet), 0
        for view, a, b in crops:
            draw.text((12, y + 3), f"{mode} / {view}  Before", font=font, fill="white")
            draw.text((width + 12, y + 3), "After", font=font, fill="white")
            sheet.paste(a, (0, y + 36)); sheet.paste(b, (width, y + 36))
            y += a.height + 36
        sheet.save(root / f"pairs/{mode}-review.png")
    (root / "visual-comparison.json").write_text(json.dumps(rows, indent=2), encoding="utf-8")


if __name__ == "__main__":
    main()
