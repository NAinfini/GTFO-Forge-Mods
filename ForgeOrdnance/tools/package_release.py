"""Builds the Thunderstore zip of ForgeOrdnance into artifacts/work/ordnance-release/.

Run from the repository root after the Release build, art build and audio build:
    dotnet build Mods/ForgeOrdnance/ForgeOrdnance.csproj -c Release --artifacts-path artifacts/dotnet-ordnance ...
    pwsh Mods/ForgeOrdnance/tools/build_assets.ps1
    pwsh Mods/ForgeOrdnance/tools/build_audio.ps1
    python Mods/ForgeOrdnance/tools/package_release.py

The zip holds plugin files at the root and the AudioKit audio folder (cues.forge and <cue>.ogg files) under audio/ (r2modman installs them into
BepInEx/plugins/<package>/), with fixed timestamps so the same inputs give the same bytes. The manifest is checked
against Thunderstore's rules first. Nothing is published.
"""
import argparse
from pathlib import Path
import json
import os
import sys

sys.path.insert(0, os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "..", "Build"))
import package_common  # noqa: E402
from package_common import media_path, vfx_files  # noqa: E402  (one per-file media rule for every package)

REPO = str(package_common.REPO)
PACKAGE = os.path.join(REPO, "Mods", "ForgeOrdnance")
OUT = os.path.join(REPO, "artifacts", "work", "ordnance-release")
# The verified bundle, its Unity receipt and build log stay in the package (build_assets.ps1 copies them there); each is read through
# media_path (this checkout's file, else the main checkout's).
BUNDLE = str(media_path("ForgeOrdnance/Assets/Models/forge-ordnance.bundle", REPO))
BUNDLE_MANIFEST = str(media_path("ForgeOrdnance/Assets/Models/forge-ordnance.manifest.json", REPO))
BUILD_LOG = str(media_path("ForgeOrdnance/Assets/Models/unity-build.log", REPO))
METADATA = ("manifest.json", "icon.png", "README.md", "CHANGELOG.md", "LICENSE", "NOTICE")


def fail(message):
    sys.exit("package_release: " + message)


POD_PARTS = {"body", "cradle", "door_1", "door_2", "door_3", "door_4", "interior", "shelf_1", "shelf_2"}
# Surfaces of the older pods the current one replaced; the bundle must never carry them again.
REMOVED_POD_SURFACES = ("Container", "Cage", "ArsenalPod_", "M02_Pod", "Pod_InnerSteel")


def check_pod(profile):
    """The bundle holds exactly one drop pod, never a second look or an older pod."""
    pods = [prefab for prefab in profile.get("prefabs", []) if "pod" in prefab["name"].lower() and prefab["name"] != "pod-impact"]
    if [prefab["name"] for prefab in pods] != ["drop-pod"]:
        fail("the bundle must hold exactly one drop pod prefab named drop-pod, found " + ", ".join(prefab["name"] for prefab in pods))
    parts = {renderer["name"] for renderer in pods[0]["renderers"]}
    if not POD_PARTS <= parts or "light_strip" in parts:
        fail("drop-pod parts are wrong: missing " + ", ".join(sorted(POD_PARTS - parts)) + (", obsolete light_strip" if "light_strip" in parts else ""))
    surfaces = {surface.split("/", 1)[1] for renderer in pods[0]["renderers"] for surface in renderer["surfaces"]}
    removed = sorted(name for name in surfaces if name.startswith(REMOVED_POD_SURFACES))
    if removed:
        fail("drop-pod uses surfaces of an older pod: " + ", ".join(removed))


# Every prefab the plugin loads by name (OrdnanceAssets, PodImpactEffects, OrdnanceFx); a bundle built before one existed is stale.
REQUIRED_PREFABS = ("beacon", "drop-pod", "turret-shell", "pod-impact", "ordnance-fx")


def check_required_prefabs(profile):
    names = {prefab["name"] for prefab in profile.get("prefabs", [])}
    missing = [name for name in REQUIRED_PREFABS if name not in names]
    if missing:
        fail("the bundle lacks the prefabs " + ", ".join(missing) + " the plugin loads by name; rebuild it with tools/build_assets.ps1")


def check_bundle(bundle, receipt, build_log):
    for path in (bundle, receipt, build_log):
        if not os.path.isfile(path):
            fail("missing " + path)
    with open(receipt, encoding="utf-8") as source:
        built = json.load(source)
    actual_bytes = os.path.getsize(bundle)
    actual_hash = package_common.sha256_file(bundle)
    if built.get("bundle") != os.path.basename(bundle) or built.get("bytes") != actual_bytes or built.get("sha256") != actual_hash:
        fail("bundle does not match its Unity build receipt: " + receipt)
    check_pod(built.get("profile", {}))
    check_required_prefabs(built.get("profile", {}))
    marker = "ORDNANCE_BUNDLE_VERIFIED " + os.path.basename(bundle) + " " + str(actual_bytes) + " bytes " + actual_hash
    with open(build_log, encoding="utf-8", errors="replace") as source:
        if marker not in source.read():
            fail("bundle has no matching successful-build marker in " + build_log)


def audio_files(folder):
    """The AudioKit audio folder: cues.forge, every <cue>.ogg it names, and the source and licence records."""
    try:
        return {entry: str(path) for entry, path in package_common.media_audio_files(os.path.join(folder, "Assets", "Audio"), ("sources.forge", "LICENSE.forge")).items()}
    except ValueError as error:
        fail(str(error))


def package(folder, extra):
    try:
        files = {name: os.path.join(folder, name) for name in METADATA}
        files.update(extra)
        package_common.package_files(Path(folder), "ForgeOrdnance", files, Path(OUT))
    except (OSError, ValueError) as error:
        fail(str(error))


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--artifacts-path", default="artifacts/dotnet-ordnance", help="dotnet build artifacts root")
    parser.add_argument("--output", help="folder that receives the zip")
    args = parser.parse_args()
    global OUT
    OUT = os.path.abspath(args.output) if args.output else OUT
    dll = os.path.join(REPO, args.artifacts_path, "bin", "ForgeOrdnance", "release", "ForgeOrdnance.dll")
    mods = os.path.join(REPO, "Mods")
    if not os.path.isdir(mods):
        fail("run from the repository root")
    check_bundle(BUNDLE, BUNDLE_MANIFEST, BUILD_LOG)
    extra = {"ForgeOrdnance.dll": dll, "forge-ordnance.bundle": BUNDLE,
             "models/LICENSE.forge": os.path.join(PACKAGE, "Assets", "Models", "LICENSE.forge")}
    extra.update(audio_files(PACKAGE))
    extra.update(vfx_files("ForgeOrdnance", "ordnance", REPO))
    extra.update({entry: str(path) for entry, path in package_common.lang_files(Path(PACKAGE)).items()})
    package(PACKAGE, extra)


if __name__ == "__main__":
    main()
