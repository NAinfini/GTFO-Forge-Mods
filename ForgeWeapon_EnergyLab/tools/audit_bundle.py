"""Inventory the actual shipped Unity objects, and decode inputs for identical-camera reviews.

python audit_bundle.py --unitypy <installed package directory> --bundle <bundle> --out <directory> [--extract]
"""
import argparse
import collections
import csv
import hashlib
import json
import sys
from pathlib import Path


def main():
    ap = argparse.ArgumentParser(description=__doc__)
    ap.add_argument("--unitypy", required=True, type=Path)
    ap.add_argument("--bundle", required=True, type=Path)
    ap.add_argument("--out", required=True, type=Path)
    ap.add_argument("--extract", action="store_true")
    opt = ap.parse_args()
    sys.path.insert(0, str(opt.unitypy.resolve()))
    import UnityPy
    from UnityPy.enums import TextureFormat
    from UnityPy.helpers.MeshHelper import MeshHandler
    import numpy as np

    opt.out.mkdir(parents=True, exist_ok=True)
    env = UnityPy.load(str(opt.bundle))
    aliases = {o.path_id: name for name, o in env.container.items()}
    rows, formats, objects = [], collections.Counter(), collections.Counter()
    for obj in env.objects:
        kind = obj.type.name
        objects[kind] += 1
        data = obj.read()
        name = getattr(data, "m_Name", str(obj.path_id)) if kind == "Mesh" else aliases.get(obj.path_id, getattr(data, "m_Name", str(obj.path_id)))
        row = dict(name=name, type=kind, serializedBytes=obj.byte_size, streamBytes=0,
                   width=0, height=0, format="", mipCount=0, gpuBytes=0,
                   vertices=0, indices=0, compression=0, readable="", channels="")
        if kind == "Texture2D":
            fmt = TextureFormat(data.m_TextureFormat).name
            stream = data.m_StreamData
            row.update(width=data.m_Width, height=data.m_Height, format=fmt,
                       mipCount=data.m_MipCount, streamBytes=stream.size if stream else 0)
            if fmt in ("DXT1", "DXT1Crunched", "BC4"):
                block = 8
            elif fmt in ("DXT5", "DXT5Crunched", "BC5", "BC7"):
                block = 16
            else:
                raise ValueError("Measure the GPU layout of " + fmt + " before using it.")
            w, h = data.m_Width, data.m_Height
            for _ in range(data.m_MipCount):
                row["gpuBytes"] += ((w + 3) // 4) * ((h + 3) // 4) * block
                w, h = max(1, w // 2), max(1, h // 2)
            formats[f"{row['width']}x{row['height']} {fmt}"] += 1
            if opt.extract:
                image = data.image
                row["extrema"] = str(image.getextrema())
                dest = opt.out / "textures" / (name + ".png")
                dest.parent.mkdir(exist_ok=True)
                image.save(dest)
        elif kind == "Mesh":
            handler = MeshHandler(data)
            handler.process()
            indices = np.array(handler.get_triangles()[0], dtype=np.int32).reshape(-1, 3)
            row.update(vertices=handler.m_VertexCount, indices=int(indices.size),
                       compression=data.m_MeshCompression, readable=data.m_IsReadable,
                       channels=",".join(str(i) for i, c in enumerate(data.m_VertexData.m_Channels) if c.dimension))
            stream = data.m_StreamData
            row["streamBytes"] = stream.size if stream else 0
            if data.m_MeshCompression == 0:
                row["gpuBytes"] = (row["streamBytes"] or len(data.m_VertexData.m_DataSize)) + len(data.m_IndexBuffer)
                row["format"] = ";".join(f"channel{i}:format{c.format}x{c.dimension}@{c.offset}" for i,c in enumerate(data.m_VertexData.m_Channels) if c.dimension)
            else:
                # These format-4 compressed meshes expand back to float32 position/normal/UV.
                row["gpuBytes"] = handler.m_VertexCount * 32 + indices.size * (2 if handler.m_Use16BitIndices else 4)
            if opt.extract:
                dest = opt.out / "meshes" / (name + ".npz")
                dest.parent.mkdir(exist_ok=True)
                np.savez_compressed(dest, vertices=np.array(handler.m_Vertices, dtype=np.float32),
                                    normals=np.array(handler.m_Normals or [], dtype=np.float32)[:, :3] if handler.m_Normals else np.empty((0, 3), np.float32),
                                    uv=np.array(handler.m_UV0 or [], dtype=np.float32), indices=indices)
        elif kind == "TextAsset" and name == "presentation-profile":
            script = data.m_Script
            if isinstance(script, bytes):
                script = script.decode("utf-8")
            (opt.out / "profile.json").write_text(script, encoding="utf-8")
        rows.append(row)
    fields = list(rows[0]) + (["extrema"] if any("extrema" in r for r in rows) else [])
    with (opt.out / "assets.csv").open("w", newline="", encoding="utf-8") as f:
        writer = csv.DictWriter(f, fieldnames=fields)
        writer.writeheader()
        writer.writerows(sorted(rows, key=lambda r: (r["type"], r["name"])))
    summary = dict(bundle=str(opt.bundle.resolve()), bytes=opt.bundle.stat().st_size,
                   sha256=hashlib.sha256(opt.bundle.read_bytes()).hexdigest(), unitypy=UnityPy.__version__,
                   objects=dict(objects), textures=dict(sorted(formats.items())),
                   gpuTextureBytes=sum(r["gpuBytes"] for r in rows if r["type"] == "Texture2D"),
                   gpuMeshBufferBytes=sum(r["gpuBytes"] for r in rows if r["type"] == "Mesh"),
                   readableMeshBufferBytes=sum(r["gpuBytes"] for r in rows if r["type"] == "Mesh" and r["readable"]),
                   bytesByType={kind: sum(r["serializedBytes"] + r["streamBytes"] for r in rows if r["type"] == kind)
                                for kind in sorted(objects)}, assets=list(env.container))
    (opt.out / "inventory.json").write_text(json.dumps(summary, indent=2), encoding="utf-8")
    print(json.dumps({k: v for k, v in summary.items() if k not in ("assets", "bundle")}, indent=2))


if __name__ == "__main__":
    main()
