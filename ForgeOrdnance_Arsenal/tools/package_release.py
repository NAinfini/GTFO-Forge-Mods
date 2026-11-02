"""Builds the Thunderstore zip of ForgeOrdnance_Arsenal into artifacts/work/ordnance-release/. Nothing is published.

Run from the repository root after the Release build, art build, audio build and icon build:
    dotnet build Mods/ForgeOrdnance_Arsenal/ForgeOrdnanceArsenal.csproj -c Release --artifacts-path artifacts/dotnet-arsenal ...
    pwsh Mods/ForgeOrdnance_Arsenal/tools/build_assets.ps1
    pwsh Mods/ForgeOrdnance_Arsenal/tools/build_audio.ps1
    pwsh Mods/ForgeOrdnance_Arsenal/tools/build_icons.ps1
    python Mods/ForgeOrdnance_Arsenal/tools/package_release.py

The zip holds manifest.json, icon.png, README.md, CHANGELOG.md, ForgeOrdnanceArsenal.dll and forge-ordnance-arsenal.bundle
at its root, the AudioKit audio folder (cues.json and <cue>.ogg files) under audio/, and definitions with their icons in ForgeOrdnance/ (r2modman installs it all into
BepInEx/plugins/NAinfini-ForgeOrdnance_Arsenal/, where ForgeOrdnance finds the folder). Fixed timestamps: the same inputs
give the same bytes. The Thunderstore manifest and Unity's hash-bound bundle receipt are checked before packaging.
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
PACKAGE = os.path.join(REPO, "Mods", "ForgeOrdnance_Arsenal")
OUT = os.path.join(REPO, "artifacts", "work", "ordnance-release")
# The verified bundle and its Unity receipt stay in the package (build_assets.ps1 copies them there); a worktree points at the main checkout with FORGE_MODS_ROOT.
BUNDLE = os.path.join(os.environ.get("FORGE_MODS_ROOT") or os.path.join(REPO, "Mods"), "ForgeOrdnance_Arsenal", "Assets", "Models", "forge-ordnance-arsenal.bundle")
BUNDLE_MANIFEST = os.path.join(os.path.dirname(BUNDLE), "forge-ordnance-arsenal.manifest.json")
ICONS = os.path.join(PACKAGE, "Assets", "icons")
TEAM = "NAinfini"


def fail(message):
    sys.exit("package_release: " + message)


def png_size(path):
    with open(path, "rb") as image:
        head = image.read(24)
    if head[:8] != b"\x89PNG\r\n\x1a\n":
        fail(path + " is not a PNG")
    return struct.unpack(">II", head[16:24])


def file_sha256(path):
    digest = hashlib.sha256()
    with open(path, "rb") as source:
        for chunk in iter(lambda: source.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


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
    digest = file_sha256(BUNDLE)
    if receipt.get("bundle") != os.path.basename(BUNDLE) or receipt.get("bytes") != size or receipt.get("sha256") != digest:
        fail("bundle does not match its Unity build receipt; rebuild it with tools/build_assets.ps1")


def check_manifest():
    with open(os.path.join(PACKAGE, "manifest.json"), encoding="utf-8") as source:
        manifest = json.load(source)
    for key in ("name", "version_number", "website_url", "description", "dependencies"):
        if key not in manifest:
            fail("manifest.json lacks " + key)
    if not re.fullmatch(r"[A-Za-z0-9_]+", manifest["name"]):
        fail("the name may hold only letters, digits and _")
    if not re.fullmatch(r"\d+\.\d+\.\d+", manifest["version_number"]):
        fail("version_number must be major.minor.patch")
    if len(manifest["description"]) > 250:
        fail("the description is longer than 250 characters")
    for dependency in manifest["dependencies"]:
        if not re.fullmatch(r"[A-Za-z0-9_]+-[A-Za-z0-9_]+-\d+\.\d+\.\d+", dependency):
            fail("dependency '" + dependency + "' is not Team-Name-1.2.3")
    if png_size(os.path.join(PACKAGE, "icon.png")) != (256, 256):
        fail("icon.png must be 256 x 256")
    return manifest


def audio_files():
    """The AudioKit audio folder: cues.json, every <cue>.ogg it names, and the source and licence records."""
    audio = os.path.join(PACKAGE, "Assets", "Audio")
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


def language_files(source, into):
    """The language files ForgeUI reads (<code>.json, key -> text) from source; zh and en must both be there."""
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


def build_package(artifacts_path):
    if not os.path.isdir(PACKAGE):
        fail("run from the repository root")
    manifest = check_manifest()
    check_bundle_receipt()
    files = {name: os.path.join(PACKAGE, name) for name in ("manifest.json", "icon.png", "README.md", "CHANGELOG.md", "LICENSE")}
    files["ForgeOrdnanceArsenal.dll"] = os.path.join(artifacts_path, "bin", "ForgeOrdnanceArsenal", "release", "ForgeOrdnanceArsenal.dll")
    files["forge-ordnance-arsenal.bundle"] = BUNDLE
    files["models/License.txt"] = os.path.join(PACKAGE, "Assets", "Models", "License.txt")
    files.update(audio_files())
    definitions = os.path.join(PACKAGE, "ForgeOrdnance", "arsenal.json")
    if not os.path.isfile(definitions):
        fail("missing " + definitions)
    files["ForgeOrdnance/arsenal.json"] = definitions
    files.update(language_files(os.path.join(PACKAGE, "lang"), "lang"))
    files.update(language_files(os.path.join(PACKAGE, "ForgeOrdnance", "lang"), "ForgeOrdnance/lang"))
    with open(definitions, encoding="utf-8") as source:
        entries = json.load(source)
    for entry in entries:
        icon = entry.get("Icon")
        if not icon:
            fail(entry["Id"] + " has no Icon")
        source = os.path.join(ICONS, icon)
        if not os.path.isfile(source):
            fail("missing icon " + source + " (run tools/build_icons.ps1)")
        if png_size(source) != (256, 256):
            fail(source + " is not 256 x 256")
        files["ForgeOrdnance/" + icon] = source
    for source in files.values():
        if not os.path.isfile(source):
            fail("missing " + source)

    os.makedirs(OUT, exist_ok=True)
    name = TEAM + "-" + manifest["name"] + "-" + manifest["version_number"] + ".zip"
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
    except BaseException:
        try:
            os.remove(temporary)
        except FileNotFoundError:
            pass
        raise

    digest = file_sha256(path)
    return name, path, digest, len(files)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--artifacts-path", default="artifacts/dotnet-arsenal", help="dotnet build artifacts root")
    args = parser.parse_args()
    name, path, digest, count = build_package(args.artifacts_path)
    print(name + ": " + str(os.path.getsize(path)) + " bytes, sha256 " + digest + ", " + str(count) + " files")


if __name__ == "__main__":
    main()
