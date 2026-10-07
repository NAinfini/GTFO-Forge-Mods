"""Measure projected pixels per UV unit from decoded bundle meshes, including a 4K safety case.

The report deliberately includes front-facing geometry hidden by other parts: it overestimates,
rather than underestimates, the resolution needed. It is an offline view, not a measured GTFO pose.
"""
import argparse
import json
from pathlib import Path
import numpy as np


def quantile(values, weights, q):
    order = np.argsort(values)
    return float(values[order][np.searchsorted(np.cumsum(weights[order]), weights.sum() * q)])


def vec(v):
    return np.array([v[k] for k in ("x", "y", "z")], dtype=np.float64)


def main():
    ap = argparse.ArgumentParser(description=__doc__)
    ap.add_argument("--decoded", required=True, type=Path)
    ap.add_argument("--out", required=True, type=Path)
    opt = ap.parse_args()
    profile = json.loads((opt.decoded / "profile.json").read_text())
    rows = []
    for w in profile["weapons"]:
        for part in w["parts"]:
            if part["surface"] != "body":
                continue
            mesh = np.load(opt.decoded / "meshes" / f"{w['mode']}-{part['name']}.npz")
            tri = mesh["indices"]
            vertices = mesh["vertices"] + vec(part["pivot"])
            uv = mesh["uv"][:, :2][tri]
            uv_basis = np.stack([uv[:, 1] - uv[:, 0], uv[:, 2] - uv[:, 0]], axis=-1)
            good = np.abs(np.linalg.det(uv_basis)) > 1e-12
            xyz, inv_uv = vertices[tri[good]], np.linalg.inv(uv_basis[good])
            normal = np.cross(xyz[:, 1] - xyz[:, 0], xyz[:, 2] - xyz[:, 0])
            samples = []
            for view in ("hip", "ads"):
                for back in (0.3, 0.5, 0.7):
                    eye = np.array([-0.14, 0.25, -back]) if view == "hip" else vec(w["sightLook"]) - [0, 0, back]
                    points = xyz - eye
                    front = (normal * points.mean(axis=1)).sum(1) < 0
                    for fov in (32, 45):
                        height, width = 2160, 3840
                        focal = height / (2 * np.tan(np.deg2rad(fov / 2)))
                        screen = points[:, :, :2] / points[:, :, 2:3] * focal
                        visible = front & (points[:, :, 2].min(1) > 0.05)
                        visible &= (screen[:, :, 0].min(1) < width / 2) & (screen[:, :, 0].max(1) > -width / 2)
                        visible &= (screen[:, :, 1].min(1) < height / 2) & (screen[:, :, 1].max(1) > -height / 2)
                        if not visible.any():
                            continue
                        basis = np.stack([screen[:, 1] - screen[:, 0], screen[:, 2] - screen[:, 0]], axis=-1)[visible]
                        area = np.minimum(np.abs(np.linalg.det(basis)), width * height) / 2
                        use = area > 0.25
                        if not use.any():
                            continue
                        # Match isotropic mip selection: its larger UV derivative limits the useful
                        # texel density. Also retain the geometric density to expose UV anisotropy.
                        singular = np.linalg.svd(basis[use] @ inv_uv[visible][use], compute_uv=False)
                        pixels = singular[:, 1]
                        samples.append(dict(view=view, backM=back, verticalFov=fov, resolution="3840x2160",
                                            screenArea=float(area[use].sum()), p95PixelsPerUv=quantile(pixels, area[use], .95),
                                            p99PixelsPerUv=quantile(pixels, area[use], .99),
                                            p95GeometricPixelsPerUv=quantile(np.sqrt(singular.prod(1)), area[use], .95)))
            required = max((s["p95PixelsPerUv"] for s in samples), default=0) * 1.25
            rows.append(dict(mode=w["mode"], part=part["name"], textureSet=part["textureSet"],
                             requiredSizeWith25PercentMargin=round(required), samples=samples))
    sizes = {name: max(r["requiredSizeWith25PercentMargin"] for r in rows if r["textureSet"] == name)
             for name in sorted({r["textureSet"] for r in rows})}
    opt.out.write_text(json.dumps(dict(method=__doc__, rows=rows, requiredSizes=sizes), indent=2), encoding="utf-8")
    print(json.dumps(sizes, indent=2))


if __name__ == "__main__":
    main()
