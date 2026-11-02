"""Builds the Thunderstore zip of ForgeOrdnance into artifacts/work/ordnance-release/.

Run from the repository root after the Release build, art build and audio build:
    dotnet build Mods/ForgeOrdnance/ForgeOrdnance.csproj -c Release --artifacts-path artifacts/dotnet-ordnance ...
    pwsh Mods/ForgeOrdnance/tools/build_assets.ps1
    pwsh Mods/ForgeOrdnance/tools/build_audio.ps1
    python Mods/ForgeOrdnance/tools/package_release.py

The zip holds plugin files at the root and the AudioKit audio folder (cues.json and <cue>.ogg files) under audio/ (r2modman installs them into
BepInEx/plugins/<package>/), with fixed timestamps so the same inputs give the same bytes. The manifest is checked
against Thunderstore's rules first. Nothing is published.
"""
import argparse
import hashlib
import json
import os
import re
import struct
import sys
import tempfile
import zipfile

REPO = os.getcwd()
PACKAGE = os.path.join(REPO, "Mods", "ForgeOrdnance")
OUT = os.path.join(REPO, "artifacts", "work", "ordnance-release")
# The verified bundle, its Unity receipt and build log stay in the package (build_assets.ps1 copies them there); a worktree points at the main checkout with FORGE_MODS_ROOT.
MODELS = os.path.join(os.environ.get("FORGE_MODS_ROOT") or os.path.join(REPO, "Mods"), "ForgeOrdnance", "Assets", "Models")
BUNDLE = os.path.join(MODELS, "forge-ordnance.bundle")
BUNDLE_MANIFEST = os.path.join(MODELS, "forge-ordnance.manifest.json")
BUILD_LOG = os.path.join(MODELS, "unity-build.log")
METADATA = ("manifest.json", "icon.png", "README.md", "CHANGELOG.md", "LICENSE")
TEAM = "NAinfini"


def fail(message):
    sys.exit("package_release: " + message)


def sha256(path):
    digest = hashlib.sha256()
    with open(path, "rb") as source:
        for chunk in iter(lambda: source.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


POD_PARTS = {"body", "cradle", "door_1", "door_2", "door_3", "door_4", "interior", "shelf_1", "shelf_2"}
# Surfaces of the square POD model that the hexagonal M02 pod replaced; the bundle must never carry them again.
REMOVED_POD_SURFACES = ("Container", "Cage", "ArsenalPod_")


def check_pod(profile):
    """The bundle holds exactly one drop pod: the hexagonal M02 (textured by M02_Pod), never a second look or the removed POD."""
    pods = [prefab for prefab in profile.get("prefabs", []) if "pod" in prefab["name"].lower() and prefab["name"] != "pod-impact"]
    if [prefab["name"] for prefab in pods] != ["drop-pod"]:
        fail("the bundle must hold exactly one drop pod prefab named drop-pod, found " + ", ".join(prefab["name"] for prefab in pods))
    parts = {renderer["name"] for renderer in pods[0]["renderers"]}
    if not POD_PARTS <= parts or "light_strip" in parts:
        fail("drop-pod parts are wrong: missing " + ", ".join(sorted(POD_PARTS - parts)) + (", obsolete light_strip" if "light_strip" in parts else ""))
    surfaces = {surface.split("/", 1)[1] for renderer in pods[0]["renderers"] for surface in renderer["surfaces"]}
    if "M02_Pod" not in surfaces:
        fail("drop-pod is not the hexagonal M02 pod (no M02_Pod surface)")
    removed = sorted(name for name in surfaces if name.startswith(REMOVED_POD_SURFACES))
    if removed:
        fail("drop-pod uses surfaces of the removed POD model: " + ", ".join(removed))


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
    actual_hash = sha256(bundle)
    if built.get("bundle") != os.path.basename(bundle) or built.get("bytes") != actual_bytes or built.get("sha256") != actual_hash:
        fail("bundle does not match its Unity build receipt: " + receipt)
    check_pod(built.get("profile", {}))
    check_required_prefabs(built.get("profile", {}))
    marker = "ORDNANCE_BUNDLE_VERIFIED " + os.path.basename(bundle) + " " + str(actual_bytes) + " bytes " + actual_hash
    with open(build_log, encoding="utf-8", errors="replace") as source:
        if marker not in source.read():
            fail("bundle has no matching successful-build marker in " + build_log)


def check_package(folder):
    manifest = json.load(open(os.path.join(folder, "manifest.json"), encoding="utf-8"))
    for key in ("name", "version_number", "website_url", "description", "dependencies"):
        if key not in manifest:
            fail(folder + ": manifest.json lacks " + key)
    if not re.fullmatch(r"[A-Za-z0-9_]+", manifest["name"]):
        fail(folder + ": the name may hold only letters, digits and _")
    if not re.fullmatch(r"\d+\.\d+\.\d+", manifest["version_number"]):
        fail(folder + ": version_number must be major.minor.patch")
    if len(manifest["description"]) > 250:
        fail(folder + ": the description is longer than 250 characters")
    for dependency in manifest["dependencies"]:
        if not re.fullmatch(r"[A-Za-z0-9_]+-[A-Za-z0-9_]+-\d+\.\d+\.\d+", dependency):
            fail(folder + ": dependency '" + dependency + "' is not Team-Name-1.2.3")
    with open(os.path.join(folder, "icon.png"), "rb") as icon:
        head = icon.read(24)
    if head[:8] != b"\x89PNG\r\n\x1a\n" or struct.unpack(">II", head[16:24]) != (256, 256):
        fail(folder + ": icon.png must be a 256 x 256 PNG")
    return manifest


def audio_files(folder):
    """The AudioKit audio folder: cues.json, every <cue>.ogg it names, and the source and licence records."""
    audio = os.path.join(folder, "Assets", "Audio")
    settings_path = os.path.join(audio, "cues.json")
    sources_path = os.path.join(audio, "sources.json")
    licence_path = os.path.join(audio, "License.txt")
    for path in (settings_path, sources_path, licence_path):
        if not os.path.isfile(path):
            fail("missing " + path + " (run tools/build_audio.ps1)")
    with open(settings_path, encoding="utf-8") as source:
        settings = json.load(source)
    cues = settings.get("cues") if isinstance(settings, dict) else None
    if not isinstance(cues, dict) or not cues:
        fail("invalid AudioKit cues.json " + settings_path)
    files = {
        "audio/cues.json": settings_path,
        "audio/sources.json": sources_path,
        "audio/License.txt": licence_path,
    }
    for cue, entry in cues.items():
        for layer in entry.get("layers", [{"clips": [cue + ".ogg"]}]):
            for clip in layer["clips"]:
                path = os.path.join(audio, clip)
                if not re.fullmatch(r"[a-z0-9_.-]+\.ogg", clip) or not os.path.isfile(path):
                    fail("missing audio file " + path + " for cue " + cue + " (run tools/build_audio.ps1)")
                files["audio/" + clip] = path
    stray = sorted(set(name for name in os.listdir(audio) if name.endswith(".ogg")) - set(key[6:] for key in files))
    if stray:
        fail("Ogg files not named by cues.json would load as extra cues: " + ", ".join(stray))
    return files


def language_files(folder, into="lang"):
    """The language files ForgeUI reads (<code>.json, key -> text) from folder/lang; zh and en must both be there."""
    source = os.path.join(folder, "lang")
    names = sorted(name for name in os.listdir(source) if name.endswith(".json")) if os.path.isdir(source) else []
    if not {"en.json", "zh.json"} <= set(names):
        fail(source + " must hold en.json and zh.json")
    files = {}
    for name in names:
        path = os.path.join(source, name)
        with open(path, encoding="utf-8") as handle:
            texts = json.load(handle)
        if not isinstance(texts, dict) or not all(isinstance(text, str) for text in texts.values()):
            fail(path + " must be a JSON object of key -> text")
        files[into + "/" + name] = path
    return files


def write_zip(name, files):
    for source in files.values():
        if not os.path.isfile(source):
            fail("missing " + source)
    os.makedirs(OUT, exist_ok=True)
    path = os.path.join(OUT, name)
    handle, temporary = tempfile.mkstemp(prefix=name + ".", suffix=".tmp", dir=OUT)
    os.close(handle)
    try:
        with zipfile.ZipFile(temporary, "w", zipfile.ZIP_DEFLATED) as archive:
            for entry, source in sorted(files.items()):
                info = zipfile.ZipInfo(entry, date_time=(2026, 1, 1, 0, 0, 0))
                info.compress_type = zipfile.ZIP_DEFLATED
                info.external_attr = 0o644 << 16
                with open(source, "rb") as data:
                    archive.writestr(info, data.read())
        os.replace(temporary, path)
    finally:
        if os.path.exists(temporary):
            os.remove(temporary)
    digest = sha256(path)
    print(name + ": " + str(os.path.getsize(path)) + " bytes, sha256 " + digest + ", " + ", ".join(sorted(files)))


def package(folder, extra):
    manifest = check_package(folder)
    files = {name: os.path.join(folder, name) for name in METADATA}
    files.update(extra)
    write_zip(TEAM + "-" + manifest["name"] + "-" + manifest["version_number"] + ".zip", files)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--artifacts-path", default="artifacts/dotnet-ordnance", help="dotnet build artifacts root")
    dll = os.path.join(REPO, parser.parse_args().artifacts_path, "bin", "ForgeOrdnance", "release", "ForgeOrdnance.dll")
    mods = os.path.join(REPO, "Mods")
    if not os.path.isdir(mods):
        fail("run from the repository root")
    check_bundle(BUNDLE, BUNDLE_MANIFEST, BUILD_LOG)
    extra = {"ForgeOrdnance.dll": dll, "forge-ordnance.bundle": BUNDLE,
             "models/License.txt": os.path.join(PACKAGE, "Assets", "Models", "License.txt")}
    extra.update(audio_files(PACKAGE))
    extra.update(language_files(PACKAGE))
    package(PACKAGE, extra)


if __name__ == "__main__":
    main()
