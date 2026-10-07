"""Ownership checks for Energy Lab generated output. Never clear an input or an arbitrary project."""
import argparse
import os
import shutil
from pathlib import Path

MOD = Path(__file__).resolve().parents[1]
MARKER = ".forge-energy-output"


def no_links(path, recursive=False):
    path = Path(path).absolute()
    for item in (path, *path.parents):
        attributes = getattr(item.stat(follow_symlinks=False), "st_file_attributes", 0) if item.exists() else 0
        if item.is_symlink() or attributes & 0x400:
            raise ValueError(f"Refusing linked output: {item}")
    if recursive and path.exists():
        for folder, dirs, files in os.walk(path, followlinks=False):
            for name in dirs + files:
                no_links(Path(folder) / name)


def reserve(path, owner, source=None):
    no_links(path)
    root = Path(path).resolve()
    if root == MOD or root in MOD.parents or MOD in root.parents:
        raise ValueError("Output must stay outside the mod folder")
    if source is not None:
        source = Path(source).resolve()
        if root == source or root in source.parents or source in root.parents:
            raise ValueError("Output and source must not overlap")
        if not source.is_dir():
            raise ValueError("Source directory missing")
    marker = root / MARKER
    no_links(marker)
    if marker.exists():
        if marker.read_text(encoding="utf-8") != owner:
            raise ValueError("Output belongs to another tool")
    elif root.exists() and any(root.iterdir()):
        raise ValueError("Nonempty output has no Energy Lab ownership marker")
    root.mkdir(parents=True, exist_ok=True)
    marker.write_text(owner, encoding="utf-8")
    return root


def clear(root, relative, owner):
    root = reserve(root, owner)
    allowed = ("Geometry", "Assets/Generated", "Assets/Weapons") if owner == "project" else ("candidate",)
    relative = Path(relative).as_posix()
    if relative not in allowed and not (owner == "project" and relative.startswith("Assets/Weapons/") and ".." not in Path(relative).parts):
        raise ValueError("Not a generated folder owned by this tool")
    target = root / relative
    no_links(target, recursive=True)
    if root not in target.resolve().parents:
        raise ValueError("Generated output escaped its owner")
    if target.exists():
        shutil.rmtree(target)
    meta = target.with_name(target.name + ".meta")
    if meta.exists():
        no_links(meta)
        meta.unlink()


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--project", type=Path)
    ap.add_argument("--source", type=Path)
    ap.add_argument("--evidence", type=Path)
    ap.add_argument("--clear-candidate", action="store_true")
    opt = ap.parse_args()
    if opt.project:
        reserve(opt.project, "project", opt.source)
    if opt.evidence:
        root = reserve(opt.evidence, "presentation", opt.source)
        if opt.clear_candidate:
            clear(root, "candidate", "presentation")


if __name__ == "__main__":
    main()
