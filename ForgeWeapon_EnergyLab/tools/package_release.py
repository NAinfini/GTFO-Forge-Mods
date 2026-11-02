"""Builds the Thunderstore zip of ForgeWeapon_EnergyLab into artifacts/work/release-packages/. Nothing is published.

Run from the repository root after the Release build (the build copies the model bundles and the audio folder beside the DLL):
    dotnet build Mods/ForgeWeapon_EnergyLab/ForgeWeaponEnergyLab.csproj -c Release --artifacts-path artifacts/dotnet-energylab ...
    python Mods/ForgeWeapon_EnergyLab/tools/package_release.py

The zip holds the plugin DLL, the ForgeEnergyLab folder (energylab.json, its lang files and the eight model bundles it names)
and the audio folder (cues.json and the Ogg files it names) at its root beside manifest.json, icon.png, README.md, CHANGELOG.md and LICENSE (r2modman installs them into
BepInEx/plugins/<package>/). The effect bundles are embedded in the DLL. Fixed timestamps: the same inputs give the same bytes.
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
PACKAGE = os.path.join(REPO, "Mods", "ForgeWeapon_EnergyLab")
OUT = os.path.join(REPO, "artifacts", "work", "release-packages")
METADATA = ("manifest.json", "icon.png", "README.md", "CHANGELOG.md", "LICENSE")
TEAM = "NAinfini"
CONTENT = "ForgeEnergyLab"
MODEL_BUNDLES = ("arc", "beam", "blast", "disc", "flame", "hole", "plasma-arc", "plasma-blast")


def fail(message):
    sys.exit("package_release: " + message)


def check_versions(version):
    """The manifest, the project's <Version> and the public API's EnergyLab.Version must name the same release."""
    with open(os.path.join(PACKAGE, "ForgeWeaponEnergyLab.csproj"), encoding="utf-8") as source:
        project = re.search(r"<Version>([^<]+)</Version>", source.read())
    with open(os.path.join(PACKAGE, "Core", "EnergyApi.cs"), encoding="utf-8") as source:
        api = re.search(r'public const string Version = "([^"]+)"', source.read())
    found = {"manifest.json": version, "ForgeWeaponEnergyLab.csproj": project and project.group(1), "Core/EnergyApi.cs": api and api.group(1)}
    if len(set(found.values())) != 1 or None in found.values():
        fail("the versions differ: " + ", ".join(name + " " + str(value) for name, value in found.items()))


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
    check_versions(manifest["version_number"])
    with open(os.path.join(PACKAGE, "icon.png"), "rb") as icon:
        head = icon.read(24)
    if head[:8] != b"\x89PNG\r\n\x1a\n" or struct.unpack(">II", head[16:24]) != (256, 256):
        fail("icon.png must be a 256 x 256 PNG")
    return manifest


def audio_files():
    folder = os.path.join(BUILD, "audio")
    settings_path = os.path.join(folder, "cues.json")
    if not os.path.isfile(settings_path):
        fail("missing " + settings_path + " (run the Release build)")
    with open(settings_path, encoding="utf-8") as source:
        cues = json.load(source).get("cues")
    if not isinstance(cues, dict) or not cues:
        fail("invalid AudioKit cues.json " + settings_path)
    files = {"audio/cues.json": settings_path}
    for cue, entry in cues.items():
        for layer in entry.get("layers", [{"clips": [cue + ".ogg"]}]):
            for clip in layer["clips"]:
                path = os.path.join(folder, clip)
                if not re.fullmatch(r"[a-z0-9_.-]+\.ogg", clip) or not os.path.isfile(path):
                    fail("missing audio file " + path + " for cue " + cue)
                files["audio/" + clip] = path
    stray = sorted(set(name for name in os.listdir(folder) if name.endswith(".ogg")) - set(key[6:] for key in files))
    if stray:
        fail("Ogg files not named by cues.json would load as extra cues: " + ", ".join(stray))
    return files


def main():
    global BUILD
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--artifacts-path", default="artifacts/dotnet-energylab", help="dotnet build artifacts root")
    BUILD = os.path.join(REPO, parser.parse_args().artifacts_path, "bin", "ForgeWeaponEnergyLab", "release")
    manifest = check_manifest()
    files = {name: os.path.join(PACKAGE, name) for name in METADATA}
    files["ForgeWeaponEnergyLab.dll"] = os.path.join(BUILD, "ForgeWeaponEnergyLab.dll")
    for model in MODEL_BUNDLES:
        name = "forge-energy-model-" + model + ".bundle"
        files[CONTENT + "/" + name] = os.path.join(BUILD, CONTENT, name)
    content = os.path.join(PACKAGE, CONTENT)
    files[CONTENT + "/energylab.json"] = os.path.join(content, "energylab.json")
    for language in sorted(os.listdir(os.path.join(content, "lang"))):
        files[CONTENT + "/lang/" + language] = os.path.join(content, "lang", language)
    files.update(audio_files())
    languages = os.path.join(PACKAGE, "lang")
    names = sorted(name for name in os.listdir(languages) if name.endswith(".json")) if os.path.isdir(languages) else []
    if not {"en.json", "zh.json"} <= set(names):
        fail(languages + " must hold en.json and zh.json")
    for name in names:
        files["lang/" + name] = os.path.join(languages, name)
    for source in files.values():
        if not os.path.isfile(source):
            fail("missing " + source)
    name = TEAM + "-" + manifest["name"] + "-" + manifest["version_number"] + ".zip"
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
    with open(path, "rb") as data:
        digest = hashlib.sha256(data.read()).hexdigest()
    print(name + ": " + str(os.path.getsize(path)) + " bytes, sha256 " + digest + ", " + str(len(files)) + " files")


if __name__ == "__main__":
    main()
