#!/usr/bin/env python3
"""Verify an extracted ForgeDevelopment package without a checkout or game install.

The manifest pins the exact bytes accepted by the build-time production parser.
This is an integrity receipt, not a signature or a claim of game behaviour.
Obtain the archive and its published SHA-256 from a trusted source.
"""
from __future__ import annotations

import argparse
import hashlib
import json
from pathlib import Path, PurePosixPath
import re
import stat
import sys

MANIFEST = "probes/package-manifest.forge"
FORMAT = "forge-probe-package"
TOOLS = ("tools/development_paths.py", "tools/install-probes.py", "tools/rec-merge.py", "tools/rec-query.py", "tools/verify-probe-package.py")
REQUIRED = {"probes/capture-readbacks.forge", *TOOLS}
SHA256 = re.compile(r"[0-9a-f]{64}\Z")


def digest(data: bytes) -> str:
    return hashlib.sha256(data).hexdigest()


def unlinked(path: Path) -> None:
    for part in (path, *path.parents):
        if part.is_symlink():
            raise ValueError(f"refusing symlink: {part}")
        if part.exists() and getattr(part.lstat(), "st_file_attributes", 0) & getattr(stat, "FILE_ATTRIBUTE_REPARSE_POINT", 0):
            raise ValueError(f"refusing reparse point: {part}")


def relative_name(name: object) -> str:
    if not isinstance(name, str) or not name or "\\" in name:
        raise ValueError(f"invalid package path: {name!r}")
    path = PurePosixPath(name)
    segments = name.split("/")
    if path.is_absolute() or any(segment in {"", ".", ".."} or segment != segment.strip() or segment.endswith(".")
                               or re.search(r'[\x00-\x1f:*?"<>|]', segment)
                               or re.fullmatch(r"(?i)(con|prn|aux|nul|com[1-9]|lpt[1-9])(?:\..*)?", segment)
                               for segment in segments):
        raise ValueError(f"unsafe package path: {name!r}")
    return name


def unique_object(pairs):
    result = {}
    for key, value in pairs:
        if key in result:
            raise ValueError(f"duplicate JSON key: {key}")
        result[key] = value
    return result


def read_json(path: Path):
    unlinked(path)
    return json.loads(path.read_text(encoding="utf-8-sig"), object_pairs_hook=unique_object)


def inventory(root: Path) -> dict[str, bytes]:
    unlinked(root)
    if not root.is_dir():
        raise ValueError(f"missing package directory: {root}")
    files = {}
    folded = {}
    for path in sorted(root.rglob("*")):
        unlinked(path)
        name = relative_name(path.relative_to(root).as_posix())
        if name.casefold() in folded:
            raise ValueError(f"Windows path collision: {folded[name.casefold()]}, {name}")
        folded[name.casefold()] = name
        if path.is_file() and name != MANIFEST:
            data = path.read_bytes()
            if not data:
                raise ValueError(f"empty package file: {name}")
            files[name] = data
        elif not path.is_file() and not path.is_dir():
            raise ValueError(f"unsupported package entry: {name}")
    if not REQUIRED.issubset(files):
        raise ValueError("missing package resources: " + ", ".join(sorted(REQUIRED - files.keys())))
    for folder in ("trace", "commands"):
        paths = [name for name in files if re.fullmatch(r"probes/" + folder + r"/[^/]+\.forge", name)]
        if not paths:
            raise ValueError(f"missing probes/{folder}/*.forge")
    return files


def create_manifest(root: Path) -> dict:
    root = root.absolute()
    destination = root / MANIFEST
    unlinked(destination)
    if destination.exists():
        raise ValueError("manifest already exists; create it only in a new build staging directory")
    files = inventory(root)
    result = {"format": FORMAT,
              "files": [{"path": name, "sha256": digest(data), "bytes": len(data)} for name, data in sorted(files.items())]}
    destination.write_text(json.dumps(result, ensure_ascii=False, indent=2, sort_keys=True) + "\n", encoding="utf-8", newline="\n")
    return verify(root)


def verify(root: Path) -> dict:
    root = root.absolute()
    manifest = read_json(root / MANIFEST)
    if not isinstance(manifest, dict) or manifest.get("format") != FORMAT:
        raise ValueError("unsupported package manifest")
    canonical = (json.dumps(manifest, ensure_ascii=False, indent=2, sort_keys=True) + "\n").encode("utf-8")
    if (root / MANIFEST).read_bytes() != canonical:
        raise ValueError("package manifest is not in its canonical UTF-8/LF form")
    rows = manifest.get("files")
    if not isinstance(rows, list) or not rows:
        raise ValueError("manifest has no file inventory")
    expected = {}
    for row in rows:
        if not isinstance(row, dict):
            raise ValueError("invalid manifest file row")
        name = relative_name(row.get("path"))
        if name == MANIFEST or name in expected or not isinstance(row.get("sha256"), str) or not SHA256.fullmatch(row["sha256"]) or type(row.get("bytes")) is not int or row["bytes"] <= 0:
            raise ValueError(f"invalid or duplicate manifest entry: {name}")
        expected[name] = row
    files = inventory(root)
    if files.keys() != expected.keys():
        raise ValueError(f"package file set differs: missing={sorted(expected.keys() - files.keys())}; unexpected={sorted(files.keys() - expected.keys())}")
    for name, data in files.items():
        if len(data) != expected[name]["bytes"] or digest(data) != expected[name]["sha256"]:
            raise ValueError(f"package hash mismatch: {name}")
    return {"format": FORMAT, "passed": True, "files": len(files), "manifestSha256": digest((root / MANIFEST).read_bytes()),
            "gameExecuted": False, "nativePluginPresent": "ForgeDevelopment.Native.dll" in files, "nativeLoadVerified": False,
            "evidenceLevel": "packaged-byte-integrity", "signatureVerified": False}


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--package-root", type=Path, default=Path(__file__).absolute().parent.parent)
    parser.add_argument("--create-manifest", action="store_true", help="Build-only: seal bytes after the production parsers accepted them")
    args = parser.parse_args()
    try:
        result = create_manifest(args.package_root) if args.create_manifest else verify(args.package_root)
        print(json.dumps(result, ensure_ascii=False, indent=2, sort_keys=True))
        return 0
    except (OSError, ValueError, TypeError, KeyError) as error:
        print(f"[fail] {error}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
