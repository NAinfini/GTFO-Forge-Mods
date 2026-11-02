"""Shared Thunderstore packaging for the Forge library and mod packages; never publishes.

A package's tools/package_release.py supplies its folder, the dlls it ships and any extra files; this module checks the
manifest, takes every dll from the Release build output (never from a copy), writes a deterministic zip and then proves the
zip against the build with verify_release.py's checks. Use it for new packages; verify_release.py checks all of them.
"""

from __future__ import annotations

import argparse
import hashlib
import json
import os
from pathlib import Path
import re
import struct
import sys
import tempfile
from urllib.parse import urlsplit
import zipfile

REPO = Path(__file__).resolve().parents[2]
TEAM = "NAinfini"
NAME = r"[A-Za-z0-9_]{1,128}"
VERSION = r"(?:0|[1-9][0-9]*)\.(?:0|[1-9][0-9]*)\.(?:0|[1-9][0-9]*)"
LIBRARY_METADATA = ("manifest.json", "icon.png", "README.md", "DEVELOPERS.md", "CHANGELOG.md", "LICENSE", "web-manifest.json")


def check_manifest(folder: Path, name: str) -> dict[str, object]:
    manifest = json.loads((folder / "manifest.json").read_text(encoding="utf-8"))
    if not isinstance(manifest, dict):
        raise ValueError("manifest.json must be an object")
    for key in ("name", "version_number", "website_url", "description"):
        if not isinstance(manifest.get(key), str):
            raise ValueError(key + " must be a string")
    if manifest["name"] != name or not re.fullmatch(NAME, name):
        raise ValueError("manifest name must be " + name + ", found " + str(manifest["name"]))
    if not re.fullmatch(VERSION, manifest["version_number"]):
        raise ValueError("version_number must be major.minor.patch")
    if len(manifest["description"]) > 250:
        raise ValueError("description exceeds 250 characters")
    url = urlsplit(manifest["website_url"])
    if manifest["website_url"] and (url.scheme not in ("http", "https") or not url.netloc):
        raise ValueError("website_url must be empty or an HTTP(S) URL")
    dependencies = manifest.get("dependencies")
    if not isinstance(dependencies, list) or any(
        not isinstance(item, str) or not re.fullmatch(NAME + "-" + NAME + "-" + VERSION, item) for item in dependencies
    ):
        raise ValueError("dependencies must be a list of Team-Name-major.minor.patch strings")
    with (folder / "icon.png").open("rb") as icon:
        head = icon.read(33)
    if len(head) != 33 or head[:16] != b"\x89PNG\r\n\x1a\n\x00\x00\x00\x0dIHDR" or struct.unpack(">II", head[16:24]) != (256, 256):
        raise ValueError("icon.png must be a 256 x 256 PNG")
    for doc in ("README.md", "CHANGELOG.md"):
        if not (folder / doc).read_text(encoding="utf-8").strip():
            raise ValueError(doc + " must not be empty")
    return manifest


def write_zip(path: Path, files: dict[str, bytes]) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    handle, temporary_name = tempfile.mkstemp(prefix=path.name + ".", suffix=".tmp", dir=path.parent)
    os.close(handle)
    temporary = Path(temporary_name)
    try:
        with zipfile.ZipFile(temporary, "w", compression=zipfile.ZIP_DEFLATED) as archive:
            for entry, data in sorted(files.items()):
                info = zipfile.ZipInfo(entry, date_time=(2026, 1, 1, 0, 0, 0))
                info.create_system = 3
                info.compress_type = zipfile.ZIP_DEFLATED
                info.external_attr = 0o644 << 16
                archive.writestr(info, data, compresslevel=9)
        os.replace(temporary, path)
    finally:
        temporary.unlink(missing_ok=True)


def package_library(folder: Path, name: str, dlls: tuple[str, ...], artifacts: Path, output: Path, metadata: tuple[str, ...] = LIBRARY_METADATA) -> Path:
    """Zip a library: metadata beside the package, every dll read from artifacts/bin/<project>/release."""
    manifest = check_manifest(folder, name)
    files = {entry: (folder / entry).read_bytes() for entry in metadata}
    for dll in dlls:
        project = dll[:-4]
        built = artifacts / "bin" / project / "release" / dll
        if not built.is_file():
            raise ValueError("missing build output " + str(built) + "; run the Release build first")
        data = built.read_bytes()
        if data[:2] != b"MZ":
            raise ValueError(dll + " is not a PE assembly")
        files[dll] = data
    path = output / (TEAM + "-" + name + "-" + str(manifest["version_number"]) + ".zip")
    write_zip(path, files)
    print(path.name + ": " + str(path.stat().st_size) + " bytes, sha256 " + hashlib.sha256(path.read_bytes()).hexdigest())
    return path


def main(name: str, dlls: tuple[str, ...]) -> int:
    parser = argparse.ArgumentParser(description="Build the " + name + " Thunderstore zip from the Release build output.")
    parser.add_argument("--artifacts-path", type=Path, required=True, help="the dotnet --artifacts-path of the Release build")
    parser.add_argument("--output", type=Path, required=True, help="folder that receives the zip")
    args = parser.parse_args()
    artifacts = args.artifacts_path if args.artifacts_path.is_absolute() else Path.cwd() / args.artifacts_path
    output = args.output if args.output.is_absolute() else Path.cwd() / args.output
    try:
        package_library(REPO / "Mods" / name, name, dlls, artifacts, output)
        return 0
    except (OSError, ValueError) as error:
        print("package_release: " + str(error), file=sys.stderr)
        return 2
