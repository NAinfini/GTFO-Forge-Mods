"""Builds the Thunderstore zip of ForgeOrdnance_Arsenal into artifacts/work/ordnance-release/. Nothing is published.

Run from the repository root after the Release build, art build, audio build and icon build:
    dotnet build Mods/ForgeOrdnance_Arsenal/ForgeOrdnanceArsenal.csproj -c Release --artifacts-path artifacts/dotnet-arsenal ...
    pwsh Mods/ForgeOrdnance_Arsenal/tools/build_assets.ps1
    pwsh Mods/ForgeOrdnance_Arsenal/tools/build_audio.ps1
    pwsh Mods/ForgeOrdnance_Arsenal/tools/build_icons.ps1
    python Mods/ForgeOrdnance_Arsenal/tools/package_release.py

The zip holds manifest.json, icon.png, README.md, CHANGELOG.md, ForgeOrdnanceArsenal.dll and forge-ordnance-arsenal.bundle
at its root, the AudioKit audio folder (cues.forge and <cue>.ogg files) under audio/, and definitions with their icons in ForgeOrdnance/ (r2modman installs it all into
BepInEx/plugins/NAinfini-ForgeOrdnance_Arsenal/, where ForgeOrdnance finds the folder). Fixed timestamps: the same inputs
give the same bytes. The Thunderstore manifest and Unity's hash-bound bundle receipt are checked before packaging.
"""
import argparse
from pathlib import Path
import json
import os
import sys

sys.path.insert(0, os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "..", "Build"))
import package_common  # noqa: E402
from package_common import media_file, media_path, vfx_files  # noqa: E402  (one per-file media rule for every package)

REPO = str(package_common.REPO)
PACKAGE = os.path.join(REPO, "Mods", "ForgeOrdnance_Arsenal")
OUT = os.path.join(REPO, "artifacts", "work", "ordnance-release")
# The verified bundle and its Unity receipt stay in the package (build_assets.ps1 copies them there); each is read through media_path
# (this checkout's file, else the main checkout's).
BUNDLE = str(media_path("ForgeOrdnance_Arsenal/Assets/Models/forge-ordnance-arsenal.bundle", REPO))
BUNDLE_MANIFEST = str(media_path("ForgeOrdnance_Arsenal/Assets/Models/forge-ordnance-arsenal.manifest.json", REPO))
ICONS = os.path.join(PACKAGE, "Assets", "icons")


def fail(message):
    sys.exit("package_release: " + message)


# Every prefab ArsenalArt loads by name (the masters and models of build_assets.ps1); a bundle built before one existed is stale.
REQUIRED_PREFABS = (
    "shell", "shell-precision", "shell-hunter", "shell-incendiary", "bomblet", "rocket", "grenade-40", "gas-canister", "stasis-emitter",
    "mine", "glue-mine", "recoilless", "turret-burst", "turret-sniper", "turret-shotgun", "dispenser", "radar", "floodlight",
    "heavy-mg", "tracking-rifle", "anti-materiel-rifle", "grenade-launcher",
)


def check_bundle_receipt():
    if not os.path.isfile(BUNDLE):
        fail("missing " + BUNDLE)
    if not os.path.isfile(BUNDLE_MANIFEST):
        fail("missing " + BUNDLE_MANIFEST + " (run tools/build_assets.ps1 successfully)")
    try:
        with open(BUNDLE_MANIFEST, encoding="utf-8") as source:
            receipt = json.load(source)
    except (OSError, ValueError) as error:
        fail("invalid bundle build receipt: " + str(error))
    size = os.path.getsize(BUNDLE)
    digest = package_common.sha256_file(BUNDLE)
    if receipt.get("bundle") != os.path.basename(BUNDLE) or receipt.get("bytes") != size or receipt.get("sha256") != digest:
        fail("bundle does not match its Unity build receipt; rebuild it with tools/build_assets.ps1")
    names = {prefab["name"] for prefab in receipt.get("profile", {}).get("prefabs", [])}
    missing = [name for name in REQUIRED_PREFABS if name not in names]
    if missing:
        fail("the bundle lacks the prefabs " + ", ".join(missing) + " the plugin loads by name; rebuild it with tools/build_assets.ps1")


def audio_files():
    """The AudioKit audio folder: cues.forge, every <cue>.ogg it names, and the source and licence records."""
    try:
        return {entry: str(path) for entry, path in package_common.media_audio_files(os.path.join(PACKAGE, "Assets", "Audio"), ("sources.forge", "LICENSE.forge")).items()}
    except ValueError as error:
        fail(str(error))


def build_package(artifacts_path):
    if not os.path.isdir(PACKAGE):
        fail("run from the repository root")
    check_bundle_receipt()
    files = {name: os.path.join(PACKAGE, name) for name in ("manifest.json", "icon.png", "README.md", "CHANGELOG.md", "LICENSE", "NOTICE")}
    files["ForgeOrdnanceArsenal.dll"] = os.path.join(artifacts_path, "bin", "ForgeOrdnanceArsenal", "release", "ForgeOrdnanceArsenal.dll")
    files["forge-ordnance-arsenal.bundle"] = BUNDLE
    files["models/LICENSE.forge"] = os.path.join(PACKAGE, "Assets", "Models", "LICENSE.forge")
    files.update(audio_files())
    files.update(vfx_files("ForgeOrdnance_Arsenal", "arsenal", REPO))
    definitions = os.path.join(PACKAGE, "ForgeOrdnance", "arsenal.forge")
    if not os.path.isfile(definitions):
        fail("missing " + definitions)
    files["ForgeOrdnance/arsenal.forge"] = definitions
    try:
        files.update({entry: str(path) for entry, path in package_common.lang_files(Path(PACKAGE)).items()})
        files.update({entry: str(path) for entry, path in package_common.lang_files(Path(PACKAGE) / "ForgeOrdnance", "ForgeOrdnance/lang").items()})
    except ValueError as error:
        fail(str(error))
    with open(definitions, encoding="utf-8") as source:
        entries = json.load(source)
    for entry in entries:
        icon = entry.get("Icon")
        if not icon:
            fail(entry["Id"] + " has no Icon")
        source = str(media_file(os.path.join(ICONS, icon)))
        if not os.path.isfile(source):
            fail("missing icon " + source + " (run tools/build_icons.ps1)")
        if package_common.png_size(source) != (256, 256):
            fail(source + " is not 256 x 256")
        files["ForgeOrdnance/" + icon] = source
    try:
        package_common.package_files(Path(PACKAGE), "ForgeOrdnance_Arsenal", files, Path(OUT))
    except (OSError, ValueError) as error:
        fail(str(error))


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--artifacts-path", default="artifacts/dotnet-arsenal", help="dotnet build artifacts root")
    parser.add_argument("--output", help="folder that receives the zip")
    args = parser.parse_args()
    global OUT
    OUT = os.path.abspath(args.output) if args.output else OUT
    build_package(os.path.join(REPO, args.artifacts_path))


if __name__ == "__main__":
    main()
