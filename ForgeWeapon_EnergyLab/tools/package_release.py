"""Builds the Thunderstore zip of ForgeWeapon_EnergyLab into artifacts/work/release-packages/. Nothing is published.

Run from the repository root after the Release build (the build copies the model bundles and the audio folder beside the DLL):
    dotnet build Mods/ForgeWeapon_EnergyLab/ForgeWeaponEnergyLab.csproj -c Release --artifacts-path artifacts/dotnet-energylab ...
    python Mods/ForgeWeapon_EnergyLab/tools/package_release.py

The zip holds the plugin DLL, the ForgeEnergyLab folder (energylab.forge, its lang files, the eight model bundles it names, the shared
bundle they depend on and Unity's manifest ForgeEnergyLab/ForgeEnergyLab)
and the audio folder (cues.forge and the Ogg files it names) at its root beside manifest.json, icon.png, README.md, CHANGELOG.md and LICENSE (r2modman installs them into
BepInEx/plugins/<package>/). The effect bundles are embedded in the DLL. Fixed timestamps: the same inputs give the same bytes.
"""
import argparse
from pathlib import Path
import json
import os
import re
import sys

sys.path.insert(0, os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "..", "Build"))
import package_common  # noqa: E402
from package_common import vfx_files  # noqa: E402  (one per-file media rule for every package)

REPO = str(package_common.REPO)
PACKAGE = os.path.join(REPO, "Mods", "ForgeWeapon_EnergyLab")
OUT = os.path.join(REPO, "artifacts", "work", "release-packages")
METADATA = ("manifest.json", "icon.png", "README.md", "CHANGELOG.md", "LICENSE", "NOTICE")
CONTENT = "ForgeEnergyLab"
MODEL_BUNDLES = ("arc", "beam", "blast", "disc", "flame", "hole", "plasma-arc", "plasma-blast")


def fail(message):
    sys.exit("package_release: " + message)


def check_versions(version):
    """The manifest, release.json (the one version table) and the public API's EnergyLab.Version must name the same release."""
    with open(os.path.join(PACKAGE, "Core", "EnergyApi.cs"), encoding="utf-8") as source:
        api = re.search(r'public const string Version = "([^"]+)"', source.read())
    found = {"manifest.json": version, "release.json": package_common.release_version(Path(REPO) / "Mods", "ForgeWeapon_EnergyLab"), "Core/EnergyApi.cs": api and api.group(1)}
    if len(set(found.values())) != 1 or None in found.values():
        fail("the versions differ: " + ", ".join(name + " " + str(value) for name, value in found.items()))


def audio_files():
    try:
        return package_common.audio_files(Path(BUILD) / "audio")
    except ValueError as error:
        fail(str(error) + " (run the Release build)")


def main():
    global BUILD, OUT
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--artifacts-path", default="artifacts/dotnet-energylab", help="dotnet build artifacts root")
    parser.add_argument("--output", help="folder that receives the zip")
    args = parser.parse_args()
    OUT = os.path.abspath(args.output) if args.output else OUT
    BUILD = os.path.join(REPO, args.artifacts_path, "bin", "ForgeWeaponEnergyLab", "release")
    with open(os.path.join(PACKAGE, "manifest.json"), encoding="utf-8") as source:
        check_versions(json.load(source)["version_number"])
    files = {name: os.path.join(PACKAGE, name) for name in METADATA}
    files["ForgeWeaponEnergyLab.dll"] = os.path.join(BUILD, "ForgeWeaponEnergyLab.dll")
    # The gun bundles, the shared bundle they depend on and Unity's manifest of the set (named after the folder, where ForgeCore reads it).
    for name in ["forge-energy-model-" + model + ".bundle" for model in MODEL_BUNDLES] + ["forge-energy-model-shared.bundle", CONTENT]:
        files[CONTENT + "/" + name] = os.path.join(BUILD, CONTENT, name)
    content = os.path.join(PACKAGE, CONTENT)
    files[CONTENT + "/energylab.forge"] = os.path.join(content, "energylab.forge")
    files.update(audio_files())
    files.update(vfx_files("ForgeWeapon_EnergyLab", "energylab", REPO))
    try:
        files.update({entry: str(path) for entry, path in package_common.lang_files(Path(content), CONTENT + "/lang").items()})
        files.update({entry: str(path) for entry, path in package_common.lang_files(Path(PACKAGE)).items()})
        package_common.package_files(Path(PACKAGE), "ForgeWeapon_EnergyLab", files, Path(OUT))
    except (OSError, ValueError) as error:
        fail(str(error))


if __name__ == "__main__":
    main()
