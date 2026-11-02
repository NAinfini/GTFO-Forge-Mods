"""Prove a set of Forge Thunderstore zips against the Release build and, optionally, against an installed profile.

    python Mods/Build/verify_release.py --packages <zip folder> --artifacts-path <dotnet artifacts> [--installed <BepInEx/plugins>]

Fails (exit 1) when
- a dll in a zip is not byte-identical to the Release build output (artifacts/bin/<project>/release/<dll>);
- a package's main dll version differs from its manifest version, or a NAinfini dependency is missing from the set or
  asks for another version than the set holds;
- a type or member that one dll references in another dll of the set does not exist there (check-assembly-refs.ps1);
- with --installed: any file of a zip is missing or different in plugins/NAinfini-<name>/, or the folder holds a dll no zip owns.
"""

from __future__ import annotations

import argparse
import hashlib
import json
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile
import zipfile

HERE = Path(__file__).resolve().parent


def sha256(data: bytes) -> str:
    return hashlib.sha256(data).hexdigest()


def read_packages(folder: Path) -> dict[str, tuple[Path, dict[str, bytes], dict[str, object]]]:
    packages = {}
    for path in sorted(folder.glob("NAinfini-*.zip")):
        with zipfile.ZipFile(path) as archive:
            files = {info.filename: archive.read(info) for info in archive.infolist() if not info.is_dir()}
        manifest = json.loads(files["manifest.json"].decode("utf-8"))
        expected = "NAinfini-" + manifest["name"] + "-" + manifest["version_number"] + ".zip"
        if path.name != expected:
            raise ValueError(path.name + " should be named " + expected)
        packages[manifest["name"]] = (path, files, manifest)
    if not packages:
        raise ValueError("no NAinfini-*.zip in " + str(folder))
    return packages


def build_outputs(artifacts: Path, dll: str) -> list[Path]:
    return sorted((artifacts / "bin").glob("*/release/" + dll))


def verify(packages_dir: Path, artifacts: Path, installed: Path | None) -> list[str]:
    problems: list[str] = []
    packages = read_packages(packages_dir)

    for name, (path, files, manifest) in packages.items():
        for entry, data in files.items():
            if not entry.endswith(".dll"):
                continue
            candidates = build_outputs(artifacts, entry)
            if not candidates:
                problems.append(name + ": " + entry + " has no build output under " + str(artifacts / "bin"))
            for built in candidates:
                if built.read_bytes() != data:
                    problems.append(name + ": " + entry + " differs from build output " + str(built))
        for dependency in manifest["dependencies"]:
            team, dep_name, version = dependency.split("-")
            if team != "NAinfini":
                continue
            if dep_name not in packages:
                problems.append(name + ": dependency " + dependency + " is not in the set")
            elif packages[dep_name][2]["version_number"] != version:
                problems.append(name + ": dependency " + dependency + " but the set holds " + str(packages[dep_name][2]["version_number"]))

    temporary = Path(tempfile.mkdtemp(prefix="verify-release-"))
    try:
        dlls = []
        for name, (_path, files, _manifest) in packages.items():
            for entry, data in files.items():
                if entry.endswith(".dll") and Path(entry).name.startswith("Forge"):
                    target = temporary / name / entry
                    target.parent.mkdir(parents=True, exist_ok=True)
                    target.write_bytes(data)
                    dlls.append(str(target))
        listing = temporary / "dlls.txt"
        listing.write_text("\n".join(dlls), encoding="utf-8")
        powershell = shutil.which("pwsh")
        if not powershell:
            raise ValueError("pwsh is required for the assembly reference check")
        result = subprocess.run(
            [powershell, "-NoProfile", "-NonInteractive", "-File", str(HERE / "check-assembly-refs.ps1"), "-List", str(listing)],
            capture_output=True, text=True, encoding="utf-8", errors="replace", check=False,
        )
        if result.returncode:
            problems.extend((result.stderr or result.stdout).strip().splitlines())
        for line in result.stdout.splitlines():
            if line.startswith("ASM "):
                _tag, assembly, version = line.split()
                if assembly in packages:
                    manifest_version = str(packages[assembly][2]["version_number"])
                    if version != manifest_version + ".0":
                        problems.append(assembly + ": assembly version " + version + " differs from manifest " + manifest_version)
            elif line.startswith("assembly refs ok"):
                print(line)
    finally:
        shutil.rmtree(temporary, ignore_errors=True)

    if installed is not None:
        for name, (_path, files, _manifest) in packages.items():
            folder = installed / ("NAinfini-" + name)
            if not folder.is_dir():
                problems.append(name + ": " + str(folder) + " is not installed")
                continue
            for entry, data in files.items():
                target = folder / entry
                if not target.is_file() or sha256(target.read_bytes()) != sha256(data):
                    problems.append(name + ": installed " + entry + " is missing or differs from the zip")
            owned = {entry for entry in files if entry.endswith(".dll")}
            for stray in folder.rglob("*.dll"):
                if stray.relative_to(folder).as_posix() not in owned:
                    problems.append(name + ": installed dll " + stray.relative_to(folder).as_posix() + " belongs to no zip")
        known = {"NAinfini-" + name for name in packages}
        for folder in installed.glob("NAinfini-*"):
            if folder.name not in known:
                problems.append("installed folder " + folder.name + " belongs to no package of the set")
        # One plugin GUID installed twice (GTFO-API at the plugins root and in its own package folder) makes two assemblies race for it and,
        # for GTFO-API, two network signatures: the same dll name must not appear in two places with different bytes.
        seen: dict[str, tuple[Path, str]] = {}
        for dll in installed.rglob("*.dll"):
            digest = sha256(dll.read_bytes())
            earlier = seen.setdefault(dll.name, (dll, digest))
            if earlier[1] != digest:
                problems.append("installed " + dll.name + " exists twice with different bytes: " + str(earlier[0]) + " and " + str(dll))

    return problems


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--packages", type=Path, required=True)
    parser.add_argument("--artifacts-path", type=Path, required=True)
    parser.add_argument("--installed", type=Path)
    args = parser.parse_args()
    try:
        problems = verify(args.packages.resolve(), args.artifacts_path.resolve(), args.installed.resolve() if args.installed else None)
    except (OSError, ValueError, KeyError) as error:
        print("verify_release: " + str(error), file=sys.stderr)
        return 2
    for problem in problems:
        print("verify_release: " + problem, file=sys.stderr)
    if problems:
        return 1
    print("verify_release: ok (" + str(len(read_packages(args.packages.resolve()))) + " packages)")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())


